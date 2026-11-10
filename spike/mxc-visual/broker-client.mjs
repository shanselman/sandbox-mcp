// Ordinary-user client. Only the explicit installer command elevates.
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { provisionContainer, deprovisionContainer } from '@microsoft/mxc-sdk/v1';
import { ISO_REQUEST } from './session.mjs';
import { atomicJson, readRecord, mergePending, copyEvidence, verifyVisualEvidence, recoverIndependent } from './broker-client-state.mjs';
import { startLiveCollector, summarizeStream, requireFreshLiveOutput } from './live-evidence.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const install = path.join(process.env.ProgramW6432 ?? process.env.ProgramFiles, 'SandboxMcpFullBroker');
const current = JSON.parse(fs.readFileSync(path.join(install, 'current.json'), 'utf8'));
if (current.schema !== 1 || !/^[A-F0-9]{64}$/.test(current.release)) throw new Error('Invalid installed release record');
const executable = path.join(install, 'versions', current.release, 'broker', 'SandboxMcp.FullBroker.exe');
const state = path.join(native(['client-path']).root, 'jobs');
fs.mkdirSync(state, { recursive: true });
const operation = process.argv[2] ?? 'status';
const variant = operation === 'run-fixture' ? process.argv[3] : undefined;
const liveOutput = process.argv[4] ? path.resolve(process.argv[4]) : null;
const gridOutput = operation === 'run-grid' && process.argv[3] ? path.resolve(process.argv[3]) : null;
if (!['status', 'run-fixture', 'run-grid', 'recover'].includes(operation)
  || process.argv.length > 5 || operation === 'run-grid' && (!gridOutput || liveOutput)
  || ['status', 'recover'].includes(operation) && process.argv.length > 3
  || variant && !['light', 'dark', 'dark-large'].includes(variant))
  throw new Error('Expected status, recover, run-grid <batch-directory>, or run-fixture [light|dark|dark-large] [live-output-directory]');

function native(args, input) {
  const result = spawnSync(executable, args, { input, encoding: 'utf8', timeout: 225000, maxBuffer: 1024 * 1024 });
  if (result.error) throw result.error;
  let response;
  try { response = JSON.parse(result.stdout || result.stderr); }
  catch { throw new Error(`Broker client failed without a JSON response (exit ${result.status})`); }
  if (result.status !== 0 || response.ok === false) {
    const error = new Error(response.error ?? `Broker client exited ${result.status}`);
    error.response = response;
    throw error;
  }
  return response;
}
const call = request => native(['call'], JSON.stringify({ protocol: 1, ...request }));
async function callLive(request) {
  const child = spawn(executable, ['call'], { stdio: ['pipe', 'pipe', 'pipe'] });
  let stdout = '', stderr = '', outputExceeded = false;
  child.stdout.on('data', data => { if (stdout.length + data.length > 1024 * 1024) outputExceeded = true; else stdout += data; });
  child.stderr.on('data', data => { stderr = (stderr + data).slice(-4096); });
  child.stdin.end(JSON.stringify({ protocol: 1, ...request }));
  const [code] = await once(child, 'exit');
  if (outputExceeded) throw new Error('Broker response exceeded limit');
  let response;
  try { response = JSON.parse(stdout || stderr); }
  catch { throw new Error(`Broker call failed without JSON (exit ${code})`); }
  if (code !== 0 || response.ok === false) throw new Error(response.error ?? `Broker call exited ${code}`);
  return response;
}
const save = record => atomicJson(path.join(state, `${record.id}.json`), record);
const readLocal = () => fs.readdirSync(state).filter(name => /^[a-f0-9]{32}\.json$/.test(name))
  .map(name => readRecord(path.join(state, name)));

async function recover() {
  const remote = call({ operation: 'recover' });
  const records = new Map(readLocal().map(record => [record.id, record]));
  const details = new Map();
  const errors = (remote.errors ?? []).map(message => new Error(message));
  const blocked = new Set();
  for (const pending of remote.pending) {
    try {
      const detail = call({ operation: 'result', id: pending.id });
      const record = mergePending(records.get(pending.id), pending, detail);
      records.set(record.id, record);
      details.set(record.id, detail);
      save(record);
    } catch (error) { blocked.add(pending.id); errors.push(error); }
  }
  const recovered = await recoverIndependent([...records.values()].filter(record => !blocked.has(record.id)), async record => {
    if (!record.containerId || !record.metadata)
      throw new Error(`Provisioning outcome is uncertain for ${record.id}; preserve this record and reconcile MXC accounts before retrying`);
    const detail = details.get(record.id);
    if (detail && (detail.state !== 'finished' || detail.report?.sessionCleanupComplete !== true))
      throw new Error(`Session cleanup is not confirmed for ${record.id}`);
    const evidence = path.join(here, 'evidence', `broker-${record.id}`);
    fs.mkdirSync(evidence, { recursive: true });
    if (detail) atomicJson(path.join(evidence, 'broker-result.json'), detail);
    else if (record.lastResponse) atomicJson(path.join(evidence, 'broker-result.json'), record.lastResponse);
    if (record.phase !== 'account-removed') {
      if (!record.evidenceCopied && record.metadata.ephemeralWorkspacePath && detail?.outputKey) {
        record.evidence = copyEvidence(record.metadata.ephemeralWorkspacePath, detail.outputKey, path.join(evidence, 'visual'));
        record.evidenceCopied = true;
        save(record);
      }
      const account = native(['account-state', record.metadata.agentUserName, record.metadata.agentUserSid]);
      if (account.exists) await deprovisionContainer(record.containerId);
      record.phase = 'account-removed';
      save(record);
    }
    if (detail) call({ operation: 'ack', id: record.id });
    record.phase = 'cleaned';
    atomicJson(path.join(evidence, 'client-record.json'), record);
    fs.unlinkSync(path.join(state, `${record.id}.json`));
    return { id: record.id, evidence, accountRemoved: true };
  });
  errors.push(...recovered.errors);
  if (errors.length) throw new AggregateError(errors, `Recovery incomplete for some records; ${recovered.outcomes.length} independently verified accounts cleaned`);
  return recovered.outcomes;
}

async function withClientLock(action) {
  const child = spawn(executable, ['client-lock'], { stdio: ['pipe', 'pipe', 'pipe'] });
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', data => { stderr = (stderr + data).slice(-4096); });
  const exit = once(child, 'exit');
  let ready = '';
  try {
    for await (const chunk of child.stdout) {
      ready += chunk.toString();
      if (ready.length > 4096) throw new Error('Invalid client-lock response');
      if (ready.includes('\n')) break;
    }
    if (JSON.parse(ready || '{}').locked !== true) throw new Error(stderr || 'Another client may own the broker workflow');
    return await action();
  } finally {
    child.stdin.end();
    const [code] = await exit;
    if (code !== 0) throw new Error(stderr || 'Client ownership coordinator failed');
  }
}

async function runGrid(root, status) {
  const variants = ['light', 'dark', 'dark-large'];
  if (status.maxCoordinatedSessions !== 3 || variants.some(variant => !status.variants?.includes(variant)))
    throw new Error('Update the installed broker before provisioning a coordinated grid');
  for (const variant of variants) requireFreshLiveOutput(path.join(root, variant));
  const records = [], collectors = [], progress = [];
  let response, failure;
  try {
    for (const variant of variants) {
      const record = { id: randomUUID().replaceAll('-', ''), variant, phase: 'provisioning', at: new Date().toISOString() };
      save(record);
      records.push(record);
      const provision = await provisionContainer(ISO_REQUEST);
      record.containerId = provision.containerId; record.metadata = provision.metadata; record.phase = 'provisioned';
      try { save(record); }
      catch (error) {
        try { await deprovisionContainer(provision.containerId); fs.unlinkSync(path.join(state, `${record.id}.json`)); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Account receipt failed and compensating cleanup is incomplete'); }
        throw error;
      }
      collectors.push(startLiveCollector(record.metadata.ephemeralWorkspacePath, path.join(root, variant), { variant, id: record.id }));
    }
    response = await callLive({ operation: 'run-grid', members: records.map(record => ({
      id: record.id, agentName: record.metadata.agentUserName, agentSid: record.metadata.agentUserSid,
      containerId: record.containerId, variant: record.variant,
    })) });
    if (!Array.isArray(response.results) || response.results.length !== 3) throw new Error('Grid did not return three member results');
    for (const record of records) {
      const result = response.results.find(result => result.report?.id === record.id);
      if (!result) throw new Error('Grid result identities do not match provisioned members');
      record.phase = 'returned'; record.lastResponse = result; save(record);
    }
    if (response.results.some(result => result.report.success !== true)) throw new Error('One or more coordinated workers failed');
  } catch (error) { failure = error; }
  finally {
    for (const collector of collectors) {
      try { progress.push(collector.finish(failure?.message)); }
      catch (error) { failure = failure ? new AggregateError([failure, error], 'Grid and collector failed') : error; }
    }
  }
  let cleanup;
  try { cleanup = await recover(); }
  catch (error) {
    if (failure) throw new AggregateError([failure, error], 'Grid failed and recovery is incomplete; do not reprovision');
    throw error;
  }
  const runs = [];
  if (!failure) for (const record of records) {
    const result = response.results.find(result => result.report.id === record.id);
    const directory = path.join(here, 'evidence', `broker-${record.id}`, 'visual');
    const visual = verifyVisualEvidence(directory, result.report.sessionId, record.metadata.agentUserName);
    const raw = JSON.parse(fs.readFileSync(path.join(directory, 'stream.json'), 'utf8'));
    const fixture = JSON.parse(fs.readFileSync(path.join(directory, 'fixture-settings.json'), 'utf8'));
    const stream = summarizeStream(raw, fixture, record.variant);
    if (!stream.cadenceMet) failure = new Error('At least one live stream missed the cadence threshold');
    runs.push({ variant: record.variant, response: result, visual, stream, cleanup: cleanup.find(item => item.id === record.id),
      observedFrames: progress.find(item => item.id === record.id)?.observedFrames,
      firstFrameAt: raw.frames[0].at, lastFrameAt: raw.frames.at(-1).at });
  }
  const sessionIds = runs.map(run => run.response.report.sessionId);
  const overlapMs = runs.length === 3 ? Math.min(...runs.map(run => Date.parse(run.lastFrameAt)))
    - Math.max(...runs.map(run => Date.parse(run.firstFrameAt))) : 0;
  if (!failure && (new Set(sessionIds).size !== 3 || overlapMs < 20000
    || runs.some(run => !run.response.report.allSessionsPresentAtWorkerLaunch || !run.cleanup?.accountRemoved)))
    failure = new Error('Three-session overlap or cleanup proof failed');
  console.log(JSON.stringify({ ok: !failure, runs, overlapMs, cleanup, ...(failure ? { error: failure.message } : {}) }, null, 2));
  if (failure) throw failure;
}

try {
  if (operation === 'status') console.log(JSON.stringify(call({ operation: 'status' }), null, 2));
  else await withClientLock(async () => {
    const recovered = await recover();
    if (operation === 'recover') { console.log(JSON.stringify({ recovered }, null, 2)); return; }
    const status = call({ operation: 'status' });
    if (!status.ready) throw new Error(status.recoveryError ?? status.compatibility?.message ?? 'Broker is not ready');
    if (operation === 'run-grid') { await runGrid(gridOutput, status); return; }
    if (variant && !status.variants?.includes(variant)) throw new Error('Installed broker does not support this variant; update it before provisioning');
    if (liveOutput) requireFreshLiveOutput(liveOutput);
    const record = { id: randomUUID().replaceAll('-', ''), phase: 'provisioning', at: new Date().toISOString() };
    save(record);
    let response, failure, collector, live;
    try {
      const provision = await provisionContainer(ISO_REQUEST);
      record.containerId = provision.containerId;
      record.metadata = provision.metadata;
      record.phase = 'provisioned';
      try { save(record); }
      catch (error) {
        try {
          await deprovisionContainer(provision.containerId);
          fs.unlinkSync(path.join(state, `${record.id}.json`));
        } catch (cleanupError) {
          throw new AggregateError([error, cleanupError], 'Could not persist the account identity or complete compensating cleanup');
        }
        throw error;
      }
      if (liveOutput) collector = startLiveCollector(record.metadata.ephemeralWorkspacePath, liveOutput, { variant, id: record.id });
      response = await callLive({
        operation: 'run-fixture', id: record.id, agentName: provision.metadata.agentUserName,
        agentSid: provision.metadata.agentUserSid, containerId: provision.containerId, ...(variant ? { variant } : {}),
      });
      record.phase = 'returned';
      record.lastResponse = response;
      save(record);
      if (response.report?.success !== true) throw new Error(response.report?.error ?? 'Visual worker failed');
    } catch (error) { failure = error; }
    finally {
      if (collector) {
        try { live = collector.finish(failure?.message); }
        catch (error) { failure = failure ? new AggregateError([failure, error], 'Run and live evidence failed') : error; }
      }
    }
    let cleanup;
    try { cleanup = await recover(); }
    catch (error) {
      if (failure) throw new AggregateError([failure, error], 'Run failed and recovery is incomplete; do not reprovision');
      throw error;
    }
    const visual = failure ? undefined : verifyVisualEvidence(path.join(here, 'evidence', `broker-${record.id}`, 'visual'),
      response.report.sessionId, record.metadata.agentUserName);
    let stream;
    if (!failure && variant) {
      const evidence = path.join(here, 'evidence', `broker-${record.id}`, 'visual');
      stream = summarizeStream(JSON.parse(fs.readFileSync(path.join(evidence, 'stream.json'), 'utf8')),
        JSON.parse(fs.readFileSync(path.join(evidence, 'fixture-settings.json'), 'utf8')), variant);
      if (!stream.cadenceMet) failure = new Error('Measured stream did not meet the 1fps tolerance');
    }
    console.log(JSON.stringify({ response, cleanup, visual, stream, live }, null, 2));
    if (failure) throw failure;
  });
} catch (error) {
  console.error(JSON.stringify({ error: error.message, causes: error.errors?.map(cause => cause.message) }, null, 2));
  process.exitCode = 1;
}

// Ordinary-user client. Only the explicit installer command elevates.
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { provisionContainer, deprovisionContainer } from '@microsoft/mxc-sdk/v1';
import { ISO_REQUEST } from './session.mjs';
import { atomicJson, readRecord, mergePending, copyEvidence, verifyVisualEvidence } from './broker-client-state.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const install = path.join(process.env.ProgramW6432 ?? process.env.ProgramFiles, 'SandboxMcpFullBroker');
const current = JSON.parse(fs.readFileSync(path.join(install, 'current.json'), 'utf8'));
if (current.schema !== 1 || !/^[A-F0-9]{64}$/.test(current.release)) throw new Error('Invalid installed release record');
const executable = path.join(install, 'versions', current.release, 'broker', 'SandboxMcp.FullBroker.exe');
const state = path.join(native(['client-path']).root, 'jobs');
fs.mkdirSync(state, { recursive: true });
const operation = process.argv[2] ?? 'status';
if (process.argv.length > 3 || !['status', 'run-fixture', 'recover'].includes(operation)) throw new Error('Expected status, run-fixture or recover');

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
const save = record => atomicJson(path.join(state, `${record.id}.json`), record);
const readLocal = () => fs.readdirSync(state).filter(name => /^[a-f0-9]{32}\.json$/.test(name))
  .map(name => readRecord(path.join(state, name)));

async function recover() {
  const remote = call({ operation: 'recover' });
  const records = new Map(readLocal().map(record => [record.id, record]));
  const details = new Map();
  for (const pending of remote.pending) {
    const detail = call({ operation: 'result', id: pending.id });
    const record = mergePending(records.get(pending.id), pending, detail);
    records.set(record.id, record);
    details.set(record.id, detail);
    save(record);
  }
  const outcomes = [];
  for (const record of records.values()) {
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
    outcomes.push({ id: record.id, evidence, accountRemoved: true });
  }
  return outcomes;
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

try {
  if (operation === 'status') console.log(JSON.stringify(call({ operation: 'status' }), null, 2));
  else await withClientLock(async () => {
    const recovered = await recover();
    if (operation === 'recover') { console.log(JSON.stringify({ recovered }, null, 2)); return; }
    const status = call({ operation: 'status' });
    if (!status.ready) throw new Error(status.recoveryError ?? 'Broker is not ready');
    const record = { id: randomUUID().replaceAll('-', ''), phase: 'provisioning', at: new Date().toISOString() };
    save(record);
    let response, failure;
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
      response = call({
        operation: 'run-fixture', id: record.id, agentName: provision.metadata.agentUserName,
        agentSid: provision.metadata.agentUserSid, containerId: provision.containerId,
      });
      record.phase = 'returned';
      record.lastResponse = response;
      save(record);
      if (response.report?.success !== true) throw new Error(response.report?.error ?? 'Visual worker failed');
    } catch (error) { failure = error; }
    let cleanup;
    try { cleanup = await recover(); }
    catch (error) {
      if (failure) throw new AggregateError([failure, error], 'Run failed and recovery is incomplete; do not reprovision');
      throw error;
    }
    const visual = failure ? undefined : verifyVisualEvidence(path.join(here, 'evidence', `broker-${record.id}`, 'visual'),
      response.report.sessionId, record.metadata.agentUserName);
    console.log(JSON.stringify({ response, cleanup, visual }, null, 2));
    if (failure) throw failure;
  });
} catch (error) {
  console.error(JSON.stringify({ error: error.message, causes: error.errors?.map(cause => cause.message) }, null, 2));
  process.exitCode = 1;
}

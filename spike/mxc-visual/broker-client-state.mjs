import fs from 'node:fs';
import path from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { validatePng } from './png-evidence.mjs';
import { calculatorIndex } from './visual-workloads.mjs';

export function requireId(id) {
  if (typeof id !== 'string' || !/^[a-f0-9]{32}$/.test(id)) throw new Error('Invalid request ID');
  return id;
}

export function atomicJson(file, value) {
  const temporary = `${file}.${randomUUID()}.tmp`;
  let descriptor;
  try {
    descriptor = fs.openSync(temporary, 'wx', 0o600);
    fs.writeFileSync(descriptor, JSON.stringify(value, null, 2));
    fs.fsyncSync(descriptor);
    fs.closeSync(descriptor);
    descriptor = undefined;
    fs.renameSync(temporary, file);
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
    if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
  }
}

export function readRecord(file) {
  if (fs.statSync(file).size > 65536) throw new Error('Client recovery record exceeds limit');
  const record = JSON.parse(fs.readFileSync(file, 'utf8'));
  requireId(record.id);
  if (path.basename(file) !== `${record.id}.json`) throw new Error('Recovery record identity mismatch');
  if (record.containerId && (typeof record.containerId !== 'string' || record.containerId.length > 4096)) throw new Error('Invalid opaque container ID');
  if (record.metadata && (typeof record.metadata.agentUserName !== 'string' || typeof record.metadata.agentUserSid !== 'string')) throw new Error('Invalid account metadata');
  if (!['provisioning', 'provisioned', 'returned', 'submitted', 'account-removed'].includes(record.phase)) throw new Error('Invalid recovery phase');
  return record;
}

export function mergePending(local, remote, detail) {
  requireId(remote.id);
  const request = detail.request;
  if (!request || request.id !== remote.id || request.agentName !== remote.agentName || request.agentSid !== remote.agentSid)
    throw new Error('Broker recovery identity mismatch');
  if (local && (local.containerId !== request.containerId || local.metadata?.agentUserSid !== request.agentSid))
    throw new Error('Local and broker recovery records disagree; refusing cleanup');
  return local ?? {
    id: remote.id,
    phase: 'submitted',
    containerId: request.containerId,
    metadata: { agentUserName: request.agentName, agentUserSid: request.agentSid },
    recoveredWithoutLocalState: true,
  };
}

export async function recoverIndependent(records, cleanup) {
  const outcomes = [], errors = [];
  for (const record of records) {
    try { outcomes.push(await cleanup(record)); }
    catch (error) { errors.push(error); }
  }
  return { outcomes, errors };
}

export function requireDeprovisionReceipt(record) {
  if (!record.deprovisionAttemptedAt && !record.deprovisionResult) return;
  const warnings = record.deprovisionResult?.warnings;
  if (!Array.isArray(warnings) || warnings.some(warning => typeof warning !== 'string'))
    throw new Error('SDK deprovision outcome is unconfirmed; preserve the receipt and reconcile instead of replaying the container ID');
  if (warnings.length)
    throw new Error(`SDK deprovision reported warnings; preserve the receipt for inspection: ${warnings.join('; ')}`);
}

const evidenceFiles = [
  'startup.png', 'startup.json',
  'worker.json', 'worker-started.json', 'job.json', 'authorization.json', 'fixture-settings.json', 'stream.json', 'live.json',
  'capture-ready.json', 'capture-started.json',
  ...Array.from({ length: 30 }, (_, i) => `frame-${String(i).padStart(3, '0')}.png`),
  ...Array.from({ length: 30 }, (_, i) => `frame-meta-${String(i).padStart(3, '0')}.json`),
  ...['1-before', '2-after'].flatMap(label => ['screen', 'window-bitblt', 'printwindow-0', 'printwindow-2'].map(method => `${label}-${method}.png`)),
];
export function copyEvidence(workspace, key, destination) {
  requireId(key);
  const source = path.join(workspace, `broker-${key}`);
  if (!fs.existsSync(source)) return { copied: false, reason: 'Worker output directory is absent' };
  if (fs.lstatSync(source).isSymbolicLink()) throw new Error('Worker output directory is a reparse point');
  fs.mkdirSync(destination, { recursive: true });
  let copied = 0;
  for (const name of evidenceFiles) {
    const file = path.join(source, name);
    if (!fs.existsSync(file)) continue;
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024) throw new Error('Unexpected worker evidence file');
    const target = path.join(destination, name);
    if (fs.existsSync(target)) {
      const prior = fs.lstatSync(target);
      if (!prior.isFile() || prior.isSymbolicLink() || prior.size !== stat.size
        || createHash('sha256').update(fs.readFileSync(file)).digest('hex') !== createHash('sha256').update(fs.readFileSync(target)).digest('hex'))
        throw new Error('Existing evidence differs; refusing to overwrite it');
    } else fs.copyFileSync(file, target, fs.constants.COPYFILE_EXCL);
    copied++;
  }
  return { copied: true, files: copied };
}

export function verifyVisualEvidence(directory, sessionId, agentName) {
  const worker = JSON.parse(fs.readFileSync(path.join(directory, 'worker.json'), 'utf8'));
  const authorization = JSON.parse(fs.readFileSync(path.join(directory, 'authorization.json'), 'utf8'));
  if (worker.sessionId !== sessionId || worker.user !== agentName || worker.error || worker.appCleanupError
    || authorization.sessionId !== sessionId || authorization.nonOwnerPipeAccessDenied !== true)
    throw new Error('Worker identity, authorization boundary, or execution did not validate');
  const uia = JSON.parse(worker.uia.Stdout);
  const automationPassed = calculatorIndex(worker.variant)
    ? uia.calculatorVerified === true && uia.assertion?.passed === true && uia.assertion.expected === 15 && uia.assertion.actual === 15
    : uia.after?.includes('Output=Hello, FullSession!') && uia.after?.includes('Counter=Count: 2');
  if (worker.uia.ExitCode !== 0 || !automationPassed)
    throw new Error('Expected UIA changes were not observed');
  const images = ['1-before', '2-after'].map(label => {
    const bytes = fs.readFileSync(path.join(directory, `${label}-printwindow-2.png`));
    if (bytes.length < 24 || bytes.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a'
      || bytes.subarray(12, 16).toString('ascii') !== 'IHDR') throw new Error('Expected PNG evidence is missing or malformed');
    validatePng(bytes, { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) });
    return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), hash: createHash('sha256').update(bytes).digest('hex') };
  });
  if (!images[0].width || !images[0].height || images[0].width !== images[1].width
    || images[0].height !== images[1].height || images[0].hash === images[1].hash)
    throw new Error('Before/after window images are not comparable or did not change');
  return { automationAndCaptureAssertionsPassed: true, nonOwnerPipeAccessDenied: true, images, pixelReviewRequired: true };
}

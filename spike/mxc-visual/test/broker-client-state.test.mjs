import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { requireId, atomicJson, readRecord, mergePending, copyEvidence, verifyVisualEvidence } from '../broker-client-state.mjs';

const id = '0123456789abcdef0123456789abcdef';
const metadata = { agentUserName: 'A1-B2', agentUserSid: 'S-1-5-21-1-2-3-1001' };
const request = { id, agentName: metadata.agentUserName, agentSid: metadata.agentUserSid, containerId: 'opaque:never-parse-this' };
const pending = { id, agentName: request.agentName, agentSid: request.agentSid };
function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-broker-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}

test('request IDs reject traversal and ambiguous forms', () => {
  assert.equal(requireId(id), id);
  for (const bad of ['..', '../x', id.toUpperCase(), ` ${id}`, '', null]) assert.throws(() => requireId(bad));
});
test('atomic records preserve the opaque container identity and replace consistently', t => {
  const file = path.join(temporary(t), `${id}.json`);
  atomicJson(file, { id, phase: 'provisioned', containerId: request.containerId, metadata });
  assert.equal(readRecord(file).containerId, request.containerId);
  atomicJson(file, { id, phase: 'account-removed', containerId: request.containerId, metadata });
  assert.equal(readRecord(file).phase, 'account-removed');
  assert.deepEqual(fs.readdirSync(path.dirname(file)), [`${id}.json`]);
});
test('corrupt local state is not silently discarded', t => {
  const file = path.join(temporary(t), `${id}.json`);
  for (const value of [
    { id: '00000000000000000000000000000000', phase: 'provisioning' },
    { id, phase: 'unknown' },
    { id, phase: 'provisioned', containerId: 123 },
  ]) {
    atomicJson(file, value);
    assert.throws(() => readRecord(file));
  }
});
test('broker state can recover missing client state without parsing container IDs', () => {
  const record = mergePending(undefined, pending, { request });
  assert.equal(record.containerId, request.containerId);
  assert.equal(record.recoveredWithoutLocalState, true);
});
test('conflicting account ownership or container records refuse cleanup', () => {
  const local = { id, metadata, containerId: request.containerId };
  assert.equal(mergePending(local, pending, { request }), local);
  assert.throws(() => mergePending({ ...local, containerId: 'different' }, pending, { request }));
  assert.throws(() => mergePending(local, { ...pending, agentSid: 'wrong' }, { request }));
  assert.throws(() => mergePending(local, pending, { request: { ...request, id: 'wrong' } }));
});
test('evidence copy is allowlisted and safely repeatable after interruption', t => {
  const root = temporary(t), workspace = path.join(root, 'shared'), destination = path.join(root, 'evidence');
  const source = path.join(workspace, `broker-${id}`);
  fs.mkdirSync(source, { recursive: true });
  fs.writeFileSync(path.join(source, 'worker.json'), '{"sessionId":14}');
  fs.writeFileSync(path.join(source, 'not-evidence.txt'), 'must not copy');
  assert.equal(copyEvidence(workspace, id, destination).files, 1);
  assert.equal(copyEvidence(workspace, id, destination).files, 1);
  assert.deepEqual(fs.readdirSync(destination), ['worker.json']);
  fs.writeFileSync(path.join(destination, 'worker.json'), 'user changed this');
  assert.throws(() => copyEvidence(workspace, id, destination), /refusing to overwrite/);
});
test('missing output is explicit, never a successful visual verdict', t => {
  const root = temporary(t);
  assert.equal(copyEvidence(root, id, path.join(root, 'out')).copied, false);
});
test('capture assertions bind the user, session, denied agent IPC and changed images', t => {
  const root = temporary(t);
  const worker = {
    sessionId: 14, user: 'A1-B2',
    uia: { ExitCode: 0, Stdout: JSON.stringify({ after: ['Output=Hello, FullSession!', 'Counter=Count: 2'] }) },
  };
  fs.writeFileSync(path.join(root, 'worker.json'), JSON.stringify(worker));
  fs.writeFileSync(path.join(root, 'authorization.json'), JSON.stringify({ sessionId: 14, nonOwnerPipeAccessDenied: true }));
  for (const [label, marker] of [['1-before', 1], ['2-after', 2]]) {
    // Synthetic headers exercise the verifier, not a claim about rendered pixels.
    const bytes = Buffer.alloc(32);
    Buffer.from('89504e470d0a1a0a', 'hex').copy(bytes);
    bytes.write('IHDR', 12);
    bytes.writeUInt32BE(520, 16);
    bytes.writeUInt32BE(400, 20);
    bytes[31] = marker;
    fs.writeFileSync(path.join(root, `${label}-printwindow-2.png`), bytes);
  }
  assert.equal(verifyVisualEvidence(root, 14, 'A1-B2').pixelReviewRequired, true);
  assert.throws(() => verifyVisualEvidence(root, 15, 'A1-B2'), /identity/);
  assert.throws(() => verifyVisualEvidence(root, 14, 'someone-else'), /identity/);
  fs.writeFileSync(path.join(root, 'authorization.json'), JSON.stringify({ sessionId: 14, nonOwnerPipeAccessDenied: false }));
  assert.throws(() => verifyVisualEvidence(root, 14, 'A1-B2'), /authorization/);
});
test('evidence copy rejects a directory junction', t => {
  const root = temporary(t), target = path.join(root, 'target'), workspace = path.join(root, 'workspace');
  fs.mkdirSync(target);
  fs.mkdirSync(workspace);
  fs.symlinkSync(target, path.join(workspace, `broker-${id}`), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => copyEvidence(workspace, id, path.join(root, 'evidence')), /reparse/);
});

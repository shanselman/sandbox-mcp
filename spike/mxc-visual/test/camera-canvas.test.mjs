import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { cameraCanvasOptions } from '../camera-canvas.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'camera-canvas-'));
  const evidence = path.join(root, 'evidence');
  for (const name of ['batch-a', 'batch-b']) {
    fs.mkdirSync(path.join(evidence, name), { recursive: true });
    fs.writeFileSync(path.join(evidence, name, 'batch.json'), JSON.stringify({
      schema: 1, mode: 'concurrent', state: 'prepared', runs: [],
    }));
  }
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return { root, evidence, canvas: cameraCanvasOptions(evidence) };
}

test('native canvas shares the read-only server and idempotent opens, then closes it', async t => {
  const { canvas } = fixture(t);
  const context = { instanceId: 'camera-test', input: { batch: 'batch-a' } };
  t.after(() => canvas.onClose(context));
  const [first, second] = await Promise.all([canvas.open(context), canvas.open(context)]);
  assert.equal(first.url, second.url);
  assert.equal(new URL(first.url).hostname, '127.0.0.1');
  assert.equal((await fetch(first.url)).status, 200);
  assert.equal((await fetch(new URL('grid.js', first.url))).status, 200);
  const state = await canvas.actions[0].handler(context);
  assert.equal(state.state, 'prepared');
  assert.equal((await fetch(new URL('state', first.url), { method: 'POST' })).status, 403);
  assert.equal((await fetch(new URL('/', first.url))).status, 404);
  assert.equal((await fetch(first.url, { headers: { Origin: 'https://not-the-viewer.example' } })).status, 403);
  await assert.rejects(canvas.open({ ...context, input: { batch: 'batch-b' } }), /new canvas instance/);
  await canvas.onClose(context);
  await canvas.onClose(context);
  await assert.rejects(fetch(first.url));
  await assert.rejects(canvas.actions[0].handler(context), /not open/);
});

test('canvas refuses paths outside evidence including junctions and absolute inputs', async t => {
  const { root, evidence, canvas } = fixture(t);
  const context = { instanceId: 'camera-test' };
  for (const batch of ['..', '../..', '.', evidence])
    await assert.rejects(canvas.open({ ...context, input: { batch } }), /inside|relative/);
  const outside = path.join(root, 'outside');
  fs.mkdirSync(outside);
  const link = path.join(evidence, 'linked-outside');
  fs.symlinkSync(outside, link, process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(canvas.open({ ...context, input: { batch: 'linked-outside' } }), /inside/);
});

test('failed opens do not poison a later retry; reconnect recovers from batch files', async t => {
  const { evidence, canvas } = fixture(t);
  const context = { instanceId: 'camera-test', input: { batch: 'batch-a' } };
  const file = path.join(evidence, 'batch-a', 'batch.json');
  fs.writeFileSync(file, '{invalid');
  await assert.rejects(canvas.open(context), /JSON|property|Unexpected/);
  const restored = { schema: 1, mode: 'concurrent', state: 'restored', runs: [] };
  fs.writeFileSync(file, JSON.stringify(restored));
  await canvas.open(context);
  await canvas.onClose(context);
  const reconnected = cameraCanvasOptions(evidence);
  t.after(() => reconnected.onClose(context));
  await reconnected.open(context);
  assert.equal((await reconnected.actions[0].handler(context)).state, 'restored');
});

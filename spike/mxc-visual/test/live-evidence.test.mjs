import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { readFrame, summarizeStream, startLiveCollector, requireFreshLiveOutput } from '../live-evidence.mjs';

const key = '0123456789abcdef0123456789abcdef';
const fixture = { variant: 'dark-large', theme: 'Dark', actualWindowDpi: 96, rasterizationScale: 1,
  fixtureLayoutScale: 1.5, dpiWasChanged: false, scaleIsSimulation: true };
const stream = () => ({ targetFps: 1, frames: Array.from({ length: 30 }, (_, sequence) => ({ sequence, elapsedMs: sequence * 1000 })) });
test('cadence is measured and fixture scaling is not presented as monitor DPI', () => {
  const result = summarizeStream(stream(), fixture, 'dark-large');
  assert.equal(result.measuredFps, 1);
  assert.equal(result.cadenceMet, true);
  assert.equal(result.actualWindowDpi, 96);
  assert.equal(result.scaleIsSimulation, true);
});
test('slow cadence is a failed threshold, not a cosmetic 1fps label', () => {
  const data = stream();
  for (const frame of data.frames) frame.elapsedMs *= 1.2;
  assert.equal(summarizeStream(data, fixture, 'dark-large').cadenceMet, false);
  data.frames[8].elapsedMs += 2500;
  assert.throws(() => summarizeStream(data, fixture, 'dark-large'), /monotonic/);
});
test('wrong settings, missing frames and duplicate sequences are rejected', () => {
  assert.throws(() => summarizeStream(stream(), { ...fixture, theme: 'Light' }, 'dark-large'), /settings/);
  assert.throws(() => summarizeStream(stream(), { ...fixture, actualWindowDpi: 0 }, 'dark-large'), /settings/);
  const short = stream(); short.frames.pop();
  assert.throws(() => summarizeStream(short, fixture, 'dark-large'), /30 distinct/);
  const duplicate = stream(); duplicate.frames[2].sequence = 1;
  assert.throws(() => summarizeStream(duplicate, fixture, 'dark-large'), /30 distinct/);
});
test('live collector binds frame to session and rejects traversal', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-live-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`), destination = path.join(root, 'copy');
  fs.mkdirSync(source, { recursive: true });
  const bytes = Buffer.alloc(24); Buffer.from('89504e470d0a1a0a', 'hex').copy(bytes);
  fs.writeFileSync(path.join(source, 'frame-000.png'), bytes);
  const frame = { sequence: 0, at: '2026-11-10T00:00:00Z', sessionId: 20, file: 'frame-000.png' };
  fs.writeFileSync(path.join(source, 'live.json'), JSON.stringify(frame));
  const observed = { sequences: new Map() };
  assert.equal(readFrame(workspace, destination, observed).sessionId, 20);
  assert.equal(observed.sequences.size, 1);
  fs.writeFileSync(path.join(destination, 'frame-000.png'), Buffer.alloc(24, 1));
  assert.throws(() => readFrame(workspace, destination, observed), /conflicts/);
  assert.throws(() => requireFreshLiveOutput(destination), /not fresh/);
  fs.writeFileSync(path.join(destination, 'frame-000.png'), bytes);
  readFrame(workspace, destination, observed);
  assert.equal(observed.sequences.size, 1);
  fs.writeFileSync(path.join(source, 'live.json'), JSON.stringify({ ...frame, sessionId: 21 }));
  assert.throws(() => readFrame(workspace, destination, observed), /identity changed/);
  fs.writeFileSync(path.join(source, 'live.json'), JSON.stringify({ ...frame, file: '../secrets.png' }));
  assert.throws(() => readFrame(workspace, destination, observed), /frame file/);
});
test('timer write failures are contained and finish always clears the interval', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-live-failure-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), destination = path.join(root, 'out');
  fs.mkdirSync(workspace);
  const collector = startLiveCollector(workspace, destination, { id: key, variant: 'dark' });
  fs.rmdirSync(workspace);
  fs.rmSync(destination, { recursive: true });
  await new Promise(resolve => setTimeout(resolve, 300));
  assert.throws(() => collector.finish(), /ENOENT|collection failed/);
  // An uncaught timer callback would fail the Node test process.
  await new Promise(resolve => setTimeout(resolve, 300));
});
test('immutable frame publication survives a reader holding earlier metadata open', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-immutable-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`), destination = path.join(root, 'copy');
  fs.mkdirSync(source, { recursive: true });
  const observed = { sequences: new Map() }, readers = [];
  try {
    for (let sequence = 0; sequence < 5; sequence++) {
      const suffix = String(sequence).padStart(3, '0');
      const bytes = Buffer.alloc(32); Buffer.from('89504e470d0a1a0a', 'hex').copy(bytes); bytes[31] = sequence;
      fs.writeFileSync(path.join(source, `frame-${suffix}.png`), bytes);
      const metadata = path.join(source, `frame-meta-${suffix}.json`);
      fs.writeFileSync(metadata + '.tmp', JSON.stringify({
        sequence, at: new Date().toISOString(), sessionId: 20, file: `frame-${suffix}.png`,
      }));
      fs.renameSync(metadata + '.tmp', metadata);
      readers.push(fs.openSync(metadata, 'r'));
      assert.equal(readFrame(workspace, destination, observed).sequence, sequence);
    }
    assert.equal(observed.sequences.size, 5);
  } finally { for (const reader of readers) fs.closeSync(reader); }
});

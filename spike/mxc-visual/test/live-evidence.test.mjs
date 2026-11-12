import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { readFrame, summarizeStream, startLiveCollector, requireFreshLiveOutput, readProgress, readBatch, validateFrame } from '../live-evidence.mjs';
import { pngFixture as png } from './png-fixture.mjs';

const key = '0123456789abcdef0123456789abcdef';
const fixture = { variant: 'dark-large', theme: 'Dark', actualWindowDpi: 96, rasterizationScale: 1,
  fixtureLayoutScale: 1.5, dpiWasChanged: false, scaleIsSimulation: true };
const frameData = sequence => ({ sequence, elapsedMs: sequence * 1000, captureMs: 0,
  at: new Date(Date.parse('2026-11-10T00:00:00Z') + sequence * 1000).toISOString(),
  sessionId: 20, pid: 4321, width: 1, height: 1, actualWindowDpi: 96, file: `frame-${String(sequence).padStart(3, '0')}.png` });
const stream = () => ({ targetFps: 1, frames: Array.from({ length: 30 }, (_, sequence) => frameData(sequence)) });
test('cadence is measured and fixture scaling is not presented as monitor DPI', () => {
  const result = summarizeStream(stream(), fixture, 'dark-large');
  assert.equal(result.measuredFps, 1);
  assert.equal(result.cadenceMet, true);
  assert.equal(result.actualWindowDpi, 96);
  assert.equal(result.scaleIsSimulation, true);
});
test('Calculator verdict requires every scenario-specific UIA result without inventing measured theme or scale', () => {
  const settings = { variant: 'calculator-16', workload: 'calculator', application: 'Microsoft.WindowsCalculator',
    theme: 'System (unmeasured)', actualWindowDpi: 96, rasterizationScale: null,
    fixtureLayoutScale: 1, dpiWasChanged: false, scaleIsSimulation: false };
  const data = { ...stream(), workload: 'calculator',
    automationChecks: [49, 108, 100].map(value => ({ expected: value, actual: value, passed: true })) };
  assert.equal(summarizeStream(data, settings, settings.variant).automationChecks.length, 3);
  data.automationChecks[1].actual = 99;
  assert.throws(() => summarizeStream(data, settings, settings.variant), /arithmetic assertions/);
  data.automationChecks[1].actual = 108;
  data.automationChecks.pop();
  assert.throws(() => summarizeStream(data, settings, settings.variant), /arithmetic assertions/);
  assert.throws(() => summarizeStream(data, { ...settings, theme: 'Dark' }, settings.variant), /settings/);
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
  const duplicate = stream(); duplicate.frames[2] = { ...duplicate.frames[1] };
  assert.throws(() => summarizeStream(duplicate, fixture, 'dark-large'), /30 distinct/);
});
test('live collector binds frame to session and rejects traversal', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-live-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`), destination = path.join(root, 'copy');
  fs.mkdirSync(source, { recursive: true });
  const bytes = png(0);
  fs.writeFileSync(path.join(source, 'frame-000.png'), bytes);
  const frame = frameData(0);
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
      const bytes = png(sequence);
      fs.writeFileSync(path.join(source, `frame-${suffix}.png`), bytes);
      const metadata = path.join(source, `frame-meta-${suffix}.json`);
      fs.writeFileSync(metadata + '.tmp', JSON.stringify(frameData(sequence)));
      fs.renameSync(metadata + '.tmp', metadata);
      readers.push(fs.openSync(metadata, 'r'));
      assert.equal(readFrame(workspace, destination, observed).sequence, sequence);
    }
    assert.equal(observed.sequences.size, 5);
  } finally { for (const reader of readers) fs.closeSync(reader); }
});
test('a delayed collector drains every published frame without rewriting capture timestamps', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-live-backlog-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`), destination = path.join(root, 'copy');
  fs.mkdirSync(source, { recursive: true });
  const observed = { sequences: new Map() };
  const publish = sequence => {
    const frame = frameData(sequence);
    fs.writeFileSync(path.join(source, frame.file), png(sequence));
    fs.writeFileSync(path.join(source, `frame-meta-${String(sequence).padStart(3, '0')}.json`), JSON.stringify(frame));
  };
  publish(0);
  const firstReceived = Date.parse(frameData(0).at) + 100;
  readFrame(workspace, destination, observed, firstReceived);
  for (let sequence = 1; sequence < 30; sequence++) publish(sequence);
  const delayedNow = Date.parse(frameData(29).at) + 8000;
  const latest = readFrame(workspace, destination, observed, delayedNow);
  assert.equal(observed.sequences.size, 30, 'polling gaps must not discard immutable frames');
  assert.equal(fs.readdirSync(destination).filter(name => name.endsWith('.png')).length, 30);
  assert.equal(latest.sequence, 29);
  assert.equal(latest.at, frameData(29).at, 'a late collection must not make an old capture look fresh');
  assert.equal(observed.sequences.get(0).receivedAt, new Date(firstReceived).toISOString());
  assert.equal(observed.sequences.get(15).receivedAt, new Date(delayedNow).toISOString());
});
test('backlog collection cannot skip corrupt or wrong-session intermediate evidence', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-live-backlog-invalid-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`);
  fs.mkdirSync(source, { recursive: true });
  for (let sequence = 0; sequence < 3; sequence++) {
    fs.writeFileSync(path.join(source, frameData(sequence).file), png(sequence));
    fs.writeFileSync(path.join(source, `frame-meta-00${sequence}.json`), JSON.stringify(frameData(sequence)));
  }
  const middle = path.join(source, 'frame-001.png');
  fs.writeFileSync(middle, Buffer.alloc(100));
  assert.throws(() => readFrame(workspace, path.join(root, 'corrupt'), { sequences: new Map() }), /PNG/);
  fs.writeFileSync(middle, png(1));
  const metadata = path.join(source, 'frame-meta-001.json');
  fs.writeFileSync(metadata, JSON.stringify({ ...frameData(1), sessionId: 99 }));
  assert.throws(() => readFrame(workspace, path.join(root, 'identity'), { sequences: new Map() }), /identity changed/);
  fs.writeFileSync(metadata, JSON.stringify({ ...frameData(1), error: 'Capture failed before the latest frame' }));
  assert.throws(() => readFrame(workspace, path.join(root, 'capture-error'), { sequences: new Map() }), /Capture failed/);
});
test('viewer reads immutable progress and final batch state without replacing open files', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-progress-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'shared'), source = path.join(workspace, `broker-${key}`), destination = path.join(root, 'out');
  fs.mkdirSync(source, { recursive: true });
  const collector = startLiveCollector(workspace, destination, { id: key, variant: 'dark' });
  const reader = fs.openSync(path.join(destination, 'progress-00000.json'), 'r');
  try {
    for (let sequence = 0; sequence < 3; sequence++) {
      const suffix = String(sequence).padStart(3, '0');
      const bytes = png(sequence);
      fs.writeFileSync(path.join(source, `frame-${suffix}.png`), bytes);
      fs.writeFileSync(path.join(source, `frame-meta-${suffix}.json`), JSON.stringify(frameData(sequence)));
      await new Promise(resolve => setTimeout(resolve, 300));
      assert.equal(readProgress(destination).frame.sequence, sequence);
    }
    assert.equal(collector.finish().observedFrames, 3);
    assert.equal(readProgress(destination).state, 'complete');
  } finally { fs.closeSync(reader); }
  fs.writeFileSync(path.join(root, 'batch.json'), '{"state":"prepared"}');
  fs.writeFileSync(path.join(root, 'batch-started.json'), '{"state":"starting"}');
  assert.equal(readBatch(root).state, 'starting');
  fs.writeFileSync(path.join(root, 'batch-finished.json'), '{"state":"passed"}');
  assert.equal(readBatch(root).state, 'passed');
});
test('malformed clocks, identities, dimensions and misleading scale claims fail closed', () => {
  for (const fields of [{ pid: 0 }, { sessionId: 1 }, { sequence: 30 }, { elapsedMs: NaN }, { captureMs: -1 },
    { width: 0 }, { width: 16000001 }, { at: 'not-a-date' }, { file: '../../host.png' }])
    assert.throws(() => validateFrame({ ...frameData(0), ...fields }));
  for (const field of ['actualWindowDpi', 'rasterizationScale']) {
    const invalid = { ...fixture }; delete invalid[field];
    assert.throws(() => summarizeStream(stream(), invalid, 'dark-large'), /settings/);
  }
  assert.throws(() => summarizeStream(stream(), { ...fixture, scaleIsSimulation: false }, 'dark-large'), /settings/);
  const mixed = stream(); mixed.frames[8].pid++;
  assert.throws(() => summarizeStream(mixed, fixture, 'dark-large'), /mixes/);
  const unordered = stream(); [unordered.frames[0], unordered.frames[1]] = [unordered.frames[1], unordered.frames[0]];
  assert.throws(() => summarizeStream(unordered, fixture, 'dark-large'), /distinct/);
});
test('capture phase timings are optional for historical streams but validated when supplied', () => {
  const data = stream();
  assert.equal(summarizeStream(data, fixture, 'dark-large').phaseTimings, null);
  data.schedule = 'skip-overdue-slots';
  for (const frame of data.frames) {
    frame.elapsedMs += 30;
    Object.assign(frame, { captureMs: 30, renderMs: 15, publicationMs: 10, scheduleDelayMs: 4 });
  }
  const result = summarizeStream(data, fixture, 'dark-large');
  assert.equal(result.captureSchedule, 'skip-overdue-slots');
  assert.deepEqual(result.phaseTimings, { maximumRenderMs: 15, maximumPublicationMs: 10, maximumScheduleDelayMs: 4 });
  data.frames[5].publicationMs = 20;
  assert.throws(() => summarizeStream(data, fixture, 'dark-large'), /phase timing/);
});

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { prepareReplay } from '../replay-evidence.mjs';
import { validatePng } from '../png-evidence.mjs';
import { pngFixture } from './png-fixture.mjs';
import { crc32, deflateSync } from 'node:zlib';

function sources(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-replay-fixture-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const inputs = ['light', 'dark', 'dark-large'].map((variant, index) => {
    const source = path.join(root, variant), visual = path.join(source, 'visual');
    fs.mkdirSync(visual, { recursive: true });
    const sessionId = 10 + index, pid = 123 + index;
    fs.writeFileSync(path.join(visual, 'worker.json'), JSON.stringify({ sessionId, appPid: pid, user: `A${index}`,
      stream: { ExitCode: 0 }, uia: { ExitCode: 0, Stdout: JSON.stringify({ after: ['Output=Hello, FullSession!', 'Counter=Count: 2'] }) } }));
    fs.writeFileSync(path.join(visual, 'authorization.json'), JSON.stringify({ sessionId, nonOwnerPipeAccessDenied: true }));
    fs.writeFileSync(path.join(visual, 'fixture-settings.json'), JSON.stringify({
      variant, theme: index === 0 ? 'Light' : 'Dark', actualWindowDpi: 96, rasterizationScale: 1,
      fixtureLayoutScale: index === 2 ? 1.5 : 1, scaleIsSimulation: index === 2, dpiWasChanged: false,
    }));
    fs.writeFileSync(path.join(visual, '1-before-printwindow-2.png'), pngFixture(0));
    fs.writeFileSync(path.join(visual, '2-after-printwindow-2.png'), pngFixture(1));
    const frames = Array.from({ length: 30 }, (_, sequence) => {
      const file = `frame-${String(sequence).padStart(3, '0')}.png`;
      fs.writeFileSync(path.join(visual, file), pngFixture(sequence));
      return { sequence, at: new Date(Date.parse('2026-11-10T00:00:00Z') + index * 200 + sequence * 1000).toISOString(),
        elapsedMs: sequence * 1000 + 1, captureMs: 1, sessionId, pid, width: 1, height: 1, actualWindowDpi: 96, file };
    });
    fs.writeFileSync(path.join(visual, 'stream.json'), JSON.stringify({ targetFps: 1, frames }));
    return source;
  });
  return { root, inputs };
}
test('offline replay copies 90 valid PNGs and never upgrades the failed source verdict', t => {
  const { root, inputs } = sources(t);
  const report = prepareReplay(path.join(root, 'replay'), inputs);
  assert.equal(report.invalidImageCount, 0);
  assert.equal(report.overlapMs, 28600);
  assert.equal(report.liveSessionsCreated, 0);
  assert.equal(report.lifecycleSafe, false);
  assert.equal(report.sourceOutcome, 'host-bugcheck');
  assert.equal(report.runs[0].replayFrames[0].at, '2026-11-10T00:00:00.000Z');
  assert.throws(() => prepareReplay(path.join(root, 'replay'), inputs), /new output/);
});
test('corrupt preserved image is explicitly excluded, hashed and never repaired in place', t => {
  const { root, inputs } = sources(t);
  const corrupt = path.join(inputs[0], 'visual', 'frame-029.png');
  fs.writeFileSync(corrupt, Buffer.alloc(80));
  const report = prepareReplay(path.join(root, 'replay'), inputs);
  assert.equal(report.invalidImageCount, 1);
  assert.equal(report.runs[0].replayFrames.length, 29);
  assert.equal(report.runs[0].invalidFrames[0].sequence, 29);
  assert.equal(report.runs[0].invalidFrames[0].sha256.length, 64);
  assert.equal(fs.existsSync(path.join(root, 'replay', 'light', 'frame-029.png')), false);
  assert(fs.readFileSync(corrupt).equals(Buffer.alloc(80)));
});
test('historical crash replay retains the legacy strict timing policy', t => {
  const { root, inputs } = sources(t);
  const file = path.join(inputs[0], 'visual', 'stream.json');
  const stream = JSON.parse(fs.readFileSync(file, 'utf8'));
  for (const frame of stream.frames.slice(8)) {
    frame.elapsedMs += 3000;
    frame.at = new Date(Date.parse(frame.at) + 3000).toISOString();
  }
  fs.writeFileSync(file, JSON.stringify(stream));
  const report = prepareReplay(path.join(root, 'replay'), inputs);
  assert.equal(report.runs[0].stream.capturePolicy.maxFrameGapMs, 2000);
  assert.equal(report.runs[0].stream.cadenceMet, false);
  assert.equal(report.captureCadenceKeepBarMet, false);
  assert.equal(report.sourceOutcome, 'host-bugcheck');
});
test('PNG integrity rejects torn chunks, modified data and missing endings', () => {
  const bytes = pngFixture(8);
  validatePng(bytes, { width: 1, height: 1 });
  assert.throws(() => validatePng(bytes, { width: 2, height: 1 }), /dimensions/);
  assert.throws(() => validatePng(bytes.subarray(0, -4), { width: 1, height: 1 }), /ending|Truncated/);
  const altered = Buffer.from(bytes); altered[altered.length - 16] ^= 0x80;
  assert.throws(() => validatePng(altered, { width: 1, height: 1 }), /checksum/);
});
test('CRC-valid but undecodable pixels and decompression overflow are rejected', () => {
  const replaceData = data => {
    const original = pngFixture(8);
    const chunk = Buffer.alloc(data.length + 12);
    chunk.writeUInt32BE(data.length); chunk.write('IDAT', 4); data.copy(chunk, 8);
    chunk.writeUInt32BE(crc32(chunk.subarray(4, -4)), chunk.length - 4);
    return Buffer.concat([original.subarray(0, 33), chunk, original.subarray(-12)]);
  };
  for (const data of [Buffer.alloc(0), Buffer.from('not deflate'), deflateSync(Buffer.alloc(1000))])
    assert.throws(() => validatePng(replaceData(data), { width: 1, height: 1 }), /PNG image data|scanline length/);
  assert.throws(() => validatePng(replaceData(deflateSync(Buffer.from([9, 0, 0, 0, 255]))),
    { width: 1, height: 1 }), /filter/);
});
test('high-bit chunk types cannot alias valid ASCII chunk names even with a correct CRC', () => {
  const bytes = pngFixture(8);
  const offset = 33; // First chunk following the fixed IHDR.
  const length = bytes.readUInt32BE(offset);
  bytes[offset + 4] |= 0x80;
  bytes.writeUInt32BE(crc32(bytes.subarray(offset + 4, offset + 8 + length)), offset + 8 + length);
  assert.throws(() => validatePng(bytes, { width: 1, height: 1 }), /checksum/);
});
test('replay refuses dot-prefixed descendants of a preserved source', t => {
  const { inputs } = sources(t);
  for (const name of ['..replay', '..output', 'output'])
    assert.throws(() => prepareReplay(path.join(inputs[0], name), inputs), /must not modify preserved sources/);
  assert.equal(fs.readdirSync(inputs[0]).length, 1);
});

import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { verifyStreamEvidence } from '../stream-evidence.mjs';
import { pngFixture } from './png-fixture.mjs';

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'stream-evidence-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const settings = { variant: 'light', theme: 'Light', actualWindowDpi: 96, rasterizationScale: 1,
    fixtureLayoutScale: 1, dpiWasChanged: false, scaleIsSimulation: false };
  const stream = { targetFps: 1, frames: Array.from({ length: 30 }, (_, sequence) => ({
    sequence, sessionId: 2, pid: 123, at: new Date(1700000000000 + sequence * 1000).toISOString(),
    elapsedMs: sequence * 1000, captureMs: 0, actualWindowDpi: 96, width: 1, height: 1,
    file: `frame-${String(sequence).padStart(3, '0')}.png`,
  })) };
  for (const frame of stream.frames) fs.writeFileSync(path.join(directory, frame.file), pngFixture(frame.sequence));
  return { directory, stream, settings, check: () => verifyStreamEvidence(directory, stream, settings, 'light', 2) };
}
test('a stream verdict validates all thirty actual PNGs, not only reported capture success', t => {
  const { check } = fixture(t);
  const result = check();
  assert.equal(result.validImages, 30);
  assert.equal(result.distinctImageHashes, 30);
  assert.equal(result.cadenceMet, true);
});
test('missing or zero-filled earlier, middle and final images cannot pass on metadata alone', t => {
  const { check, directory, stream } = fixture(t);
  for (const index of [0, 15, 29]) {
    const file = path.join(directory, stream.frames[index].file);
    fs.unlinkSync(file);
    assert.throws(check, /ENOENT/);
    fs.writeFileSync(file, Buffer.alloc(100));
    assert.throws(check, /PNG/);
    fs.writeFileSync(file, pngFixture(index));
  }
  assert.equal(check().validImages, 30);
});
test('stream verification binds expected session and rejects traversal before reading an image', t => {
  const { directory, stream, settings, check } = fixture(t);
  assert.throws(() => verifyStreamEvidence(directory, stream, settings, 'light', 3), /expected session/);
  stream.frames[0].file = '../host.png';
  assert.throws(check, /frame file/);
});
test('stream evidence directory cannot be redirected through a junction', t => {
  const { directory, stream, settings } = fixture(t);
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'stream-evidence-link-'));
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }));
  const link = path.join(parent, 'redirected');
  fs.symlinkSync(directory, link, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => verifyStreamEvidence(link, stream, settings, 'light', 2), /directory/);
});

import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { capturePolicy, policyArguments, LEGACY_CAPTURE_POLICY } from '../capture-policy.mjs';
import { summarizeStream } from '../live-evidence.mjs';

const settings = { variant: 'light', theme: 'Light', actualWindowDpi: 96, rasterizationScale: 1,
  fixtureLayoutScale: 1, dpiWasChanged: false, scaleIsSimulation: false };
function streamWithStall(extraMs = 3000) {
  return { targetFps: 1, frames: Array.from({ length: 30 }, (_, sequence) => {
    const elapsedMs = sequence * 1000 + (sequence >= 8 ? extraMs : 0);
    return { sequence, sessionId: 2, pid: 123, at: new Date(1700000000000 + elapsedMs).toISOString(),
      elapsedMs, captureMs: 0, actualWindowDpi: 96, width: 1, height: 1,
      file: `frame-${String(sequence).padStart(3, '0')}.png` };
  }) };
}
test('default policy reports isolated stalls while explicit strict limits still fail', () => {
  const stream = streamWithStall(), before = JSON.stringify(stream);
  const flexible = summarizeStream(stream, settings, 'light');
  assert.equal(flexible.maximumIntervalMs, 4000);
  assert.equal(flexible.cadenceMet, true);
  assert.match(flexible.timingWarnings[0], /4000ms.*No hard maximum/);
  assert.equal(flexible.capturePolicy.maxFrameGapMs, null);
  const strict = summarizeStream(stream, settings, 'light', LEGACY_CAPTURE_POLICY);
  assert.equal(strict.cadenceMet, false);
  assert.match(strict.cadenceIssues[0], /selected 2000ms limit/);
  assert.equal(summarizeStream(stream, settings, 'light', { maxFrameGapMs: 5000 }).cadenceMet, true);
  assert.equal(JSON.stringify(stream), before, 'policy evaluation must not change recorded timestamps or results');
});
test('average-rate failures remain explicit and only a selected policy can relax them', () => {
  const stream = streamWithStall(10000);
  const normal = summarizeStream(stream, settings, 'light');
  assert.equal(normal.cadenceMet, false);
  assert.match(normal.cadenceIssues[0], /Average .* below/);
  const observational = summarizeStream(stream, settings, 'light', { minAverageFps: null });
  assert.equal(observational.cadenceMet, true);
  assert.ok(observational.timingWarnings.length > 0);
  stream.frames.pop();
  assert.throws(() => summarizeStream(stream, settings, 'light', { minAverageFps: null, maxFrameGapMs: null }), /30 distinct/);
});
test('invalid policies fail rather than silently reverting to defaults', () => {
  for (const value of [null, [], 'strict', { schema: 2 }, { maxFrameGapMs: 0 }, { maxFrameGapMs: '5000' },
    { maxFrameGapMs: NaN }, { minAverageFps: 0 }, { minAverageFps: Infinity }, { minAverageFps: 2 },
    { minOverlapMs: -1 }, { minOverlapMs: 0.5 }, { unknownOption: true }])
    assert.throws(() => capturePolicy(value), /Invalid capture policy/);
});
test('prepare persists the chosen policy and execution cannot replace it through CLI options', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-policy-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const policyFile = path.join(root, 'policy.json'), batch = path.join(root, 'batch');
  fs.writeFileSync(policyFile, JSON.stringify({ maxFrameGapMs: 4500, minAverageFps: 0.8, minOverlapMs: 15000 }));
  const parsed = policyArguments(['--prepare', batch, '--policy', policyFile]);
  assert.deepEqual(parsed.args, ['--prepare', batch]);
  assert.equal(parsed.policy.maxFrameGapMs, 4500);
  assert.throws(() => policyArguments(['--policy']), /exactly one/);
  assert.throws(() => policyArguments(['--policy', policyFile, '--policy', policyFile]), /exactly one/);
  const runner = fileURLToPath(new URL('../variant-run.mjs', import.meta.url));
  const prepared = spawnSync(process.execPath, [runner, '--prepare', batch, '--policy', policyFile], { encoding: 'utf8' });
  assert.equal(prepared.status, 0, prepared.stderr);
  const saved = JSON.parse(fs.readFileSync(path.join(batch, 'batch.json')));
  assert.deepEqual(saved.capturePolicy, parsed.policy);
  const refused = spawnSync(process.execPath, [runner, batch, '--policy', policyFile], { encoding: 'utf8' });
  assert.notEqual(refused.status, 0);
  assert.match(refused.stderr, /uses its saved policy/);
  assert.equal(fs.existsSync(path.join(batch, 'batch-started.json')), false);
  saved.capturePolicy = null;
  fs.writeFileSync(path.join(batch, 'batch.json'), JSON.stringify(saved));
  const malformed = spawnSync(process.execPath, [runner, batch], { encoding: 'utf8' });
  assert.notEqual(malformed.status, 0);
  assert.match(malformed.stderr, /Invalid capture policy/);
  assert.equal(fs.existsSync(path.join(batch, 'batch-started.json')), false, 'a malformed saved policy fails before broker work');
});

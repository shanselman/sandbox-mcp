import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { batchVariants, CALCULATOR_VARIANTS, SAMPLE_CALCULATOR_VARIANTS, FIXTURE_VARIANTS, sampleAppearance } from '../visual-workloads.mjs';
import { startCameraServer } from '../camera-server.mjs';
import { pngFixture } from './png-fixture.mjs';

test('fixed Calculator grids are bounded and cannot repeat identities or escape allowlisted workloads', () => {
  const batch = variants => ({ runs: variants.map(variant => ({ variant })) });
  for (const count of [1, 3, 4, 8, 16])
    assert.equal(batchVariants(batch(CALCULATOR_VARIANTS.slice(0, count))).length, count);
  assert.equal(batchVariants(batch(SAMPLE_CALCULATOR_VARIANTS)).length, 16);
  assert.deepEqual(batchVariants(batch(FIXTURE_VARIANTS)), FIXTURE_VARIANTS);
  for (const variants of [[], [...CALCULATOR_VARIANTS, 'calculator-17'], ['calculator-00'], ['calculator-1'],
    ['calculator-01', 'calculator-01'], ['calculator-01', 'light'], ['..\\calc.exe'], ['sample-calculator-17']])
    assert.throws(() => batchVariants(batch(variants)), /Expected/);
});
test('sixteen sample tiles balance light/dark across four explicitly simulated layout sizes', () => {
  const appearances = SAMPLE_CALCULATOR_VARIANTS.map(sampleAppearance);
  for (const theme of ['Light', 'Dark']) for (const scale of [1, 1.25, 1.5, 1.75])
    assert.equal(appearances.filter(item => item.theme === theme && item.fixtureLayoutScale === scale
      && item.scaleIsSimulation === (scale !== 1)).length, 2);
  assert.throws(() => sampleAppearance('calculator-01'), /sample-calculator/);
});

test('camera routes show sixteen separate Calculator tiles and only serve allowlisted frame paths', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'calculator-camera-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.writeFileSync(path.join(root, 'batch.json'), JSON.stringify({ schema: 1, mode: 'concurrent', state: 'prepared',
    runs: CALCULATOR_VARIANTS.map(variant => ({ variant, state: 'pending' })) }));
  fs.mkdirSync(path.join(root, 'calculator-16'));
  fs.writeFileSync(path.join(root, 'calculator-16', 'frame-000.png'), pngFixture());
  const server = await startCameraServer(root);
  t.after(() => server.close());
  assert.equal(server.state().runs.length, 16);
  assert.equal((await fetch(new URL('calculator-16/frame-000.png', server.url))).status, 200);
  assert.equal((await fetch(new URL('calculator-17/frame-000.png', server.url))).status, 404);
  const readback = { variant: 'calculator-16', currentTime: 1, duration: 30, loops: 1, readyState: 4, paused: false, error: '' };
  assert.equal((await fetch(new URL('playback', server.url), { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify([readback]) })).status, 200);
});

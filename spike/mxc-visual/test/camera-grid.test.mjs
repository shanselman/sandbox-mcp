import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
const here = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('viewer is loopback/capability scoped and distinguishes live, stale and recorded', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-grid-test-'));
  fs.writeFileSync(path.join(root, 'batch.json'), JSON.stringify({ schema: 1, mode: 'sequential', runs: [
    { variant: 'light', state: 'starting' }, { variant: 'dark', state: 'starting' }, { variant: 'dark-large', state: 'passed' },
  ] }));
  for (const [variant, age] of [['light', 0], ['dark', 60000], ['dark-large', 0]]) {
    fs.mkdirSync(path.join(root, variant));
    fs.writeFileSync(path.join(root, variant, 'progress.json'), JSON.stringify({
      state: 'live', frame: { at: new Date(Date.now() - age).toISOString(), file: 'frame-000.png', sequence: 0, sessionId: 20 },
    }));
  }
  const child = spawn(process.execPath, [path.join(here, 'camera-grid.mjs'), root], { stdio: ['ignore', 'pipe', 'pipe'] });
  const exited = once(child, 'exit');
  t.after(async () => { child.kill(); await exited; fs.rmSync(root, { recursive: true, force: true }); });
  let text = '';
  const started = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.stdout.on('data', data => { text += data; if (text.includes('\n')) resolve(JSON.parse(text)); });
    child.once('exit', code => reject(new Error(`Viewer exited before readiness: ${code}`)));
  });
  assert.equal(new URL(started.url).hostname, '127.0.0.1');
  const response = await fetch(started.url + 'state');
  const body = await response.json();
  assert.deepEqual(body.runs.map(run => run.state), ['live', 'stale', 'passed']);
  assert.equal((await fetch(new URL('/state', started.url))).status, 404);
  assert.equal((await fetch(started.url + 'state', { headers: { Origin: 'https://unrelated.invalid' } })).status, 403);
  assert.equal((await fetch(started.url, { method: 'POST' })).status, 403);
  assert.equal((await fetch(started.url + 'job.json')).status, 404);
  assert.equal(response.headers.get('cache-control'), 'no-store');
});

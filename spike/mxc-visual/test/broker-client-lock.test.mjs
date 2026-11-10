import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const executable = path.join(root, 'broker', 'bin', 'Release', 'net10.0-windows', 'SandboxMcp.FullBroker.exe');
test('kernel-owned client coordination excludes a second caller and releases on EOF', async t => {
  const first = spawn(executable, ['client-lock'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = once(first, 'exit');
  t.after(async () => { if (first.exitCode === null) first.stdin.end(); await exited; });
  let text = '';
  await new Promise((resolve, reject) => {
    first.once('error', reject);
    first.stdout.on('data', chunk => { text += chunk; if (text.includes('\n')) resolve(); });
    first.once('exit', code => { if (!text.includes('\n')) reject(new Error(`Lock holder exited ${code}`)); });
  });
  assert.equal(JSON.parse(text).locked, true);
  const second = spawnSync(executable, ['client-lock'], { input: '', encoding: 'utf8', timeout: 5000 });
  assert.notEqual(second.status, 0);
  first.stdin.end();
  assert.equal((await exited)[0], 0);
  const third = spawnSync(executable, ['client-lock'], { input: '', encoding: 'utf8', timeout: 5000 });
  assert.equal(third.status, 0, third.stderr);
  assert.equal(JSON.parse(third.stdout).locked, true);
});

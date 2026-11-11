import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

if (process.platform !== 'win32') throw new Error('The package source smoke test requires Windows.');
const sourceArchive = process.argv[2];
const runtimeArchive = process.argv[3];
if (!sourceArchive || !runtimeArchive) {
  throw new Error('Usage: node test/package-source-smoke.js <relink-source.zip> <bun-package.zip>');
}

const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'sandbox-mcp relink source '));
const sourceRoot = path.join(temp, 'source');
const runtimeRoot = path.join(temp, 'runtime');
fs.mkdirSync(sourceRoot);
fs.mkdirSync(runtimeRoot);
const ps = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const expand = (archive, destination) => {
  const result = spawnSync(ps, [
    '-NoProfile', '-NonInteractive', '-Command',
    `Expand-Archive -LiteralPath '${path.resolve(archive).replaceAll("'", "''")}' -DestinationPath '${destination.replaceAll("'", "''")}' -Force`,
  ], { encoding: 'utf8' });
  assert.equal(result.status, 0, `Could not extract ${archive}: ${result.stderr || result.stdout}`);
};

try {
  expand(sourceArchive, sourceRoot);
  expand(runtimeArchive, runtimeRoot);

  for (const relative of [
    'src/cli.js',
    'src/core.js',
    'scripts/package-bun.ps1',
    'scripts/package-bun.md',
    'package.json',
    'package-lock.json',
    'LICENSES/LGPL-2.0.txt',
    'LICENSES/LGPL-2.1.txt',
    'BUN-LICENSE.md',
    'RELINKING.md',
    'SOURCE-INFO.txt',
    'SOURCE-MANIFEST.sha256',
  ]) {
    assert.ok(fs.existsSync(path.join(sourceRoot, relative)), `Relinking source is missing ${relative}.`);
  }

  const info = fs.readFileSync(path.join(sourceRoot, 'SOURCE-INFO.txt'), 'utf8');
  const sourceCommit = info.match(/^Application source commit: ([a-f0-9]{40})$/m)?.[1];
  const bunCommit = info.match(/^Bun source commit: ([a-f0-9]{40})$/m)?.[1];
  const webkitCommit = info.match(/^WebKit source commit: ([a-f0-9]{40})$/m)?.[1];
  assert.ok(sourceCommit, 'Application source commit is missing.');
  assert.ok(bunCommit, 'Bun source commit is missing.');
  assert.ok(webkitCommit, 'WebKit source commit is missing.');

  const instructions = fs.readFileSync(path.join(sourceRoot, 'RELINKING.md'), 'utf8');
  for (const commit of [sourceCommit, bunCommit, webkitCommit]) {
    assert.ok(instructions.includes(commit), `Relinking instructions omit source commit ${commit}.`);
  }
  assert.match(fs.readFileSync(path.join(sourceRoot, 'LICENSES/LGPL-2.0.txt'), 'utf8'),
    /GNU LIBRARY GENERAL PUBLIC LICENSE/);
  assert.match(fs.readFileSync(path.join(sourceRoot, 'LICENSES/LGPL-2.1.txt'), 'utf8'),
    /GNU LESSER GENERAL PUBLIC LICENSE/);

  const manifest = fs.readFileSync(path.join(sourceRoot, 'SOURCE-MANIFEST.sha256'), 'utf8').trim().split(/\r?\n/);
  assert.ok(manifest.length > 0, 'Source manifest is empty.');
  const listedFiles = new Set();
  for (const line of manifest) {
    const match = line.match(/^([a-f0-9]{64})  (.+)$/);
    assert.ok(match, `Invalid source manifest entry: ${line}`);
    assert.ok(!listedFiles.has(match[2]), `Duplicate source manifest entry: ${match[2]}`);
    listedFiles.add(match[2]);
    const file = path.resolve(sourceRoot, ...match[2].split('/'));
    assert.ok(file.startsWith(sourceRoot + path.sep), `Source manifest path escapes the archive: ${match[2]}`);
    assert.ok(fs.existsSync(file), `Source manifest file is missing: ${match[2]}`);
    assert.equal(createHash('sha256').update(fs.readFileSync(file)).digest('hex'), match[1],
      `Source manifest hash differs for ${match[2]}.`);
  }
  const archiveFiles = fs.readdirSync(sourceRoot, { recursive: true })
    .filter((relative) => fs.statSync(path.join(sourceRoot, relative)).isFile())
    .map((relative) => relative.replaceAll('\\', '/'))
    .filter((relative) => relative !== 'SOURCE-MANIFEST.sha256')
    .sort();
  assert.deepEqual([...listedFiles].sort(), archiveFiles, 'Source manifest does not cover every archive file.');

  const buildInfo = fs.readFileSync(path.join(runtimeRoot, 'BUILD-INFO.txt'), 'utf8');
  for (const line of [
    `Source commit: ${sourceCommit}`,
    `Bun source commit: ${bunCommit}`,
    `WebKit source commit: ${webkitCommit}`,
  ]) {
    assert.ok(buildInfo.includes(line), `Runtime package and relinking source disagree on ${line}.`);
  }
  assert.ok(fs.existsSync(path.join(runtimeRoot, 'RELINKING.md')), 'Runtime package has no relinking instructions.');
  for (const license of ['LGPL-2.0.txt', 'LGPL-2.1.txt']) {
    const runtimeLicense = path.join(runtimeRoot, 'LICENSES', license);
    assert.ok(fs.existsSync(runtimeLicense), `Runtime package is missing ${license}.`);
    assert.deepEqual(fs.readFileSync(runtimeLicense), fs.readFileSync(path.join(sourceRoot, 'LICENSES', license)),
      `Runtime and source packages disagree on ${license}.`);
  }
  console.log(`Relinking source smoke test passed (${manifest.length} files, matching app/Bun/WebKit revisions and full LGPL texts).`);
} finally {
  fs.rmSync(temp, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}

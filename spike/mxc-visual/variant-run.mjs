import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { atomicJson } from './broker-client-state.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const prepare = process.argv[2] === '--prepare';
const root = path.resolve(prepare ? process.argv[3] : process.argv[2] ?? '');
if ((!prepare && process.argv.length !== 3) || (prepare && process.argv.length !== 4)) throw new Error('Expected [--prepare] <new-batch-directory>');
const batchFile = path.join(root, 'batch.json');
if (prepare) {
  if (fs.existsSync(root)) throw new Error('Use a new batch directory');
  fs.mkdirSync(root, { recursive: true });
  atomicJson(batchFile, { schema: 1, startedAt: new Date().toISOString(), mode: 'concurrent', targetFps: 1,
    runs: ['light', 'dark', 'dark-large'].map(variant => ({ variant, state: 'pending' })) });
  console.log(JSON.stringify({ root, prepared: true }));
} else {
  const batch = JSON.parse(fs.readFileSync(batchFile, 'utf8'));
  if (batch.schema !== 1 || batch.mode !== 'concurrent' || batch.runs.some(run => run.state !== 'pending'))
    throw new Error('Batch is not fresh; do not silently repeat completed/provisioned work');
  let failed = false;
  for (const run of batch.runs) {
    run.state = 'starting';
    run.startedAt = new Date().toISOString();
  }
  atomicJson(batchFile, batch);
  const child = spawn(process.execPath, [path.join(here, 'broker-client.mjs'), 'run-grid', root],
    { stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '', stderr = '', outputExceeded = false;
  child.stdout.on('data', data => { if (stdout.length + data.length > 1024 * 1024) outputExceeded = true; else stdout += data; });
  child.stderr.on('data', data => { stderr = (stderr + data).slice(-4096); });
  try {
    const [code] = await once(child, 'exit');
    if (outputExceeded) throw new Error('Client output exceeded limit');
    let result;
    try { result = JSON.parse(stdout); } catch { throw new Error(stderr || 'Client did not return JSON evidence'); }
    atomicJson(path.join(root, 'grid-result.json'), result);
    if (code !== 0 || result.ok !== true || result.runs?.length !== 3 || result.overlapMs < 20000)
      throw new Error(result.error ?? stderr ?? 'Grid assertions failed');
    batch.overlapMs = result.overlapMs;
    for (const run of batch.runs) {
      const observed = result.runs.find(item => item.variant === run.variant);
      if (!observed?.visual?.automationAndCaptureAssertionsPassed || !observed.stream?.cadenceMet || !observed.cleanup?.accountRemoved)
        throw new Error('Visual/cadence/cleanup assertions failed');
      Object.assign(run, {
        state: 'passed', sessionId: observed.response.report.sessionId, requestId: observed.response.report.id,
        createMs: observed.response.report.createMs, evidence: observed.cleanup.evidence, stream: observed.stream,
        observedFrames: observed.observedFrames, cleanupConfirmed: true, firstFrameAt: observed.firstFrameAt,
        lastFrameAt: observed.lastFrameAt, completedAt: new Date().toISOString(),
      });
      const source = path.join(observed.cleanup.evidence, 'visual');
      for (const name of ['1-before-printwindow-2.png', '2-after-printwindow-2.png', 'fixture-settings.json', 'stream.json', 'authorization.json'])
        fs.copyFileSync(path.join(source, name), path.join(root, run.variant, name), fs.constants.COPYFILE_EXCL);
    }
  } catch (error) {
    failed = true;
    for (const run of batch.runs) if (run.state !== 'passed') { run.state = 'failed'; run.error = error.message; }
  }
  batch.completedAt = new Date().toISOString();
  batch.state = failed ? 'failed' : 'passed';
  atomicJson(batchFile, batch);
  atomicJson(path.join(root, 'report.json'), batch);
  const lines = [
    '# Full-session visual variants', '',
    `Outcome: **${batch.state.toUpperCase()}**. Three coordinated Full sessions; measured common frame interval: ${batch.overlapMs ?? 0} ms.`,
    'MXC owns account/workspace lifecycle. The installed owner-only broker calls experimental Windows Full-session APIs.',
    '', '| Variant | Session | Theme | Actual window DPI | Fixture scale | Frames | Measured fps | Max gap ms | Cleanup |',
    '|---|---:|---|---:|---:|---:|---:|---:|---|',
    ...batch.runs.map(run => `| ${run.variant} | ${run.sessionId ?? '-'} | ${run.stream?.theme ?? '-'} | ${run.stream?.actualWindowDpi ?? '-'} | ${run.stream?.fixtureLayoutScale ?? '-'} | ${run.stream?.frames ?? 0} | ${run.stream?.measuredFps.toFixed(3) ?? '-'} | ${run.stream?.maximumIntervalMs ?? '-'} | ${run.cleanupConfirmed ? 'confirmed' : 'not confirmed'} |`),
    '', 'The large variant uses a **1.5x fixture render transform**, not a changed monitor DPI. Actual DPI/rasterization values are measured.',
    'The target is 1fps; the keep bar is average >=0.9fps, no gap >2000ms, and all 30 frames captured per run.',
    'Capture/automation checks are not a substitute for inspecting the before/after pixels.',
    '',
    ...batch.runs.flatMap(run => run.state === 'passed' ? [
      `## ${run.variant}`, '',
      `![Before](${run.variant}/1-before-printwindow-2.png)`,
      `![After](${run.variant}/2-after-printwindow-2.png)`, '',
    ] : [`## ${run.variant}`, '', `${run.state}: ${run.error ?? ''}`, '']),
  ];
  fs.writeFileSync(path.join(root, 'report.md'), lines.join('\n'));
  console.log(JSON.stringify({ root, state: batch.state, runs: batch.runs }, null, 2));
  if (failed) process.exitCode = 1;
}

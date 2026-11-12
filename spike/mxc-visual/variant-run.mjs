import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { atomicJson } from './broker-client-state.mjs';
import { readBatch } from './live-evidence.mjs';
import { capturePolicy, policyArguments, describePolicy, LEGACY_CAPTURE_POLICY } from './capture-policy.mjs';
import { FIXTURE_VARIANTS, CALCULATOR_VARIANTS, batchVariants } from './visual-workloads.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const options = policyArguments(process.argv.slice(2));
const prepare = options.args[0] === '--prepare';
const calculatorCount = prepare && options.args[2] === '--calculator' ? Number(options.args[3]) : null;
const root = path.resolve(prepare ? options.args[1] ?? '' : options.args[0] ?? '');
if ((!prepare && (options.args.length !== 1 || options.explicit))
  || (prepare && !(options.args.length === 2 || options.args.length === 4 && Number.isInteger(calculatorCount) && calculatorCount >= 1 && calculatorCount <= 16)))
  throw new Error('Expected --prepare <new-batch-directory> [--calculator <1..16>] [--policy <JSON-file>], or <prepared-directory> (uses its saved policy)');
const batchFile = path.join(root, 'batch.json');
if (prepare) {
  if (fs.existsSync(root)) throw new Error('Use a new batch directory');
  fs.mkdirSync(root, { recursive: true });
  atomicJson(batchFile, { schema: 1, startedAt: new Date().toISOString(), mode: 'concurrent', targetFps: 1,
    capturePolicy: options.policy, runs: (calculatorCount ? CALCULATOR_VARIANTS.slice(0, calculatorCount) : FIXTURE_VARIANTS)
      .map(variant => ({ variant, ...(calculatorCount ? { name: `Calculator ${Number(variant.slice(-2))}` } : {}), state: 'pending' })) });
  console.log(JSON.stringify({ root, prepared: true }));
} else {
  const batch = readBatch(root);
  const variants = batchVariants(batch);
  if (batch.schema !== 1 || batch.mode !== 'concurrent' || batch.runs.some(run => run.state !== 'pending'))
    throw new Error('Batch is not fresh; do not silently repeat completed/provisioned work');
  const policy = capturePolicy(Object.hasOwn(batch, 'capturePolicy') ? batch.capturePolicy : LEGACY_CAPTURE_POLICY);
  batch.capturePolicy = policy;
  let failed = false;
  for (const run of batch.runs) {
    run.state = 'starting';
    run.startedAt = new Date().toISOString();
  }
  atomicJson(path.join(root, 'batch-started.json'), batch);
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
    if (JSON.stringify(result.capturePolicy) !== JSON.stringify(policy))
      throw new Error('Client timing policy differs from the prepared batch policy');
    batch.overlapMs = result.overlapMs;
    for (const run of batch.runs) {
      const observed = result.runs?.find(item => item.variant === run.variant);
      if (!observed) continue;
      Object.assign(run, {
        sessionId: observed.response.report.sessionId, requestId: observed.response.report.id,
        createMs: observed.response.report.createMs, evidence: observed.cleanup.evidence, stream: observed.stream,
        warnings: observed.stream?.timingWarnings ?? [], observedFrames: observed.observedFrames,
        cleanupConfirmed: observed.cleanup.accountRemoved === true, firstFrameAt: observed.firstFrameAt,
        lastFrameAt: observed.lastFrameAt, completedAt: new Date().toISOString(),
      });
      const source = path.join(observed.cleanup.evidence, 'visual');
      for (const name of ['1-before-printwindow-2.png', '2-after-printwindow-2.png', 'fixture-settings.json', 'stream.json', 'authorization.json'])
        fs.copyFileSync(path.join(source, name), path.join(root, run.variant, name), fs.constants.COPYFILE_EXCL);
    }
    if (code !== 0 || result.ok !== true || result.runs?.length !== variants.length || result.overlapMs < policy.minOverlapMs)
      throw new Error(result.error || stderr || 'Grid assertions failed');
    if (result.runs.some(run => !run.visual?.automationAndCaptureAssertionsPassed || !run.stream?.cadenceMet
      || run.stream?.validImages !== 30 || run.observedFrames !== 30 || !run.cleanup?.accountRemoved))
      throw new Error('Visual/cadence/collection/cleanup assertions failed');
    for (const run of batch.runs) run.state = 'passed';
  } catch (error) {
    failed = true;
    for (const run of batch.runs) if (run.state !== 'passed') { run.state = 'failed'; run.error = error.message; }
  }
  batch.completedAt = new Date().toISOString();
  batch.state = failed ? 'failed' : 'passed';
  atomicJson(path.join(root, 'batch-finished.json'), batch);
  atomicJson(path.join(root, 'report.json'), batch);
  const lines = [
    '# Full-session visual variants', '',
    `Outcome: **${batch.state.toUpperCase()}**. ${variants.length} coordinated Full sessions; measured common frame interval: ${batch.overlapMs ?? 0} ms.`,
    'MXC owns account/workspace lifecycle. The installed owner-only broker calls experimental Windows Full-session APIs.',
    '', '| Variant | Session | Theme | Actual window DPI | Fixture scale | Frames | Measured fps | Max gap ms | Cleanup |',
    '|---|---:|---|---:|---:|---:|---:|---:|---|',
    ...batch.runs.map(run => `| ${run.variant} | ${run.sessionId ?? '-'} | ${run.stream?.theme ?? '-'} | ${run.stream?.actualWindowDpi ?? '-'} | ${run.stream?.fixtureLayoutScale ?? '-'} | ${run.stream?.frames ?? 0} | ${run.stream?.measuredFps.toFixed(3) ?? '-'} | ${run.stream?.maximumIntervalMs ?? '-'} | ${run.cleanupConfirmed ? 'confirmed' : 'not confirmed'} |`),
    '', 'The large variant uses a **1.5x fixture render transform**, not a changed monitor DPI. Actual DPI/rasterization values are measured.',
    `The target is 1fps; selected timing policy: ${describePolicy(policy)} All 30 frames must be captured, validated and collected.`,
    ...batch.runs.flatMap(run => (run.warnings ?? []).map(warning => `Timing warning (${run.variant}): ${warning}`)),
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

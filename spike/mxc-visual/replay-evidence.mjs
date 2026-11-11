// Offline only: no MXC SDK, broker IPC, native session calls, or account operations.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { summarizeStream, validatePng } from './live-evidence.mjs';
import { atomicJson, verifyVisualEvidence } from './broker-client-state.mjs';
import { LEGACY_CAPTURE_POLICY } from './capture-policy.mjs';

function readJson(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 2 * 1024 * 1024) throw new Error('Invalid preserved JSON evidence');
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}
function noLinks(root) {
  for (let current = path.resolve(root); ; current = path.dirname(current)) {
    if (fs.lstatSync(current).isSymbolicLink()) throw new Error('Replay source/destination must not use a reparse point');
    if (path.dirname(current) === current) break;
  }
}
export function prepareReplay(destination, sources) {
  if (sources.length !== 3 || fs.existsSync(destination)) throw new Error('Expected three preserved sources and a new output directory');
  const runs = sources.map(source => {
    noLinks(source);
    const visual = path.join(source, 'visual');
    noLinks(visual);
    const worker = readJson(path.join(visual, 'worker.json'));
    const fixture = readJson(path.join(visual, 'fixture-settings.json'));
    const stream = readJson(path.join(visual, 'stream.json'));
    const metrics = summarizeStream(stream, fixture, fixture.variant, LEGACY_CAPTURE_POLICY);
    verifyVisualEvidence(visual, worker.sessionId, worker.user);
    if (worker.error || worker.appCleanupError || worker.stream?.ExitCode !== 0
      || stream.frames.some(frame => frame.sessionId !== worker.sessionId || frame.pid !== worker.appPid))
      throw new Error('Preserved worker/stream identity mismatch');
    const files = stream.frames.map(frame => {
      const input = path.join(visual, frame.file);
      const stat = fs.lstatSync(input);
      if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024) throw new Error('Invalid preserved image');
      const bytes = fs.readFileSync(input);
      let invalidReason;
      try { validatePng(bytes, frame); } catch (error) { invalidReason = error.message; }
      return { file: frame.file, sequence: frame.sequence, bytes, sha256: createHash('sha256').update(bytes).digest('hex'), invalidReason };
    });
    const distinctImages = new Set(files.filter(file => !file.invalidReason).map(file => file.sha256)).size;
    if (distinctImages < 2) throw new Error('Recorded stream does not demonstrate changing pixels');
    const invalidFrames = files.filter(file => file.invalidReason).map(({ bytes, ...file }) => file);
    return { variant: fixture.variant, sessionId: worker.sessionId, fixture, stream: metrics, distinctImages, invalidFrames,
      replayFrames: stream.frames.filter(frame => !invalidFrames.some(invalid => invalid.sequence === frame.sequence)),
      reportedFrames: stream.frames, files, visual };
  });
  if (new Set(runs.map(run => run.sessionId)).size !== 3
    || !['light', 'dark', 'dark-large'].every(variant => runs.filter(run => run.variant === variant).length === 1))
    throw new Error('Replay does not contain three unique session variants');
  runs.sort((a, b) => ['light', 'dark', 'dark-large'].indexOf(a.variant) - ['light', 'dark', 'dark-large'].indexOf(b.variant));
  const commonStart = Math.max(...runs.map(run => Date.parse(run.replayFrames[0].at)));
  const commonEnd = Math.min(...runs.map(run => Date.parse(run.replayFrames.at(-1).at)));
  const overlapMs = Math.max(0, commonEnd - commonStart);
  noLinks(path.dirname(path.resolve(destination)));
  for (const source of sources) {
    const relative = path.relative(path.resolve(source), path.resolve(destination));
    const outside = path.isAbsolute(relative) || relative === '..' || relative.startsWith('..' + path.sep);
    if (!outside) throw new Error('Replay output must not modify preserved sources');
  }
  fs.mkdirSync(destination);
  const hashes = [];
  for (const run of runs) {
    const directory = path.join(destination, run.variant);
    fs.mkdirSync(directory);
    for (const file of run.files) {
      if (!file.invalidReason) fs.writeFileSync(path.join(directory, file.file), file.bytes, { flag: 'wx' });
      hashes.push({ file: `${run.variant}/${file.file}`, sha256: file.sha256 });
    }
    for (const name of ['1-before-printwindow-2.png', '2-after-printwindow-2.png'])
      fs.copyFileSync(path.join(run.visual, name), path.join(directory, name), fs.constants.COPYFILE_EXCL);
  }
  const report = {
    schema: 1, mode: 'replay', sourceOutcome: 'host-bugcheck', liveSessionsCreated: 0,
    sourceWarning: 'SOURCE RUN FAILED WITH HOST BUGCHECK 0xEF. Invalid preserved images are excluded and reported. Recorded pixels do not establish safe session teardown.',
    sourceStartAt: new Date(Math.min(...runs.map(run => Date.parse(run.reportedFrames[0].at)))).toISOString(),
    sourceEndAt: new Date(Math.max(...runs.map(run => Date.parse(run.reportedFrames.at(-1).at)))).toISOString(),
    generatedAt: new Date().toISOString(), overlapMs, overlapKeepBarMet: overlapMs >= 20000,
    captureCadenceKeepBarMet: runs.every(run => run.stream.cadenceMet),
    lifecycleSafe: false, state: 'replay',
    invalidImageCount: runs.reduce((sum, run) => sum + run.invalidFrames.length, 0),
    runs: runs.map(({ files, visual, reportedFrames, ...run }) => ({ ...run, state: 'replay-waiting' })),
  };
  atomicJson(path.join(destination, 'frame-manifest.json'), { hashes, copiedFrames: hashes.length - report.invalidImageCount,
    invalidImageCount: report.invalidImageCount, sourceUntouched: true });
  atomicJson(path.join(destination, 'batch.json'), report);
  atomicJson(path.join(destination, 'report.json'), report);
  fs.writeFileSync(path.join(destination, 'report.md'), [
    '# Offline replay of the failed Full-session grid', '',
    '**SOURCE RUN FAILED WITH HOST BUGCHECK 0xEF. This is recorded evidence, not live Windows sessions.**', '',
    `Original common frame interval: **${overlapMs} ms** (required >=20000ms: ${report.overlapKeepBarMet ? 'met' : 'NOT MET'}).`,
    `Preserved files examined: ${hashes.length}; verified PNGs copied: ${hashes.length - report.invalidImageCount}; invalid images excluded: ${report.invalidImageCount}. Original timestamps are unchanged.`,
    '', '| Variant | Recorded session | Frames | Measured fps | Maximum gap ms | DPI | Fixture scale |',
    '|---|---:|---:|---:|---:|---:|---:|',
    ...runs.map(run => `| ${run.variant} | ${run.sessionId} | ${run.replayFrames.length}/${run.stream.frames} valid PNGs | ${run.stream.measuredFps.toFixed(3)} | ${run.stream.maximumIntervalMs} | ${run.stream.actualWindowDpi} | ${run.stream.fixtureLayoutScale} |`),
    '', 'The enlarged fixture is a render-transform simulation, not changed monitor DPI.',
    'No broker operation, account creation, session creation, stop, or recovery is performed by this replay.',
    'A successful capture/encoding result does not override the failed original lifecycle.', '',
    ...runs.flatMap(run => run.invalidFrames.map(frame => `- Excluded ${run.variant}/${frame.file}: ${frame.invalidReason}; preserved SHA-256 ${frame.sha256}.`)), '',
    ...runs.flatMap(run => [`## ${run.variant}`, '', `![Before](${run.variant}/1-before-printwindow-2.png)`,
      `![After](${run.variant}/2-after-printwindow-2.png)`, '']),
  ].join('\n'));
  return report;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 6) throw new Error('Usage: replay-evidence.mjs <new-output> <preserved-run-1> <preserved-run-2> <preserved-run-3>');
    const report = prepareReplay(path.resolve(process.argv[2]), process.argv.slice(3).map(source => path.resolve(source)));
    console.log(JSON.stringify({ output: path.resolve(process.argv[2]), sourceOutcome: report.sourceOutcome,
      overlapMs: report.overlapMs, overlapKeepBarMet: report.overlapKeepBarMet,
      runs: report.runs.map(({ replayFrames, ...run }) => run) }, null, 2));
  } catch (error) { console.error(JSON.stringify({ error: error.message })); process.exitCode = 1; }
}

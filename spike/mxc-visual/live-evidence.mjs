import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { atomicJson } from './broker-client-state.mjs';
import { validatePng } from './png-evidence.mjs';
import { capturePolicy } from './capture-policy.mjs';
import { calculatorIndex, sampleCalculatorIndex } from './visual-workloads.mjs';
export { validatePng } from './png-evidence.mjs';

export function validateFrame(frame) {
  if (!frame || !Number.isInteger(frame.sequence) || frame.sequence < 0 || frame.sequence >= 30
    || !Number.isInteger(frame.sessionId) || frame.sessionId <= 1
    || !Number.isInteger(frame.pid) || frame.pid <= 0 || !Number.isFinite(Date.parse(frame.at))
    || !Number.isFinite(frame.elapsedMs) || frame.elapsedMs < 0)
    throw new Error('Invalid frame metadata');
  if (frame.error) return;
  if (!Number.isInteger(frame.width) || !Number.isInteger(frame.height) || frame.width <= 0 || frame.height <= 0
    || frame.width * frame.height > 16000000 || !Number.isFinite(frame.actualWindowDpi) || frame.actualWindowDpi <= 0
    || !Number.isFinite(frame.captureMs) || frame.captureMs < 0 || frame.captureMs > frame.elapsedMs)
    throw new Error('Invalid frame geometry/timing');
  if (frame.file !== `frame-${String(frame.sequence).padStart(3, '0')}.png`) throw new Error('Invalid frame file');
  const phases = ['renderMs', 'publicationMs', 'scheduleDelayMs'];
  if (phases.some(key => Object.hasOwn(frame, key))
    && (phases.some(key => !Number.isFinite(frame[key]) || frame[key] < 0)
      || frame.renderMs + frame.publicationMs > frame.captureMs))
    throw new Error('Invalid capture phase timing');
}

export function readFrame(workspace, destination, observed, now = Date.now()) {
  const directories = fs.readdirSync(workspace).filter(name => /^broker-[a-f0-9]{32}$/.test(name));
  if (directories.length === 0) return null;
  if (directories.length !== 1) throw new Error('Ambiguous live output; refusing to display a different session');
  const source = path.join(workspace, directories[0]);
  if (fs.lstatSync(source).isSymbolicLink()) throw new Error('Live output is a reparse point');
  const metadataFiles = fs.readdirSync(source).filter(name => /^frame-meta-\d{3}\.json$/.test(name)).sort();
  if (metadataFiles.length > 30) throw new Error('Too many immutable frame records');
  if (!metadataFiles.length && !fs.existsSync(path.join(source, 'live.json')))
    return fs.existsSync(path.join(source, 'startup.json'))
      ? readPublishedFrame(source, 'startup.json', destination, observed, now) : null;
  // A poll can miss several publication times. Drain the immutable backlog,
  // but retain original capture times so collected-late frames are not "live".
  const pending = metadataFiles.length ? metadataFiles.filter(name =>
    !observed.sequences.has(Number(name.slice(11, 14))) || name === metadataFiles.at(-1)) : ['live.json'];
  let latest;
  for (const name of pending) {
    latest = readPublishedFrame(source, name, destination, observed, now);
    if (latest.error) throw new Error(`Frame ${latest.sequence}: ${latest.error}`);
  }
  return latest;
}

function readPublishedFrame(source, name, destination, observed, now) {
  const status = path.join(source, name);
  if (fs.lstatSync(status).isSymbolicLink() || fs.statSync(status).size > 16384) throw new Error('Invalid live metadata');
  const frame = JSON.parse(fs.readFileSync(status, 'utf8'));
  const startup = name === 'startup.json';
  if (startup) {
    if (frame.kind !== 'startup' || frame.file !== 'startup.png' || frame.error)
      throw new Error('Invalid startup preview');
    validateFrame({ ...frame, sequence: 0, file: 'frame-000.png' });
  } else validateFrame(frame);
  if (Date.parse(frame.at) > now + 5000) throw new Error('Frame clock is in the future');
  if (!startup && name !== 'live.json' && name !== `frame-meta-${String(frame.sequence).padStart(3, '0')}.json`)
    throw new Error('Frame sequence does not match immutable metadata');
  if (observed.sessionId && (observed.sessionId !== frame.sessionId || observed.pid !== frame.pid))
    throw new Error('Live session/process identity changed');
  observed.sessionId = frame.sessionId;
  observed.pid = frame.pid;
  if (frame.error) return { ...frame, receivedAt: new Date(now).toISOString(), error: String(frame.error).slice(0, 1000) };
  const image = path.join(source, frame.file);
  const stat = fs.lstatSync(image);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024) throw new Error('Invalid live image');
  const bytes = fs.readFileSync(image);
  validatePng(bytes, frame);
  const hash = createHash('sha256').update(bytes).digest('hex');
  const previous = startup ? observed.startup : observed.sequences.get(frame.sequence);
  if (previous && (previous.hash !== hash || previous.at !== frame.at || previous.elapsedMs !== frame.elapsedMs))
    throw new Error('Immutable frame changed after publication');
  fs.mkdirSync(destination, { recursive: true });
  const target = path.join(destination, frame.file);
  if (fs.existsSync(target)) {
    const prior = fs.lstatSync(target);
    if (!prior.isFile() || prior.isSymbolicLink() || prior.size !== bytes.length
      || !fs.readFileSync(target).equals(bytes))
      throw new Error('Existing live frame conflicts with the current capture');
  } else {
    fs.writeFileSync(target + '.tmp', bytes, { flag: 'wx' });
    fs.renameSync(target + '.tmp', target);
  }
  const settings = path.join(source, 'fixture-settings.json');
  let fixture;
  if (fs.existsSync(settings)) {
    if (fs.lstatSync(settings).isSymbolicLink() || fs.statSync(settings).size > 16384) throw new Error('Invalid fixture settings');
    fixture = JSON.parse(fs.readFileSync(settings, 'utf8'));
  }
  const receipt = previous ?? { ...frame, hash, receivedAt: new Date(now).toISOString() };
  if (startup) observed.startup = receipt;
  else if (!previous) observed.sequences.set(frame.sequence, receipt);
  return { ...frame, receivedAt: receipt.receivedAt,
    hash, fixture };
}

export function requireFreshLiveOutput(destination) {
  if (fs.existsSync(destination) && (fs.lstatSync(destination).isSymbolicLink()
    || !fs.statSync(destination).isDirectory() || fs.readdirSync(destination).length !== 0))
    throw new Error('Live output is not fresh; choose a new run directory');
}

export function readProgress(directory) {
  const final = path.join(directory, 'progress-final.json');
  if (fs.existsSync(final)) return JSON.parse(fs.readFileSync(final, 'utf8'));
  if (!fs.existsSync(directory)) return null;
  const records = fs.readdirSync(directory).filter(name => /^progress-\d{5}\.json$/.test(name)).sort();
  const latest = records.at(-1);
  if (latest) return JSON.parse(fs.readFileSync(path.join(directory, latest), 'utf8'));
  const legacy = path.join(directory, 'progress.json');
  return fs.existsSync(legacy) ? JSON.parse(fs.readFileSync(legacy, 'utf8')) : null;
}

export function readBatch(root) {
  const file = ['batch-finished.json', 'batch-started.json', 'batch.json'].find(name => fs.existsSync(path.join(root, name)));
  if (!file) throw new Error('Missing prepared batch');
  return JSON.parse(fs.readFileSync(path.join(root, file), 'utf8'));
}

export function startLiveCollector(workspace, destination, initial) {
  requireFreshLiveOutput(destination);
  const observed = { sequences: new Map() };
  let stopped = false, fault;
  let publication = 0;
  let progress = { ...initial, state: 'starting', targetFps: 1 };
  fs.mkdirSync(destination, { recursive: true });
  const write = () => atomicJson(path.join(destination, `progress-${String(publication++).padStart(5, '0')}.json`), progress);
  write();
  const tick = () => {
    if (stopped) return;
    try {
      const frame = readFrame(workspace, destination, observed);
      if (frame?.kind === 'startup' && frame.hash !== progress.preview?.hash) {
        progress = { ...progress, state: 'preparing', preview: frame }; write();
      } else if (frame && frame.kind !== 'startup' && (frame.sequence !== progress.frame?.sequence || frame.error !== progress.frame?.error)) {
        progress = { ...progress, state: frame.error ? 'capture-error' : 'live', frame }; write();
      }
    } catch (error) {
      fault = error.message;
      progress = { ...progress, state: 'capture-error', error: fault };
      try { write(); }
      catch (writeError) { fault += `; progress write failed: ${writeError.message}`; }
    }
  };
  const interval = setInterval(tick, 250);
  return {
    finish(error) {
      clearInterval(interval);
      try { tick(); }
      finally { stopped = true; }
      progress = { ...progress, state: error || fault ? 'failed' : 'complete', completedAt: new Date().toISOString(),
        ...(error || fault ? { error: error ?? fault } : {}), observedFrames: observed.sequences.size };
      atomicJson(path.join(destination, 'progress-final.json'), progress);
      atomicJson(path.join(destination, 'observed-frames.json'), [...observed.sequences.values()]);
      if (fault) throw new Error(`Live evidence collection failed: ${fault}`);
      return progress;
    },
  };
}

export function summarizeStream(stream, fixture, expectedVariant, policyInput) {
  const policy = capturePolicy(policyInput);
  if (!stream || !Array.isArray(stream.frames) || stream.targetFps !== 1) throw new Error('Missing stream report');
  const frames = stream.frames;
  frames.forEach(validateFrame);
  const good = frames.filter(frame => !frame.error);
  if (frames.length !== 30 || good.length !== 30 || good.some((frame, i) => frame.sequence !== i))
    throw new Error('The run did not capture all 30 distinct requested frames');
  if (new Set(good.map(frame => frame.sessionId)).size !== 1 || new Set(good.map(frame => frame.pid)).size !== 1)
    throw new Error('Stream mixes different sessions/processes');
  const expectedTheme = expectedVariant === 'light' ? 'Light' : 'Dark';
  const expectedScale = expectedVariant === 'dark-large' ? 1.5 : 1;
  const calculator = calculatorIndex(expectedVariant);
  const sample = sampleCalculatorIndex(expectedVariant);
  const appearanceValid = sample
    ? fixture?.workload === 'calculator-sample' && fixture.application === 'SandboxShowcase.WinUI.Calculator'
      && fixture.theme === (sample % 2 ? 'Light' : 'Dark') && Number.isFinite(fixture.rasterizationScale) && fixture.rasterizationScale > 0
    : calculator
    ? fixture?.workload === 'calculator' && fixture.application === 'Microsoft.WindowsCalculator'
      && fixture.theme === 'System (unmeasured)' && fixture.rasterizationScale === null
    : fixture?.theme === expectedTheme && Number.isFinite(fixture.rasterizationScale) && fixture.rasterizationScale > 0;
  if (fixture?.variant !== expectedVariant || !appearanceValid
    || fixture.fixtureLayoutScale !== expectedScale || !Number.isFinite(fixture.actualWindowDpi) || fixture.actualWindowDpi <= 0 || fixture.dpiWasChanged !== false
    || fixture.scaleIsSimulation !== (expectedScale !== 1) || good.some(frame => frame.actualWindowDpi !== fixture.actualWindowDpi))
    throw new Error('Measured fixture settings differ from requested variant');
  if (calculator) {
    const expected = [17 + 2 * calculator, 60 + 3 * calculator, 100];
    if (stream.workload !== (sample ? 'calculator-sample' : 'calculator') || stream.automationChecks?.length !== 3
      || stream.automationChecks.some((check, index) => check.passed !== true || check.expected !== expected[index] || check.actual !== expected[index]))
      throw new Error('Calculator arithmetic assertions did not match this session scenario');
  }
  const intervals = good.slice(1).map((frame, i) => frame.elapsedMs - good[i].elapsedMs);
  if (intervals.some(value => value <= 0)) throw new Error('Frame clock is not monotonic');
  if (good.slice(1).some((frame, i) => Date.parse(frame.at) <= Date.parse(good[i].at)))
    throw new Error('Recorded capture timestamps are not monotonic');
  const sorted = [...intervals].sort((a, b) => a - b);
  const span = good.at(-1).elapsedMs - good[0].elapsedMs;
  const fps = 29 * 1000 / span;
  const maximumIntervalMs = Math.max(...intervals);
  const cadenceIssues = [];
  if (policy.minAverageFps !== null && fps < policy.minAverageFps)
    cadenceIssues.push(`Average ${fps.toFixed(3)} fps is below the selected ${policy.minAverageFps} fps minimum`);
  if (policy.maxFrameGapMs !== null && maximumIntervalMs > policy.maxFrameGapMs)
    cadenceIssues.push(`Maximum gap ${maximumIntervalMs}ms exceeds the selected ${policy.maxFrameGapMs}ms limit`);
  const lateIntervals = intervals.filter(interval => interval > 2 * 1000 / stream.targetFps);
  const timingWarnings = lateIntervals.length ? [
    `${lateIntervals.length} gap(s) exceeded twice the target frame interval; worst ${maximumIntervalMs}ms. ${
      policy.maxFrameGapMs === null ? 'No hard maximum-gap limit selected.' : `Selected maximum-gap limit: ${policy.maxFrameGapMs}ms.`}`,
  ] : [];
  const measuredPhases = good.filter(frame => Number.isFinite(frame.renderMs));
  const phaseTimings = measuredPhases.length === good.length ? {
    maximumRenderMs: Math.max(...good.map(frame => frame.renderMs)),
    maximumPublicationMs: Math.max(...good.map(frame => frame.publicationMs)),
    maximumScheduleDelayMs: Math.max(...good.map(frame => frame.scheduleDelayMs)),
  } : null;
  return {
    frames: good.length, failedFrames: frames.length - good.length, targetFps: 1, measuredFps: fps,
    intervalMedianMs: sorted[Math.floor(sorted.length / 2)], intervalP95Ms: sorted[Math.ceil(sorted.length * .95) - 1],
    maximumIntervalMs, cadenceMet: cadenceIssues.length === 0, cadenceIssues, timingWarnings, capturePolicy: policy,
    captureSchedule: stream.schedule ?? 'legacy-fixed-deadlines', phaseTimings,
    actualWindowDpi: fixture.actualWindowDpi, rasterizationScale: fixture.rasterizationScale,
    theme: fixture.theme, fixtureLayoutScale: fixture.fixtureLayoutScale, scaleIsSimulation: fixture.scaleIsSimulation,
    captureSizes: [...new Set(good.map(frame => `${frame.width}x${frame.height}`))],
    ...(calculator ? { workload: sample ? 'calculator-sample' : 'calculator', automationChecks: stream.automationChecks } : {}),
  };
}

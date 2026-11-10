import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { atomicJson } from './broker-client-state.mjs';

export function readFrame(workspace, destination, observed, now = Date.now()) {
  const directories = fs.readdirSync(workspace).filter(name => /^broker-[a-f0-9]{32}$/.test(name));
  if (directories.length === 0) return null;
  if (directories.length !== 1) throw new Error('Ambiguous live output; refusing to display a different session');
  const source = path.join(workspace, directories[0]);
  if (fs.lstatSync(source).isSymbolicLink()) throw new Error('Live output is a reparse point');
  const metadataFiles = fs.readdirSync(source).filter(name => /^frame-meta-\d{3}\.json$/.test(name)).sort();
  const status = path.join(source, metadataFiles.at(-1) ?? 'live.json');
  if (!fs.existsSync(status)) return null;
  if (fs.lstatSync(status).isSymbolicLink() || fs.statSync(status).size > 16384) throw new Error('Invalid live metadata');
  const frame = JSON.parse(fs.readFileSync(status, 'utf8'));
  if (!Number.isInteger(frame.sequence) || frame.sequence < 0 || frame.sequence >= 30
    || !Number.isInteger(frame.sessionId) || frame.sessionId <= 0 || !Number.isFinite(Date.parse(frame.at)))
    throw new Error('Invalid frame metadata');
  if (metadataFiles.length && path.basename(status) !== `frame-meta-${String(frame.sequence).padStart(3, '0')}.json`)
    throw new Error('Frame sequence does not match immutable metadata');
  if (observed.sessionId && observed.sessionId !== frame.sessionId) throw new Error('Live session identity changed');
  observed.sessionId = frame.sessionId;
  if (frame.error) return { ...frame, receivedAt: new Date(now).toISOString(), error: String(frame.error).slice(0, 1000) };
  if (!/^frame-\d{3}\.png$/.test(frame.file) || frame.file !== `frame-${String(frame.sequence).padStart(3, '0')}.png`)
    throw new Error('Invalid frame file');
  const image = path.join(source, frame.file);
  const stat = fs.lstatSync(image);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024) throw new Error('Invalid live image');
  const bytes = fs.readFileSync(image);
  if (bytes.length < 24 || bytes.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a') throw new Error('Invalid PNG');
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
  if (!observed.sequences.has(frame.sequence)) observed.sequences.set(frame.sequence, { ...frame, receivedAt: new Date(now).toISOString() });
  return { ...frame, receivedAt: observed.sequences.get(frame.sequence).receivedAt,
    hash: createHash('sha256').update(bytes).digest('hex'), fixture };
}

export function requireFreshLiveOutput(destination) {
  if (fs.existsSync(destination) && (fs.lstatSync(destination).isSymbolicLink()
    || !fs.statSync(destination).isDirectory() || fs.readdirSync(destination).length !== 0))
    throw new Error('Live output is not fresh; choose a new run directory');
}

export function startLiveCollector(workspace, destination, initial) {
  requireFreshLiveOutput(destination);
  const observed = { sequences: new Map() };
  let stopped = false, fault;
  let progress = { ...initial, state: 'starting', targetFps: 1 };
  fs.mkdirSync(destination, { recursive: true });
  const write = () => atomicJson(path.join(destination, 'progress.json'), progress);
  write();
  const tick = () => {
    if (stopped) return;
    try {
      const frame = readFrame(workspace, destination, observed);
      if (frame) { progress = { ...progress, state: frame.error ? 'capture-error' : 'live', frame }; write(); }
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
      write();
      atomicJson(path.join(destination, 'observed-frames.json'), [...observed.sequences.values()]);
      if (fault) throw new Error(`Live evidence collection failed: ${fault}`);
      return progress;
    },
  };
}

export function summarizeStream(stream, fixture, expectedVariant) {
  if (!stream || !Array.isArray(stream.frames) || stream.targetFps !== 1) throw new Error('Missing stream report');
  const frames = stream.frames;
  const good = frames.filter(frame => !frame.error);
  if (frames.length !== 30 || good.length !== 30 || new Set(good.map(frame => frame.sequence)).size !== 30)
    throw new Error('The run did not capture all 30 distinct requested frames');
  const expectedTheme = expectedVariant === 'light' ? 'Light' : 'Dark';
  const expectedScale = expectedVariant === 'dark-large' ? 1.5 : 1;
  if (fixture?.variant !== expectedVariant || fixture.theme !== expectedTheme
    || fixture.fixtureLayoutScale !== expectedScale || fixture.actualWindowDpi <= 0 || fixture.dpiWasChanged !== false)
    throw new Error('Measured fixture settings differ from requested variant');
  const intervals = good.slice(1).map((frame, i) => frame.elapsedMs - good[i].elapsedMs);
  if (intervals.some(value => value <= 0)) throw new Error('Frame clock is not monotonic');
  const sorted = [...intervals].sort((a, b) => a - b);
  const span = good.at(-1).elapsedMs - good[0].elapsedMs;
  const fps = 29 * 1000 / span;
  return {
    frames: good.length, failedFrames: frames.length - good.length, targetFps: 1, measuredFps: fps,
    intervalMedianMs: sorted[Math.floor(sorted.length / 2)], intervalP95Ms: sorted[Math.ceil(sorted.length * .95) - 1],
    maximumIntervalMs: Math.max(...intervals), cadenceMet: fps >= .9 && Math.max(...intervals) <= 2000,
    actualWindowDpi: fixture.actualWindowDpi, rasterizationScale: fixture.rasterizationScale,
    theme: fixture.theme, fixtureLayoutScale: fixture.fixtureLayoutScale, scaleIsSimulation: fixture.scaleIsSimulation,
  };
}

import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { atomicJson } from './broker-client-state.mjs';
import { validateFrame, validatePng } from './live-evidence.mjs';
import { isVariant } from './visual-workloads.mjs';

const execute = promisify(execFile);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
let encoding = Promise.resolve();
const pending = new Map();

function readFile(file, limit) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > limit)
    throw new Error('Invalid recording input/output file');
  return fs.readFileSync(file);
}

export async function encodeRecording(directory, suppliedFrames) {
  if (fs.lstatSync(directory).isSymbolicLink()) throw new Error('Recording directory is a reparse point');
  const frames = suppliedFrames ?? JSON.parse(readFile(path.join(directory, 'observed-frames.json'), 128 * 1024));
  if (!Array.isArray(frames) || !frames.length || frames.length > 30)
    throw new Error('No captured frames available for recording');
  const sources = frames.map((frame, index) => {
    validateFrame(frame);
    if (frame.error) throw new Error('Cannot encode an invalid captured frame');
    const previous = frames[index - 1];
    if (previous && (frame.sequence <= previous.sequence || Date.parse(frame.at) <= Date.parse(previous.at)
      || frame.elapsedMs <= previous.elapsedMs || frame.sessionId !== previous.sessionId || frame.pid !== previous.pid
      || frame.width !== previous.width || frame.height !== previous.height))
      throw new Error('Recording frame identity, geometry or timing changed');
    const bytes = readFile(path.join(directory, frame.file), 16 * 1024 * 1024);
    validatePng(bytes, frame);
    const sha256 = hash(bytes);
    if (frame.hash && frame.hash !== sha256) throw new Error('Recorded frame hash changed');
    return { frame, bytes, sha256 };
  });
  // Keep the measured gaps; only the final still gets a synthetic one-second hold.
  const durations = frames.map((frame, index) => index + 1 < frames.length
    ? Date.parse(frames[index + 1].at) - Date.parse(frame.at) : 1000);
  const durationMs = durations.reduce((sum, duration) => sum + duration, 0);
  if (durationMs > 660000) throw new Error('Recording exceeds the bounded capture duration');
  const fingerprint = hash(JSON.stringify({ version: 2, durations,
    sources: sources.map(({ frame, sha256 }) => ({ sequence: frame.sequence, at: frame.at, sha256 })) }));
  const file = `recording-${fingerprint}.mp4`;
  const receiptFile = path.join(directory, 'recording.json');
  if (fs.existsSync(receiptFile)) {
    const receipt = JSON.parse(readFile(receiptFile, 16384));
    if (receipt.fingerprint === fingerprint && receipt.file === file) {
      if (hash(readFile(path.join(directory, file), 64 * 1024 * 1024)) !== receipt.sha256)
        throw new Error('Saved recording hash changed');
      return receipt;
    }
  }
  const temporary = fs.mkdtempSync(path.join(directory, '.recording-'));
  try {
    // Encode only verified copies, never arbitrary paths from frame metadata.
    for (const { frame, bytes } of sources)
      fs.writeFileSync(path.join(temporary, frame.file), bytes, { flag: 'wx' });
    const timeline = ['ffconcat version 1.0'];
    for (const [index, frame] of frames.entries())
      timeline.push(`file ${frame.file}`, 'option framerate 1000', `duration ${(durations[index] / 1000).toFixed(3)}`);
    timeline.push(`file ${frames.at(-1).file}`, 'option framerate 1000');
    fs.writeFileSync(path.join(temporary, 'frames.ffconcat'), timeline.join('\n') + '\n', { flag: 'wx' });
    try {
      await execute('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-nostdin', '-n',
        '-protocol_whitelist', 'file', '-f', 'concat', '-safe', '0', '-i', 'frames.ffconcat',
        '-an', '-enc_time_base', '1:1000', '-fps_mode', 'vfr', '-vf', 'pad=ceil(iw/2)*2:ceil(ih/2)*2',
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p',
        '-x264-params', 'fps=30/1', '-bf', '0', '-threads', '2', '-video_track_timescale', '1000',
        '-movflags', '+faststart', 'recording.mp4'],
      { cwd: temporary, windowsHide: true, timeout: 120000, maxBuffer: 128 * 1024 });
    } catch (error) {
      throw new Error(error.code === 'ENOENT' ? 'FFmpeg is required on PATH to create recorded videos'
        : `Video encoding failed: ${String(error.stderr || error.message).slice(-2000)}`);
    }
    const bytes = readFile(path.join(temporary, 'recording.mp4'), 64 * 1024 * 1024);
    if (bytes.length < 32 || bytes.toString('ascii', 4, 8) !== 'ftyp') throw new Error('Encoder did not produce an MP4');
    fs.renameSync(path.join(temporary, 'recording.mp4'), path.join(directory, file));
    const receipt = { schema: 1, fingerprint, file, sha256: hash(bytes), frames: frames.length,
      durationMs, lastFrameHoldMs: 1000, timing: 'capture-timestamps', createdAt: new Date().toISOString() };
    atomicJson(receiptFile, receipt);
    return receipt;
  } finally { fs.rmSync(temporary, { recursive: true, force: true }); }
}

export function cameraRecordings(root) {
  const entries = new Map();
  return (run, progress, batch) => {
    if (!isVariant(run.variant)) throw new Error('Unexpected recording variant');
    const finished = batch.mode === 'replay' || Boolean(batch.completedAt)
      || ['passed', 'failed', 'skipped'].includes(run.state) || ['complete', 'failed'].includes(progress?.state);
    if (!finished || run.state === 'skipped') return null;
    const directory = path.join(root, run.variant);
    // The collector publishes completion just before its immutable frame index.
    if (batch.mode !== 'replay' && !batch.completedAt && !['passed', 'failed'].includes(run.state)
      && !fs.existsSync(path.join(directory, 'observed-frames.json'))) return { state: 'encoding' };
    if (!entries.has(run.variant)) {
      const entry = { state: 'encoding' };
      entries.set(run.variant, entry);
      const key = path.resolve(directory);
      let job = pending.get(key);
      if (!job) {
        job = encoding.then(() => encodeRecording(directory, run.replayFrames));
        pending.set(key, job);
        // Serialize CPU work across tiles/panels; failure remains visible on its own tile.
        encoding = job.then(() => {}, () => {});
        job.then(() => pending.delete(key), () => pending.delete(key));
      }
      job.then(receipt => Object.assign(entry, { state: 'ready', url: `${run.variant}/${receipt.file}`,
        frames: receipt.frames, durationMs: receipt.durationMs }),
      error => Object.assign(entry, { state: 'error', error: error.message }));
    }
    return { ...entries.get(run.variant) };
  };
}

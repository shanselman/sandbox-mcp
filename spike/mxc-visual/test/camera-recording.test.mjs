import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { encodeRecording } from '../camera-recording.mjs';
import { startCameraServer } from '../camera-server.mjs';
import { pngFixture } from './png-fixture.mjs';

const available = ['ffmpeg', 'ffprobe'].every(command => spawnSync(command, ['-version'], { windowsHide: true }).status === 0);
const videoTest = { skip: !available && 'FFmpeg and ffprobe on PATH are required for video integration tests' };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'camera-recording-'));
  const directory = path.join(root, 'light');
  fs.mkdirSync(directory);
  const frames = [0, 200, 650].map((time, sequence) => {
    const bytes = pngFixture(sequence * 80, 5, 3);
    const frame = { sequence, file: `frame-00${sequence}.png`, at: new Date(1700000000000 + time).toISOString(),
      elapsedMs: time + 10, captureMs: 10, sessionId: 20, pid: 40, width: 5, height: 3, actualWindowDpi: 96, hash: hash(bytes) };
    fs.writeFileSync(path.join(directory, frame.file), bytes);
    return frame;
  });
  fs.writeFileSync(path.join(directory, 'observed-frames.json'), JSON.stringify(frames));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return { root, directory, frames };
}

test('recorded MP4 keeps measured gaps, pads odd dimensions, persists, and verifies its sources on reopen', videoTest, async t => {
  const { directory, frames } = fixture(t);
  const receipt = await encodeRecording(directory);
  const video = path.join(directory, receipt.file);
  const probe = JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0',
    '-show_entries', 'stream=codec_name,pix_fmt,width,height,duration,level',
    '-show_entries', 'frame=pts_time', '-of', 'json', video], { encoding: 'utf8', windowsHide: true }));
  assert.equal(probe.streams[0].codec_name, 'h264');
  assert.equal(probe.streams[0].pix_fmt, 'yuv420p');
  assert.equal(probe.streams[0].width, 6);
  assert.equal(probe.streams[0].height, 4);
  assert.ok(probe.streams[0].level <= 41, 'small videos do not advertise an excessive decoder level');
  assert.deepEqual(probe.frames.map(frame => Number(frame.pts_time)), [0, 0.2, 0.65, 1.65]);
  assert.ok(Math.abs(Number(probe.streams[0].duration) * 1000 - 1650) < 40);
  const mtime = fs.statSync(video).mtimeMs;
  assert.deepEqual(await encodeRecording(directory), receipt);
  assert.equal(fs.statSync(video).mtimeMs, mtime, 'existing video is reused across viewer restarts');
  for (const frame of frames) assert.equal(hash(fs.readFileSync(path.join(directory, frame.file))), frame.hash);
  fs.writeFileSync(path.join(directory, frames[0].file), pngFixture(100, 5, 3));
  await assert.rejects(encodeRecording(directory), /hash changed/);
});

test('recording rejects traversal, changed identity, backwards timing and invalid PNGs before encoding', async t => {
  const { directory, frames } = fixture(t);
  await assert.rejects(encodeRecording(directory, [{ ...frames[0], file: '..\\outside.png' }]), /frame file/);
  await assert.rejects(encodeRecording(directory, [frames[0], { ...frames[1], sessionId: 21 }]), /identity/);
  await assert.rejects(encodeRecording(directory, [frames[1], frames[0]]), /timing/);
  fs.writeFileSync(path.join(directory, frames[0].file), 'not a PNG');
  await assert.rejects(encodeRecording(directory), /PNG/);
  assert.equal(fs.existsSync(path.join(directory, 'recording.json')), false);
});

test('live-to-failed recording retains verdict, serves scoped seek ranges, and reports playback', videoTest, async t => {
  const { root, directory, frames } = fixture(t);
  const batch = { schema: 1, mode: 'concurrent', state: 'running', runs: [{ variant: 'light', state: 'running' }] };
  fs.writeFileSync(path.join(root, 'batch.json'), JSON.stringify(batch));
  fs.writeFileSync(path.join(directory, 'progress.json'), JSON.stringify({ state: 'live', frame: frames.at(-1) }));
  const server = await startCameraServer(root);
  t.after(() => server.close());
  assert.equal(server.state().runs[0].recording, null);
  assert.equal(fs.existsSync(path.join(directory, 'recording.json')), false);
  fs.unlinkSync(path.join(directory, 'observed-frames.json'));
  fs.writeFileSync(path.join(directory, 'progress-final.json'), JSON.stringify({ state: 'complete', frame: frames.at(-1) }));
  assert.equal(server.state().runs[0].recording.state, 'encoding', 'wait for final frame index without caching an error');
  fs.writeFileSync(path.join(directory, 'observed-frames.json'), JSON.stringify(frames));
  fs.writeFileSync(path.join(root, 'batch-finished.json'), JSON.stringify({
    ...batch, state: 'failed', completedAt: new Date().toISOString(),
    runs: [{ ...batch.runs[0], state: 'failed', error: 'Original capture ended early' }],
  }));
  let state;
  for (let attempt = 0; ; attempt++) {
    state = server.state();
    if (state.runs[0].recording.state !== 'encoding') break;
    assert.ok(attempt < 200, 'encoding completes within ten seconds');
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  const run = state.runs[0];
  assert.equal(run.state, 'failed');
  assert.equal(run.recording.state, 'ready', JSON.stringify(run.recording));
  assert.equal(run.recording.frames, 3);
  const url = new URL(run.recording.url, server.url);
  const full = Buffer.from(await (await fetch(url)).arrayBuffer());
  const partial = await fetch(url, { headers: { Range: 'bytes=4-7' } });
  assert.equal(partial.status, 206);
  assert.equal(partial.headers.get('content-range'), `bytes 4-7/${full.length}`);
  assert.equal(await partial.text(), 'ftyp');
  const suffix = await fetch(url, { headers: { Range: 'bytes=-20' } });
  assert.deepEqual(Buffer.from(await suffix.arrayBuffer()), full.subarray(-20));
  const head = await fetch(url, { method: 'HEAD' });
  assert.equal(Number(head.headers.get('content-length')), full.length);
  assert.equal((await head.arrayBuffer()).byteLength, 0);
  for (const range of ['bytes=999999999-', 'bytes=10-4', 'bytes=0-1,4-5', 'bytes=-0'])
    assert.equal((await fetch(url, { headers: { Range: range } })).status, 416);
  assert.equal((await fetch(url, { headers: { Origin: 'https://other.invalid' } })).status, 403);
  assert.equal((await fetch(new URL(`/${run.recording.url}`, server.url))).status, 404);
  const payload = [{ variant: 'light', currentTime: 0.5, duration: 1.65, paused: false, readyState: 4, loops: 1, error: '' }];
  const report = value => fetch(new URL('playback', server.url), { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
  assert.equal((await report(payload)).status, 200);
  assert.deepEqual(server.playback().tiles, payload);
  assert.equal((await report([{ ...payload[0], command: 'not-an-execution-endpoint' }])).status, 400);
});

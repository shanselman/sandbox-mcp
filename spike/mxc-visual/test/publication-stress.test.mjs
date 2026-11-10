import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { atomicJson } from '../broker-client-state.mjs';
import { cameraState, replayState } from '../viewer-state.mjs';

const worker = fileURLToPath(new URL('./publication-writer.mjs', import.meta.url));
test('five rounds of three independent writers with a concurrent reader preserve 1500 snapshots', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-publication-stress-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  let observed = 0;
  for (let round = 0; round < 5; round++) {
    const directory = path.join(root, String(round)); fs.mkdirSync(directory);
    const children = [0, 1, 2].map(writer => spawn(process.execPath, [worker, directory, String(writer), '100'], { stdio: 'pipe' }));
    let done = false, writerError;
    const exited = Promise.all(children.map(child => once(child, 'exit'))).then(codes => {
      if (!codes.every(([code]) => code === 0)) writerError = new Error('A synthetic writer failed');
    }).catch(error => { writerError = error; }).finally(() => { done = true; });
    const seen = new Map();
    const read = () => {
      for (const name of fs.readdirSync(directory).filter(name => /^snapshot-\d-\d{4}\.json$/.test(name))) {
        const bytes = fs.readFileSync(path.join(directory, name));
        const data = JSON.parse(bytes.toString());
        assert.equal(data.marker, `${data.writer}:${data.sequence}`);
        assert.equal(data.padding.length, 2048);
        assert.equal(name, `snapshot-${data.writer}-${String(data.sequence).padStart(4, '0')}.json`);
        if (seen.has(name)) assert(bytes.equals(seen.get(name)), 'An immutable snapshot changed');
        else seen.set(name, bytes);
      }
    };
    const deadline = Date.now() + 15000;
    try {
      while (!done) { if (Date.now() > deadline) throw new Error('Synthetic writers exceeded deadline'); read(); await new Promise(resolve => setTimeout(resolve, 5)); }
      await exited; if (writerError) throw writerError; read();
    }
    finally { for (const child of children) if (child.exitCode === null) child.kill(); await Promise.allSettled(children.map(child => child.exitCode === null ? once(child, 'exit') : Promise.resolve())); }
    assert.equal(seen.size, 300);
    assert.equal(fs.readdirSync(directory).filter(name => name.endsWith('.tmp')).length, 0);
    observed += seen.size;
  }
  assert.equal(observed, 1500);
});
test('atomic writer does not replace a committed receipt if serialization fails', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'full-journal-failure-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const file = path.join(root, 'state.json'); atomicJson(file, { phase: 'provisioned' });
  const cyclic = {}; cyclic.self = cyclic;
  assert.throws(() => atomicJson(file, cyclic));
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { phase: 'provisioned' });
  assert.deepEqual(fs.readdirSync(root), ['state.json']);
});
test('failed collector never becomes a completed-success tile and future frames never become live', () => {
  const run = { variant: 'dark', state: 'starting' }, now = Date.now();
  assert.equal(cameraState({}, run, { state: 'failed' }, now).state, 'failed');
  assert.equal(cameraState({}, { ...run, state: 'passed' }, { state: 'capture-error' }, now).state, 'failed');
  assert.equal(cameraState({}, run, { state: 'live', frame: { at: new Date(now + 60000).toISOString() } }, now).state, 'invalid-clock');
});
test('replay retains original time, gaps and failed outcome, never live or passed', () => {
  const source = Date.parse('2026-11-10T05:10:00Z'), playback = 100000;
  const batch = { mode: 'replay', sourceOutcome: 'host-bugcheck', sourceStartAt: new Date(source).toISOString(),
    sourceEndAt: new Date(source + 29000).toISOString(), runs: [{ variant: 'dark', replayFrames: [
      { sequence: 0, at: new Date(source).toISOString(), file: 'frame-000.png' },
      { sequence: 29, at: new Date(source + 29000).toISOString(), file: 'frame-029.png' },
    ] }] };
  const initial = replayState(batch, playback, playback);
  assert.equal(initial.runs[0].state, 'replay');
  assert.equal(initial.runs[0].progress.frame.at, batch.sourceStartAt);
  assert.equal(replayState(batch, playback + 5000, playback).runs[0].state, 'replay-gap');
  const final = replayState(batch, playback + 60000, playback);
  assert.equal(final.state, 'replay-complete');
  assert.equal(final.sourceOutcome, 'host-bugcheck');
  assert.equal(final.runs[0].progress.frame.at, batch.sourceEndAt);
  assert.throws(() => replayState({ ...batch, sourceOutcome: 'passed' }, playback, playback));
});

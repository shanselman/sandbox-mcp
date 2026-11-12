export function cameraState(batch, run, progress, now) {
  const frame = progress?.frame;
  const ageMs = frame ? now - Date.parse(frame.at) : null;
  let state;
  if (run.state === 'failed' || progress?.state === 'failed' || progress?.state === 'capture-error' || frame?.error)
    state = 'failed';
  else if (['passed', 'skipped'].includes(run.state)) state = run.state;
  else if (progress?.state === 'complete') state = 'complete';
  else if (frame && (!Number.isFinite(ageMs) || ageMs < -5000)) state = 'invalid-clock';
  else if (frame && ageMs > 2500) state = 'stale';
  else state = progress?.state ?? run.state;
  return { ...run, progress, state, ageMs,
    image: frame?.file && /^frame-\d{3}\.png$/.test(frame.file) ? `${run.variant}/${frame.file}`
      : progress?.preview?.file === 'startup.png' ? `${run.variant}/startup.png` : null };
}

export function replayState(batch, now, playbackStartedAt) {
  if (batch.mode !== 'replay' || batch.sourceOutcome !== 'host-bugcheck')
    throw new Error('Replay requires explicit failed-source provenance');
  const origin = Date.parse(batch.sourceStartAt);
  const end = Date.parse(batch.sourceEndAt);
  if (!Number.isFinite(origin) || !Number.isFinite(end) || end < origin) throw new Error('Invalid original replay clock');
  const elapsedMs = Math.max(0, now - playbackStartedAt);
  const captureClock = Math.min(end, origin + elapsedMs);
  const complete = elapsedMs >= end - origin;
  return {
    ...batch, state: complete ? 'replay-complete' : 'replay',
    replay: { elapsedMs, durationMs: end - origin, captureClock: new Date(captureClock).toISOString(),
      originalTimestampsUnmodified: true, sourceFailed: true },
    runs: batch.runs.map(run => {
      const frame = run.replayFrames.findLast(frame => Date.parse(frame.at) <= captureClock);
      const ageMs = frame ? captureClock - Date.parse(frame.at) : null;
      const ended = frame && frame.sequence === run.replayFrames.at(-1)?.sequence;
      return {
        ...run, replayFrames: undefined, state: complete || ended ? 'replay-complete'
          : !frame ? 'replay-waiting' : ageMs > 2500 ? 'replay-gap' : 'replay',
        ageMs, progress: { state: 'replay', frame: frame ? { ...frame, fixture: run.fixture } : null },
        image: frame ? `${run.variant}/${frame.file}` : null,
      };
    }),
  };
}

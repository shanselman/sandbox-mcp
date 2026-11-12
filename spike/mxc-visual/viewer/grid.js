const cards = new Map();
let replay = false;
const zoomLevels = [0.75, 1, 1.25, 1.5, 2];
let zoomIndex = 1;
function setZoom(index) {
  zoomIndex = Math.max(0, Math.min(zoomLevels.length - 1, index));
  const zoom = zoomLevels[zoomIndex];
  document.getElementById('grid').style.setProperty('--zoom', zoom);
  document.getElementById('zoom-level').textContent = zoom === 1 ? 'Auto' : `${zoom * 100}%`;
  document.getElementById('zoom-out').disabled = zoomIndex === 0;
  document.getElementById('zoom-in').disabled = zoomIndex === zoomLevels.length - 1;
}
document.getElementById('zoom-out').addEventListener('click', () => setZoom(zoomIndex - 1));
document.getElementById('zoom-in').addEventListener('click', () => setZoom(zoomIndex + 1));
document.getElementById('zoom-fit').addEventListener('click', () => setZoom(1));
setZoom(1);
function identity(run, index) {
  for (const field of ['id', 'requestId', 'variant']) {
    const value = run[field];
    if ((typeof value === 'string' && value.trim()) || (typeof value === 'number' && Number.isFinite(value))) return `${field}:${value}`;
  }
  return `index:${index}`;
}
function sessionName(run, index) {
  return [run.name, run.title, run.variant].find(value => typeof value === 'string' && value.trim()) ?? `Session ${index + 1}`;
}
function card(key) {
  if (cards.has(key)) return cards.get(key);
  const root = document.createElement('article');
  const heading = document.createElement('div'); heading.className = 'tile-head';
  const title = document.createElement('h2');
  const badge = document.createElement('span'); badge.className = 'badge';
  const appearance = document.createElement('span'); appearance.className = 'variant-label'; appearance.hidden = true;
  heading.append(title, badge, appearance);
  const picture = document.createElement('div'); picture.className = 'picture';
  const image = document.createElement('img'); image.hidden = true;
  const placeholder = document.createElement('div'); placeholder.className = 'placeholder'; placeholder.textContent = 'Waiting for this agent session';
  const video = document.createElement('video'); video.hidden = true;
  video.controls = true; video.muted = true; video.loop = true; video.playsInline = true;
  video.autoplay = !globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  video.preload = 'auto';
  picture.append(image, placeholder, video);
  const evidence = document.createElement('details'); evidence.className = 'evidence';
  const evidenceSummary = document.createElement('summary');
  const metrics = document.createElement('div'); metrics.className = 'metrics';
  evidence.append(evidenceSummary, metrics);
  const warning = document.createElement('p'); warning.className = 'tile-warning'; warning.hidden = true;
  const playbackNote = document.createElement('p'); playbackNote.className = 'playback-note'; playbackNote.hidden = true;
  root.append(heading, picture, evidence, warning, playbackNote); document.getElementById('grid').append(root);
  const result = { root, title, badge, appearance, image, placeholder, video, metrics, evidenceSummary, warning, playbackNote,
    loops: 0, lastTime: 0, playbackError: '' };
  video.addEventListener('timeupdate', () => {
    if (video.currentTime + 0.1 < result.lastTime && !video.seeking) result.loops++;
    result.lastTime = video.currentTime;
  });
  video.addEventListener('loadeddata', () => {
    if (!video.dataset.recording || result.playbackError) return;
    video.hidden = false; image.hidden = true; placeholder.hidden = true;
    if (video.autoplay) video.play().catch(error => {
      result.playbackError = `Autoplay unavailable: ${error.message}. Use the video Play control.`;
      playbackNote.textContent = result.playbackError; playbackNote.hidden = false;
    });
  });
  video.addEventListener('playing', () => { result.playbackError = ''; playbackNote.hidden = true; });
  video.addEventListener('error', () => {
    result.playbackError = `Recorded video could not play (media error ${video.error?.code ?? 'unknown'}). Showing the last image.`;
    video.hidden = true; image.hidden = !image.dataset.frame; placeholder.hidden = Boolean(image.dataset.frame);
    playbackNote.textContent = result.playbackError; playbackNote.hidden = false;
  });
  cards.set(key, result); return result;
}
function clearVideo(tile) {
  if (!tile.video.dataset.recording) return;
  tile.video.pause(); tile.video.removeAttribute('src'); delete tile.video.dataset.recording;
  tile.video.load(); tile.video.hidden = true;
  tile.image.hidden = !tile.image.dataset.frame; tile.placeholder.hidden = Boolean(tile.image.dataset.frame);
  tile.playbackError = ''; tile.loops = 0; tile.lastTime = 0;
}
function renderRecording(tile, run) {
  tile.variant = run.variant;
  const finished = ['passed', 'complete', 'failed', 'replay-complete'].includes(run.state);
  const recording = finished ? run.recording : null;
  if (recording?.state === 'ready') {
    if (tile.video.dataset.recording !== recording.url) {
      clearVideo(tile);
      tile.video.dataset.recording = recording.url;
      tile.video.src = recording.url;
      tile.video.title = `Recorded ${sessionName(run, 0)} session; looping playback`;
      tile.video.load();
    }
    if (tile.video.readyState >= 2 && !tile.video.error) {
      tile.video.hidden = false; tile.image.hidden = true; tile.placeholder.hidden = true;
    }
    tile.badge.textContent = replay ? 'REPLAY · SOURCE FAILED · loop'
      : run.state === 'failed' ? 'FAILED · recorded loop' : 'RECORDED · loop';
    tile.playbackNote.textContent = tile.playbackError;
  } else {
    clearVideo(tile);
    tile.playbackNote.textContent = recording?.state === 'encoding' ? 'Preparing recorded video…'
      : recording?.state === 'error' ? `Recording unavailable: ${recording.error}` : '';
  }
  tile.playbackNote.hidden = !tile.playbackNote.textContent;
}
function renderBatch(batch) {
  replay = batch.mode === 'replay';
  const grid = document.getElementById('grid');
  const keys = new Set(batch.runs.map(identity));
  for (const [key, tile] of cards) {
    if (!keys.has(key)) { clearVideo(tile); tile.root.remove(); cards.delete(key); }
  }
  grid.style.setProperty('--tile-count', Math.max(1, keys.size));
  grid.dataset.density = keys.size > 8 ? 'dense' : keys.size > 4 ? 'compact' : 'standard';
  document.getElementById('subtitle').textContent = replay
    ? 'OFFLINE REPLAY · original capture timing · no live Windows sessions'
    : `${keys.size} session${keys.size === 1 ? '' : 's'} · 1fps target per session · PrintWindow flag 2`;
  document.getElementById('provenance').textContent = replay ? batch.sourceWarning ?? 'Preserved evidence from a failed source run. No live Windows sessions.'
    : 'MXC provisions the accounts; our installed broker creates Full sessions through experimental Windows APIs. Completed tiles show recorded evidence, not live desktops.';
  let live = 0, passed = 0;
  for (const [index, run] of batch.runs.entries()) {
    const tile = card(identity(run, index)), frame = run.progress?.frame, preview = run.progress?.preview, fixture = frame?.fixture ?? preview?.fixture;
    const name = sessionName(run, index);
    tile.title.textContent = name; tile.title.title = name;
    const measured = fixture ?? run.stream;
    const appearance = measured ?? run.requestedAppearance;
    tile.appearance.textContent = appearance
      ? `${appearance.theme} · ${Math.round(appearance.fixtureLayoutScale * 100)}%${appearance.scaleIsSimulation ? ' sim' : ''} · ${measured ? `${appearance.actualWindowDpi} DPI` : 'requested'}`
      : '';
    tile.appearance.title = tile.appearance.textContent;
    tile.appearance.hidden = !appearance;
    tile.image.alt = `Captured application window in ${name} session`;
    if (grid.children[index] !== tile.root) grid.insertBefore(tile.root, grid.children[index] ?? null);
    tile.badge.className = `badge ${run.state}`;
    tile.badge.textContent = replay ? run.state.toUpperCase().replaceAll('-', ' · ')
      : run.state === 'live' ? 'LIVE · 1fps target' : run.state === 'preparing' ? 'PREPARING · startup image'
      : run.state === 'passed' || run.state === 'complete' ? 'RECORDED · finished' : run.state.toUpperCase();
    if (run.state === 'live') live++;
    if (run.state === 'passed') passed++;
    if (run.image && tile.image.dataset.frame !== run.image) {
      tile.image.src = run.image; tile.image.dataset.frame = run.image;
      tile.image.hidden = false; tile.placeholder.hidden = true;
    }
    if (!run.image) {
      tile.image.hidden = true; tile.placeholder.hidden = false; delete tile.image.dataset.frame;
      tile.image.removeAttribute('src');
    }
    renderRecording(tile, run);
    tile.evidenceSummary.textContent = !frame && preview ? 'Evidence · startup preview (not a measured frame)'
      : `Evidence · frame ${frame ? frame.sequence + 1 : '—'}/30`;
    tile.metrics.textContent = [
      `Session ${frame?.sessionId ?? run.sessionId ?? '—'} · frame ${frame ? frame.sequence + 1 : '—'}/30`,
      `Theme ${fixture?.theme ?? run.stream?.theme ?? 'pending'} · actual DPI ${fixture?.actualWindowDpi ?? run.stream?.actualWindowDpi ?? '—'}`,
      `Fixture scale ${fixture?.fixtureLayoutScale ?? run.stream?.fixtureLayoutScale ?? '—'}× · capture ${frame?.width ?? '—'}×${frame?.height ?? '—'}`,
      frame ? `Recorded ${new Date(frame.at).toLocaleTimeString()} · ${replay ? 'playback gap' : 'age'} ${(Math.max(0, run.ageMs) / 1000).toFixed(1)}s`
        : preview ? `Startup picture ${new Date(preview.at).toLocaleTimeString()} · measured capture has not started` : 'No frame received',
      run.stream ? `Measured ${run.stream.measuredFps.toFixed(3)} fps · max gap ${run.stream.maximumIntervalMs}ms` : '',
      run.stream?.automationChecks ? `${run.stream.automationChecks.length} verified Calculator results: ${run.stream.automationChecks.map(check => check.actual).join(', ')}` : '',
    ].filter(Boolean).join('\n');
    tile.warning.textContent = [
      run.error ?? run.progress?.error ?? frame?.error ?? '',
      ...(run.warnings ?? run.stream?.timingWarnings ?? []),
      run.invalidFrames?.length ? `INVALID PRESERVED IMAGES: ${run.invalidFrames.length} (excluded)` : '',
    ].filter(Boolean).join('\n');
    tile.warning.hidden = !tile.warning.textContent;
  }
  document.getElementById('summary').textContent = replay ? 'REPLAY · SOURCE FAILED' : `${live} live · ${passed}/${batch.runs.length} passed`;
  document.getElementById('details').textContent = replay
    ? `Replaying preserved evidence; original overlap ${batch.overlapMs}ms. No new sessions. ${batch.state === 'replay-complete' ? 'Playback complete.' : ''}`
    : batch.completedAt
    ? `Run ${batch.state}. Finished ${new Date(batch.completedAt).toLocaleString()}. Tiles retain recorded evidence and loop available videos.`
    : 'Coordinated run. A stale badge means no fresh frame within 2.5 seconds.';
  document.getElementById('error').textContent = '';
}
function showDisconnected(error) {
  document.getElementById('error').textContent = error.message;
  document.getElementById('summary').textContent = replay ? 'REPLAY · SOURCE FAILED · DISCONNECTED' : 'DISCONNECTED';
  for (const tile of cards.values()) { tile.badge.className = 'badge stale'; tile.badge.textContent = 'DISCONNECTED · last frame'; }
}
async function update() {
  try {
    const response = await fetch('state', { cache: 'no-store' });
    if (!response.ok) throw new Error(`Viewer state returned HTTP ${response.status}`);
    renderBatch(await response.json());
    const playback = [...cards.values()].filter(tile => tile.video.dataset.recording).map(tile => ({
      variant: tile.variant, currentTime: tile.video.currentTime || 0,
      duration: Number.isFinite(tile.video.duration) ? tile.video.duration : 0,
      paused: tile.video.paused, readyState: tile.video.readyState, loops: tile.loops,
      error: tile.playbackError.slice(0, 500),
    }));
    if (playback.length) {
      const observed = await fetch('playback', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(playback) });
      if (!observed.ok) throw new Error(`Playback readback returned HTTP ${observed.status}`);
    }
  } catch (error) {
    showDisconnected(error);
  } finally { setTimeout(update, 1000); }
}
update();

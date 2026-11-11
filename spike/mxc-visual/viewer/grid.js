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
  heading.append(title, badge);
  const picture = document.createElement('div'); picture.className = 'picture';
  const image = document.createElement('img'); image.hidden = true;
  const placeholder = document.createElement('div'); placeholder.className = 'placeholder'; placeholder.textContent = 'Waiting for this agent session';
  picture.append(image, placeholder);
  const evidence = document.createElement('details'); evidence.className = 'evidence';
  const evidenceSummary = document.createElement('summary');
  const metrics = document.createElement('div'); metrics.className = 'metrics';
  evidence.append(evidenceSummary, metrics);
  const warning = document.createElement('p'); warning.className = 'tile-warning'; warning.hidden = true;
  root.append(heading, picture, evidence, warning); document.getElementById('grid').append(root);
  const result = { root, title, badge, image, placeholder, metrics, evidenceSummary, warning }; cards.set(key, result); return result;
}
function renderBatch(batch) {
  replay = batch.mode === 'replay';
  const grid = document.getElementById('grid');
  const keys = new Set(batch.runs.map(identity));
  for (const [key, tile] of cards) {
    if (!keys.has(key)) { tile.root.remove(); cards.delete(key); }
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
    const tile = card(identity(run, index)), frame = run.progress?.frame, fixture = frame?.fixture;
    const name = sessionName(run, index);
    tile.title.textContent = name; tile.title.title = name;
    tile.image.alt = `Captured WinUI window in ${name} session`;
    if (grid.children[index] !== tile.root) grid.insertBefore(tile.root, grid.children[index] ?? null);
    tile.badge.className = `badge ${run.state}`;
    tile.badge.textContent = replay ? run.state.toUpperCase().replaceAll('-', ' · ')
      : run.state === 'live' ? 'LIVE · 1fps target' : run.state === 'passed' || run.state === 'complete' ? 'RECORDED · finished' : run.state.toUpperCase();
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
    tile.evidenceSummary.textContent = `Evidence · frame ${frame ? frame.sequence + 1 : '—'}/30`;
    tile.metrics.textContent = [
      `Session ${frame?.sessionId ?? run.sessionId ?? '—'} · frame ${frame ? frame.sequence + 1 : '—'}/30`,
      `Theme ${fixture?.theme ?? run.stream?.theme ?? 'pending'} · actual DPI ${fixture?.actualWindowDpi ?? run.stream?.actualWindowDpi ?? '—'}`,
      `Fixture scale ${fixture?.fixtureLayoutScale ?? run.stream?.fixtureLayoutScale ?? '—'}× · capture ${frame?.width ?? '—'}×${frame?.height ?? '—'}`,
      frame ? `Recorded ${new Date(frame.at).toLocaleTimeString()} · ${replay ? 'playback gap' : 'age'} ${(Math.max(0, run.ageMs) / 1000).toFixed(1)}s` : 'No frame received',
      run.stream ? `Measured ${run.stream.measuredFps.toFixed(3)} fps · max gap ${run.stream.maximumIntervalMs}ms` : '',
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
    ? `Run ${batch.state}. Finished ${new Date(batch.completedAt).toLocaleString()}. Tiles retain recorded evidence.`
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
  } catch (error) {
    showDisconnected(error);
  } finally { setTimeout(update, 1000); }
}
update();

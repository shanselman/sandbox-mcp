const cards = new Map();
function card(variant) {
  if (cards.has(variant)) return cards.get(variant);
  const root = document.createElement('article');
  const heading = document.createElement('div'); heading.className = 'tile-head';
  const title = document.createElement('h2'); title.textContent = variant;
  const badge = document.createElement('span'); badge.className = 'badge';
  heading.append(title, badge);
  const picture = document.createElement('div'); picture.className = 'picture';
  const image = document.createElement('img'); image.hidden = true; image.alt = `Captured WinUI window in ${variant} session`;
  const placeholder = document.createElement('div'); placeholder.className = 'placeholder'; placeholder.textContent = 'Waiting for this agent session';
  picture.append(image, placeholder);
  const metrics = document.createElement('div'); metrics.className = 'metrics';
  root.append(heading, picture, metrics); document.getElementById('grid').append(root);
  const result = { root, badge, image, placeholder, metrics }; cards.set(variant, result); return result;
}
async function update() {
  try {
    const response = await fetch('state', { cache: 'no-store' });
    if (!response.ok) throw new Error(`Viewer state returned HTTP ${response.status}`);
    const batch = await response.json();
    let live = 0, passed = 0;
    for (const run of batch.runs) {
      const tile = card(run.variant), frame = run.progress?.frame, fixture = frame?.fixture;
      tile.badge.className = `badge ${run.state}`;
      tile.badge.textContent = run.state === 'live' ? 'LIVE · 1fps target' : run.state === 'passed' || run.state === 'complete' ? 'RECORDED · finished' : run.state.toUpperCase();
      if (run.state === 'live') live++;
      if (run.state === 'passed') passed++;
      if (run.image && tile.image.dataset.frame !== run.image) {
        tile.image.src = run.image; tile.image.dataset.frame = run.image;
        tile.image.hidden = false; tile.placeholder.hidden = true;
      }
      tile.metrics.textContent = [
        `Session ${frame?.sessionId ?? run.sessionId ?? '—'} · frame ${frame ? frame.sequence + 1 : '—'}/30`,
        `Theme ${fixture?.theme ?? run.stream?.theme ?? 'pending'} · actual DPI ${fixture?.actualWindowDpi ?? run.stream?.actualWindowDpi ?? '—'}`,
        `Fixture scale ${fixture?.fixtureLayoutScale ?? run.stream?.fixtureLayoutScale ?? '—'}× · capture ${frame?.width ?? '—'}×${frame?.height ?? '—'}`,
        frame ? `Captured ${new Date(frame.at).toLocaleTimeString()} · age ${(Math.max(0, run.ageMs) / 1000).toFixed(1)}s` : 'No frame received',
        run.stream ? `Measured ${run.stream.measuredFps.toFixed(3)} fps · max gap ${run.stream.maximumIntervalMs}ms` : '',
        run.error ?? run.progress?.error ?? frame?.error ?? '',
      ].filter(Boolean).join('\n');
    }
    document.getElementById('summary').textContent = `${live} live · ${passed}/${batch.runs.length} passed`;
    document.getElementById('details').textContent = batch.completedAt
      ? `Run ${batch.state}. Finished ${new Date(batch.completedAt).toLocaleString()}. Tiles retain recorded evidence.`
      : 'Coordinated run. A stale badge means no fresh frame within 2.5 seconds.';
    document.getElementById('error').textContent = '';
  } catch (error) {
    document.getElementById('error').textContent = error.message;
    document.getElementById('summary').textContent = 'DISCONNECTED';
    for (const tile of cards.values()) { tile.badge.className = 'badge stale'; tile.badge.textContent = 'DISCONNECTED · last frame'; }
  } finally { setTimeout(update, 1000); }
}
update();

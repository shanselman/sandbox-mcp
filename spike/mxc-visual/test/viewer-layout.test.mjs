import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const viewer = fileURLToPath(new URL('../viewer/', import.meta.url));
const script = fs.readFileSync(path.join(viewer, 'grid.js'), 'utf8');

class Element {
  constructor(tagName) {
    this.tagName = tagName;
    this.children = [];
    this.dataset = {};
    this.style = { setProperty: (key, value) => { this.style[key] = String(value); } };
  }
  set innerHTML(_) { throw new Error('Agent names must never be rendered as HTML'); }
  append(...children) { for (const child of children) this.insertBefore(child, null); }
  insertBefore(child, reference) {
    child.remove();
    const index = reference ? this.children.indexOf(reference) : this.children.length;
    this.children.splice(index, 0, child);
    child.parent = this;
  }
  remove() {
    if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1);
    this.parent = null;
  }
  removeAttribute(name) { delete this[name]; }
}

function harness() {
  const elements = new Map(['grid', 'subtitle', 'provenance', 'summary', 'details', 'error'].map(id => [id, new Element('div')]));
  const calls = [];
  const context = vm.createContext({
    document: { createElement: tag => new Element(tag), getElementById: id => elements.get(id) },
    fetch: (...args) => { calls.push(args); return new Promise(() => {}); },
    setTimeout: () => { throw new Error('No live polling in a DOM unit test'); },
  });
  vm.runInContext(script, context);
  return { context, elements, calls, tiles: () => elements.get('grid').children };
}

function batch(count, extra = {}) {
  return { state: 'running', runs: Array.from({ length: count }, (_, index) => ({
    id: `agent-${index}`, variant: 'light', name: `Agent ${index + 1}`, state: 'live',
    image: 'synthetic.svg', ageMs: 200,
    progress: { frame: { sequence: index, sessionId: index + 10, at: '2026-11-09T12:00:00Z', width: 640, height: 480,
      fixture: { theme: 'light', actualWindowDpi: 96, fixtureLayoutScale: 1 } } },
  })), ...extra };
}

test('density follows actual tile count and removed sessions do not linger', () => {
  const { context, elements, tiles, calls } = harness();
  for (const [count, density] of [[4, 'standard'], [8, 'compact'], [16, 'dense'], [1, 'standard'], [0, 'standard']]) {
    context.renderBatch(batch(count));
    assert.equal(tiles().length, count);
    assert.equal(elements.get('grid').dataset.density, density);
    assert.equal(elements.get('grid').style['--tile-count'], String(Math.max(1, count)));
  }
  assert.equal(calls[0][0], 'state');
  assert.equal(calls[0][1].cache, 'no-store');
});

test('stable id/requestId keys survive renaming and reordering; names remain text', () => {
  const { context, tiles } = harness();
  const first = batch(2);
  first.runs[0].name = '<img src=x onerror=alert(1)>';
  delete first.runs[1].id;
  first.runs[1].requestId = 'stable-request';
  context.renderBatch(first);
  const [a, b] = tiles();
  a.children[2].open = true;
  assert.equal(a.children[0].children[0].textContent, first.runs[0].name);
  const changed = { ...first, runs: [
    { ...first.runs[1], name: undefined, title: 'Renamed request' },
    { ...first.runs[0], name: 'Renamed agent', variant: 'dark' },
  ] };
  context.renderBatch(changed);
  assert.equal(tiles()[0], b);
  assert.equal(tiles()[1], a);
  assert.equal(a.children[2].open, true);
  assert.equal(a.children[0].children[0].textContent, 'Renamed agent');
  assert.equal(b.children[0].children[0].textContent, 'Renamed request');
  assert.match(a.children[1].children[0].alt, /Renamed agent/);
});

test('legacy variant identity and fixture metrics remain compatible', () => {
  const { context, tiles } = harness();
  const legacy = batch(1);
  delete legacy.runs[0].id;
  delete legacy.runs[0].name;
  context.renderBatch(legacy);
  const tile = tiles()[0];
  context.renderBatch(legacy);
  assert.equal(tiles()[0], tile);
  assert.equal(tile.children[0].children[0].textContent, 'light');
  assert.match(tile.children[2].children[1].textContent, /Theme light · actual DPI 96/);
  assert.match(tile.children[2].children[1].textContent, /Fixture scale 1× · capture 640×480/);
});

test('replay, source failure, stale and recorded indicators survive compact presentation', () => {
  const { context, elements, tiles } = harness();
  const replay = batch(4, { mode: 'replay', state: 'replay-complete', overlapMs: 3000, sourceWarning: 'SOURCE FAILED: preserved evidence only' });
  replay.runs[0].state = 'source-failed';
  replay.runs[0].error = 'Original source stopped';
  replay.runs[0].invalidFrames = ['bad.png'];
  replay.runs[1].state = 'stale';
  replay.runs[2].state = 'replay-complete';
  context.renderBatch(replay);
  assert.equal(elements.get('summary').textContent, 'REPLAY · SOURCE FAILED');
  assert.match(elements.get('details').textContent, /Playback complete/);
  assert.match(tiles()[0].children[0].children[1].textContent, /SOURCE · FAILED/);
  assert.equal(tiles()[0].children[3].hidden, false);
  assert.match(tiles()[0].children[3].textContent, /Original source stopped\nINVALID PRESERVED IMAGES: 1/);
  assert.equal(tiles()[1].children[0].children[1].textContent, 'STALE');
  assert.equal(tiles()[2].children[0].children[1].textContent, 'REPLAY · COMPLETE');
  context.showDisconnected(new Error('Offline'));
  assert.equal(elements.get('summary').textContent, 'REPLAY · SOURCE FAILED · DISCONNECTED');
  assert.match(tiles()[0].children[0].children[1].textContent, /DISCONNECTED/);
  assert.equal(tiles()[0].children[1].children[0].hidden, false);
  const completed = batch(1, { state: 'passed', completedAt: '2026-11-09T12:01:00Z' });
  completed.runs[0].state = 'passed';
  context.renderBatch(completed);
  assert.equal(tiles()[0].children[0].children[1].textContent, 'RECORDED · finished');
  assert.match(elements.get('details').textContent, /Tiles retain recorded evidence/);
  assert.equal(elements.get('error').textContent, '');
});

test('a withdrawn or excluded frame is not left visible as current evidence', () => {
  const { context, tiles } = harness();
  const state = batch(1);
  context.renderBatch(state);
  const [image, placeholder] = tiles()[0].children[1].children;
  assert.equal(image.hidden, false);
  delete state.runs[0].image;
  context.renderBatch(state);
  assert.equal(image.hidden, true);
  assert.equal(placeholder.hidden, false);
  assert.equal(image.src, undefined);
  state.runs[0].image = 'synthetic.svg';
  context.renderBatch(state);
  assert.equal(image.hidden, false);
  assert.equal(placeholder.hidden, true);
});

const browserPath = process.env.VIEWER_TEST_BROWSER ?? [
  process.env['ProgramFiles(x86)'] && path.join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe'),
].find(candidate => candidate && fs.existsSync(candidate));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function connect(url) {
  const socket = new WebSocket(url);
  await once(socket, 'open');
  const pending = new Map();
  let sequence = 0;
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    const request = pending.get(message.id);
    if (!request) return;
    pending.delete(message.id);
    clearTimeout(request.timer);
    if (message.error) request.reject(new Error(JSON.stringify(message.error)));
    else request.resolve(message.result);
  });
  return {
    close: () => socket.close(),
    send(method, params = {}) {
      return new Promise((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Browser command timed out: ${method}`)); }, 5000);
        pending.set(id, { resolve, reject, timer });
        socket.send(JSON.stringify({ id, method, params }));
      });
    },
  };
}

test('real browser: 4/8/16 tiles, narrow viewports, token and system theme precedence', {
  skip: !browserPath && 'Set VIEWER_TEST_BROWSER to an existing Chromium/Edge executable for real layout verification',
  timeout: 60000,
}, async t => {
  // Isolated browser profile stays under this checkout; no packages, native helpers or real sessions.
  const profile = path.join(path.dirname(fileURLToPath(import.meta.url)), `.viewer-layout-${process.pid}-${randomUUID()}`);
  fs.mkdirSync(profile);
  let current = batch(4);
  const server = http.createServer((request, response) => {
    if (request.url === '/state') {
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(current));
    } else if (request.url === '/synthetic.svg') {
      response.setHeader('content-type', 'image/svg+xml');
      response.end('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="480"><rect width="640" height="480" fill="#8090a0"/><text x="20" y="60">SYNTHETIC LAYOUT TEST</text></svg>');
    } else if (['/', '/grid.css', '/grid.js'].includes(request.url)) {
      const file = request.url === '/' ? 'index.html' : request.url.slice(1);
      response.setHeader('content-type', file.endsWith('.css') ? 'text/css' : file.endsWith('.js') ? 'text/javascript' : 'text/html');
      response.end(fs.readFileSync(path.join(viewer, file)));
    } else { response.writeHead(404); response.end(); }
  });
  let child, exited, browser;
  t.after(async () => {
    if (browser) { await browser.send('Browser.close').catch(() => {}); browser.close(); }
    if (child) {
      if (child.exitCode === null) {
        await Promise.race([exited, delay(3000)]);
        if (child.exitCode === null) child.kill();
      }
      await exited;
    }
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const base = `http://127.0.0.1:${server.address().port}/`;
  assert.equal((await fetch(base)).status, 200);
  child = spawn(browserPath, ['--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--disable-background-networking', '--disable-extensions', '--disable-component-update',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
  exited = once(child, 'exit');
  const endpoint = await new Promise((resolve, reject) => {
    let output = '';
    const timer = setTimeout(() => reject(new Error(`Browser readiness timeout: ${output.slice(-2000)}`)), 15000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Browser exited before readiness: ${code}`)); });
    child.stderr.on('data', data => {
      output += data;
      const match = output.match(/DevTools listening on (ws:\/\/\S+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
  });
  const targets = await (await fetch(`http://${new URL(endpoint).host}/json/list`)).json();
  browser = await connect(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  const evaluate = async expression => {
    const result = await browser.send('Runtime.evaluate', { expression, returnByValue: true });
    assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  await browser.send('Page.navigate', { url: base });
  for (let attempts = 0; ; attempts++) {
    if (await evaluate('typeof renderBatch === "function" && document.querySelectorAll("article").length === 4')) break;
    assert.ok(attempts < 50, 'synthetic viewer becomes responsive');
    await delay(100);
  }
  const sizes = [];
  for (const width of [1200, 320, 240]) {
    await browser.send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    for (const count of [4, 8, 16]) {
      current = batch(count);
      await evaluate(`renderBatch(${JSON.stringify(current)})`);
      const layout = await evaluate(`(() => {
        const tiles = [...document.querySelectorAll('article')].map(tile => {
          const box = tile.getBoundingClientRect();
          const picture = tile.querySelector('.picture').getBoundingClientRect();
          const badge = tile.querySelector('.badge').getBoundingClientRect();
          return { left: box.left, right: box.right, top: box.top, bottom: box.bottom, width: box.width,
            pictureHeight: picture.height, badgeHeight: badge.height, evidenceOpen: tile.querySelector('details').open };
        });
        return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
          gridBottom: document.getElementById('grid').getBoundingClientRect().bottom, tiles };
      })()`);
      assert.equal(layout.tiles.length, count);
      assert.ok(layout.scrollWidth <= layout.width, `${count} tiles at ${width}px have no horizontal overflow`);
      for (const tile of layout.tiles) {
        assert.ok(tile.left >= 0 && tile.right <= width, 'tile stays inside viewport');
        assert.ok(tile.pictureHeight > 50 && tile.badgeHeight > 10, 'picture and status stay visible');
        assert.equal(tile.evidenceOpen, false, 'metrics do not consume initial grid space');
      }
      for (let index = 1; index < count; index++) {
        const previous = layout.tiles[index - 1], tile = layout.tiles[index];
        assert.ok(tile.left >= previous.right || tile.top >= previous.bottom, 'tiles do not overlap');
      }
      if (width === 1200) {
        sizes.push(layout.tiles[0].width);
        assert.ok(layout.gridBottom <= 900, `${count} synthetic tiles fit the 1200×900 viewport`);
      }
    }
  }
  assert.ok(sizes[0] > sizes[1] && sizes[1] > sizes[2], 'more tiles start smaller');
  for (const [scheme, expected] of [['light', 'rgb(255, 255, 255)'], ['dark', 'rgb(13, 17, 23)']]) {
    await browser.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: scheme }] });
    assert.equal(await evaluate('getComputedStyle(document.documentElement).backgroundColor'), expected);
  }
  await evaluate('document.documentElement.dataset.colorMode = "light"');
  assert.equal(await evaluate('getComputedStyle(document.documentElement).backgroundColor'), 'rgb(255, 255, 255)');
  await browser.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
  await evaluate('document.documentElement.dataset.colorMode = "dark"');
  assert.equal(await evaluate('getComputedStyle(document.documentElement).backgroundColor'), 'rgb(13, 17, 23)');
  await evaluate(`document.documentElement.style.setProperty('--background-color-default', '#123456');
    document.documentElement.style.setProperty('--text-color-default', '#fedcba');
    document.documentElement.style.setProperty('--border-color-default', '#abcdef');
    document.documentElement.style.setProperty('--text-color-muted', '#987654');
    document.documentElement.style.setProperty('--color-focus-outline', '#654321');`);
  assert.equal(await evaluate('getComputedStyle(document.documentElement).backgroundColor'), 'rgb(18, 52, 86)');
  assert.equal(await evaluate('getComputedStyle(document.documentElement).color'), 'rgb(254, 220, 186)');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("article")).borderTopColor'), 'rgb(171, 205, 239)');
  assert.equal(await evaluate('getComputedStyle(document.querySelector(".metrics")).color'), 'rgb(152, 118, 84)');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("a")).color'), 'rgb(101, 67, 33)');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("img")).objectFit'), 'contain');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("img")).filter'), 'none');

  for (const [modeTarget, tokenTarget] of [['root', 'root'], ['body', 'body'], ['root', 'body'], ['body', 'root']]) {
    await evaluate(`for (const node of [document.documentElement, document.body]) {
      node.removeAttribute('data-color-mode');
      node.removeAttribute('style');
    }`);
    await browser.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
    await evaluate(`(() => {
      const mode = ${modeTarget === 'body' ? 'document.body' : 'document.documentElement'};
      const tokens = ${tokenTarget === 'body' ? 'document.body' : 'document.documentElement'};
      mode.dataset.colorMode = 'light';
      for (const [name, value] of Object.entries({
        '--background-color-default': '#faf8f5', '--text-color-default': '#24292f',
        '--text-color-muted': '#626b75', '--border-color-default': '#c7cdd3',
        '--color-focus-outline': '#0969da', '--font-sans': 'monospace',
      })) tokens.style.setProperty(name, value);
    })()`);
    const palette = await evaluate(`(() => {
      const style = getComputedStyle(document.body);
      return { background: style.backgroundColor, color: style.color, scheme: style.colorScheme,
        font: style.fontFamily, border: getComputedStyle(document.querySelector('article')).borderTopColor,
        muted: getComputedStyle(document.querySelector('.metrics')).color };
    })()`);
    assert.deepEqual(palette, { background: 'rgb(250, 248, 245)', color: 'rgb(36, 41, 47)', scheme: 'light',
      font: 'monospace', border: 'rgb(199, 205, 211)', muted: 'rgb(98, 107, 117)' },
    `app mode on ${modeTarget} and tokens on ${tokenTarget} override the opposite OS theme`);
    await evaluate(`document.querySelector('a').focus()`);
    assert.equal(await evaluate('getComputedStyle(document.activeElement).outlineColor'), 'rgb(9, 105, 218)');
    await evaluate(`(() => {
      const mode = ${modeTarget === 'body' ? 'document.body' : 'document.documentElement'};
      const tokens = ${tokenTarget === 'body' ? 'document.body' : 'document.documentElement'};
      tokens.style.setProperty('--background-color-default', '#20242a');
      mode.dataset.colorMode = 'dark';
    })()`);
    assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'), 'rgb(32, 36, 42)');
    assert.equal(await evaluate('getComputedStyle(document.body).colorScheme'), 'dark');
    await browser.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
    assert.equal(await evaluate('getComputedStyle(document.body).colorScheme'), 'dark', 'OS changes cannot override the app');
    await evaluate(`${tokenTarget === 'body' ? 'document.body' : 'document.documentElement'}.style.setProperty('--background-color-default', '#2b3138')`);
    assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'), 'rgb(43, 49, 56)',
      'token-only updates apply without reloading or changing the mode');
  }
  await evaluate(`document.documentElement.dataset.colorMode = 'light'; document.body.dataset.colorMode = 'dark'`);
  assert.equal(await evaluate('getComputedStyle(document.body).colorScheme'), 'light', 'root host mode takes precedence');
  await evaluate(`for (const node of [document.documentElement, document.body]) {
    node.removeAttribute('data-color-mode'); node.removeAttribute('style');
  }`);
  assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'), 'rgb(255, 255, 255)', 'removing host tokens restores system fallback');
  await browser.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'), 'rgb(13, 17, 23)');
});

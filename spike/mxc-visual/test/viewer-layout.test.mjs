import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { CAMERA_CSP, startCameraServer } from '../camera-server.mjs';
import { pngFixture } from './png-fixture.mjs';

const viewer = fileURLToPath(new URL('../viewer/', import.meta.url));
const script = fs.readFileSync(path.join(viewer, 'grid.js'), 'utf8');

class Element {
  constructor(tagName) {
    this.tagName = tagName;
    this.children = [];
    this.dataset = {};
    this.listeners = {};
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
  addEventListener(name, callback) { this.listeners[name] = callback; if (name !== 'error') this[name] = callback; }
  load() { this.loads = (this.loads ?? 0) + 1; }
  pause() { this.paused = true; }
  play() { this.paused = false; return Promise.resolve(); }
}

function harness() {
  const elements = new Map(['grid', 'subtitle', 'provenance', 'summary', 'details', 'error', 'zoom-in', 'zoom-out', 'zoom-fit', 'zoom-level'].map(id => [id, new Element('div')]));
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

test('zoom is bounded, retained through polling and reset to adaptive sizing', () => {
  const { context, elements } = harness();
  const value = () => elements.get('grid').style['--zoom'];
  assert.equal(value(), '1');
  for (let i = 0; i < 10; i++) elements.get('zoom-in').click();
  assert.equal(value(), '2');
  assert.equal(elements.get('zoom-in').disabled, true);
  context.renderBatch(batch(16));
  assert.equal(value(), '2');
  for (let i = 0; i < 10; i++) elements.get('zoom-out').click();
  assert.equal(value(), '0.75');
  assert.equal(elements.get('zoom-out').disabled, true);
  elements.get('zoom-fit').click();
  assert.equal(value(), '1');
  assert.equal(elements.get('grid').dataset.density, 'dense');
  assert.equal(elements.get('zoom-level').textContent, 'Auto');
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
  completed.runs[0].stream = { measuredFps: 0.95, maximumIntervalMs: 4000,
    timingWarnings: ['Capture gap 4000ms; selected policy reports only'] };
  context.renderBatch(completed);
  assert.equal(tiles()[0].children[3].hidden, false);
  assert.match(tiles()[0].children[3].textContent, /4000ms/);
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

test('live frames switch to one persistent looping video without polling restarting playback', () => {
  const { context, tiles } = harness();
  const state = batch(1);
  context.renderBatch(state);
  const [image, , video] = tiles()[0].children[1].children;
  state.runs[0].image = 'next.png';
  context.renderBatch(state);
  assert.equal(image.src, 'next.png');
  state.runs[0].state = 'passed';
  state.runs[0].recording = { state: 'ready', url: 'light/recording-test.mp4' };
  context.renderBatch(state);
  video.readyState = 4;
  video.loadeddata();
  video.currentTime = 8.5;
  context.renderBatch(state);
  context.renderBatch(state);
  assert.equal(video.loads, 1);
  assert.equal(video.currentTime, 8.5);
  assert.equal(video.loop, true);
  assert.equal(video.muted, true);
  assert.equal(video.controls, true);
  assert.equal(video.hidden, false);
  assert.equal(image.hidden, true);
  assert.equal(tiles()[0].children[0].children[1].textContent, 'RECORDED · loop');
  state.runs[0].state = 'failed';
  state.runs[0].error = 'Original run failed';
  context.renderBatch(state);
  assert.equal(tiles()[0].children[0].children[1].textContent, 'FAILED · recorded loop');
  assert.match(tiles()[0].children[3].textContent, /Original run failed/);
  context.renderBatch(batch(0));
  assert.equal(video.paused, true);
  assert.equal(video.src, undefined);
});

test('encoding and playback errors keep the captured image and explain the failure', () => {
  const { context, tiles } = harness();
  const state = batch(1);
  state.runs[0].state = 'complete';
  state.runs[0].recording = { state: 'error', error: 'FFmpeg is missing' };
  context.renderBatch(state);
  const [image, , video] = tiles()[0].children[1].children;
  assert.equal(image.hidden, false);
  assert.match(tiles()[0].children[4].textContent, /FFmpeg is missing/);
  state.runs[0].recording = { state: 'ready', url: 'light/recording-test.mp4' };
  context.renderBatch(state);
  video.error = { code: 4 };
  video.listeners.error();
  context.renderBatch(state);
  assert.equal(image.hidden, false);
  assert.equal(video.hidden, true);
  assert.match(tiles()[0].children[4].textContent, /media error 4/);
});
test('startup pictures remain visible when automation fails before the measured stream', () => {
  const { context, tiles } = harness();
  const state = batch(1);
  state.runs[0].state = 'preparing';
  state.runs[0].image = 'sample-calculator-01/startup.png';
  state.runs[0].progress = { preview: { file: 'startup.png', at: '2026-11-11T00:00:00Z' } };
  context.renderBatch(state);
  assert.equal(tiles()[0].children[1].children[0].hidden, false);
  assert.match(tiles()[0].children[0].children[1].textContent, /PREPARING/);
  assert.match(tiles()[0].children[2].children[0].textContent, /not a measured frame/);
  state.runs[0].state = 'failed';
  state.runs[0].error = 'UIA readiness failed';
  context.renderBatch(state);
  assert.equal(tiles()[0].children[1].children[0].hidden, false);
  assert.equal(tiles()[0].children[0].children[1].textContent, 'FAILED');
});
test('compact tile labels distinguish requested layout simulation from measured monitor DPI', () => {
  const { context, tiles } = harness();
  const state = batch(1);
  state.runs[0].progress = null;
  state.runs[0].requestedAppearance = { theme: 'Dark', fixtureLayoutScale: 1.75, scaleIsSimulation: true };
  context.renderBatch(state);
  const label = tiles()[0].children[0].children[2];
  assert.match(label.textContent, /Dark · 175% sim · requested/);
  state.runs[0].progress = { frame: { sequence: 0, at: '2026-11-11T00:00:00Z',
    fixture: { ...state.runs[0].requestedAppearance, actualWindowDpi: 96 } } };
  context.renderBatch(state);
  assert.match(label.textContent, /175% sim · 96 DPI/);
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
    response.setHeader('Content-Security-Policy', CAMERA_CSP);
    if (request.url === '/state') {
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(current));
    } else if (request.url === '/synthetic.svg') {
      response.setHeader('content-type', 'image/svg+xml');
      response.end('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="480"><rect width="640" height="480" fill="#8090a0"/><text x="20" y="60">SYNTHETIC LAYOUT TEST</text></svg>');
    } else if (request.url === '/appearance' && request.method === 'POST') {
      request.resume();
      response.setHeader('content-type', 'application/json');
      response.end('{"ok":true}');
    } else if (request.url === '/playback' && request.method === 'POST') {
      request.resume();
      response.setHeader('content-type', 'application/json');
      response.end('{"ok":true}');
    } else if (['/', '/grid.css', '/grid.js', '/appearance.js'].includes(request.url)) {
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
  await browser.send('Emulation.setDeviceMetricsOverride', { width: 1200, height: 900, deviceScaleFactor: 1, mobile: false });
  const widthAt = () => evaluate('document.querySelector("article").getBoundingClientRect().width');
  const autoWidth = await widthAt();
  await evaluate('document.getElementById("zoom-in").click(); document.getElementById("zoom-in").click()');
  assert.ok(await widthAt() > autoWidth, 'larger tiles actually grow');
  for (const width of [320, 240]) {
    await browser.send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'), 'zoom does not overflow a narrow panel');
  }
  await evaluate('document.getElementById("zoom-fit").click()');
  assert.equal(await evaluate('document.getElementById("zoom-level").textContent'), 'Auto');
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
  await evaluate(`(() => {
    const theme = document.createElement('style'); theme.id = 'host-theme-test';
    theme.textContent = ':root, body { --background-color-default: #243648; --text-color-default: #f1f3f5; }';
    document.head.append(theme);
  })()`);
  assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'), 'rgb(36, 54, 72)',
    'the actual CSP permits the canvas host inline theme stylesheet');
  assert.equal(await evaluate('getComputedStyle(document.body).color'), 'rgb(241, 243, 245)');
  await evaluate(`(() => {
    const inline = document.createElement('script');
    inline.textContent = 'window.inlineScriptUnexpectedlyRan = true';
    document.head.append(inline);
  })()`);
  assert.equal(await evaluate('window.inlineScriptUnexpectedlyRan === true'), false, 'inline scripts remain blocked');

  await t.test('real video: live-to-recorded transition, autoplay, loops, polling and pause', {
    skip: spawnSync('ffmpeg', ['-version'], { windowsHide: true }).status !== 0 && 'FFmpeg is required for real video verification',
  }, async () => {
    const root = path.join(profile, 'camera-video');
    const directory = path.join(root, 'light');
    fs.mkdirSync(directory, { recursive: true });
    const start = Date.now() - 650;
    const frames = [0, 200, 650].map((time, sequence) => {
      const frame = { sequence, file: `frame-00${sequence}.png`, at: new Date(start + time).toISOString(),
        elapsedMs: time + 10, captureMs: 10, sessionId: 20, pid: 40, width: 320, height: 240, actualWindowDpi: 96 };
      fs.writeFileSync(path.join(directory, frame.file), pngFixture(sequence * 80, 320, 240));
      return frame;
    });
    fs.writeFileSync(path.join(directory, 'observed-frames.json'), JSON.stringify(frames));
    fs.writeFileSync(path.join(directory, 'progress.json'), JSON.stringify({ state: 'live', frame: frames.at(-1) }));
    const source = { schema: 1, mode: 'concurrent', state: 'running', runs: [
      { variant: 'light', name: 'Synthetic video test', state: 'running', sessionId: 20 },
    ] };
    fs.writeFileSync(path.join(root, 'batch.json'), JSON.stringify(source));
    const camera = await startCameraServer(root);
    t.after(() => camera.close());
    await browser.send('Page.navigate', { url: camera.url });
    for (let attempt = 0; ; attempt++) {
      if (await evaluate(`location.href === ${JSON.stringify(camera.url)} && document.querySelectorAll('article').length === 1`)) break;
      assert.ok(attempt < 50, 'real camera server becomes visible');
      await delay(100);
    }
    await evaluate(`window.videoStarts = 0;
      document.querySelector('video').addEventListener('loadstart', () => window.videoStarts++);`);
    assert.equal(await evaluate('document.querySelector("img").hidden'), false);
    fs.writeFileSync(path.join(root, 'batch-finished.json'), JSON.stringify({
      ...source, state: 'passed', completedAt: new Date().toISOString(),
      runs: [{ ...source.runs[0], state: 'passed' }],
    }));
    for (let attempt = 0; ; attempt++) {
      if (await evaluate('document.querySelector("video").readyState >= 2 && !document.querySelector("video").paused')) break;
      assert.ok(attempt < 50, JSON.stringify(await evaluate(`({ error: document.querySelector('video').error?.code,
        note: document.querySelector('.playback-note').textContent })`)));
      await delay(100);
    }
    for (let attempt = 0; ; attempt++) {
      if (await evaluate('[...cards.values()][0].loops >= 2')) break;
      assert.ok(attempt < 100, 'recorded video wraps twice within ten seconds');
      await delay(100);
    }
    const playback = await evaluate(`(() => {
      const tile = [...cards.values()][0], video = tile.video;
      return { starts: window.videoStarts, time: video.currentTime, duration: video.duration,
        loops: tile.loops, paused: video.paused, hidden: video.hidden, imageHidden: tile.image.hidden,
        loop: video.loop, muted: video.muted, controls: video.controls, badge: tile.badge.textContent };
    })()`);
    assert.equal(playback.starts, 1, 'state polling does not reload the video');
    assert.ok(playback.loops >= 2, JSON.stringify(playback));
    assert.ok(playback.time > 0 && playback.time < playback.duration);
    assert.equal(playback.paused, false);
    assert.equal(playback.hidden, false);
    assert.equal(playback.imageHidden, true);
    assert.equal(playback.loop, true);
    assert.equal(playback.muted, true);
    assert.equal(playback.controls, true);
    assert.equal(playback.badge, 'RECORDED · loop');
    await evaluate('document.querySelector("video").pause(); document.querySelector("video").currentTime = 0.4');
    await delay(1300);
    assert.equal(await evaluate('document.querySelector("video").paused'), true, 'polling respects a user pause');
    const pausedAt = await evaluate('document.querySelector("video").currentTime');
    assert.ok(Math.abs(pausedAt - 0.4) < 0.02, `Paused seek position: ${pausedAt}`);
    if (process.env.VIEWER_TEST_SCREENSHOT) {
      const screenshot = await browser.send('Page.captureScreenshot', { format: 'png' });
      fs.writeFileSync(process.env.VIEWER_TEST_SCREENSHOT, Buffer.from(screenshot.data, 'base64'));
    }
  });
});

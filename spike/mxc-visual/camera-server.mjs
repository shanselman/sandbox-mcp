import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { readProgress, readBatch } from './live-evidence.mjs';
import { cameraState, replayState } from './viewer-state.mjs';
import { cameraRecordings } from './camera-recording.mjs';
import { isVariant, VARIANT_PATTERN } from './visual-workloads.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const videoPattern = new RegExp(`^${VARIANT_PATTERN}/recording-[a-f0-9]{64}\\.mp4$`);
const imagePattern = new RegExp(`^${VARIANT_PATTERN}/(startup|frame-\\d{3}|1-before-printwindow-2|2-after-printwindow-2)\\.png$`);
// The canvas host mirrors its theme using inline style elements, not scripts.
export const CAMERA_CSP = "default-src 'self'; script-src 'self'; style-src 'self'; style-src-elem 'self' 'unsafe-inline'; img-src 'self'; media-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'";
export async function startCameraServer(batchPath) {
  const root = path.resolve(batchPath);
  if (!fs.existsSync(path.join(root, 'batch.json'))) throw new Error('Expected a prepared batch directory');
  const token = randomBytes(24).toString('hex');
  const playbackStartedAt = Date.now();
  let origin;
  let appearance;
  let playback;
  const recording = cameraRecordings(root);
  const requests = { documents: 0, states: 0, appearances: 0 };
  const state = () => {
    const batch = readBatch(root);
    if (batch.mode === 'replay') {
      const replay = replayState(batch, Date.now(), playbackStartedAt);
      return { ...replay, runs: replay.runs.map((run, index) => ({
        ...run, recording: recording(batch.runs[index], null, batch),
      })), serverTime: new Date().toISOString() };
    }
    const runs = batch.runs.map(run => {
      if (!isVariant(run.variant)) throw new Error('Unexpected variant');
      const progress = readProgress(path.join(root, run.variant));
      return { ...cameraState(batch, run, progress, Date.now()), recording: recording(run, progress, batch) };
    });
    return { ...batch, runs, serverTime: new Date().toISOString() };
  };
  const server = http.createServer(async (request, response) => {
    const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
      'Content-Security-Policy': CAMERA_CSP };
    const send = (status, type, body) => { response.writeHead(status, { ...headers, 'Content-Type': type }); response.end(body); };
    try {
      if (request.headers.host !== new URL(origin).host || request.headers.origin && request.headers.origin !== origin)
        return send(403, 'text/plain', 'Forbidden');
      const url = new URL(request.url, origin);
      const prefix = `/${token}/`;
      if (!url.pathname.startsWith(prefix)) return send(404, 'text/plain', 'Not found');
      const relative = url.pathname.slice(prefix.length);
      if (relative === 'playback' && request.method === 'POST') {
        if (request.headers['content-type'] !== 'application/json') return send(415, 'text/plain', 'Expected JSON');
        let body = '';
        for await (const chunk of request) {
          body += chunk;
          if (Buffer.byteLength(body) > 16384) { send(413, 'text/plain', 'Playback readback is too large'); request.destroy(); return; }
        }
        let value;
        try { value = JSON.parse(body); } catch { return send(400, 'text/plain', 'Invalid playback JSON'); }
        if (!Array.isArray(value) || value.length > 16 || value.some(tile =>
          !tile || Object.keys(tile).sort().join() !== 'currentTime,duration,error,loops,paused,readyState,variant'
          || !isVariant(tile.variant)
          || ![tile.currentTime, tile.duration].every(number => Number.isFinite(number) && number >= 0 && number <= 661)
          || !Number.isInteger(tile.loops) || tile.loops < 0
          || !Number.isInteger(tile.readyState) || tile.readyState < 0 || tile.readyState > 4
          || typeof tile.paused !== 'boolean' || typeof tile.error !== 'string' || tile.error.length > 500))
          return send(400, 'text/plain', 'Invalid playback readback');
        playback = { tiles: value, observedAt: new Date().toISOString() };
        return send(200, 'application/json', '{"ok":true}');
      }
      if (relative === 'appearance' && request.method === 'POST') {
        if (request.headers['content-type'] !== 'application/json') return send(415, 'text/plain', 'Expected JSON');
        let bytes = 0;
        const chunks = [];
        for await (const chunk of request) {
          bytes += chunk.length;
          if (bytes > 4096) { send(413, 'text/plain', 'Theme readback is too large'); request.destroy(); return; }
          chunks.push(chunk);
        }
        let value;
        try { value = JSON.parse(Buffer.concat(chunks).toString('utf8')); }
        catch { return send(400, 'text/plain', 'Invalid theme readback JSON'); }
        if (!value || !['host', 'system'].includes(value.source)
          || !(['light', 'dark'].includes(value.mode) || value.source === 'host' && value.mode === null)
          || Object.keys(value).sort().join() !== 'background,backgroundToken,cspDirectives,font,foreground,mode,source'
          || !Array.isArray(value.cspDirectives) || value.cspDirectives.length > 8
          || value.cspDirectives.some(directive => typeof directive !== 'string' || !/^[a-z-]{1,80}$/.test(directive))
          || ['background', 'foreground', 'backgroundToken', 'font'].some(key => typeof value[key] !== 'string' || value[key].length > 256))
          return send(400, 'text/plain', 'Invalid theme readback');
        appearance = { ...value, observedAt: new Date().toISOString() };
        requests.appearances++;
        return send(200, 'application/json', '{"ok":true}');
      }
      const videoRoute = videoPattern.test(relative);
      if (request.method !== 'GET' && !(request.method === 'HEAD' && videoRoute)) return send(403, 'text/plain', 'Forbidden');
      if (videoRoute) {
        const directory = path.join(root, relative.split('/')[0]);
        if (fs.lstatSync(directory).isSymbolicLink()) throw new Error('Invalid recording directory');
        const file = path.join(root, ...relative.split('/'));
        if (!fs.existsSync(file)) return send(404, 'text/plain', 'Recording not available');
        const stat = fs.lstatSync(file);
        if (stat.isSymbolicLink() || !stat.isFile() || stat.size > 64 * 1024 * 1024) throw new Error('Invalid recording');
        let start = 0, end = stat.size - 1;
        const range = request.headers.range;
        if (range) {
          const match = /^bytes=(\d*)-(\d*)$/.exec(range);
          if (match && (match[1] || match[2])) {
            start = match[1] ? Number(match[1]) : Math.max(0, stat.size - Number(match[2]));
            end = match[1] && match[2] ? Math.min(Number(match[2]), end) : end;
          }
          if (!match || !(match[1] || match[2]) || !Number.isSafeInteger(start) || !Number.isSafeInteger(end)
            || start < 0 || start > end || start >= stat.size) {
            response.writeHead(416, { ...headers, 'Content-Range': `bytes */${stat.size}` }); response.end(); return;
          }
        }
        response.writeHead(range ? 206 : 200, { ...headers, 'Content-Type': 'video/mp4',
          'Accept-Ranges': 'bytes', 'Content-Length': end - start + 1,
          ...(range ? { 'Content-Range': `bytes ${start}-${end}/${stat.size}` } : {}) });
        if (request.method === 'HEAD') { response.end(); return; }
        const stream = fs.createReadStream(file, { start, end });
        stream.on('error', error => response.destroy(error));
        response.on('close', () => stream.destroy());
        stream.pipe(response);
        return;
      }
      if (relative === '') {
        requests.documents++;
        return send(200, 'text/html; charset=utf-8', fs.readFileSync(path.join(here, 'viewer', 'index.html')));
      }
      if (['grid.js', 'grid.css', 'appearance.js'].includes(relative))
        return send(200, relative.endsWith('.js') ? 'text/javascript; charset=utf-8' : 'text/css; charset=utf-8', fs.readFileSync(path.join(here, 'viewer', relative)));
      if (relative === 'state') {
        requests.states++;
        return send(200, 'application/json', JSON.stringify(state()));
      }
      if (relative === 'report.md' || relative === 'report.json') {
        const file = path.join(root, relative);
        return fs.existsSync(file) ? send(200, relative.endsWith('.json') ? 'application/json' : 'text/plain; charset=utf-8', fs.readFileSync(file))
          : send(404, 'text/plain', 'Report is not finished');
      }
      if (imagePattern.test(relative)) {
        const file = path.join(root, ...relative.split('/'));
        if (!fs.existsSync(file)) return send(404, 'text/plain', 'Frame not yet available');
        const stat = fs.lstatSync(file);
        if (stat.isSymbolicLink() || !stat.isFile() || stat.size > 16 * 1024 * 1024) throw new Error('Invalid image');
        return send(200, 'image/png', fs.readFileSync(file));
      }
      send(404, 'text/plain', 'Not found');
    } catch (error) { send(500, 'application/json', JSON.stringify({ error: error.message })); }
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  origin = `http://127.0.0.1:${server.address().port}`;
  let closing;
  return {
    url: `${origin}/${token}/`, root, state, appearance: () => appearance, playback: () => playback, requests: () => ({ ...requests }),
    close: () => closing ??= new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

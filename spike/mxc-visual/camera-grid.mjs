import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { atomicJson } from './broker-client-state.mjs';
import { readProgress, readBatch } from './live-evidence.mjs';
import { cameraState, replayState } from './viewer-state.mjs';

const root = path.resolve(process.argv[2] ?? '');
if (process.argv.length !== 3 || !fs.existsSync(path.join(root, 'batch.json'))) throw new Error('Expected a prepared batch directory');
const here = path.dirname(fileURLToPath(import.meta.url));
const token = randomBytes(24).toString('hex');
const playbackStartedAt = Date.now();
let origin;
const server = http.createServer((request, response) => {
  const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'" };
  const send = (status, type, body) => { response.writeHead(status, { ...headers, 'Content-Type': type }); response.end(body); };
  try {
    if (request.headers.host !== new URL(origin).host || request.method !== 'GET'
      || request.headers.origin && request.headers.origin !== origin)
      return send(403, 'text/plain', 'Forbidden');
    const url = new URL(request.url, origin);
    const prefix = `/${token}/`;
    if (!url.pathname.startsWith(prefix)) return send(404, 'text/plain', 'Not found');
    const relative = url.pathname.slice(prefix.length);
    if (relative === '') return send(200, 'text/html; charset=utf-8', fs.readFileSync(path.join(here, 'viewer', 'index.html')));
    if (['grid.js', 'grid.css'].includes(relative))
      return send(200, relative.endsWith('.js') ? 'text/javascript; charset=utf-8' : 'text/css; charset=utf-8', fs.readFileSync(path.join(here, 'viewer', relative)));
    if (relative === 'state') {
      const batch = readBatch(root);
      if (batch.mode === 'replay') {
        const replay = replayState(batch, Date.now(), playbackStartedAt);
        return send(200, 'application/json', JSON.stringify({ ...replay, serverTime: new Date().toISOString() }));
      }
      const runs = batch.runs.map(run => {
        if (!['light', 'dark', 'dark-large'].includes(run.variant)) throw new Error('Unexpected variant');
        const progress = readProgress(path.join(root, run.variant));
        return cameraState(batch, run, progress, Date.now());
      });
      return send(200, 'application/json', JSON.stringify({ ...batch, runs, serverTime: new Date().toISOString() }));
    }
    if (relative === 'report.md' || relative === 'report.json') {
      const file = path.join(root, relative);
      return fs.existsSync(file) ? send(200, relative.endsWith('.json') ? 'application/json' : 'text/plain; charset=utf-8', fs.readFileSync(file))
        : send(404, 'text/plain', 'Report is not finished');
    }
    if (/^(light|dark|dark-large)\/(frame-\d{3}|1-before-printwindow-2|2-after-printwindow-2)\.png$/.test(relative)) {
      const file = path.join(root, ...relative.split('/'));
      if (!fs.existsSync(file)) return send(404, 'text/plain', 'Frame not yet available');
      const stat = fs.lstatSync(file);
      if (stat.isSymbolicLink() || !stat.isFile() || stat.size > 16 * 1024 * 1024) throw new Error('Invalid image');
      return send(200, 'image/png', fs.readFileSync(file));
    }
    send(404, 'text/plain', 'Not found');
  } catch (error) { send(500, 'application/json', JSON.stringify({ error: error.message })); }
});
server.listen(0, '127.0.0.1', () => {
  origin = `http://127.0.0.1:${server.address().port}`;
  const url = `${origin}/${token}/`;
  atomicJson(path.join(root, 'viewer.json'), { url, pid: process.pid, loopbackOnly: true, readOnly: true });
  console.log(JSON.stringify({ url, pid: process.pid, root }));
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close(() => process.exit(0)));

// Stages 1-3: one IsolationSession, launch an app + in-session helper, capture,
// drive a state change via in-session UIA, recapture, copy evidence out, clean up.
// Usage: node winui.mjs [appDir] [exeName] [label]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { withSession, log } from './session.mjs';
import { atomicJson } from './broker-client-state.mjs';

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1'));
const appDir = path.resolve(process.argv[2] ?? path.join(here, '../../samples/WinUI/bin/unpackaged'));
const exe = process.argv[3] ?? 'HelloWinUI.exe';
const label = process.argv[4] ?? 'winui';
const uiaArgs = process.argv[5] ?? '--set NameBox=Farm --invoke GreetButton,IncrementButton,IncrementButton';
const helperDir = path.join(here, 'bin/helper');
const runId = `${label}-${new Date().toISOString().replace(/[:.]/g, '-')}`;
const evidence = path.join(here, 'evidence', runId);
fs.mkdirSync(evidence, { recursive: true });

const robocopy = (src, dst) => {
  try { execFileSync('robocopy', [src, dst, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1']); }
  catch (e) { if (e.status >= 8) throw e; }
};
const json = (s) => { try { return JSON.parse(s); } catch { return { raw: s }; } };

const rec = await withSession(runId, async ({ metadata, run, spawn, rec }) => {
  const ws = metadata.ephemeralWorkspacePath;
  const r = { steps: {} };
  let t = Date.now();
  robocopy(appDir, path.join(ws, 'app'));
  robocopy(helperDir, path.join(ws, 'helper'));
  r.steps.stageMs = Date.now() - t;
  log('staged into', ws, r.steps.stageMs, 'ms');

  // Commands go through cmd /c, which mangles multiple quoted args; workspace paths have no spaces.
  const helper = path.join(ws, 'helper', 'CaptureHelper.exe');
  const outDir = path.join(ws, 'out');
  const name = path.parse(exe).name;

  t = Date.now();
  const app = await spawn(`"${path.join(ws, 'app', exe)}"`, { workingDirectory: path.join(ws, 'app') });
  r.steps.spawnId = app.id;
  log('spawned app id', app.id);

  try {
    const cap = async (tag) => {
      const x = await run(`${helper} capture --name ${name} --out ${outDir} --label ${tag} --wait 30000 --settle 1500`, 90000);
      log('capture', tag, 'exit', x.exitCode);
      return { exitCode: x.exitCode, ...json(x.stdout), stderr: x.stderr };
    };
    r.before = await cap('1-before');
    r.firstFrameMs = Date.now() - t;

    const u = await run(`${helper} uia --name ${name} ${uiaArgs}`, 60000);
    r.uia = { exitCode: u.exitCode, ...json(u.stdout), stderr: u.stderr };
    log('uia exit', u.exitCode, JSON.stringify(r.uia.after ?? r.uia.error));

    r.after = await cap('2-after');
    r.windows = json((await run(`${helper} windows`, 30000)).stdout);
  } finally {
    try { app.kill(); } catch (error) { r.appKillError = error.message; }
    try { r.appExit = await Promise.race([app.wait(), new Promise((res) => setTimeout(() => res('wait-timeout'), 10000))]); } catch (e) { r.appExit = e?.message; }
    if (fs.existsSync(outDir)) robocopy(outDir, evidence);
  }
  return r;
}, record => atomicJson(path.join(evidence, 'record.json'), record));

fs.writeFileSync(path.join(evidence, 'record.json'), JSON.stringify(rec, null, 2));
const summary = (c) => c?.captures?.map((x) => `${x.method}: ${x.size ?? ''} ${JSON.stringify(x.stats ?? x.error)}`);
console.log(JSON.stringify({
  evidence, error: rec.error, cleanup: rec.cleanup, cleanupResults: rec.cleanupResults,
  session: rec.result?.before?.sessionId, screen: rec.result?.before?.screen, desktop: rec.result?.before?.inputDesktop,
  firstFrameMs: rec.result?.firstFrameMs,
  before: summary(rec.result?.before), uia: rec.result?.uia?.after ?? rec.result?.uia?.error,
  uiaCount: rec.result?.uia?.uiaDescendants, after: summary(rec.result?.after),
}, null, 2));
if (rec.error || rec.checkpointErrors?.length || Object.values(rec.cleanup).some(value => value !== 'ok')
  || rec.result?.before?.exitCode !== 0 || rec.result?.uia?.exitCode !== 0 || rec.result?.after?.exitCode !== 0)
  process.exitCode = 1;

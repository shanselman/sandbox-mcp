// Provision/remove the agent account through MXC; only session creation uses WTS.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { provisionContainer, deprovisionContainer } from '@microsoft/mxc-sdk/v1';
import { ISO_REQUEST } from './session.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const kind = process.argv[2] ?? 'full';
if (!['full', 'utility'].includes(kind)) throw new Error('Expected full or utility');
const parent = process.argv[3] ?? 'caller';
if (!['caller', 'console'].includes(parent)) throw new Error('Expected caller or console');
const authentication = process.argv[4] ?? 'name-only';
if (!['name-only', 'local-credential'].includes(authentication)) throw new Error('Expected name-only or local-credential');
const output = path.join(here, 'evidence', `wts-mxc-${kind}-${new Date().toISOString().replace(/[:.]/g, '-')}`);
fs.mkdirSync(output, { recursive: true });
const record = { at: new Date().toISOString(), kind, parent, authentication, cleanup: {} };
let containerId;
let workspace;
try {
  const provision = await provisionContainer(ISO_REQUEST);
  containerId = provision.containerId;
  record.metadata = provision.metadata;
  workspace = provision.metadata.ephemeralWorkspacePath;
  fs.cpSync(path.join(here, 'bin', 'headless'), path.join(workspace, 'headless'), { recursive: true });
  fs.cpSync(path.join(here, 'bin', 'helper'), path.join(workspace, 'helper'), { recursive: true });
  fs.cpSync(path.resolve(here, '..', '..', 'samples', 'WinUI', 'bin', 'unpackaged'), path.join(workspace, 'app'), { recursive: true });
  const result = spawnSync(path.join(workspace, 'headless', 'HeadlessSpike.exe'), [
    'run', kind, parent, path.join(workspace, 'out'),
    path.join(workspace, 'app', 'HelloWinUI.exe'),
    path.join(workspace, 'helper', 'CaptureHelper.exe'),
    `agent-user=${provision.metadata.agentUserName}`,
    ...(authentication === 'local-credential' ? ['local-credential'] : []),
  ], { encoding: 'utf8' });
  // Do not impose a parent timeout that could kill the host before its WTS cleanup.
  record.host = { status: result.status, stdout: result.stdout, stderr: result.stderr };
  if (result.error) throw result.error;
  if (result.status !== 0) process.exitCode = 1;
} catch (error) {
  record.error = { code: error.code, message: error.message };
  process.exitCode = 1;
} finally {
  if (workspace && fs.existsSync(path.join(workspace, 'out'))) {
    try { fs.cpSync(path.join(workspace, 'out'), path.join(output, 'out'), { recursive: true }); }
    catch (error) { record.evidenceCopyError = error.message; process.exitCode = 1; }
  }
  if (containerId) {
    try { await deprovisionContainer(containerId); record.cleanup.deprovision = 'ok'; }
    catch (error) { record.cleanup.deprovision = { code: error.code, message: error.message }; process.exitCode = 1; }
  }
  fs.writeFileSync(path.join(output, 'record.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify({ output, ...record }, null, 2));
}

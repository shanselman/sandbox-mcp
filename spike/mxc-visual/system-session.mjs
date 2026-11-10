// Explicitly approved, one-shot System service experiment; not a production backend.
import fs from 'node:fs';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { provisionContainer, deprovisionContainer } from '@microsoft/mxc-sdk/v1';
import { ISO_REQUEST } from './session.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const executable = path.join(here, 'bin', 'headless', 'HeadlessSpike.exe');
const visual = process.argv[2] === '--visual';
if (process.argv.length > 3 || (process.argv[2] && !visual)) throw new Error('Expected no arguments or --visual');
const output = path.join(here, 'evidence', `system-full-${new Date().toISOString().replace(/[:.]/g, '-')}`);
const serviceName = `SbxFullProbe_${randomBytes(6).toString('hex')}`;
fs.mkdirSync(output, { recursive: false });
const record = { at: new Date().toISOString(), output, serviceName, visual, cleanup: {} };
let containerId;
let workspace;
let serviceMayExist = false;
try {
  const provision = await provisionContainer(ISO_REQUEST);
  containerId = provision.containerId;
  record.metadata = provision.metadata;
  workspace = provision.metadata.ephemeralWorkspacePath;
  record.containerId = containerId;
  fs.writeFileSync(path.join(output, 'record.json'), JSON.stringify(record, null, 2));
  if (visual) {
    fs.cpSync(path.join(here, 'bin', 'headless'), path.join(workspace, 'headless'), { recursive: true });
    fs.cpSync(path.join(here, 'bin', 'helper'), path.join(workspace, 'helper'), { recursive: true });
    fs.cpSync(path.resolve(here, '..', '..', 'samples', 'WinUI', 'bin', 'unpackaged'), path.join(workspace, 'app'), { recursive: true });
    fs.mkdirSync(path.join(workspace, 'visual'));
  }
  const job = path.join(output, 'job.json');
  fs.writeFileSync(job, JSON.stringify({
    ServiceName: serviceName,
    AgentName: provision.metadata.agentUserName,
    AgentSid: provision.metadata.agentUserSid,
    OwnerSid: null,
    Output: output,
    Visual: visual,
    Workspace: visual ? workspace : null,
  }, null, 2));
  serviceMayExist = true;
  const result = spawnSync(executable, ['system-control', job], { encoding: 'utf8' });
  record.controller = { status: result.status, stdout: result.stdout, stderr: result.stderr };
  if (result.error) throw result.error;
  if (result.status !== 0) process.exitCode = 1;
  const probe = path.join(output, 'system-probe.json');
  if (fs.existsSync(probe)) {
    record.probe = JSON.parse(fs.readFileSync(probe, 'utf8'));
    if (record.probe.exitCode !== 0) process.exitCode = 1;
  } else {
    throw new Error('System probe did not produce its report; inspect service-install.json and service-failure.json');
  }
} catch (error) {
  record.error = { code: error.code, message: error.message };
  process.exitCode = 1;
} finally {
  if (serviceMayExist) {
    const status = spawnSync(executable, ['system-status', serviceName], { encoding: 'utf8' });
    if (!status.error && status.status === 0) {
      try {
        record.cleanup.service = JSON.parse(status.stdout);
        serviceMayExist = record.cleanup.service.exists !== false;
      } catch (error) {
        record.cleanup.service = { error: error.message };
      }
      if (visual && workspace && fs.existsSync(path.join(workspace, 'visual'))) {
        try { fs.cpSync(path.join(workspace, 'visual'), path.join(output, 'visual'), { recursive: true }); }
        catch (error) { record.evidenceCopyError = error.message; process.exitCode = 1; }
      }
    } else {
      record.cleanup.service = { error: status.error?.message ?? status.stderr, status: status.status };
    }
  }
  if (serviceMayExist) {
    record.cleanup.deprovision = 'retained: service state is not confirmed absent; do not retry provisioning';
    process.exitCode = 1;
  } else if (containerId) {
    try { await deprovisionContainer(containerId); record.cleanup.deprovision = 'ok'; }
    catch (error) { record.cleanup.deprovision = { code: error.code, message: error.message }; process.exitCode = 1; }
  }
  fs.writeFileSync(path.join(output, 'record.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify(record, null, 2));
}

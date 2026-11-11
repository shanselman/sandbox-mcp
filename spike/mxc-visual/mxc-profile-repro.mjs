// One SDK-only session; read-only host inventory. Does not delete leftover profiles.
import { execFileSync } from 'node:child_process';
import {
  provisionContainer, startContainer, runInContainer,
  stopContainer, deprovisionContainer,
} from '@microsoft/mxc-sdk/v1';

const profiles = () => JSON.parse(execFileSync('powershell.exe', [
  '-NoProfile', '-NonInteractive', '-Command',
  '$ErrorActionPreference="Stop"; ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_UserProfile | Select-Object SID,LocalPath,Loaded)',
], { encoding: 'utf8' }));

const before = new Set(profiles().map(profile => profile.SID));
const provision = await provisionContainer({
  containment: 'isolation_session',
  network: {
    egress: { default: 'allow' },
    ingress: { default: 'allow', hostLoopback: 'allow' },
  },
});
console.log('provision', JSON.stringify(provision));
try {
  console.log('start', JSON.stringify(await startContainer(provision.containerId)));
  const result = await runInContainer(provision.containerId, { command: 'whoami', timeoutMs: 10000 });
  console.log('exec', JSON.stringify(result));
  if (result.exitCode !== 0) throw new Error('whoami failed');
} finally {
  try {
    console.log('stop', JSON.stringify(await stopContainer(provision.containerId)));
  } finally {
    console.log('deprovision', JSON.stringify(await deprovisionContainer(provision.containerId)));
  }
}
const remaining = profiles().filter(profile => !before.has(profile.SID));
console.log('newProfilesAfterDeprovision', JSON.stringify(remaining));
if (remaining.length) process.exitCode = 1;

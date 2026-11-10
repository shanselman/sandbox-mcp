// Stage 0: report host + MXC backend availability. Read-only.
import os from 'node:os';
import { createRequire } from 'node:module';
import { getPlatformSupport, getAvailableBackends, probe } from '@microsoft/mxc-sdk/v1';

const require = createRequire(import.meta.url);
const out = {
  at: new Date().toISOString(),
  host: os.hostname(),
  os: `${os.type()} ${os.release()} ${os.arch()}`,
  node: process.version,
  mxcSdk: require('@microsoft/mxc-sdk/package.json').version,
};

async function attempt(name, fn) {
  try { out[name] = await fn(); }
  catch (e) { out[name] = { error: e?.code ?? e?.name, message: e?.message }; }
}

await attempt('platformSupport', () => getPlatformSupport());
await attempt('availableBackends', () => getAvailableBackends());
await attempt('probe', () => probe());

console.log(JSON.stringify(out, null, 2));

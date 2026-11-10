// Shared helpers: own one IsolationSession and always clean it up.
import {
  provisionContainer, startContainer, runInContainer, spawnInContainer,
  stopContainer, deprovisionContainer,
} from '@microsoft/mxc-sdk/v1';

export const ISO_REQUEST = {
  containment: 'isolation_session',
  network: {
    egress: { default: 'allow' },
    ingress: { default: 'allow', hostLoopback: 'allow' },
  },
};

const t0 = Date.now();
export const log = (...a) => console.error(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...a);

export async function withSession(label, fn) {
  const rec = { label, provisionedAt: new Date().toISOString(), cleanup: {} };
  let containerId;
  try {
    const p = await provisionContainer(ISO_REQUEST, {});
    containerId = p.containerId;
    rec.metadata = p.metadata;
    log(label, 'provisioned', p.metadata?.agentUserName, p.metadata?.ephemeralWorkspacePath);
    await startContainer(containerId);
    log(label, 'started');
    const run = (command, timeoutMs = 60000, extra = {}) =>
      runInContainer(containerId, { command, timeoutMs, ...extra });
    const spawn = (command, extra = {}) => spawnInContainer(containerId, { command, ...extra });
    rec.result = await fn({ containerId, metadata: p.metadata, run, spawn, rec });
  } catch (e) {
    rec.error = { code: e?.code, message: e?.message };
    log(label, 'ERROR', e?.code, e?.message);
  } finally {
    if (containerId) {
      for (const [k, f] of [['stop', stopContainer], ['deprovision', deprovisionContainer]]) {
        try { await f(containerId); rec.cleanup[k] = 'ok'; }
        catch (e) { rec.cleanup[k] = `${e?.code}: ${e?.message}`; }
      }
      log(label, 'cleanup', JSON.stringify(rec.cleanup));
    }
  }
  return rec;
}

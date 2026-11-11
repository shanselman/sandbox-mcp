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
const sdkLifecycle = { provisionContainer, startContainer, runInContainer, spawnInContainer, stopContainer, deprovisionContainer };

export async function withSession(label, fn, checkpoint = () => {}, lifecycle = sdkLifecycle) {
  const rec = { label, provisionedAt: new Date().toISOString(), phase: 'provisioning', cleanup: {}, cleanupResults: {} };
  let containerId;
  try {
    checkpoint(rec);
    const p = await lifecycle.provisionContainer(ISO_REQUEST, {});
    containerId = p.containerId;
    rec.containerId = containerId;
    rec.metadata = p.metadata;
    rec.provisionWarnings = p.warnings;
    rec.phase = 'provisioned';
    checkpoint(rec);
    log(label, 'provisioned', p.metadata?.agentUserName, p.metadata?.ephemeralWorkspacePath);
    rec.startResult = await lifecycle.startContainer(containerId);
    rec.phase = 'started';
    checkpoint(rec);
    log(label, 'started');
    const run = (command, timeoutMs = 60000, extra = {}) =>
      lifecycle.runInContainer(containerId, { command, timeoutMs, ...extra });
    const spawn = (command, extra = {}) => lifecycle.spawnInContainer(containerId, { command, ...extra });
    rec.result = await fn({ containerId, metadata: p.metadata, run, spawn, rec });
  } catch (e) {
    rec.error = { code: e?.code, message: e?.message };
    log(label, 'ERROR', e?.code, e?.message);
  } finally {
    if (containerId) {
      for (const [k, f] of [['stop', lifecycle.stopContainer], ['deprovision', lifecycle.deprovisionContainer]]) {
        try {
          const result = await f(containerId);
          rec.cleanupResults[k] = result;
          rec.cleanup[k] = result.warnings.length ? 'warnings' : 'ok';
        }
        catch (e) { rec.cleanup[k] = `${e?.code}: ${e?.message}`; }
        try { checkpoint(rec); }
        catch (error) {
          (rec.checkpointErrors ??= []).push(error.message);
          log(label, 'CHECKPOINT ERROR', error.message);
        }
      }
      log(label, 'cleanup', JSON.stringify(rec.cleanup));
    }
    rec.phase = rec.error || rec.checkpointErrors?.length || Object.values(rec.cleanup).some(value => value !== 'ok') ? 'failed' : 'finished';
    checkpoint(rec);
  }
  return rec;
}

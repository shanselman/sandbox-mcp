import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { startCameraServer } from './camera-server.mjs';

const evidence = fileURLToPath(new URL('./evidence/', import.meta.url));

export function cameraCanvasOptions(evidenceRoot = evidence) {
  const entries = new Map();
  function resolveBatch(batch) {
    if (typeof batch !== 'string' || !batch || path.isAbsolute(batch))
      throw new Error('Expected a batch path relative to the spike evidence directory');
    const root = fs.realpathSync(evidenceRoot);
    const target = fs.realpathSync(path.resolve(root, batch));
    const relative = path.relative(root, target);
    if (!relative || relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative))
      throw new Error('Batch must be inside the spike evidence directory');
    return target;
  }
  return {
    id: 'camera-grid',
    displayName: 'Agent Camera Grid',
    description: 'Watch live headless-session frames and looping recorded videos with the Copilot theme; never creates sessions.',
    inputSchema: { type: 'object', properties: { batch: { type: 'string', minLength: 1 } }, required: ['batch'], additionalProperties: false },
    actions: [{
      name: 'get_state',
      description: 'Read this evidence batch and its recorded/live/replay status without starting any sessions.',
      inputSchema: { type: 'object', properties: {}, additionalProperties: false },
      handler: async ctx => {
        const entry = entries.get(ctx.instanceId);
        if (!entry) throw new Error('Camera grid instance is not open');
        const viewer = await entry.server;
        return { ...viewer.state(), appearance: viewer.appearance() ?? null };
      },
    }, {
      name: 'get_view',
      description: 'Read renderer-reported theme, video playback and page activity, without inspecting other app content.',
      inputSchema: { type: 'object', properties: {}, additionalProperties: false },
      handler: async ctx => {
        const entry = entries.get(ctx.instanceId);
        if (!entry) throw new Error('Camera grid instance is not open');
        const viewer = await entry.server;
        return { appearance: viewer.appearance() ?? null, playback: viewer.playback() ?? null, requests: viewer.requests() };
      },
    }],
    open: async ctx => {
      const root = resolveBatch(ctx.input?.batch);
      let entry = entries.get(ctx.instanceId);
      if (entry && entry.root !== root) throw new Error('Use a new canvas instance for a different evidence batch');
      if (!entry) {
        entry = { root, server: startCameraServer(root) };
        entries.set(ctx.instanceId, entry);
      }
      try {
        const viewer = await entry.server;
        return { url: viewer.url, title: 'Agent camera grid', status: viewer.state().mode === 'replay' ? 'Offline replay' : 'Session evidence' };
      } catch (error) {
        if (entries.get(ctx.instanceId) === entry) entries.delete(ctx.instanceId);
        // A bad state file can fail after the server has already started.
        const viewer = await entry.server.catch(() => null);
        if (viewer) await viewer.close();
        throw error;
      }
    },
    onClose: async ctx => {
      const entry = entries.get(ctx.instanceId);
      if (!entry) return;
      entries.delete(ctx.instanceId);
      await (await entry.server).close();
    },
  };
}

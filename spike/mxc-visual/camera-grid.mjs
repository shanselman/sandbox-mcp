import { atomicJson } from './broker-client-state.mjs';
import { startCameraServer } from './camera-server.mjs';
import path from 'node:path';

if (process.argv.length !== 3) throw new Error('Expected a prepared batch directory');
const viewer = await startCameraServer(process.argv[2]);
try {
  atomicJson(path.join(viewer.root, 'viewer.json'), { url: viewer.url, pid: process.pid, loopbackOnly: true, readOnly: true });
  console.log(JSON.stringify({ url: viewer.url, pid: process.pid, root: viewer.root }));
} catch (error) {
  await viewer.close();
  throw error;
}
for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => {
  viewer.close().catch(error => { console.error(error); process.exitCode = 1; });
});

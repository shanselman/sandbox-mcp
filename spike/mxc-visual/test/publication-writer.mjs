// Invoked only by the offline stress test, in an isolated temporary directory.
import path from 'node:path';
import { atomicJson } from '../broker-client-state.mjs';
const [directory, writer, count] = process.argv.slice(2);
for (let sequence = 0; sequence < Number(count); sequence++) {
  const body = { writer: Number(writer), sequence, marker: `${writer}:${sequence}`, padding: 'x'.repeat(2048) };
  atomicJson(path.join(directory, `snapshot-${writer}-${String(sequence).padStart(4, '0')}.json`), body);
  if (sequence % 5 === 0) await new Promise(resolve => setTimeout(resolve, 1));
}

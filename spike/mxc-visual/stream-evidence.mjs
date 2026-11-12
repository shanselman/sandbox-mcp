import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { summarizeStream } from './live-evidence.mjs';
import { validatePng } from './png-evidence.mjs';
import { calculatorIndex } from './visual-workloads.mjs';

export function verifyStreamEvidence(directory, stream, fixture, variant, expectedSessionId, policy) {
  const summary = summarizeStream(stream, fixture, variant, policy);
  if (!Number.isInteger(expectedSessionId) || expectedSessionId <= 1
    || stream.frames.some(frame => frame.sessionId !== expectedSessionId))
    throw new Error('Stream images do not belong to the expected session');
  const root = fs.lstatSync(directory);
  if (!root.isDirectory() || root.isSymbolicLink()) throw new Error('Invalid stream evidence directory');
  const hashes = new Set();
  for (const frame of stream.frames) {
    const file = path.join(directory, frame.file);
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024)
      throw new Error(`Invalid preserved stream image: ${frame.file}`);
    const bytes = fs.readFileSync(file);
    validatePng(bytes, frame);
    hashes.add(createHash('sha256').update(bytes).digest('hex'));
  }
  if (calculatorIndex(variant) && hashes.size < 6) throw new Error('Calculator recording did not show enough changing states');
  // Distinct encodings alone do not prove a semantic UI change; keep the UIA
  // assertions and visual review requirement in the caller's final verdict.
  return { ...summary, validImages: stream.frames.length, distinctImageHashes: hashes.size };
}

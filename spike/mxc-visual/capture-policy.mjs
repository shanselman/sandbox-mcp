import fs from 'node:fs';

export const DEFAULT_CAPTURE_POLICY = Object.freeze({
  schema: 1, minAverageFps: 0.9, maxFrameGapMs: null, minOverlapMs: 20000,
});
export const LEGACY_CAPTURE_POLICY = Object.freeze({ ...DEFAULT_CAPTURE_POLICY, maxFrameGapMs: 2000 });

export function capturePolicy(input = {}) {
  if (!input || typeof input !== 'object' || Array.isArray(input)
    || Object.keys(input).some(key => !Object.hasOwn(DEFAULT_CAPTURE_POLICY, key)))
    throw new Error('Invalid capture policy fields');
  const policy = { ...DEFAULT_CAPTURE_POLICY, ...input };
  if (policy.schema !== 1
    || policy.minAverageFps !== null && (!Number.isFinite(policy.minAverageFps) || policy.minAverageFps <= 0 || policy.minAverageFps > 1)
    || policy.maxFrameGapMs !== null && (!Number.isSafeInteger(policy.maxFrameGapMs) || policy.maxFrameGapMs <= 0)
    || !Number.isSafeInteger(policy.minOverlapMs) || policy.minOverlapMs < 0)
    throw new Error('Invalid capture policy: expected schema 1, fps in (0,1] or null, positive gap milliseconds or null, and nonnegative overlap milliseconds');
  return policy;
}

export function policyArguments(argv) {
  const args = [...argv];
  const index = args.indexOf('--policy');
  if (index < 0) return { args, policy: capturePolicy(), explicit: false };
  if (!args[index + 1] || args[index + 1].startsWith('--') || args.indexOf('--policy', index + 1) >= 0)
    throw new Error('Expected exactly one --policy <JSON-file>');
  const file = args[index + 1];
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16384) throw new Error('Invalid capture policy file');
  const policy = capturePolicy(JSON.parse(fs.readFileSync(file, 'utf8')));
  args.splice(index, 2);
  return { args, policy, explicit: true };
}

export function describePolicy(input) {
  const policy = capturePolicy(input);
  return `Average fps: ${policy.minAverageFps === null ? 'report only' : `>=${policy.minAverageFps}`}; maximum gap: ${
    policy.maxFrameGapMs === null ? 'report/warn only' : `<=${policy.maxFrameGapMs}ms`}; common coverage: >=${policy.minOverlapMs}ms.`;
}

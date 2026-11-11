import assert from 'node:assert/strict';
import test from 'node:test';
import { withSession } from '../session.mjs';

function fakeLifecycle() {
  const calls = [];
  return {
    calls,
    provisionContainer: async () => { calls.push('provision'); return { containerId: 'opaque-test-id', metadata: { agentUserName: 'TEST' }, warnings: [] }; },
    startContainer: async id => { assert.equal(id, 'opaque-test-id'); calls.push('start'); return { warnings: [] }; },
    runInContainer: async (id, request) => { assert.equal(id, 'opaque-test-id'); calls.push('exec'); return { stdout: request.command }; },
    spawnInContainer: async () => { throw new Error('No process spawn expected'); },
    stopContainer: async id => { assert.equal(id, 'opaque-test-id'); calls.push('stop'); return { warnings: [] }; },
    deprovisionContainer: async id => { assert.equal(id, 'opaque-test-id'); calls.push('deprovision'); return { warnings: [] }; },
  };
}
test('pure-MXC wrapper checkpoints identity before start and retains SDK cleanup warnings', async () => {
  const sdk = fakeLifecycle(), checkpoints = [];
  sdk.deprovisionContainer = async () => { sdk.calls.push('deprovision'); return { warnings: ['Removal needs inspection'] }; };
  const result = await withSession('offline-warnings', async ({ run }) => run('probe'),
    record => checkpoints.push({ phase: record.phase, id: record.containerId, calls: [...sdk.calls] }), sdk);
  assert.deepEqual(sdk.calls, ['provision', 'start', 'exec', 'stop', 'deprovision']);
  const provisioned = checkpoints.find(point => point.phase === 'provisioned');
  assert.equal(provisioned.id, 'opaque-test-id');
  assert.deepEqual(provisioned.calls, ['provision']);
  assert.deepEqual(result.cleanupResults.deprovision.warnings, ['Removal needs inspection']);
  assert.equal(result.cleanup.deprovision, 'warnings');
  assert.equal(result.phase, 'failed');
});
test('a failed cleanup checkpoint does not skip SDK deprovision', async () => {
  const sdk = fakeLifecycle();
  const result = await withSession('offline-checkpoint', async () => ({}), record => {
    if (record.cleanup.stop && !record.cleanup.deprovision) throw new Error('Synthetic evidence write failure');
  }, sdk);
  assert.deepEqual(sdk.calls, ['provision', 'start', 'stop', 'deprovision']);
  assert.deepEqual(result.checkpointErrors, ['Synthetic evidence write failure']);
  assert.equal(result.cleanup.deprovision, 'ok');
  assert.equal(result.phase, 'failed');
});
test('failed initial durable recording prevents any provisioning side effect', async () => {
  const sdk = fakeLifecycle();
  const result = await withSession('offline-no-receipt', async () => assert.fail('workload must not run'), record => {
    if (record.phase === 'provisioning') throw new Error('No durable receipt');
  }, sdk);
  assert.deepEqual(sdk.calls, []);
  assert.equal(result.phase, 'failed');
  assert.match(result.error.message, /No durable receipt/);
});

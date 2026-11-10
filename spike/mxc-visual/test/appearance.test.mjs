import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = fs.readFileSync(new URL('../viewer/appearance.js', import.meta.url), 'utf8');
function harness({ rootMode = null, bodyMode = null, token = '', scheme = 'light dark', ok = true } = {}) {
  const label = {}, submissions = [], events = {};
  const style = { backgroundColor: 'rgb(55, 29, 50)', color: 'rgb(242, 226, 241)', colorScheme: scheme,
    fontFamily: 'Segoe UI', getPropertyValue: () => token };
  const context = vm.createContext({
    document: {
      documentElement: { getAttribute: () => rootMode }, body: { getAttribute: () => bodyMode },
      getElementById: () => label, addEventListener: (name, handler) => { events[name] = handler; },
    },
    getComputedStyle: () => style, matchMedia: () => ({ matches: true }), setInterval: () => {},
    fetch: async (_url, options) => { submissions.push(JSON.parse(options.body)); return { ok, status: ok ? 200 : 400 }; },
  });
  vm.runInContext(script, context);
  return { context, submissions, label, events, style };
}
test('host stylesheet tokens are recognized even without mode attributes', async () => {
  const { context, submissions, label } = harness({ token: '#371d32' });
  await context.reportAppearance();
  assert.equal(submissions.at(-1).source, 'host');
  assert.equal(submissions.at(-1).mode, null, 'do not mistake the OS preference for an app-supplied mode');
  assert.equal(label.textContent, 'App theme (host colors)');
});
test('theme readback honors root/body mode precedence and standalone fallback', () => {
  for (const [options, mode, source] of [
    [{ rootMode: 'light', bodyMode: 'dark' }, 'light', 'host'],
    [{ bodyMode: 'light' }, 'light', 'host'],
    [{}, 'dark', 'system'],
  ]) {
    const { submissions } = harness(options);
    assert.equal(submissions[0].mode, mode);
    assert.equal(submissions[0].source, source);
  }
});
test('renderer readback reports CSP failures and subsequent color changes', async () => {
  const { context, events, style, submissions } = harness({ token: '#371d32' });
  await context.reportAppearance();
  events.securitypolicyviolation({ violatedDirective: 'style-src-elem' });
  style.backgroundColor = 'rgb(250, 248, 245)';
  await context.reportAppearance();
  assert.deepEqual(submissions.at(-1).cspDirectives, ['style-src-elem']);
  assert.equal(submissions.at(-1).background, 'rgb(250, 248, 245)');
});
test('readback failure remains explicit instead of claiming observation succeeded', async () => {
  const { context, label } = harness({ ok: false });
  await context.reportAppearance();
  assert.match(label.textContent, /theme readback unavailable: HTTP 400/);
});

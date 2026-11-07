import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const read = (file) => fs.readFileSync(new URL(`../../${file}`, import.meta.url), 'utf8');
const html = read('site/index.html');
const siteUrl = 'https://shanselman.github.io/sandbox-mcp/';
const attrs = [...html.matchAll(/\b(?:href|src)="([^"]+)"/g)].map((m) => m[1]);
const serverTools = [...read('src/server.js').matchAll(/registerTool\('(\w+)'/g)].map((m) => m[1]);

test('local files and in-page links resolve', () => {
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));
  for (const target of attrs.filter((a) => !/^https?:/.test(a))) {
    if (target.startsWith('#')) assert.ok(ids.has(target.slice(1)), `no element with id ${target}`);
    else assert.ok(fs.existsSync(new URL(`../../site/${target}`, import.meta.url)), `missing file: ${target}`);
  }
});

test('social image exists where the absolute URL says it does', () => {
  const image = html.match(/property="og:image" content="([^"]+)"/)[1];
  assert.ok(image.startsWith(siteUrl));
  assert.ok(fs.existsSync(new URL(`../../site/${image.slice(siteUrl.length)}`, import.meta.url)));
});

test('install commands match the plugin and marketplace names', () => {
  const plugin = JSON.parse(read('plugin.json')).name;
  const marketplace = JSON.parse(read('.github/plugin/marketplace.json')).name;
  assert.ok(html.includes(`copilot plugin install ${plugin}@${marketplace}`));
  assert.ok(html.includes('copilot plugin marketplace add shanselman/sandbox-mcp'));
});

test('site positions the CLI as standalone and MCP as optional', () => {
  assert.match(html, /optional MCP adapter/i);
  assert.match(html, /same engine/i);
  assert.match(html, /CLI does not require this plugin/i);
  assert.match(html, /no Node\.js install, MCP setup or PATH changes/i);
});

test('site explains the project CLI name and matches social metadata', () => {
  const title = html.match(/<title>([^<]+)<\/title>/)[1];
  assert.equal(html.match(/property="og:title" content="([^"]+)"/)[1], title);
  const description = html.match(/name="description" content="([^"]+)"/)[1];
  assert.equal(html.match(/property="og:description" content="([^"]+)"/)[1], description);
  assert.match(html, /Docker also has its own <code>sbx<\/code> command/);
  assert.match(html, /docs\.docker\.com\/reference\/cli\/sbx/);
});

test('site shows app-specific UI assertions and pairs commands with results', () => {
  assert.match(html, /Starting isn&rsquo;t proof/);
  assert.match(html, /Wait-SbxUi @app -AutomationId Output -Name 'Hello, Sandbox Tester!'/);
  assert.match(html, /greeting\.txt does not match the label/);
  assert.match(html, /sbx run \.\\samples\\HelloSandbox\\bin --test \.\\samples\\HelloSandbox\\test\.ps1<\/span>\s+PASS\s+HelloSandbox\.exe/);
  assert.match(html, /sbx run \.\\NativeProof\.exe --network off<\/span>\s+<span class="bad"><b>FAIL<\/b><\/span>\s+NativeProof\.exe/);
  assert.equal((html.match(/class="dot red"/g) ?? []).length, 3);
  assert.match(html, /3 red flag\(s\)/);
});

test('hero demo waits until visible and exposes a readable transcript', () => {
  const script = read('site/script.js');
  const styles = read('site/styles.css');
  assert.match(script, /new IntersectionObserver/);
  assert.match(styles, /height: min\(440px, 70vh\)/);
  assert.match(html, /<details class="demo-transcript">/);
  assert.match(html, /Read the full demo transcript/);
});

test('site removes screenshot galleries and keeps compatibility timings and CLI examples', () => {
  assert.doesNotMatch(html, /Measured, not promised|id="measured"|#measured/i);
  assert.doesNotMatch(html, /assets\/(?:compat-(?:native|wpf|winui)\.png|sandbox-teaser\.gif|haptics-teaser\.png)/i);
  for (const image of ['compat-native.png', 'compat-wpf.png', 'compat-winui.png', 'sandbox-teaser.gif', 'haptics-teaser.png']) {
    assert.ok(!fs.existsSync(new URL(`../../site/assets/${image}`, import.meta.url)), `site still ships ${image}`);
  }
  assert.match(html, /id="compatibility"/);
  assert.match(html, /App type<\/th><th scope="col">Packaging<\/th>/);
  assert.equal((html.match(/class="yes">PASS<\/td><td>\d+\.\d+ \/ \d+\.\d+ s/g) ?? []).length, 7);
  assert.match(html, /sbx run \.\\samples\\HelloSandbox\\bin/);
});

test('the site only names real MCP tools and states the right tool count', () => {
  const named = [...html.matchAll(/\bsandbox_[a-z]\w*/g)].map((m) => m[0]);
  assert.deepEqual(named.filter((t) => !serverTools.includes(t)), []);
  assert.equal(Number(html.match(/data-tool-count>(\d+)</)[1]), serverTools.length);
});

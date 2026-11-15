import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { ROOT as REPO_ROOT } from '../../src/core.js';
import { detonateCommand, processTree, findings, network, headline, detonateSummary, renderDetonateReport } from '../../src/detonate.js';

const watch = path.join(REPO_ROOT, 'agent', 'watch.ps1').replace(/'/g, "''");
const ps = (script) => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
  { encoding: 'utf8', timeout: 30_000 });

test('audit setup stops on either auditpol or DNS setup failure instead of returning an armed timestamp', () => {
  // Mock every setup side effect: this test never changes host audit policy, registry or logs.
  const output = ps(`
    function auditpol.exe {
      $global:calls++
      $global:LASTEXITCODE = if ($global:calls -eq $global:failAt) { 5 } else { 0 }
    }
    function wevtutil.exe {
      $global:calls++
      $global:LASTEXITCODE = if ($global:calls -eq $global:failAt) { 5 } else { 0 }
    }
    function New-Item {}
    function Set-ItemProperty {}
    function Start-Sleep {}
    $results = foreach ($failAt in 1, 2, 3, 0) {
      $global:failAt = $failAt; $global:calls = 0
      $value = $null; $failure = $null
      try { $value = & '${watch}' -Arm } catch { $failure = $_.Exception.Message }
      [pscustomobject]@{ failAt=$failAt; calls=$global:calls; value=$value; error=$failure }
    }
    ConvertTo-Json -InputObject @($results) -Compress
  `);
  const results = JSON.parse(output);
  for (const [i, pattern] of [/Process-creation auditing unavailable/, /Connection auditing unavailable/, /DNS auditing unavailable/].entries()) {
    assert.equal(results[i].calls, i + 1, 'setup must stop at the failing command');
    assert.equal(results[i].value, null, 'failed setup must not return the collection start time');
    assert.match(results[i].error, pattern);
    assert.match(results[i].error, /exited with code 5/);
  }
  assert.equal(results[3].error, null);
  assert.equal(results[3].calls, 3);
  assert.match(results[3].value, /^\d{4}-\d{2}-\d{2}T/);
});

test('event collection distinguishes no matching events from failures in each log query', () => {
  const output = ps(`
    function Get-WinEvent {
      [CmdletBinding()] param($FilterHashtable, $MaxEvents)
      $global:queries++
      $id = if ($global:queries -eq $global:failAt) { $global:failureId } else { 'NoMatchingEventsFound' }
      $record = [Management.Automation.ErrorRecord]::new(
        [InvalidOperationException]::new('Synthetic event query result'), $id,
        [Management.Automation.ErrorCategory]::ObjectNotFound, $FilterHashtable.LogName)
      $PSCmdlet.ThrowTerminatingError($record)
    }
    function Get-Process { [pscustomobject]@{ Id=1234 } }
    $results = foreach ($case in @(
      @{ failAt=0; id='none' },
      @{ failAt=1; id='UnauthorizedAccess' },
      @{ failAt=2; id='EventLogUnavailable' },
      @{ failAt=3; id='NoMatchingLogsFound' }
    )) {
      $global:queries=0; $global:failAt=$case.failAt; $global:failureId=$case.id
      $value=$null; $failure=$null
      try { $value = & '${watch}' -Since '2026-01-01T00:00:00Z' } catch { $failure=$_.Exception.Message }
      [pscustomobject]@{ failAt=$case.failAt; queries=$global:queries; value=$value; error=$failure }
    }
    ConvertTo-Json -InputObject @($results) -Compress
  `);
  const results = JSON.parse(output);
  assert.equal(results[0].error, null);
  const empty = JSON.parse(results[0].value);
  for (const key of ['processes', 'connections', 'dns']) assert.deepEqual(empty[key], []);
  assert.deepEqual(empty.alive, [1234]);
  for (const [i, ids] of ['4688', '5156, 5157', '3006, 3008'].entries()) {
    assert.equal(results[i + 1].queries, i + 1);
    assert.equal(results[i + 1].value, null, 'failed collection must not emit an empty activity report');
    assert.match(results[i + 1].error, /Event collection unavailable/);
    assert.ok(results[i + 1].error.includes(`IDs ${ids}`));
  }
  assert.match(results[3].error, /Microsoft-Windows-DNS-Client\/Operational/);
});

test('detonateCommand runs scripts with their default host and installers silently', () => {
  assert.deepEqual(detonateCommand('C:\\d\\a.js'), { file: 'wscript.exe', args: '"C:\\d\\a.js"' });
  assert.deepEqual(detonateCommand('C:\\d\\a.VBS', '/x'), { file: 'wscript.exe', args: '"C:\\d\\a.VBS" /x' });
  assert.deepEqual(detonateCommand('C:\\d\\a.hta'), { file: 'mshta.exe', args: '"C:\\d\\a.hta"' });
  assert.equal(detonateCommand('C:\\d\\a.ps1').file, 'powershell.exe');
  assert.equal(detonateCommand('C:\\d\\a.msi').file, 'msiexec.exe');
  assert.deepEqual(detonateCommand('C:\\d\\a.exe', '-q'), { file: 'C:\\d\\a.exe', args: '-q' });
  assert.throws(() => detonateCommand('C:\\d\\a.docx'), /Don't know how to run \.docx/);
});

const ev = (pid, ppid, exe, time) => ({ pid, ppid, path: `C:\\Windows\\System32\\${exe}`, commandLine: exe, time: `2026-01-01T10:00:${time}.0000000-08:00` });
const AGENT = 100, ROOT = 200;
const events = [
  ev(150, AGENT, 'powershell.exe', '00'), // agent's own exec helper (noise)
  ev(151, 150, 'conhost.exe', '00'), // ...and its child
  ev(ROOT, AGENT, 'powershell.exe', '01'),
  ev(201, ROOT, 'schtasks.exe', '02'),
  ev(202, ROOT, 'net.exe', '03'),
  ev(203, 202, 'net1.exe', '04'),
  ev(300, 4, 'svchost.exe', '05'), // Windows doing its own thing
];

test('processTree separates the file\'s tree from the agent\'s helpers and from Windows', () => {
  const t = processTree(events, ROOT, AGENT);
  assert.deepEqual([...t.pids].sort(), [200, 201, 202, 203]);
  assert.deepEqual(t.tree.map((p) => [p.pid, p.depth]), [[200, 0], [201, 1], [202, 1], [203, 2]]);
  assert.deepEqual(t.others.map((p) => p.pid), [300]);
});

test('network keeps only the tree\'s activity, one row per name and per endpoint', () => {
  const c = {
    dns: [
      { pid: 200, name: 'C2.example', status: '9003', time: 't1' },
      { pid: 200, name: 'c2.example', status: '0', time: 't2' },
      { pid: 300, name: 'login.live.com', status: '0', time: 't3' },
    ],
    connections: [
      { pid: 200, address: '203.0.113.10', port: 80, protocol: 'TCP', blocked: true },
      { pid: 200, address: '203.0.113.10', port: 80, protocol: 'TCP', blocked: true },
      { pid: 201, address: '198.51.100.7', port: 4444, protocol: 'TCP', blocked: false },
      { pid: 300, address: '239.255.255.250', port: 1900, protocol: 'UDP', blocked: true },
    ],
  };
  const n = network(c, new Set([200, 201]));
  assert.deepEqual(n.lookups.map((l) => [l.name, l.ok]), [['C2.example', true]]);
  assert.deepEqual(n.connections.map((x) => [x.address, x.count, x.blocked]), [['203.0.113.10', 2, true], ['198.51.100.7', 1, false]]);
});

const emptyDiff = () => ({
  values: [], services: [], programsAdded: [], programsRemoved: [],
  files: { added: [], removed: [], changed: [], executables: [], executableCount: 0, addedCount: 0, changedCount: 0 },
  registry: { added: [], removed: [], changed: [], addedCount: 0 },
});

test('findings flags persistence as high, recon and network as medium, most serious first', () => {
  const diff = emptyDiff();
  diff.values.push({ change: 'added', name: 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\Updater', after: 'evil.exe' });
  diff.values.push({ change: 'changed', name: 'HKCU\\Environment\\Path', after: 'x' });
  diff.services.push({ change: 'added', name: 'EvilSvc', after: '2|16|0|C:\\evil.exe' });
  diff.files.added.push({ path: 'C:\\Windows\\System32\\Tasks\\Updater' });
  diff.files.executables.push('C:\\Users\\u\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\a.cmd');
  diff.files.executableCount = 1;
  diff.files.changed.push('C:\\Windows\\System32\\drivers\\etc\\hosts');
  diff.registry.added.push({ path: 'HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Exclusions' });
  const tree = processTree(events, ROOT, AGENT).tree;
  const f = findings({ tree, diff, lookups: [{ name: 'c2.example' }], connections: [{ address: '1.2.3.4', port: 80 }], windows: [{ title: 'Hi' }] });
  const text = f.map((x) => `${x.level}: ${x.text}`).join('\n');
  for (const re of [/high: starts itself with Windows.*Run\\Updater/, /high: installed a service.*EvilSvc.*C:\\evil\.exe/, /high: created a scheduled task: `Updater`/,
    /high: put something in a Startup folder/, /high: changed the hosts file/, /high: changed security policy.*Windows Defender/,
    /medium: changed environment variables: `Path`/, /medium: wrote 1 runnable file/, /medium: ran built-in Windows tools.*schtasks\.exe, net\.exe, net1\.exe/,
    /medium: looked up 1 internet name\(s\): c2\.example/, /medium: tried to connect to 1 address\(es\): 1\.2\.3\.4:80/,
    /info: started 3 other process/, /info: opened 1 window/]) assert.match(text, re);
  const levels = f.map((x) => x.level);
  assert.deepEqual(levels, [...levels].sort((a, b) => ['high', 'medium', 'info'].indexOf(a) - ['high', 'medium', 'info'].indexOf(b)));
  assert.equal(f.filter((x) => /Startup folder/.test(x.text)).length, 1);
});

test('findings for a file that did nothing is empty, and removed autoruns are not flagged', () => {
  const diff = emptyDiff();
  diff.values.push({ change: 'removed', name: 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\Old' });
  assert.deepEqual(findings({ tree: [events[2]].map((e) => ({ ...e, depth: 0 })), diff }), []);
});

test('headline, summary and report read sensibly for results and errors', () => {
  const r = {
    file: 'x.ps1', hostPath: 'C:\\x.ps1', sha256: 'ab', seconds: 50, watchedSeconds: 20, sandbox: { network: 'off', bootSeconds: 15 },
    findings: [{ level: 'high', text: 'created a scheduled task: `T`' }, { level: 'info', text: 'started 1 other process(es)' }],
    tree: [{ ...events[2], depth: 0 }], lookups: [], connections: [], stillRunning: [], shots: [], windows: [], crashes: [],
    outDir: 'C:\\out', runDir: 'C:\\run', since: events[2].time, launchedAt: events[2].time,
  };
  assert.equal(headline(r), 'x.ps1: 1 red flag(s) (watched 20s, network off)');
  const s = detonateSummary(r);
  assert.match(s, /findings\s+🔴 created a scheduled task: T/);
  assert.doesNotMatch(s, /other process/);
  const md = renderDetonateReport(r);
  assert.match(md, /## Findings/);
  assert.match(md, /\+0\.0s\s+start\s+powershell\.exe/);
  assert.match(md, /Network was off/);
  assert.match(md, /treat them as untrusted/);

  const e = { file: 'x.ps1', error: 'boom\nmore', findings: [], shots: [], windows: [], crashes: [], seconds: 1, tree: [] };
  assert.equal(headline(e), 'ERROR  x.ps1: boom');
  assert.match(renderDetonateReport({ ...e, outDir: 'C:\\out' }), /## Error/);
});

test('unavailable event collection is rendered as an error, never a nothing-notable verdict', () => {
  const result = {
    file: 'sample.exe', error: 'collecting events failed: Event collection unavailable for Security (IDs 4688)',
    findings: [], shots: [], windows: [], crashes: [], seconds: 1, outDir: 'C:\\out',
  };
  assert.match(headline(result), /^ERROR.*Event collection unavailable/);
  const report = renderDetonateReport(result);
  assert.match(report, /## Error/);
  assert.match(report, /Event collection unavailable for Security/);
  assert.doesNotMatch(report, /nothing notable/);
});

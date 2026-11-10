# Full-session safe verification after the host crash

This records the original safe-only pass. The later user-approved Windows
update, broker installation and live qualification are recorded separately in
[Windows update qualification](windows-update-qualification.md).

## Status and scope

**The concurrent native Full-session experiment is unsafe pending Windows
investigation.** Its third run ended in `0xEF CRITICAL_PROCESS_DIED`. Earlier
single-session success and passing offline tests do not establish native
lifecycle safety.

This follow-up performs no new WTS creation, stop or recovery calls, no MXC
account operations, no service installation/update/restart, no host settings
changes, and no diagnostic uploads. The installed package stays unchanged.
All new code below is source-only on the experimental branch.

## Tests and changes

### Real orchestration, simulated native effects

`spike/mxc-visual/broker/SessionBackend.cs` isolates the native adapter.
`Runtime.RunGroup` and `Runtime.Cleanup` accept an internal backend for tests;
the normal production path still explicitly selects the Windows adapter.
There is no IPC field or environment override for selecting a test backend.

`LifecycleTests.cs` exercises:

- Every member position failing credential acquisition or initial journal save.
- Native creation failing before or after a simulated side effect.
- Failure to persist a returned session ID.
- Cancellation before any side effect and after a successful creation.
- Worker failure and independent teardown of other members.
- A failing stop, successful stop without disappearance, and failed final save.
- Wrong/changed session identity, invalid/console IDs and unrelated sessions.
- An unrecorded second session under the same account: it is not implicitly
  owned merely because its SID matches.
- Recovery after interruption, including preserving the previous error and
  explicitly declining to claim pre-interruption teardown success.
- 1,000 deterministic ownership permutations.

The source now persists **the exact session ID and stop-intent timestamp**
before entering the experimental native stop call. This is diagnostic
hardening, **not a fix for the Windows bugcheck**. If that journal write fails,
the native stop is not attempted. The next controlled live investigation
will need separate approval and an appropriate machine.

The replay/publication findings also prompted a shared source-only writer
that flushes new evidence to disk before renaming it to a never-overwritten
final name. Serializer failure and destination collisions are tested. This
is a stronger persistence request, not proof against kernel crashes, storage
failure or power loss, and it has not been installed or live-capture-tested.

### Publication, evidence and viewer

The tests reproduce Windows file-sharing behavior in temporary folders:
replacing a file held without delete sharing is refused, while publishing
100 distinct immutable snapshots succeeds. A separate process-level test
uses three writers and an independent reader for 1,500 snapshots per suite
execution, with strict content/sequence checks.

Frame validation now checks session/process identity, chronological ordering,
finite measurements, explicit scale semantics, dimensions, PNG chunk CRCs,
and a complete PNG ending. A collector failure cannot be displayed as
successful completion. Future timestamps cannot be labeled live.

An independent review found three additional source issues and each now has
a targeted regression: recovery must honor the persisted stop target even
when native creation never returned an ID; replay must reject descendants
named `..replay` rather than mistaking them for `..`; and checksum-valid PNGs
must also contain bounded, decodable image data with valid scanline geometry.
PNG validation therefore includes bounded decompression and scanline filters,
not only signature/CRC checks. A follow-up also caught high-bit chunk names
being accepted by lossy ASCII decoding; byte-preserving decoding plus a
correct-CRC negative fixture covers that case.

The replay code imports no MXC SDK and makes no broker request. It reads
preserved reports/images, copies verified images to a new folder, preserves
capture timestamps, and renders the original timing under an explicit
**REPLAY / SOURCE FAILED** banner.

## New findings from the old evidence

The workers reported 30 successful captures each. The actual preserved
`frame-029.png` in **all three runs is entirely zero-filled**, not a PNG.
Their hashes match the pre-existing preservation manifests; these bytes
were not changed by the offline investigation. The original cause of that
data loss is unknown. A crash-related persistence failure is a possibility,
not an established explanation.

The offline report therefore examines 90 files, copies **87 valid PNGs** and
reports/excludes the three invalid final files without repairing the source.
Changing earlier frame contents are verified by actual image hashes and
visual inspection, not by trusting the worker's success field.

| Recorded variant | Valid images / reported frames | Recorded mean fps | Largest interval | Actual window DPI | Fixture scale |
|---|---:|---:|---:|---:|---:|
| Light | 29 / 30 | 1.000 | 2,930 ms | 96 | 1.0x |
| Dark | 29 / 30 | 1.000 | 4,479 ms | 96 | 1.0x |
| Enlarged dark | 29 / 30 | 1.000 | 1,395 ms | 96 | 1.5x simulated |

Common coverage across valid image timestamps is **14,676 ms**, below the
previously declared 20-second bar. The first two streams also miss the
2,000-ms maximum-gap bar. An average near 1fps is not a pass by itself.
Independently of these capture failures, the host crash makes the original
run a lifecycle failure.

## Existing-dump read-only follow-up

A copy of the archived minidump was analyzed locally, without another
elevation, live-process attach or change to the original dumps.

- Critical process: `csrss.exe`, PID `0xbb78`; terminating process:
  `smss.exe`, PID `0x4d4`.
- CSRSS `BreakOnTermination` was set. Its recorded creation time was
  `2026-11-10T05:09:16.6863471Z`, near the first experimental session's creation.
- The public minidump inspection could not resolve the session-space type;
  `!process` printed `SessionId: none`. **That is not proof of session 0 or a
  particular agent session.** No private structure offsets were guessed.
- The recorded exit status was zero at the sampled point; this does not
  establish why SMSS initiated termination.

The exact dying session and initiating Windows lifecycle sequence still need
the Windows engineers' analysis of the full dump. The local findings are an
addendum, not a new root-cause claim.

## Repeatable safe commands

From the repository root, using a supported Node version:

```powershell
dotnet build spike\mxc-visual\broker -c Release
.\spike\mxc-visual\broker\bin\Release\net10.0-windows\SandboxMcp.FullBroker.exe selftest
node --test "spike\mxc-visual\test\*.test.mjs"
```

For replay, supply the three preserved run directories in any order and a
**new** output directory. Sources must contain `visual\worker.json`,
`fixture-settings.json`, `stream.json`, authorization and PNG evidence.

```powershell
node spike\mxc-visual\replay-evidence.mjs <new-output> <preserved-run-1> <preserved-run-2> <preserved-run-3>
node spike\mxc-visual\camera-grid.mjs <new-output>
```

The loopback viewer is read-only and can be opened in Copilot's Browser canvas
or an ordinary browser. Stop its specific process after viewing. Do not use
the live `variant-run` or `run-grid` commands as substitutes for replay.

## Confidence boundaries

The final local suite contains **101 C# checks and 36 Node tests**. The C#
checks execute the coordinator against the in-memory backend, not Windows
session APIs. Core suites were repeated three times; each Node stress case
includes five rounds of three independent writers, 1,500 snapshots per suite
execution. These are deterministic acceptance checks, not a performance
speedup claim or a native stability benchmark.

The installed service and all **537 installed files** were checked read-only
against the original package, with no mismatches. The original host sessions
remained the only sessions; the three crash-run users were already absent.

### Compact viewer and theme follow-up

The user separately requested an ambient-theme-aware, count-responsive grid.
The viewer now starts with compact tiles and scales layout for 4/8/16 image
tiles, keeps status/errors visible, and puts detailed metrics under an
expandable evidence panel. Agent-provided names use text-only rendering and
stable provided IDs survive renaming/reordering. This changes presentation
capacity, **not the broker's native session limit**.

Theme prior art is `hanselprojects/src/dashboard/styles.ts` lines 3-9
(semantic tokens), 16-18 (derived surfaces), and 26 (`--font-sans`).
The viewer consumes documented semantic tokens and `data-color-mode` when
provided, otherwise using the browser's system color-scheme preference.
An isolated headless Edge profile tested 4/8/16 tiles at 1200, 320 and 240 CSS
pixel widths, no horizontal overflow, token precedence and system fallbacks.
The temporary test browser/profile/server were cleaned up.

**Actual Browser-canvas limitation:** readback in the current built-in
Browser surface found no `data-color-mode` or injected background token.
The observed dark palette therefore came from the system fallback. This
does not prove mirroring of an independently selected Copilot app theme.
That observation was initially overgeneralized as a platform limitation.
The later comparison with Agent Review exposed missing body-level theme
handling in our CSS. The viewer now resolves semantic aliases on both root
and body and follows either node's `data-color-mode`, with root precedence.
Real-browser regressions cover all four root/body mode/token combinations,
token-only changes, app-versus-OS precedence, focus colors, and fallback
restoration. CSS selectors provide live updates without a polling bridge.
The observed ordinary Browser page still exposes no tokens on either node;
actual extension hosting remains separate from those contract tests.
Zoom controls were not included in this original safe-verification pass.

#### Subsequent native-canvas result

The project now registers `camera-grid` with the same documented SDK canvas
pattern used by the user's Agent Review and hanselprojects extensions.
It shares the standalone viewer's loopback/capability-scoped server and adds
75%-200% tile zoom with Auto fit. It reads existing batch files only.

Actual renderer readback identified a second issue: the original CSP blocked
the host's inline theme stylesheet with `style-src-elem`. The server now allows
inline **style elements**, while retaining self-only scripts, connections and
images. A real-browser regression confirms host styles apply and injected
inline scripts remain blocked. Agent names still render through `textContent`,
not HTML. This is not a private host bridge or a global CSP disablement.

After reloading the native canvas, readback reported background token
`#371d32`, computed background `rgb(55, 29, 50)`, foreground
`rgb(242, 226, 241)`, the host font stack, and no CSP violations. Previously
the fallback was `rgb(13, 17, 23)` with no token. The live host supplies theme
tokens even when mode attributes are absent; the diagnostic therefore marks
that as host colors and leaves an unspecified mode unknown rather than
mislabeling the OS preference as the app mode.

`get_view` returns bounded renderer-reported appearance and page-activity
counts. The report stays in memory; no evidence files are changed. This
observation proves actual palette integration on this app build, not just
that a mocked token can change CSS.

### What remains unproved

Offline fault coverage can establish ordering, bookkeeping, ownership
refusals and evidence-verdict behavior under the model. It cannot prove the
correctness of the experimental Windows contract, timing of native teardown,
kernel/driver behavior, or host stability.

Proxmox research is saved separately in
[source-grounded reference learnings](proxmox-reference-learnings.md). No
Proxmox infrastructure or Deskhand integration was added.

# MXC IsolationSession visual spike

**HOST-CRASH WARNING:** the third concurrent Full-session run ended in
`0xEF CRITICAL_PROCESS_DIED` after its workers finished. Do not run these live
experiments on a primary machine while the Windows lifecycle investigation
is unresolved. Offline tests/replay are not proof that the native APIs are
safe. The first safe-only verification pass deliberately left the installed
service unchanged; the later user-approved candidate update is recorded below.

**Post-update follow-up:** the user installed a candidate Windows fix,
rebooted, and approved a new pinned broker package. See
[Windows update qualification and distribution plan](../../docs/windows-update-qualification.md)
for exact binary identities, installation, initial live evidence and remaining
warnings. This does not retroactively change the failed crash-run verdict.

**Current cleanup hold:** profile classification suppressed the observed shell
startup crashes, but a broader read-only audit found retained companion
profiles under a different SID family. The earlier account-SID-only cleanup
checks missed these. Do not start further native batches until supported
companion cleanup is established; do not remove profiles based only on names
or inferred SID relationships. Offline capture-policy tests remain usable.
Client recovery now persists SDK deprovision outcomes and retains receipts
when removal is uncertain or returns warnings instead of acknowledging them
as clean. A warning-free SDK response still does not verify companion cleanup.
The [tiny SDK-only repro](../../docs/pure-mxc-profile-repro.md) demonstrates
retention without the custom broker.

Experimental code; see [the handoff](../../docs/visual-test-farm-spikes.md)
for goals, results and the evidence contract. Not used by sandbox-mcp itself.

**Earlier single-session outcome:** an owner-only broker was installed and two ordinary-user
runs produced changing WinUI pixels without per-run UAC. Start with
[the setup guide](#setup-build-and-run-start-here) and
[its host validation](#installed-host-validation-2026-11-09). The earlier
sections below preserve the experiments in chronological order, including
failures that the later System-context path resolved.

Read [the Proxmox reference learnings](../../docs/proxmox-reference-learnings.md)
for source-verified lifecycle/capture ideas and limitations; no upstream code
was copied.

**Historical safe-only work after the crash:** [offline lifecycle tests, replay and findings](../../docs/full-session-safe-verification.md).
The replay uses copies of preserved images only and explicitly retains the
failed source-run verdict. That hardening was later included in the explicitly
approved candidate broker update; it did not alter the original evidence.

## Setup, build, and run (start here)

**Build/package support is not native runtime qualification.** Both `win-x64`
and `win-arm64` broker, worker and capture-helper payloads can be built with
the .NET SDK. The private credential ABI is currently inspected/pinned for
ARM64 only. An x64 package is a build/inspection artifact, **not an enabled
x64 Full-session broker**. Do not install/run it to try to bypass that limit.
Even ARM64 `experimental_available` means eligible for staged testing, not
proof that concurrent Windows session creation/teardown is safe.

### 1. Prerequisites and architecture

- Use a disposable Windows test host with the experimental IsolationSession
  and WTS APIs. Updating to the latest stable Windows does not guarantee
  these APIs exist. A VM still needs the required Windows capabilities; this
  spike is not a Windows guest image.
- Use **PowerShell 7+**, the **.NET 10 SDK**, and stable **Node 24.21+ or
  26.8+**. The MXC SDK rejects prerelease Node. Use the native Node/SDK for the
  host architecture, rather than accidentally using an emulated shell's tools.
- On the **execution host**, install the .NET 10 **Windows Desktop Runtime**
  matching the broker RID; it supplies the helper's Windows Desktop and the
  broker/worker's .NET runtime dependencies. The payloads are framework-dependent.
  Cross-publishing does not require the other architecture's runtime, but
  executing its EXE does. An ARM64 runtime cannot run a `win-x64` apphost.
- The WinUI fixture is self-contained for .NET and Windows App SDK, but those
  settings do **not** bundle the VC++ runtime. Supply licensed app-local
  release CRT DLLs as in [the sample build](../../samples/WinUI/README.md), or
  install the current Microsoft VC++ v14 Redistributable for the **fixture**
  architecture on the test host. The current x64 fixture needs x64 CRTs even
  when the broker/helper are ARM64.
- First builds need access to the locked NuGet/npm dependencies. Existing
  restored outputs can be reused; do not install Windows features, change
  policies, or acquire elevation just to compile/package.

PowerShell 7 applies to this spike's packaging and focused packaging tests,
not every test in the repository. The main guest unit checks deliberately
use **Windows PowerShell 5.1** for `TypeLibConverter` and
`ITypeLibImporterNotifySink`, which are absent from PowerShell 7/.NET 10.
Do not replace those test subprocesses with `pwsh` or interpret the packaging
tests below as full-repository test coverage.

From the repository root, in an ordinary-user PowerShell terminal:

```powershell
Set-Location .\spike\mxc-visual
$ErrorActionPreference = 'Stop'
[Runtime.InteropServices.RuntimeInformation]::OSArchitecture
[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
dotnet --info
node -p "process.version + ' ' + process.arch"
npm ci  # first setup, or after the lockfile changes
```

`OSArchitecture` describes Windows; `ProcessArchitecture` describes this
PowerShell process. On ARM64 Windows an emulated x64 terminal can report
`X64` for the latter. The packager defaults to the **OS** architecture, not
the shell architecture; use an explicit RID for reproducible/cross builds.
`arm64` means 64-bit ARM, not `win-arm`/32-bit ARM.

| Target Windows | Broker / worker / helper RID | Current WinUI fixture | Native Full-session eligibility |
|---|---|---|---|
| x64 | `win-x64` | `win-x64`, native | **Blocked**: x64 private credential ABI not qualified |
| ARM64 | `win-arm64` | `win-x64`, explicit Windows x64 emulation | Only with matching credential ABI and required exports; staged testing only |

The fixture project deliberately targets x64 today. Do not change only its
RID to ARM64 while retaining x64 `PlatformTarget` or copy x64 native DLLs into
an ARM64 fixture. The packager can check a separately prepared native ARM64
fixture, but the sample's native ARM64 build/run is not established here.

### 2. Build the fixture, then package the three native components

The broker uses the **unpackaged** fixture, not an installed/signed MSIX. A
fresh build needs the sample's Windows App SDK/Windows SDK build-tool packages;
`dotnet restore --locked-mode` uses its committed lockfile. Skip restore if
the same dependencies are already restored:

```powershell
dotnet restore ..\..\samples\WinUI\HelloWinUI.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed' }
dotnet publish ..\..\samples\WinUI\HelloWinUI.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore -o ..\..\samples\WinUI\bin\unpackaged
if ($LASTEXITCODE -ne 0) { throw 'Fixture publish failed' }
```

This does not launch the app, sign/install an MSIX, or provision a session.
Use the complete publish directory (including XBF/PRI and native DLLs).
The sample's `build.ps1` additionally copies the VS C++ x64 release CRT and
builds/signs an MSIX; that route requires its documented VS tools and `winapp`,
but MSIX packaging/signing is unnecessary for this spike. A plain publish
without app-local CRTs is accepted with a warning and records
`fixtureVcRuntime: "system-required"`; otherwise it records `"app-local"`.
The package validator does not prove that the target host has the system CRT.

Choose **one** package command, or run both sequentially to cross-build:

```powershell
# x64 payload; no x64 private-ABI enablement is implied.
$package = & .\broker-package.ps1 -RuntimeIdentifier win-x64 | Select-Object -Last 1

# ARM64 payload with the existing x64 fixture under Windows emulation.
$package = & .\broker-package.ps1 -RuntimeIdentifier win-arm64 -AllowX64FixtureEmulation | Select-Object -Last 1
```

Each command publishes `broker\FullBroker.csproj`, `headless\HeadlessSpike.csproj`
and `helper\CaptureHelper.csproj` in order, copies the fixture, checks EXE
machine types, dependency RIDs, runtime configuration and fixture dependencies,
then writes the SHA-256 manifest. It never installs or starts a service.
Output is a new `bin\broker-package-<RID>-<UTC timestamp>\` directory:

```text
manifest.json
broker\SandboxMcp.FullBroker.exe  (+ managed/runtime configuration files)
broker\package-architecture.json (hashed architecture/runtime metadata)
worker\HeadlessSpike.exe         (+ managed/runtime configuration files)
helper\CaptureHelper.exe         (+ managed/runtime configuration files)
fixture\HelloWinUI.exe           (+ the entire unpackaged fixture)
```

The hashed `broker\package-architecture.json` records the target/fixture RIDs,
`"native"` or `"x64-emulation"`, and the fixture CRT requirement. The installer's
strict schema-1 manifest remains unchanged (`schema` and `files` only).
Without the explicit emulation switch, a mismatched fixture fails **before**
publishing. An ARM64 fixture is never accepted for an x64 package. Unsupported
RIDs, mixed native components and reused output directories fail closed.
`-FixtureDirectory <directory>` selects an existing complete fixture;
`-Output <new-directory>` selects a fresh output. A failed publish leaves an
incomplete directory **without** a manifest: preserve its diagnostics and use
a new directory for the retry, never install it.

For separate development builds (not an installable package):

```powershell
$rid = 'win-arm64' # or win-x64; these three components always use the same RID
dotnet publish .\broker\FullBroker.csproj -c Release -r $rid --self-contained false -o "bin\$rid\broker"
dotnet publish .\headless\HeadlessSpike.csproj -c Release -r $rid --self-contained false -o "bin\$rid\headless"
dotnet publish .\helper\CaptureHelper.csproj -c Release -r $rid --self-contained false -o "bin\$rid\helper"
```

Check each exit code before continuing. Do not run SDK builds/publishes
concurrently against the same checkout: restore/intermediate files are shared
even when the final output directories differ. Coordinate with anyone building
an install candidate. Do not overwrite packages a running broker is using.

### 3. File-only verification, then read-only host diagnosis

These commands do not call experimental APIs, use the client workflow lock,
install a service, or require the target architecture's runtime:

```powershell
.\broker-package.ps1 -VerifyPackage $package
pwsh -NoProfile -File .\test\broker-package.test.ps1
```

Offline verification rechecks architecture metadata and every file hash.
`nativeCompatibilityChecked: false` is intentional. The focused tests use
synthetic files and a mocked publisher; they never invoke `dotnet` or Windows
session/account operations.

On the **matching target host**, with its matching .NET runtime:

```powershell
$broker = Join-Path $package 'broker\SandboxMcp.FullBroker.exe'
& $broker doctor
# Only after doctor is eligible for staged testing:
& $broker verify-package $package
```

Unlike the PowerShell file-only verifier, the broker's `verify-package` also
requires native host compatibility. `doctor` checks exports/binary identity,
not session lifecycle safety, and exits nonzero when blocked:

| Code | Meaning / next step |
|---|---|
| `experimental_apis_missing` | Required exports unavailable. Update Windows/restart if requested, then check again; availability depends on build/channel. |
| `architecture_not_validated` | APIs may exist, but this process architecture is not qualified. x64 needs an inspected **x64** IsoSessionCore ABI and separately approved evidence on a genuine x64 Windows host. ARM64 x64 emulation is not that evidence. |
| `experimental_build_not_validated` | The undocumented credential ABI's inspected binary identity changed. Stop; obtain a qualified release, not a bypass. |
| `experimental_available` | Required exports and inspected private credential ABI are present. Available for staged testing, **not validated** lifecycle behavior or production/native-safety certification. |

Missing exports take precedence over architecture diagnostics. Do not invent
an x64 hash, copy/downgrade system DLLs, disable gates, or remove the credential
pin. The WTS/service availability gate checks required exports with
`NativeLibrary.TryGetExport`; WTS/internal service DLLs are **not** hash-pinned.
Only the undocumented IsoSessionCore credential ABI retains an exact SHA-256
pin, alongside the ARM64 process-architecture guard.
A Windows update can legitimately change service implementations;
rerun diagnosis and staged qualification after servicing. A change to
IsoSessionCore still blocks the private call until its ABI is re-established.
See [the update qualification plan](../../docs/windows-update-qualification.md).

### 4. Explicit one-time native UAC install, then one ordinary-user run

**Stop here unless the test-host owner explicitly approves native testing.**
Read the crash warning above first. Package validation alone is not approval.
When updating an existing installation, coordinate a quiet window with no
live client/batch, inspect its status, and reconcile pending work before any
update. Do not update/uninstall as a second workflow owner.

```powershell
# Deliberate privileged action: one Windows UAC prompt, same owning account.
& $broker install $package
if ($LASTEXITCODE -ne 0) { throw 'Installation did not complete; inspect the result before retrying' }

# Ordinary user: inspect the INSTALLED broker (not necessarily your latest build).
node broker-client.mjs status
Get-Content "$env:ProgramFiles\SandboxMcpFullBroker\current.json"
Get-FileHash "$package\manifest.json" -Algorithm SHA256 # compare with current.json's release
# Only when ready with no pending/uncertain work:
node broker-client.mjs run-fixture
node broker-client.mjs status
```

The System service belongs to the installing account. Elevation as a different
administrator is unsupported. Subsequent runs need no UAC. Compare installed
status and `current.json` release with the intended package rather than assuming a fresh build
changed the service. Single-run evidence is written to
`evidence\broker-<request-id>\`; inspect `visual\` before/after PNGs,
`broker-result.json` and `client-record.json`. Confirm distinct requested UI
states, authorization checks and account/session/profile cleanup, not just
process exit. Preserve failed evidence.

`status` is a read-only broker operation; the Node wrapper may initialize its
local state directory. `doctor` diagnoses the binary you invoked. Neither
creates or recovers sessions. **`recover` is not read-only**: it tears down owned
pending sessions/accounts. Run `node broker-client.mjs recover` only in an
approved, exclusive recovery window. Never delete uncertain ownership records
to make the status look clean.

### 5. Only then consider a coordinated grid and local viewer

After an approved single run and confirmed cleanup, the next **separately
approved** native step is three coordinated sessions, not arbitrary 4/8/16
concurrency. Use a fresh evidence directory:

```powershell
# Terminal 1: preparation and read-only viewer do not create native sessions.
node variant-run.mjs --prepare evidence\grid-attempt-1
node camera-grid.mjs evidence\grid-attempt-1
# Open the printed capability-scoped loopback URL; keep this terminal running.

# Terminal 2, same spike directory: this starts the approved native batch.
node variant-run.mjs evidence\grid-attempt-1
node broker-client.mjs status
```

Inspect the final `report.json`, `report.md`, per-variant frames and cleanup.
Keep the viewer URL private; stop only your viewer process when finished.
The viewer can reopen a completed batch without rerunning native work; recorded
tiles are not live sessions. All 30 frames/member must be present, valid and
collected. New batches default to average >=0.9fps and >=20 seconds common
coverage, but have **no hard maximum-frame-gap cutoff**. Long gaps remain
visible timing warnings, not silently discarded measurements. Larger viewer
layouts/replay do not establish native capacity or make failed batches pass.

### Capture timing policy

Select tolerances **before** starting a run. A policy JSON file accepts:

```json
{
  "schema": 1,
  "minAverageFps": 0.9,
  "maxFrameGapMs": null,
  "minOverlapMs": 20000
}
```

`minAverageFps` may be a number in `(0,1]` or `null` for report-only rate.
`maxFrameGapMs` may be a positive integer or `null` for report/warn-only gaps.
`minOverlapMs` is a nonnegative integer. Integrity, ownership, frame count
and cleanup checks cannot be disabled by this policy.

```powershell
node variant-run.mjs --prepare evidence\grid-policy-1 --policy capture-policy.json
# Later execution uses the policy saved in batch.json, not a new CLI override.
node variant-run.mjs evidence\grid-policy-1

node broker-client.mjs run-fixture light evidence\single-policy-1 --policy capture-policy.json
```

For the former strict behavior, explicitly select `"maxFrameGapMs": 2000`.
The selected policy and any timing warnings appear in the report. Older
prepared batches without a policy and historical crash replay retain the
legacy strict interpretation. Existing failed reports are never rewritten as
passing merely because a newer default is more flexible.

The source capture helper now skips overdue schedule slots rather than
capturing a burst to catch up after a stall. It separately records rendering,
PNG publication (encoding/flush/rename), and schedule-delay timings. The old
`captureMs` combined those phases, so previous long captures cannot be
attributed specifically to `PrintWindow` rather than file publication.
This helper change requires a new approved installed package; offline clock
tests are not evidence that the actual stall has been removed.

Do not run `test\broker-client-lock.test.mjs` or a broad spike test glob while
live workflows are active: that test deliberately acquires the real owner lock.
Use focused file-only tests during live work; reserve full offline suites for
an agreed quiet window. The historical experiments below are **not** an
automatic setup checklist.

## Viewer and historical experiments

The viewer has compact fit-to-count layouts for 4/8/16 recorded image tiles
and semantic theme-token support with system-appearance fallback. Detailed
evidence is expandable; supplied names render as text. Host mode and semantic
tokens are supported on either the root or body, including live token/mode
changes and precedence over the OS theme. The project extension in
`.github/extensions/camera-grid` hosts the same viewer in a native extension
canvas, the theme-aware path used by Agent Review and hanselprojects.
Open `camera-grid` with `{ "batch": "<directory under spike/mxc-visual/evidence>" }`.
`get_state` reads evidence; `get_view` reports only the renderer's public theme
tokens and page activity. Neither creates sessions. Theme readback is bounded
in-memory diagnostic data, never an evidence-file write or execution endpoint.
The standalone Browser/CLI path remains available with OS fallback.
Tile zoom ranges from 75% to 200%, with **Auto fit** restoring count-responsive
sizing. Zoom does not modify screenshots, fixture scale, or native capacity.
The host mirrors its palette with an inline stylesheet: the viewer permits
inline style elements but not inline scripts. `get_view` can distinguish
actual injected host tokens from OS fallback and surface CSP rejection.

Requires Windows with the IsolationSession backend, .NET 10 SDK, and **Node
24.21+ or 26.8+** (prerelease Node fails the SDK's native-stdio check).

```powershell
cd spike\mxc-visual
npm install
dotnet publish helper -c Release -r win-arm64 --self-contained false -o bin\helper  # or win-x64
node probe.mjs                     # Stage 0: backend availability
node smoke.mjs                     # Stage 1: lifecycle, whoami, session shape
node winui.mjs                     # WinUI sample (build samples\WinUI first)
node winui.mjs ..\..\samples\WinForms\bin\publish WinForms.exe winforms "--invoke Greet"
node winui.mjs ..\..\samples\Wpf\bin\publish Wpf.exe wpf "--invoke Greet"
```

Each run provisions one session, stages the app and `helper\` into the
ephemeral workspace, captures before/after a UIA-driven state change, copies
PNGs plus `record.json` to `evidence\<run>\`, then stops and deprovisions.

`helper\CaptureHelper.exe` runs inside the session:
`capture --name <proc> --out <dir> --label <tag>`, `uia --name <proc> --set Id=text --invoke Id1,Id2`, `windows`.

## Experimental WTS Full headless sessions

This independent native-API spike tests the user-supplied
`EXPERIMENTAL_WTSCreateHeadlessSession` contract, not an MXC backend switch.
It never changes host desktop settings or stops an existing session.

```powershell
dotnet publish headless -c Release -r win-arm64 --self-contained false -o bin\headless
.\bin\headless\HeadlessSpike.exe probe
.\bin\headless\HeadlessSpike.exe run full caller evidence\headless-attempt-1 ..\..\samples\WinUI\bin\unpackaged\HelloWinUI.exe bin\helper\CaptureHelper.exe
```

Use a fresh output directory for each attempt. Replace `full` with `utility`
for a control, or `caller` with `console` to select the other parent sentinel.
Optional `no-program` skips the initial worker, and `padded-size` tests the
padded struct size instead of the header's V1 size. The latter is an explicitly
non-header-conforming ABI comparison, not a recommended parameter value.

The host writes `host.json` before and after the call. If creation succeeds,
the initial worker launches the sample and capture helper in that session,
then writes `worker.json`; the host waits at most 90 seconds after creation
and stops its owned session in `finally`. Capture subprocesses have individual
30-second timeouts. `LogonTimeoutMs` is 20000; the native creation call itself
is synchronous and cannot be forcibly cancelled by this harness.

**Initial result:** both exports exist on build 28000.3231, but creation with
null `pAuthIdentity` / zero `cbAuthIdentity` returns Win32 error 87 for Full and
Utility. No session was created, so the worker/capture path has not run.
This default mode deliberately does not collect, guess, or persist credentials.
At this stage, authentication requirements and Full-session DWM were unknown.

### MXC-owned agent account

```powershell
node headless.mjs full caller
node headless.mjs utility caller
node headless.mjs full console
```

This wrapper uses `provisionContainer` without `startContainer` to create the
agent user and shared workspace. It stages the app/helper there, passes an
SSPI-validated EX2 identity naming that user (no password/credential payload)
to WTS, preserves evidence, then calls `deprovisionContainer` in `finally`.
It does not reset passwords or create local users manually.

**Follow-up result:** EX2 validation succeeds and WTS now returns error 5,
Access is denied, for both kinds and both parent choices tested. No WTS
session was created; MXC removed every provisioned account. Raw WTS permissions
were unresolved at this stage. A user-authorized RunAs-launched Full-session control
returned the same error 5; elevation alone did not resolve it. This does not
prove that the identity could authenticate with the required caller contract.

### Build-pinned local-agent credential experiment

```powershell
node headless.mjs full caller local-credential
.\bin\headless\HeadlessSpike.exe metadata
```

The opt-in mode calls `IsoSessionGetLocalAgentLogonCredential` for the
just-provisioned account. Its experimental ABI was derived from matching
Microsoft public symbols, static argument checks and the Windows broker's
call site; it is **not a published SDK contract**. `LocalAgentCredential.cs`
pins the inspected ARM64 DLL's SHA-256 and refuses other builds. See the
handoff for provenance, argument meanings and paired cleanup evidence.

Only the caller's own user SID and the broker's observed `"unknown"` app
scope are used. Credential contents never enter logs, arguments, managed
strings or evidence files. If retrieval succeeds, SSPI validates/marshals
the identity, the harness requires EX2, then invokes WTS. Serialized EX2 is
validated through the size-aware
`SspiUnmarshalAuthIdentity` API; the serialized allocation may legitimately
be longer than `cbStructureLength`. Native buffers
are zeroed and freed. Retrieval failure stops before WTS; no fallback is
silently substituted.

**Observed:** retrieval returned `0x80070522` (required privilege not held)
and a null pointer. No credential was obtained and no Full session was
created. The ordinary MXC session lifecycle succeeded under the same
unelevated caller. Both disposable accounts/profiles were removed. The
credential-success and Full-session capture paths were unexercised in this trial.
No elevation, privilege enabling, service changes or token borrowing were
attempted in this trial. The caller contract was established later below.

`metadata` reads the installed `Windows.AI.winmd` without activating its
classes. This build exposes no credential getter or Full-kind selector in
the inspected IsolationSession interfaces.

### Independently authenticated WTS control

```powershell
.\bin\headless\HeadlessSpike.exe credential-selftest
.\bin\headless\HeadlessSpike.exe controlled-account evidence\controlled-attempt-1 ..\..\samples\WinUI\bin\unpackaged bin\helper
```

The first command is synthetic and creates no account or session. It checks
Kerberos packing, the distinct online-ID EX2 format, and a local-password
EX2 whose username/password are verified through SSPI normalization. It
prints only metadata and comparison results, not credential material.

The second command requires **explicit approval for elevated temporary-account
setup and cleanup** and displays the native UAC prompt. Its parent must be
unelevated. It creates one temporary standard local user, verifies the random
password with `LogonUserW`, tries Full then Utility with local-password EX2,
and removes the account/profile through the setup helper. The elevated helper
does not perform logon checks, WTS calls or app execution. A private pipe
restricted to the caller SID and peer process ID transports the temporary
password in memory; no credentials are saved.

The account lease ends on parent disconnect or after ten minutes. Failures
are recorded, not silently treated as cleanup success. Evidence is copied
from an ACL-restricted staging directory under Public Documents. The report
identifies that directory; remove only that exact owned directory after
checking helper/session teardown and evidence copy, never the parent folder.

**Observed:** ordinary `LogonUserW` succeeded, while Full and Utility WTS
creation each returned error 5 without creating a session. The account,
staging directory and any profile were confirmed absent afterward. This
eliminates missing credentials as the sole explanation, but does not establish
which WTS caller/account/policy requirement denied the request.

### Initial-program / RPC authorization control

```powershell
.\bin\headless\HeadlessSpike.exe initial-program-control evidence\initial-program-attempt-1 ..\..\samples\WinUI\bin\unpackaged bin\helper
```

This unelevated negative control creates no account. It confirms a generated
local account name does not exist, then compares Full/Utility with and without
the initial worker. All four calls returned error 5 on the tested build.

The subsequent matched-symbol investigation mapped both RPC operations to
**private System-caller security callbacks, with the administrator fallback
disabled**. A client-only debugger trace confirmed non-null bindings followed
by RPC exception 5, not a normal server-method return. See the handoff for
exact interface UUIDs, operation numbers, module hashes and evidence.
Do not treat these exports as ordinary desktop-caller APIs on this build.

There is a second constraint: the default initial-executable allowlist covers
`%SystemRoot%\System32`, not our workspace. No allowlist or feature was changed,
and no System token was borrowed. Successful Full creation and capture were
unverified until the separately approved System-context controls below.

### Explicitly approved System-context control

```powershell
node system-session.mjs
node system-session.mjs --visual  # separate approval: fixed WinUI + UIA/capture test
```

Run only with explicit approval for a temporary LocalSystem service. The
normal caller provisions one MXC agent; an elevated installer creates a
uniquely named, demand-start service for this fixed experiment. It retrieves
only that agent's identity on behalf of its provisioning owner, requests
Full with **no initial program**, samples DWM/session-process presence,
stops its owned session, and removes the service. No network listener,
arbitrary command execution, token borrowing or policy changes.

The wrapper verifies service absence before deprovisioning. If service state
cannot be confirmed, it retains the account and records the recovery state
instead of blindly retrying or claiming cleanup. Review `record.json`,
`service-install.json`, and `system-probe.json` in the printed evidence path.

**Observed:** the first approved run retrieved the credential but stopped at
an over-strict harness length check. The corrected size-aware validator has
synthetic positive and truncated-input controls. A second separately approved
run **created Full session 13 in 1518 ms**, observed DWM/Winlogon/CSRSS and later
Userinit/Explorer, then stopped the session and removed the service, account
and profile. Independent cleanup checks passed.

For these experiments, each temporary service run needs explicit approval.
A third, separately approved `--visual` run **captured changing WinUI pixels**
in Full session 14. The service verified the owned session token and launched
only the fixed worker as the disposable agent using `CreateProcessAsUserW`.
PrintWindow flag 2 produced visually inspected before/after client images:
`Hello, Sandbox!` / `Count: 0` became `Hello, FullSession!` / `Count: 2`.
The app's "Windows Sandbox" heading is fixture text, not the execution backend.
Whole-screen and window-region GDI capture failed; flag 0 produced a black
client area. WGC reported support but no WGC frames were captured.

All owned app/worker/session/service/account/profile resources were removed.
The images, UIA reports and cleanup receipts are under
`evidence\system-full-2026-11-10T02-47-23-206Z\`. This remains a build-specific
experimental path, not a production broker or a verified parallel Full farm.

The repeated UAC prompt is a consequence of the spike's install/remove-per-run
design, not a requirement for each screenshot. A one-time-installed,
administrator-protected broker with caller-authenticated local IPC could
remove per-run UAC. The separately installed implementation below now provides
that design; do not leave the temporary service pointing at user-editable
repository binaries.

## Owner-only installed broker (experimental)

`broker\` is a separate, minimal service executable; it does not expose the
one-shot spike's administrative commands over IPC. Installation is scoped to
the current Windows account. The first protocol supports only the fixed WinUI
fixture, status, result retrieval, recovery and acknowledgement.

```powershell
# ARM64 example; see the setup guide for x64 builds and private-ABI limits.
# The current fixture is x64 and runs under Windows emulation.
$package = & .\broker-package.ps1 -RuntimeIdentifier win-arm64 -AllowX64FixtureEmulation | Select-Object -Last 1
& "$package\broker\SandboxMcp.FullBroker.exe" verify-package $package

# Explicit approval required: one UAC install/update, automatic System service.
& "$package\broker\SandboxMcp.FullBroker.exe" install $package

# Ordinary-user commands; use the supported Node version described above.
node broker-client.mjs status
node broker-client.mjs run-fixture
node broker-client.mjs recover

# Explicit privileged teardown. Run recovery first.
& "$package\broker\SandboxMcp.FullBroker.exe" uninstall
```

The development installer requires elevation as the same Windows account;
over-the-shoulder installation by a different administrator is not supported.
Its fixed paths are `%ProgramFiles%\SandboxMcpFullBroker` and
`%ProgramData%\SandboxMcpFullBroker`. Payloads are versioned, hashed and
administrator/System-owned; ordinary users cannot replace privileged code.
The installed service is `SandboxMcpFullBroker`, automatic start, LocalSystem.
The package includes the self-contained fixture, so it is much larger than the
broker itself. No service is installed by packaging or unit tests.

The pipe rejects remote clients, checks the authenticated caller SID, and
accepts no executable paths, commands, credentials or privileged output paths.
The normal client verifies the pipe server PID against SCM before sending.
The service writes only private journals; the fixed worker writes screenshots
as the agent. A second pipe with the same access policy lets that agent prove
it cannot access the owner-only endpoint.

The client owns MXC account provisioning/deprovisioning and keeps durable
opaque-ID records under its actual Windows LocalAppData directory. A kernel
file-sharing coordinator prevents concurrent client workflows; it releases
automatically on process exit. Uninstall holds the same coordinator and refuses
pending client records before asking for elevation.

The service journals before WTS creation, verifies session token ownership
before launch/teardown, and reconciles interrupted creation on startup.
Journal creation/update remnants are reconciled without discarding committed
state. Acknowledgement uses a rename-to-cleanup marker so interruption cannot
erase the sole recovery record. Unknown session ownership or corrupt state
blocks further creation rather than being treated as cleanup success.

If provisioning is interrupted before MXC returns its opaque ID, the client
cannot invent that ID or safely delete an unknown account: the uncertain record
is retained and further runs require reconciliation. A failed or ambiguous
response is never retried as a fresh create automatically.

The broker fails closed for an unqualified process architecture or a changed
undocumented IsoSessionCore credential ABI. WTS exports are checked; WTS and
internal service DLLs are not hash-pinned.
It is not a stable Windows API contract, a multi-user service, a generic
System process launcher, or a verified parallel Full-session farm.
Installation, native IPC, and repeated no-UAC execution must be validated on
the target host; passing local tests alone is not that proof.

### Installed-host validation (2026-11-09)

The original release below used a five-DLL hash gate. That is historical:
the current export-based availability check and private-credential-only hash
pin are described in the setup guide above.

With explicit user approval, release
`814A137226D9485CEA0F50B1B1EBEC6B9D4D4742487D8D5CA846D8F52673EAF5`
was installed as `SandboxMcpFullBroker`, automatic start, LocalSystem, scoped
to the approving Windows account. The installer elevated once. Two subsequent
`run-fixture` calls were made from the ordinary user without elevation/UAC:

- Sessions 15 and 16 were created in 5105 ms and 4517 ms respectively.
  These are two observations, not a startup-performance benchmark.
- Both runs produced visually inspected flag-2 PNGs with the greeting and
  counter changing as requested. The agent's owner-only pipe denial check
  passed in both runs.
- Both agent accounts, profile directories/registry entries and Full sessions
  were removed. Both broker and client recovery queues were empty afterward.
- Actual ordinary-user writes to the protected installation and state
  directories were denied. Registry inspection confirmed automatic start and
  LocalSystem; the broker remains installed intentionally.

Evidence is in `evidence\broker-cd9d766b27fa460e8dae9aa9843aa720\`,
`evidence\broker-b9b2afe315294b73871644ccec6f7ba2\`,
`installed-broker-validation.json`, `installed-broker-status.json`, and
`installed-broker-profile-cleanup.json`. A final read-only integrity check
verified all 537 installed files against the package with no mismatches
(`installed-broker-final-integrity.json`); the service was ready with no
pending jobs. The final package is retained at
`bin\broker-package-20261110-041038`; superseded generated packages were removed.

The host's default prerelease Node does not satisfy the MXC SDK gate. On this
host, the previously provisioned portable runtime works:

```powershell
$node = "$env:LOCALAPPDATA\sandbox-mcp-spike\node-v24.22.2-win-arm64\node.exe"
& $node .\broker-client.mjs status
& $node .\broker-client.mjs run-fixture
& $node .\broker-client.mjs recover
```

The packaged C# checks (41) and Node checks (10) passed. An independent
pre-installation review found two cleanup races; both were fixed with
regressions. Journal interruption is tested with filesystem simulations.
Live forced-service-crash/reboot recovery and actual upgrade/uninstall remain
unexercised. This is a working installed experimental broker, not a claim of
production readiness or of completed parallel-farm support.

Local checks (no installation):

```powershell
dotnet build broker -c Release
.\broker\bin\Release\net10.0-windows\SandboxMcp.FullBroker.exe selftest
node --test test\broker-client-state.test.mjs test\broker-client-lock.test.mjs
```

Do not run `broker-client-lock.test.mjs` concurrently with live client
workflows: it deliberately exercises the real owner workflow lock and must
fail to acquire it while a live run holds it. Run the full offline suite
between live batches, not as a second native-workflow owner.

### Coordinated camera-grid prototype

The next protocol revision adds one bounded `run-grid` request containing
exactly three unique owned accounts and the fixed `light`, `dark`, and
`dark-large` variants. All sessions are created and journaled before the
workers launch together. Recovery attempts every independently owned member
even if another member cannot be cleaned; uncertain records remain visible.
This is not arbitrary or unlimited multi-session execution.

The ordinary client collects immutable, numbered frames at a 1fps target.
The read-only loopback viewer displays fresh frames, stale/error states, and
recorded final tiles. It can be opened in Copilot's built-in Browser canvas
or an ordinary browser; no new canvas extension is required.

```powershell
# Build the changed sample without running the packaging/signing steps:
dotnet publish ..\..\samples\WinUI\HelloWinUI.csproj -c Release -p:Platform=x64 --no-restore -o ..\..\samples\WinUI\bin\unpackaged
$package = & .\broker-package.ps1 -RuntimeIdentifier win-arm64 -AllowX64FixtureEmulation | Select-Object -Last 1
& "$package\broker\SandboxMcp.FullBroker.exe" doctor
# Explicit approval required for the privileged installed-service update:
& "$package\broker\SandboxMcp.FullBroker.exe" install $package

# Choose a new batch folder; the two long-running commands use separate terminals.
node variant-run.mjs --prepare evidence\grid-attempt-1
node camera-grid.mjs evidence\grid-attempt-1  # prints a capability-scoped loopback URL
node variant-run.mjs evidence\grid-attempt-1
```

The viewer process is an ordinary-user process, not a System service. It has
no execution endpoint and serves only allowlisted report/frame files. Stop
that specific process after viewing. Completed tiles are explicitly recorded,
not live. The batch writes `report.md` and `report.json`; the keep bar is all
30 frames per member, average >=0.9fps, maximum gap <=2000ms, and >=20 seconds
of common frame coverage across three distinct sessions.

Actual window DPI and XAML rasterization scale are measured. `dark-large`
uses a 1.5x **fixture render transform**, not a changed monitor DPI. No host
theme, display setting or initial-program allowlist is changed.

`doctor` and the broker's readiness response now distinguish missing
experimental API exports from an unvalidated build/architecture. Missing-API
guidance recommends current Windows updates and a restart when requested.
The latest stable Windows is **not guaranteed** to expose these experimental
APIs. An uninspected private credential ABI or architecture remains blocked;
ordinary WTS/service DLL servicing does not require the old five-DLL hashes.
The ready code is `experimental_available`, not a validated-lifecycle verdict.
The checks do not recommend copying DLLs, disabling gates or removing ABI pins.

The coordinated path has local regression coverage but its runtime outcome
must come from the generated batch evidence, not from these instructions.

The first coordinated attempt (`evidence\grid-20261110-0456`) demonstrated
three simultaneous Full sessions (17/18/19) and live viewer frames, but
**failed** before 30 frames per member. Replacing mutable `live.json` failed
with Access denied; a failed worker caused bounded cancellation and cleanup
of all three accounts/sessions. Inline UIA updates also stalled capture.
The failed report is retained, not counted as a passing 1fps run.

The corrected stream publishes immutable `frame-meta-NNN.json` files next to
immutable PNGs, avoiding replacement of a file another account may have open.
UIA verifies the greeting/counter before capture; an app-side timer animates
a fixture heartbeat while the capture loop does only capture/publication.
Actual monitor DPI is still unchanged. The cadence/overlap keep bar is not
lowered to accommodate a failed run.

The second attempt (`evidence\grid-20261110-0503`) captured all 30 frames in
each of sessions 20/21/22 and cleaned up, but still failed the overall keep
bar: normal-user viewer progress replacement encountered the same Windows
file-replacement problem, and one large-variant frame gap was 5843 ms.
Average cadence alone (about 1fps in each stream) was not counted as a pass.
Viewer progress and batch state now use immutable snapshots as well.
This last correction is ordinary client/viewer code, not a service update.

## Experimental microVM and export discovery

`microvm.json` and `hyperlight.json` are bounded smoke requests for the current
MXC development contract. They are not Windows desktop images. At the inspected
source revision, NVX runs Linux guests on Windows x64 only, while Hyperlight
runs x86_64 unikernel runtimes. The installed 1.0.1 executor predates the
current NVX schema and rejects the `microvm` settings object.

```powershell
.\node_modules\@microsoft\mxc-sdk\bin\arm64\wxc-exec.exe --experimental microvm.json
.\node_modules\@microsoft\mxc-sdk\bin\arm64\wxc-exec.exe --experimental hyperlight.json
.\bin\headless\HeadlessSpike.exe platform
node exports.mjs C:\Windows\System32\wtsapi32.dll C:\Windows\System32\winsta.dll C:\Windows\System32\IsoSessionCore.dll
```

`platform` calls the documented read-only `WHvGetCapability` hypervisor probe.
`exports.mjs` reads PE export tables from files without loading DLLs or calling
their exports. An exported name does not establish a callable signature,
authorization contract, or Windows guest/graphics support. It is not safe to
guess signatures for credential-returning APIs.

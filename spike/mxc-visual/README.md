# MXC IsolationSession visual spike

**HOST-CRASH WARNING:** the third concurrent Full-session run ended in
`0xEF CRITICAL_PROCESS_DIED` after its workers finished. Do not run these live
experiments on a primary machine while the Windows lifecycle investigation
is unresolved. Offline tests/replay are not proof that the native APIs are
safe. The installed service has deliberately not been updated by subsequent
safe-only source work.

**Post-update follow-up:** the user installed a candidate Windows fix,
rebooted, and approved a new pinned broker package. See
[Windows update qualification and distribution plan](../../docs/windows-update-qualification.md)
for exact binary identities, installation, initial live evidence and remaining
warnings. This does not retroactively change the failed crash-run verdict.

Experimental code; see [the handoff](../../docs/visual-test-farm-spikes.md)
for goals, results and the evidence contract. Not used by sandbox-mcp itself.

**Earlier single-session outcome:** an owner-only broker was installed and two ordinary-user
runs produced changing WinUI pixels without per-run UAC. Start with
[the installed broker](#owner-only-installed-broker-experimental) and
[its host validation](#installed-host-validation-2026-11-09). The earlier
sections below preserve the experiments in chronological order, including
failures that the later System-context path resolved.

Read [the Proxmox reference learnings](../../docs/proxmox-reference-learnings.md)
for source-verified lifecycle/capture ideas and limitations; no upstream code
was copied.

**Safe work after the crash:** [offline lifecycle tests, replay and findings](../../docs/full-session-safe-verification.md).
The replay uses copies of preserved images only and explicitly retains the
failed source-run verdict. Source-only hardening has not been installed over
the broker package that ran the experiment.

The viewer has compact fit-to-count layouts for 4/8/16 recorded image tiles
and semantic theme-token support with system-appearance fallback. Detailed
evidence is expandable; supplied names render as text. Host mode and semantic
tokens are supported on either the root or body, including live token/mode
changes and precedence over the OS theme. The project extension in
`.github/extensions/camera-grid` hosts the same viewer in a native extension
canvas, the theme-aware path used by Agent Review and hanselprojects.
Open `camera-grid` with `{ "batch": "<directory under spike/mxc-visual/evidence>" }`.
Its only action, `get_state`, reads evidence; it never creates sessions.
The standalone Browser/CLI path remains available with OS fallback.
Tile zoom ranges from 75% to 200%, with **Auto fit** restoring count-responsive
sizing. Zoom does not modify screenshots, fixture scale, or native capacity.

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
# Requires the existing WinUI fixture, .NET 10 SDK/Desktop runtime and this
# exact validated Windows ARM64 DLL set. Produces a new package each time.
$package = & .\broker-package.ps1 | Select-Object -Last 1
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

The broker fails closed when any pinned experimental Windows DLL changes.
It is not a stable Windows API contract, a multi-user service, a generic
System process launcher, or a verified parallel Full-session farm.
Installation, native IPC, and repeated no-UAC execution must be validated on
the target host; passing local tests alone is not that proof.

### Installed-host validation (2026-11-09)

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
$package = & .\broker-package.ps1 | Select-Object -Last 1
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
APIs; a newer binary set still requires a validated broker release. The
checks do not recommend copying DLLs, disabling gates or removing ABI pins.

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

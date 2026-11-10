# Visual test farm: research and Windows spike handoff

Research date: 2026-11-09. Branch: `sandbox-multiplexer`.
Starting repository revision: `1c9adfc` (sandbox-mcp 1.2.0).

**Latest safety status:** the third concurrent experiment ended in a host
bugcheck (`0xEF`, CSRSS terminated by SMSS). Exact causality is unresolved.
The user deferred further live testing and sent the dump to Windows
engineers. See [the safe-only verification findings](full-session-safe-verification.md)
and [Proxmox reference learnings](proxmox-reference-learnings.md). Earlier
successful milestones below are historical evidence, not a safety clearance.

**Status (updated 2026-11-09, Windows ARM64): changing WinUI client pixels are
now verified in a WTS Full session, using an in-session helper and PrintWindow
flag 2. An owner-only broker is now installed under protected Program Files
with one elevated installation; two subsequent ordinary-user visual runs
needed no UAC and cleaned up their sessions/accounts/profiles. The broker
remains installed intentionally. Whole-screen GDI capture still fails;
parallel Full sessions and general app execution remain untested.**
See [Spike results](#spike-results-2026-11-09-windows-arm64) and the
[System-context follow-up](#approved-fixed-function-system-service-credential-retrieval-succeeded).

The investigation below is chronological. Statements of blockers and unknowns
describe their stage, not a reversal of the current installed-broker outcome.
See [the installed-broker evidence](#installed-owner-only-broker-one-elevated-setup-repeated-ordinary-runs)
for the latest verified scope and remaining limitations.

## User's goal and next action

Extend sandbox-mcp from one Windows Sandbox desktop into a visual test farm:
run 4, 8, or 16 app/environment variants, including themes, dark/light mode,
DPI and resolution, collect screenshots and test results, and display a
refreshing "security camera grid." A Copilot canvas is one possible viewer;
a locally hosted web app must also work without Copilot. This need not be RDP.

The user explicitly requested:

> do it, lets do some spikes and see if we can run some winui apps in a sessions
> and get pixels out, the goal is a visual test farm

**Next: separately scope parallel Full-session, variant and general-app tests.
The one-app pixel proof and an installed owner-only experimental broker are
verified; production hardening, broad Windows compatibility and concurrency
are not.**
Do not treat the absence of an MXC screenshot API as proof that capture is
impossible. The user specifically proposed staging a utility inside the session.

## Execution-location blocker

The research agent was actually running on remote Linux host `adc-sandbox`,
not the Windows machine displaying the Copilot app. Confirmed with
`hostname`, `uname -srm`, and `pwd`:

```text
adc-sandbox
Linux 6.12.8+ x86_64
/workspaces/sandbox-mcp/shanselman/sandbox-mcp
```

`dotnet` and Node were available, but neither `powershell.exe` nor `pwsh` was
on PATH, and no Windows execution bridge was established. The user stopped
before implementation to move the work to Windows and asked to persist this
handoff. Check the execution OS at the start of the next session; a Windows
client app does not imply Windows agent execution.

## Findings, with confidence boundaries

### Windows Sandbox is still the single-desktop lane

The current engine drives `wsb.exe` and keeps one running target in
`%LOCALAPPDATA%\sandbox-mcp\current.json`. Changing that bookkeeping alone
cannot remove the platform restriction.

MXC's `windows_sandbox` backend explicitly documents one Windows Sandbox VM
per logon session, shared lifecycle/create-and-run ownership, and one admitted
lifecycle execution at a time. Switching from `wsb` to that MXC backend does
not itself provide parallel desktops. Do not infer a supported multi-session
workaround from the wording "per logon session."

Source: [MXC Windows Sandbox backend](https://github.com/microsoft/mxc/blob/main/docs/backends/windows-sandbox/windows-sandbox.md).

### MXC IsolationSession is the first spike target

Public Node entrypoint: `@microsoft/mxc-sdk/v1` (not the package root).
The documented lifecycle includes:

- `getPlatformSupport`, `getAvailableBackends`, and validation operations.
- `provisionContainer`, returning opaque `containerId` and optional metadata.
- `startContainer`.
- `runInContainer` / `spawnInContainer` for captured output / live pipes.
- `stopContainer`, then `deprovisionContainer` to remove the agent user.

Provision metadata includes `agentUserName`, `agentUserSid`, and
`ephemeralWorkspacePath`. The workspace is intended for staging files between
the caller and that isolated user, and is deleted on deprovision. Copy evidence
out before cleanup. Keep the returned container ID opaque.

The typed IsolationSession provision request requires an explicitly unrestricted
network posture:

```javascript
{
  containment: 'isolation_session',
  network: {
    egress: { default: 'allow' },
    ingress: { default: 'allow', hostLoopback: 'allow' }
  }
}
```

This is not equivalent to our network-off Windows Sandbox mode. The backend
rejects filesystem policy and UI policy; do not silently substitute it for
clean-machine testing or untrusted-app detonation.

The backend documentation says that windows, GDI, and the session's own
clipboard work, while `SendInput` is denied. Windows are not visible on the
human user's desktop. **Interactive console / PTY means terminal interaction,
not an interactive GUI desktop.** We did not establish a public MXC switch
between a fully interactive GUI session and a headless session. Older Windows
API documentation exposes `InteractiveConsole` and session configuration sizes;
these are not evidence of a graphical viewer.

Concurrency documentation needs empirical verification: the detailed section
says separate IDs represent distinct OS agent users, concurrent provisioning is
independent, and deprovisioning one leaves others intact. The same document's
scope/known-issues notes still say v1 targets one state-aware sandbox per
consumer. Do not promise 4/8/16 concurrent sessions without measuring.

Other documented constraints:

- `start` cannot run from Session 0 (for example a SYSTEM service).
- Backend availability depends on the Windows build and feature availability.
- Current Node SDK documentation requires Node 24+, with additional minimum
  patch versions for Windows native stdio. Check the selected package version
  and its runtime requirements rather than assuming our current Node 20
  minimum is sufficient.
- Native backend discovery may list backends the typed V1 creation/lifecycle
  API cannot launch. The typed Node lifecycle union examined contained
  `isolation_session` and `wslc`, not every backend in the README.

Sources:
[lifecycle](https://github.com/microsoft/mxc/blob/main/docs/container-lifecycle.md),
[Node API](https://github.com/microsoft/mxc/blob/main/docs/api-reference/node/v1/api.md),
[Node types](https://github.com/microsoft/mxc/blob/main/docs/api-reference/node/v1/types.md),
[Node installation/runtime requirements](https://github.com/microsoft/mxc/blob/main/sdk/node/README.md),
[IsolationSession TypeScript guide](https://github.com/microsoft/mxc/blob/main/docs/development/architecture/backends/isolation-session/state-aware-typescript.md),
[IsolationSession backend semantics](https://github.com/microsoft/mxc/blob/main/docs/development/architecture/backends/isolation-session/state-aware-rust.md).

### Pixels: promising experiment, not yet a supported capability

[microsoft/mxc#1705](https://github.com/microsoft/mxc/issues/1705) was open and
awaiting triage when read. Its author reports, using MXC .NET SDK 1.0.1 on
Windows 11 Home 25H2 build 26200.9711:

- Commands run under an OS-assigned user in a separate session.
- A window can be created and `Graphics.CopyFromScreen` returns a 640x480
  capture with black background and no shell.
- Cross-process UI Automation saw a top-level window but only a Pane descendant;
  the textbox was not exposed.
- State-aware commands retained state between calls.

These are **third-party observations**, not results from this repository or
Microsoft guarantees. In particular, "black background" does not prove that
every application window capture is black or that no app pixels can be read.
The earlier assistant recommendation to jump straight to OpenVMM was too strong.

Candidate helper paths, to run inside the same session and desktop as the app:

1. Enumerate app windows by PID and record HWND, bounds, visibility, window
   class, session ID, and capture errors.
2. `Graphics.CopyFromScreen` / screen DC capture as a baseline.
3. Win32 `PrintWindow`, trying flags 0 and `PW_RENDERFULLCONTENT` (2) as
   separate experiments. Application support varies; a successful API return
   is not proof of complete or fresh pixels. The call is synchronous and can
   hang, so bound it using an owned helper process and a timeout.
4. Windows Graphics Capture targeting the HWND (`CreateForWindow`), checking
   support and bounding frame acquisition. It may depend on composition/display
   capabilities absent in this session. Do not promise that this works.
5. If generic capture fails, consider an explicitly instrumented sample-app
   render/export path as a separate experiment. Such output is not equivalent
   to an unmodified application's real desktop rendering.

PNG transport can use the ephemeral workspace, or an explicitly framed/encoded
stdio protocol; never mix arbitrary binary bytes into a text/JSON output
channel. Use supported communication within the worker's own session, not
host-desktop access or cross-session security bypasses.

API references:
[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow),
[Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
[CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow),
[Windows IsolationSession API](https://learn.microsoft.com/en-us/windows/win32/secauthz/isoenvbroker).
The Learn pages were discoverable via search, but direct fetches repeatedly
failed DNS resolution during this research. Prefer MXC's current SDK contract
over older projected Windows API signatures.

### OpenVMM is a fallback, not a required first dependency

OpenVMM documents one VM per process. A farm could orchestrate multiple
processes, each with its own guest storage, resources, management endpoint and
VNC port. This is not a turnkey Windows test farm: Windows images, guest
installation/licensing, automation, isolation and cleanup still need design.

Its CLI exposes graphics with `--gfx` and a selectable `--vnc-port`; VNC
defaults to loopback and has no authentication. Do not expose it directly.
The current management docs use `--rpc path=...,transport=...` for gRPC/ttrpc
(older search snippets used different flags). RPC coverage and compatibility
evolve; consult the selected version's `vmservice.proto`.
No runtime feasibility was tested here.

Sources:
[management](https://github.com/microsoft/OpenVMM/blob/main/Guide/src/reference/openvmm/management.md),
[RPC](https://github.com/microsoft/OpenVMM/blob/main/Guide/src/reference/openvmm/management/grpc.md),
[VNC graphics](https://github.com/microsoft/OpenVMM/blob/main/Guide/src/reference/openvmm/graphical_console.md),
[protocol](https://github.com/microsoft/OpenVMM/blob/main/openvmm/openvmm_ttrpc_vmservice/src/vmservice.proto).

## Existing repository pieces to reuse

- `src/core.js`: `wsb` lifecycle, singleton target state, agent transport,
  launch, screenshot, UIA and artifact handling.
- `src/coordination.js`: state locks and cooperative desktop turns. In a farm,
  desktop turns and capture identities must be scoped per worker, not globally.
- `src/server.js` and `src/cli.js`: adapters sharing the engine.
- `src/run.js`: app launch/test/evidence/report orchestration, currently tied
  to the singleton engine.
- `src/capture.js`: capture-path checks and preview-to-source coordinates.
- `src/recording.js` and `agent/recording.ps1`: bounded frame recording and HTML
  evidence publishing.
- `agent/desktop.ps1`: deliberately requires an interactive desktop and shell.
  Do not weaken that production safety check to make an IsolationSession
  experiment appear supported. Keep the spike independent.
- `samples/WinUI`: self-contained unpackaged WinUI 3 and MSIX sample, with
  greeting and counter states.
- `samples/HelloSandbox`, `samples/WinForms`, `samples/Wpf`,
  `samples/NativeWin32`: comparison applications.

See [sample build instructions](../samples/README.md),
[WinUI requirements](../samples/WinUI/README.md), and
[existing measured compatibility](compatibility.md). Those compatibility
results are from Windows Sandbox, **not IsolationSession**. WinUI's build
currently needs Windows, .NET 10, Windows SDK and Visual Studio C++ tools;
the build script also uses winapp for packaging.

## Bounded Windows spike and evidence contract

Keep experimental code separate from production backends until real evidence
supports integration. Do not build the canvas or a 16-worker scheduler before
proving that one WinUI window yields fresh pixels.

| Stage | Experiment | Required evidence |
|---|---|---|
| 0 | Probe the real Windows host and selected MXC package | OS build, Node/.NET and MXC versions, backend availability; clear unsupported errors |
| 1 | Launch unpackaged HelloWinUI plus helper in one persistent IsolationSession | Container identity, app/helper session IDs, process outcomes, enumerated HWNDs |
| 2 | Run the capture methods separately | PNGs, method, dimensions, timings, errors/timeouts, window identity and raw diagnostics |
| 3 | Change a recognizable sample state and recapture | Visually inspect both images and show that expected content changed, not merely that PNG hashes differ |
| 4 | Repeat unchanged setup at least twice, then compare WinForms/WPF/Win32 | Repeatability plus per-framework pass/fail/unsupported matrix |
| 5 | Run two distinct sessions simultaneously | Different visible app markers, correct per-worker captures, no cross-worker routing, independent teardown |
| 6 | Exercise requested theme/DPI variants | Requested and measured effective settings, with fresh app launch when necessary; no host settings changed |
| 7 | Grow to 4, then 8/16 if resources and correctness allow | Startup/first-frame times, capture freshness, CPU/memory, failure counts, cleanup evidence |

**Keep bar:** a genuine WinUI capture must contain expected app content and show
a known state transition on repeat runs. A PNG file, nonzero dimensions,
non-black pixels, or a changing hash alone is not a pass. Inspect the images.
A capability that works only for WinForms must not be reported as WinUI support.
App-driven state changes, UIA-driven changes and synthetic input have different
contracts and must be recorded separately; `SendInput` is currently blocked.

**Stop rule:** if the Windows backend is unavailable, report the exact probe
failure instead of inventing runtime results. If the bounded capture methods
fail, retain their artifacts and errors, classify the result as unsupported or
inconclusive for that host, and consider the VM fallback. Do not keep changing
capture methods without new evidence.

**Cleanup:** retain host-side evidence, terminate only owned helper/app
processes, stop each owned session, and deprovision each owned user. Cleanup
must run after failures and timeouts and report its own errors. Never remove
unrelated accounts or sessions. Remember that staging workspaces disappear
when deprovisioned.

**Evidence record:** per attempt, include timestamp, source revision,
OS/package versions, worker/container identity, app build and framework,
requested/effective settings, capture method, frame sequence, dimensions,
artifact paths, observed app state, errors and cleanup outcome. Preserve failed
attempts as well as successful ones. Timing claims need repeated measurements;
there is no measured baseline yet.

**Authority:** the user requested spikes and subsequently authorized saving and
pushing this handoff. No merge, pull request, production-backend rewrite,
host theme changes, or 16-session launch was requested for this handoff.

## Spike results (2026-11-09, Windows ARM64)

Code: [`spike/mxc-visual/`](../spike/mxc-visual/README.md). Evidence PNGs and
`record.json` files are written to the ignored `spike/mxc-visual/evidence/`.

**Host:** `SCOTTHA-ULTRA`, Windows 10.0.28000.3231 (26H1) Enterprise, ARM64,
.NET SDK 10.0.403, `@microsoft/mxc-sdk` 1.0.1. Host user in session 1.

### Stage 0: probe

`getPlatformSupport()` reports `isolation_session`, `windows_sandbox`, and
`processcontainer`; `probe().probes.isolationSessionAvailable = true`.

**Gotcha:** exec failed with `backend_unavailable` under the machine's Node
`v27.0.0-alpha`, because the SDK's native-stdio gate requires Node 24.21+ or
26.8+ and rejects that prerelease. Provision/start/stop/deprovision still
worked. A portable Node 24.22.2 (ARM64, outside the repo) fixed exec.

### Stage 1: lifecycle and session shape

- Provision ~0.5 s, start 2.5–14 s (first run ~30 s), stop+deprovision ~1.5 s.
- Each container is a fresh OS user (`P9-X9` style names) in its own
  interactive session (observed IDs 3–11), input desktop `Default`,
  `UserInteractive=True`, a single **640×480** display.
- **No DWM:** `DwmIsCompositionEnabled=false`, no `dwm.exe` in the session,
  Windows Graphics Capture unavailable ("service does not exist"). Windows draw
  with the classic non-composited frame.
- `runInContainer` commands go through `cmd /c`; more than one quoted argument
  gets mangled. Workspace paths have no spaces, so leave them unquoted.
- `spawnInContainer().id` returned `0`, not an OS PID; the helper finds the app
  by process name within its own session.
- Staging the 433-file WinUI payload into `ephemeralWorkspacePath` with
  robocopy took ~1.5–2 s.

### Stages 2–3: capture and state transition

In-session helper ran four methods; state was changed via in-session UIA
(`SendInput` was not used). Images were inspected by eye.

| App | Screen / window BitBlt | PrintWindow 0 / 2 | UIA tree + state change | Verdict |
|---|---|---|---|---|
| WinForms (.NET 10, x64 emulated) | Real content; `Ready` → `Hello from WinForms` visible | Same as BitBlt | 8 elements, Invoke works | **Pass** |
| WPF (.NET 10, x64 emulated) | Real content; `Hello from WPF` visible | **Black client area** | 9 elements, Invoke works | **Pass** (BitBlt only) |
| WinUI 3 (WinAppSDK 2.6.5, x64 emulated) | Title bar/frame only; **XAML content blank white** | Same blank | **15 elements; `NameBox`, `GreetButton`, `IncrementButton` all work**, `Output=Hello, Farm!`, `Counter=Count: 2` | **Fail** (pixels) |

WinUI was run three times with identical results in the MXC-created sessions.
The missing compositor is consistent with the blank content, but these
experiments do not establish the behavior of every kind of headless session.
UIA working on WinUI contradicts the
[microsoft/mxc#1705](https://github.com/microsoft/mxc/issues/1705) observation
on this host/build; that report's app was not WinUI.

### Stage 5: two concurrent sessions

WinForms and WPF ran in parallel sessions (11 and 10): 23 s wall for both,
each capture showed only its own app and its own state change, and both tore
down cleanly. No leftover agent users or profiles remained after all runs.

### Not yet run

Stage 4 for native Win32 (needs Visual Studio C++ tools, not installed), Stage 6
(theme/DPI: the 640×480 single display is a constraint to investigate), Stage 7
(4/8/16 workers), and the instrumented WinUI render path.

### Decision guidance

- IsolationSession is viable today for a **GDI/WPF** visual farm: fast, parallel,
  real pixels, UIA-driven state. Caveat: network is always unrestricted.
- **The tested MXC configuration did not give WinUI 3 pixels.** An experimental
  WTS Full headless session is another lead, not yet a demonstrated solution
  (see below). Other options are: (a) an
  app-side `RenderTargetBitmap` export hook (instrumented, not equivalent to
  real desktop rendering), (b) ask MXC for a composited/DWM session or a
  capture API (comment on #1705 with this evidence), or (c) the VM lane
  (Windows Sandbox / OpenVMM), which does have DWM.

### Experimental WTS Full session follow-up

The user supplied a WTS header and requested trying
`EXPERIMENTAL_WTSCreateHeadlessSession`, whose `Kind` distinguishes Utility (0)
and Full (1). The header alone does not specify their composition support,
authentication requirements, or security equivalence to MXC IsolationSession.
Do not treat direct WTS creation as a proven isolation backend.

Implemented an independent harness under `spike/mxc-visual/headless/`.
On this ARM64 host, both experimental create/stop exports are present in
system `wtsapi32.dll` version 10.0.28000.3086. The supplied V1 size macro yields
36 bytes; `Marshal.SizeOf` includes trailing padding and yields 40.

All attempts used the caller-parent sentinel `0xFFFFFFFF`, a 20000 ms logon
timeout, no credentials (`cbAuthIdentity=0`, `pAuthIdentity=NULL`), and the
normal non-elevated caller token:

| Kind | Size | Initial program | Result |
|---|---|---|---|
| Full | 36 | Owned WinUI/capture worker | Error 87: The parameter is incorrect |
| Full | 36 | NULL | Error 87 |
| Utility | 36 | NULL | Error 87 |
| Full | 40 (padded ABI comparison) | Owned worker | Error 87 |

Failures returned in 0-1 ms, with output session ID -1. WTS enumeration before
and after every attempt showed only existing sessions 0 and 1. No sessions
were created or stopped and no authentication or host configuration changed.
Raw host records are retained in ignored `evidence/headless-*/host.json`.

**Blocked before logon, not a graphics failure.** Error 87 does not identify
the rejected field. The next prerequisite is the supported authentication
contract or a known-good minimal call for this Windows build. No credentials
were guessed and the supplied header was not uploaded elsewhere. Whether a
successfully created Full session supplied DWM/WinUI pixels was untested then.

#### Follow-up: use MXC to own the agent account

Per the user's direction, cloned `microsoft/mxc` locally (source revision
`caef80977b538bcb4870c9c4796c4c59aae1d3d9`). The clone is excluded locally,
not vendored into this repository.

The source trace is
`src/mxc-sdk/src/backends/isolation_session/common/state_aware.rs` (provision,
start, stop, deprovision) ->
`common/manager.rs` (`add_user`, `start_session`). Provision delegates to
Windows `IsoSessionOps.AddUserAsync3`, `AddUserAsync2`, or `AddUserAsync`,
depending on feature level. It returns an account name, SID and workspace;
it does not return a password or reusable credential. Start delegates to
`StartSessionAsync2` / `StartSessionAsync`. None of the examined SDK options
expose the WTS Full/Utility choice.

Added `headless.mjs` to use the published MXC APIs for account provisioning and
deprovisioning, stage payloads in the shared workspace, and use raw WTS only
for headless creation/stop. The native harness constructs an EX2 identity
naming that newly provisioned agent and the local machine, with Unicode
strings and no credential payload. `SspiValidateAuthIdentity` returns success.
No passwords were retrieved, reset, guessed or persisted.

| Kind | Parent | Identity | Result |
|---|---|---|---|
| Full | Caller | Validated EX2, MXC agent user | Error 5: Access is denied |
| Utility | Caller | Validated EX2, MXC agent user | Error 5 |
| Full | Console | Validated EX2, MXC agent user | Error 5 |

Each WTS call failed in 1 ms with session ID -1. Sessions remained 0 and 1.
MXC successfully deprovisioned every created account, including two earlier
identity-encoding attempts that stopped before calling WTS. Those failed
attempts are retained too: SSPI's string encoder/marshaller returned version
0x200, whereas this API explicitly requests version-0x201 EX2.

**Blocker at this stage:** authorization at the direct WTS boundary under the
normal non-elevated caller. The error progression from 87 to 5 is evidence
that a non-null validated identity changes validation behavior, not proof of
successful logon or of an elevation requirement. An explicitly authorized
elevated trial or the documented broker/caller requirements are the next
checks. WinUI pixels in a Full session were still untested.

#### Elevated control

The user explicitly authorized one elevated trial. Launched the same
`headless.mjs full caller` wrapper with Windows `RunAs`. MXC provisioned a
fresh agent user, the EX2 identity passed SSPI validation, and WTS again
returned error 5 (Access is denied) in 1 ms with session ID -1. No WTS session
was created; session enumeration was unchanged before/after. MXC successfully
deprovisioned the account. Evidence:
`evidence/wts-mxc-full-2026-11-10T00-29-00-098Z/record.json`.

Elevation alone did not resolve the failure. The harness did not record the
child's token elevation/privilege inventory, so this is a RunAs-launched
control, not a measurement of every required privilege. This result alone
did not establish a System requirement or justify a service/token workaround.
The credential and caller contract were unresolved until the subsequent
matched-symbol/RPC investigation and separately approved service experiments.

### Deeper microVM / available-API investigation

Inspected the locally cloned MXC source, its public remote branches, public
pull requests, the shipped 1.0.1 executors, and system PE export tables.
Authenticated `gh api` branch enumeration was denied by Microsoft SAML
enforcement; public REST reads and `git ls-remote` provided public metadata
without changing account authorization. This research does not cover private
branches or private proposals.

**The experimental microVM backend is real, but it is not a Windows desktop
backend.** Current source maps `microvm` to the signed NVX agent bundle,
using OpenVMM to run a Linux guest and caller-selected registry image.
The execution module is explicitly gated on Windows x86_64 and the `microvm`
Cargo feature. ARM64 execution is not implemented in that integration.
Hyperlight is a separate x86_64 Unikraft runtime lane, not a WinUI-capable
Windows guest. Sources:

- [NVX backend and platform requirements](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/docs/backends/nvx/nvx.md).
- [Native SDK architecture gates](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/src/mxc-sdk/src/lib.rs).
- [Hyperlight guest/runtime contract](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/docs/backends/hyperlight/hyperlight-backend.md).

Relevant public changes inspected:

- [microsoft/mxc#1528](https://github.com/microsoft/mxc/pull/1528)
  (NVX Phase 1: Minimal Signed MicroVM integration): merged implementation of
  the initial Windows x64 signed-agent Linux guest path.
- [microsoft/mxc#1691](https://github.com/microsoft/mxc/pull/1691)
  (NVX: Reuse registered images across create-and-run requests): open proposal
  to share registered-image state across requests, not add Windows guests.
- [microsoft/mxc#1693](https://github.com/microsoft/mxc/pull/1693)
  (NVX: Start MicroVMs from platform snapshot templates): open proposal for
  signed-agent snapshot clones and concurrent starts, still a Linux-guest
  Windows x64 lane. Its author's timings are not measurements from this spike.
- [microsoft/mxc#1697](https://github.com/microsoft/mxc/pull/1697)
  (Add W365 Agents Phase 1 development request contract): open contract-only
  Windows 365 proposal; it explicitly does not implement backend execution.

Fetched and inspected public `feature/isolation-session-internal` at
`a466903b2f9664111a95a7c7f8cf788e783aa1a7` and `ppenna/nvx`.
The isolation branch extends Entra agent lifecycle support, but the examined
bindings still expose `StartSessionAsync` / `StartSessionAsync2`, not a Full
headless-kind selector. No ready WinUI/Full-session API was found in the
bounded public PR searches; that is a coverage statement, not proof of absence.

#### Experiments on this ARM64 host

| Probe | Observed result |
|---|---|
| Documented `WHvGetCapability(HypervisorPresent)` | S_OK, 4 bytes, true |
| Host services | vmcompute, hns, TermService running |
| Installed ARM64 executor, current microVM JSON | Rejects unknown `microvm` object (package/source contract mismatch) |
| Installed ARM64 executor, legacy selector without that object | Selects NanVix, fails preflight: `nanvixd.exe` absent |
| Installed ARM64 executor, experimental Hyperlight request | Explicit error: requires x86_64 |
| Shipped x64 executor under emulation, read-only `--probe` | Runs, but reports Hyperlight unavailable; does not establish native x64 hypervisor execution |

No VM was created by these attempts. Hardware virtualization is present;
missing/unsupported guest runtime integration is a separate issue. The
installed package is older than the cloned development implementation.
Simply setting `experimental` cannot update its contract or provide its
missing signed bundle. No bundle verification or architecture gate was bypassed.

#### Native export inspection

Added `exports.mjs`, a read-only PE parser (does not load inspected DLLs).
Retained JSON inventories under ignored `evidence/`.

- `wtsapi32.dll`: experimental create/stop exports already exercised.
- `winsta.dll`: `WinStationCreateHeadlessSession`,
  `WinStationStopHeadlessSession`, `WinStationCreateAgentSessionTransport`.
- `IsoSessionCore.dll`: `IsoSessionGetLocalAgentLogonCredential`,
  `IsoSessionGetEnterpriseAgentLogonCredential`, agent account create/delete,
  owner information and enumeration exports.
- `computecore.dll`: HCS create/start/lifecycle exports.
- `WinHvPlatform.dll`: PE machine type is ARM64; notably the inspected export
  table did not contain named WHv entries, yet the documented loader call to
  `WHvGetCapability` succeeded. Static export names are not complete evidence
  of loader/API availability on this system.

The local SDK headers, examined MXC source/bindings, and bounded public web
lookup did not provide the declaration of
`IsoSessionGetLocalAgentLogonCredential`. Its name is a useful lead for the
Full-session authentication gap, not authorization to guess its ABI or read
credentials. A declaration and ownership/freeing contract for that API, or a
broker API that accepts the Full kind, would enable a grounded next experiment.
No private credential export was invoked during that initial investigation.
The later user-authorized, build-pinned experiment below supersedes this point.

**Revised direction:** retain two distinct tracks: MXC NVX for Linux workloads
on a supported x64 host, and WTS Full/Windows VM APIs for actual Windows/WinUI
rendering. The former does not by itself solve the latter. HCS/WHP availability
does not supply a Windows guest image, WinUI-capable desktop or capture channel.

### Local-agent credential / WTS Full follow-up experiment

The user explicitly requested experiments on
`IsoSessionGetLocalAgentLogonCredential` and experimental WTS Full sessions.
This follow-up used a fresh MXC-owned local agent, the existing unelevated
caller, and no password prompts, account-password changes or token borrowing.

#### Establishing a build-specific call shape

Neither the installed SDK headers nor the public IsolationSession reference
provided a declaration. Instead, downloaded matching **Microsoft public
symbols** and used the installed ARM64 debugger on DLL files (`cdb -z`), not
on a live process or a memory dump. Public symbols have no type information
for this C export. Static argument validation, diagnostic strings, and the
Windows broker's call site establish this build-specific shape:

```text
HRESULT IsoSessionGetLocalAgentLogonCredential(
    PSID owningUserSid, PCWSTR owningAppId, PCWSTR agentName,
    SSPI-opaque-identity* credential);
IsoSessionFreeLogonCredential(credential);
```

This is **derived experimental ABI evidence, not a published/supported header**.
The core validates the SID, converts the two strings to counted Unicode, and
returns an LSA-allocated identity. The broker supplies the literal `"unknown"`
as owning app ID, passes the returned object to `SspiMarshalAuthIdentity` /
`SspiEncodeAuthIdentityAsStrings`, and releases it through the paired export.
The free routine delegates to `LsaFreeReturnBuffer`.

The native spike refuses to call on anything except ARM64 and the inspected
`IsoSessionCore.dll` SHA-256:
`0E38ED92079D7A7F1C46C548EEE4A5FC51B43505C4BFCA5C3E019919E4E88C40`.
Matched core PDB identity: `F03ADBB8F935DAD6B10BEAC5EF0101AE`, age 1.
The inspected broker DLL SHA-256 is
`6E6CC705EF4C2C8A6935229F8D75A378360DA402F8420B4BF30D3FECCB2CD0A6`.
Do not treat this inference or hash pin as a portable Windows API contract.

Added opt-in `local-credential` mode to the existing runner. It uses the
current caller's user SID and only the just-provisioned MXC agent name.
If retrieval succeeds, it validates/marshals with SSPI, requires actual EX2
version/length agreement, and passes the buffer directly to WTS. It never
decodes credential contents into strings, writes them to files, or prints
them. Native identity buffers are zeroed and freed using the matching APIs.

#### Observed result and control

| Experiment | Observation |
|---|---|
| Direct credential retrieval, fresh local agent | `0x80070522`, Win32 1314: **A required privilege is not held by the client.** Returned identity pointer was null. |
| Credential-backed WTS Full creation | **Not reached.** The harness stops when retrieval fails; it does not substitute a name-only identity and claim an authenticated attempt. |
| Normal MXC provision/start/exec/stop/deprovision under the same caller | Succeeded. Worker ran in session 12, interactive, 640x480. |
| Installed `Windows.AI.winmd` inspection | Preview interfaces through `IIsoSessionOpsPreview4`; no credential-returning method or Full-kind selector in the inspected IsolationSession metadata. |
| Cleanup | Both experiment/control accounts and profile directories removed; only original sessions 0 and 1 remained. |

The caller-token privilege inventory was recorded read-only. No privilege was
enabled, no RunAs attempt was repeated, and no system service was modified.
1314 identifies a privilege failure for this **direct low-level call**; it
does not identify the exact required privilege, establish that elevation fixes
it, or imply that the brokered MXC path requires elevation. The successful
ordinary MXC control is evidence that the brokered path still works.

Static inspection of the supplied WTS wrapper also confirms its 36-byte
minimum parameter size and non-null/nonempty identity requirement. It checks
`HeadlessTerminal` and `UtilitySessionLsmIntegration` feature gates, returning
error 50 if disabled, then forwards valid inputs to
`WinStationCreateHeadlessSession`. This explains the earlier null-identity
error 87 and locates the earlier error 5 below those initial checks. It is not
a demonstration of successful Full-session authentication or graphics.
No feature gate was changed or bypassed.

Evidence: `evidence/wts-mxc-full-2026-11-10T01-01-11-801Z/`,
`credential-mxc-control.json`, `installed-isolation-metadata.json`,
`credential-caller-privileges.txt`, and `credential-static-*.txt` /
`headless-static-wts.txt` under the spike (all gitignored).
At this stage, the credential-success, EX2-marshalling and Full-session capture
branches were unexercised. The remaining requirement was a permitted
caller/broker route for obtaining the agent identity or requesting a Full
session, not another blind elevation or feature-toggle experiment.

### Independently authenticated WTS control

The user approved an elevated helper **only for temporary standard-account
setup and cleanup**. The parent refuses to run this experiment elevated.
Neither `LogonUserW` nor either WTS call runs in the elevated helper.
All implementation remains under `spike/mxc-visual/`; no production backend
or host feature settings were changed.

#### Credential-encoding controls

The input now carries an actual generated password rather than only an
SSPI-valid username. Synthetic tests established the following before creating
an account:

- `CredPackAuthenticationBufferW` with flags 0 produces
  `KERB_INTERACTIVE_LOGON`, not EX2; username/password unpack correctly.
- With `CRED_PACK_ID_PROVIDER_CREDENTIALS` (8), it produces valid EX2 but sets
  the online-ID provider flag (`0x80000`). An online identity is not the
  intended local-account control. Unpack/repack retains that identity.
- The local-password EX2 is built from the public `sspi.h` structures and
  password credential-type GUID, with the packed password layout checked
  against Windows-generated output. It passes `SspiValidateAuthIdentity`.
  `SspiPrepareForCredWrite` recovers the original qualified username and exact
  password bytes in memory. **CredWrite is never called.**
- Initial test-oracle mistakes are retained in the evidence: EX2 unpacking
  can return encoded strings; cross-format `SspiCompareAuthIdentities`
  returned same-user but not same-identity; EX2 normalization returns
  `CRED_TYPE_DOMAIN_EXTENDED` (6), not legacy domain-password type 2.
  A successful format validation or round-trip alone was not treated as
  proof of an authenticated Windows logon.

References:
[CredPackAuthenticationBufferW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credpackauthenticationbufferw),
[EX2](https://learn.microsoft.com/en-us/windows/win32/api/sspi/ns-sspi-sec_winnt_auth_identity_ex2),
[packed credentials](https://learn.microsoft.com/en-us/windows/win32/api/sspi/ns-sspi-sec_winnt_auth_packed_credentials),
[SSPI normalization](https://learn.microsoft.com/en-us/windows/win32/api/sspi/nf-sspi-sspiprepareforcredwrite).

#### Runtime observations

| Step | Result |
|---|---|
| Approved helper creates one random-named standard local account | Succeeded; only Users membership, no administrator membership. |
| Unelevated parent calls `LogonUserW`, interactive logon, with generated credentials | **Succeeded.** This independently confirms the account/password can authenticate. |
| Unelevated WTS Full/caller with credential-bearing local EX2 | **Win32 5, Access is denied**, session ID -1, measured 0 ms. |
| Unelevated WTS Utility/caller with the same credential representation | **Win32 5, Access is denied**, session ID -1, measured 0 ms. |
| Session enumeration | Before and after each call: original sessions 0 and 1 only. |
| Cleanup | Account deleted; no profile existed; helper exited successfully. Owned staging directory subsequently removed after evidence copy. |

This rules out a missing password as the **sole** explanation for the tested
direct WTS failure. It does not prove that WTS accepted/authenticated this
identity before denying the request, or identify its required caller rights,
supported account types, broker route or policy. Full-session graphics remain
untested. No additional elevated WTS trial was performed.

The helper uses a private named pipe restricted to the caller's SID, verifies
the peer process ID, and retains the account only until the parent disconnects
or a ten-minute lease expires. The random password travels only in memory
over that pipe, never in arguments, environment variables, logs or files.
Native and managed password buffers are cleared. The staged executable/app
tree grants the new account read/execute; only its fresh output folders grant
modify access. The helper does not borrow tokens or launch WTS sessions.

The four already-enabled LocalSessionManager/RemoteConnectionManager
Operational/Admin channels contained no events in the bounded control
window. Only event metadata was requested; logs were not enabled or changed.
This absence is **not** evidence that no authentication/policy check occurred.

Static inspection of `winsta.dll` resolves distinct `CreateFullSessionInternal`
and `CreateHeadlessSessionInternal` branches, which obtain a public binding
and issue RPC calls. It does not identify which binding/server check produced
the runtime denial. Private routines were inspected as file bytes, not called.

Evidence: `evidence/wts-controlled-2026-11-10T01-20-09-514Z/`,
`credential-packing-final-verified.json`, earlier `credential-packing-*.json`
controls, `controlled-wts-events.json`, and `headless-static-winsta.txt` /
`headless-static-internal-resolved.txt`. The final cleanup receipt is
`cleanup-verification.json`; the earlier control report records the staging
path as retained at that earlier point.

**At that stage, the narrow remaining question was:** the supported caller/broker contract for
experimental headless creation on this build. The experiment now separates
that question from missing credentials, basic account authentication, and
WinUI capture. It still does not establish a viable parallel WinUI farm.

### RPC caller contract: identified and corroborated

The next investigation mapped the actual client interface UUIDs and operation
numbers to the installed server dispatch tables, then followed registration
to the security callbacks. This is matched-public-symbol/static evidence for
these exact installed binaries, not a portable SDK promise.

| Kind | Client RPC interface / operation | Server dispatch | Registered callback |
|---|---|---|---|
| Full | `1f260487-ba29-4f13-928a-bbd29761b083`, operation 11 | `termsrv!RpcCreateHeadlessFullSession` | `CRCMPrivateRpc::staticRCMPrivateRpcSecurityCallback` |
| Utility | `11f25515-c879-400a-989e-b074d5f092fe`, operation 12 | `lsm!RpcCreateHeadlessSession` | `CPrivateRpc::staticPrivateRpcSecurityCallback` |

Both registrations select the **private** callback, with the administrator
fallback argument set to zero, not the separate administrator callback.
They impersonate the RPC caller and check `CUtils::IsCallerSystem`.
The Full check rejects restricted tokens and uses an access descriptor whose
only allow ACE grants System: `O:SYG:BAD:(A;;0x1;;;SY)`.
The Utility callback contains the diagnostic:
`staticRpcSecurityCallback: Callers have to be system`.

This explains why supplying credentials for the *target account* does not
satisfy the *caller's* RPC authorization, and why the earlier RunAs control
did not establish a solution. It does not justify borrowing a System token,
weakening policy or silently installing a service. Normal MXC can broker its
supported Utility operations; the inspected WinRT interface still exposes
no Full-kind selector.

#### Initial-program policy was a separate, tested hypothesis

Both server implementations check the requested initial executable before
credential processing. Their default allowed root is
`%SystemRoot%\System32`; this host has no overriding `Headless*` values under
`HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations`.
Our worker paths are outside that default. An executable rejected by this
check produces `E_ACCESSDENIED`, so it is a separate constraint to account for
even after the caller contract is satisfied.

Added `initial-program-control`: it generates an account name, confirms
`NetUserGetInfo` returns `NERR_UserNotFound` before every attempt, creates no
account, and supplies a syntactically valid local EX2 with a synthetic
password. Both kinds were tested with the worker and with a null initial
program. **All four returned Win32 5**, and only original sessions 0 and 1
remained. Removing the initial program therefore did not resolve the observed
denial; this negative control does not claim successful authentication.

#### Client-only runtime corroboration

Launched only our negative-control executable under the installed ARM64
debugger, with build-pinned breakpoints around the existing client RPC calls.
No Windows service was debugged, no server memory was read, and no credentials
or buffers were printed. Each of the four requests showed:

1. A non-null RPC binding was obtained.
2. The corresponding `NdrClientCall3` was entered.
3. RPC raised exception **5** through `RpcRaiseException`; the normal-return
   breakpoint was not reached.

Combined with exact UUID/operation/registration mapping, this corroborates an
RPC authorization rejection rather than an ordinary HRESULT returned by the
session-creation implementation. We did not observe the server callback live.
The first debugger-script attempt failed to open its command file before
the test calls; it is retained as a failed trace, not counted as evidence.

Build pins (SHA-256):

- `winsta.dll`: `0480DC132339AC3F06277824A925774819994EA550BEDEABA0ECA9A0EF47AB3A`.
- `termsrv.dll`: `B85A40F624CC8A10A301AA55CCE2F53243E60E4D19D2A6F799E5E2E2B2601B66`.
- `lsm.dll`: `D132944B00F3FEEAF7AC64BBE29A2867A1B2CD36C3330478FCE09896FB441262`.

Evidence includes `headless-client-rpc-interface.txt`,
`headless-termsrv-registration.txt`, `headless-full-private-interface.txt`,
`headless-full-caller-check.txt`, `headless-system-access-descriptor.txt`,
`headless-lsm-private-registration.txt`,
`headless-utility-dispatch-confirmed.txt`, and
`headless-client-runtime-trace-v2.txt`. Untraced controls are in
`initial-program-2026-11-10T01-57-02-590Z/`; the completed traced controls are
in `initial-program-traced-2026-11-10T02-01-26-392Z/`.
Debugger processes exited, no account was created, and no service, permission,
registry policy or feature flag was changed.

**Conclusion at this stage:** this build's direct Full/Utility WTS exports reach
System-authorized private RPC interfaces. A supported broker exposing Full,
or a separately authorized fixed-function System-context experiment, is
needed to progress. Full-session creation, DWM and WinUI pixels were not yet
proven; there was no evidence that Proxmox was required.

### Approved fixed-function System service: credential retrieval succeeded

The user explicitly approved one temporary LocalSystem service, limited to a
fresh MXC account, Full creation with no initial program, owned-session/DWM
diagnostics, and cleanup. Added `system-session.mjs` and
`headless/SystemSessionProbe.cs`; the normal caller provisions through MXC
and records its own SID, an approved elevated installer creates a random
demand-start service, and the service performs only the fixed experiment.
It has no network listener, command endpoint or arbitrary process-launch path.
Its job/account SID is checked, its job and binaries are held read-only during
the service run, and the evidence directory is restricted to the caller and
System. The wrapper confirms service absence before deprovisioning.

The first run (`system-full-2026-11-10T02-18-38-499Z/`) established:

- The service actually ran as LocalSystem in session 0.
- `IsoSessionGetLocalAgentLogonCredential`, using the original provisioning
  caller SID and the broker's observed app scope, returned **S_OK** and an
  identity. SSPI validation and marshaling also returned **S_OK**.
- The wire format was version `0x201`, 110 bytes. The harness's strict
  comparison against the header's structure length rejected it **before**
  calling WTS. This was a harness-side stop, not a Windows creation denial.
- The service stopped and was removed; MXC removed the account/profile.
  System saw additional pre-existing session-enumeration entries, but the
  before/after inventories were identical. No Full session was created.

Corrected the validation using the documented
[SspiUnmarshalAuthIdentity](https://learn.microsoft.com/en-us/windows/win32/api/sspi/nf-sspi-sspiunmarshalauthidentity)
with the actual serialized byte count, then `SspiValidateAuthIdentity` on
the decoded allocation. The raw buffer is still passed unchanged to WTS;
there is no credential rewrite or unchecked acceptance.

The synthetic regression reproduced the issue without an account or service:
a 212-byte EX2 became a 220-byte marshaled buffer while retaining 212 in
`cbStructureLength`. Size-aware unmarshaling and validation succeeded and
preserved the original username/password. Supplying half the buffer length
was rejected with `0x80090308`. The first run's credential was already zeroed
and freed; it was not retained for debugging.

Evidence: `credential-serialization-size-aware.json`, the run's
`system-probe.json`, `service-install.json`, and `record.json`.
No automatic service retry was performed after the first approved run.

#### Second separately approved run: Full session and DWM verified

The user approved one more run of the same limited experiment after the
synthetic size-aware regression passed. In
`system-full-2026-11-10T02-26-38-659Z/`:

| Observation | Result |
|---|---|
| Caller | Actual LocalSystem, session 0; target is a fresh MXC-owned local agent. |
| Credential retrieval, validation, marshal/unmarshal | All returned S_OK. Marshaled bytes 110; header structure length 102, confirming the eight-byte difference. |
| WTS kind / parent / initial program | Full / console / null. |
| Creation | **Succeeded**, owned session ID 13, **1518 ms**. |
| Session processes | DWM, Winlogon and CSRSS seen in the first sample; Userinit and Explorer also seen in subsequent samples. |
| DWM | PID 59624 in session 13, observed repeatedly over about 3.4 seconds. |
| Session teardown | Experimental stop succeeded; session 13 disappeared. Before/after System-visible inventories matched. |
| Remaining resources | Service absence confirmed; MXC deprovision succeeded; account, profile and owned DWM process absence independently verified. |

This proves creation of a real Full Windows session with a DWM process on the
ARM64 host using the inspected experimental APIs, **without Proxmox or a
separate Windows VM**. It does not prove WGC support, successful WinUI rendering,
independent DPI/resolution, parallel Full sessions or a deployable public
broker contract. DWM presence alone is not a screenshot verdict.

The service did not launch the sample or capture helper; that was outside
the approved fixed-function run. No initial-program allowlist, host feature,
network listener or persistent service was added. Evidence and independent
cleanup confirmation are in `system-probe.json`, `service-install.json`,
`record.json` and `cleanup-verification.json` in the run folder above.

#### Separately approved visual run: changing WinUI pixels verified

The user approved one additional bounded visual test. `--visual` stages only
the existing WinUI sample, capture helper and worker into the newly provisioned
agent workspace. After Full creation with a null initial program, the service
uses the documented `WTSQueryUserToken` for **that returned session**, verifies
the token's user SID and session ID, and uses `CreateProcessAsUserW` to launch
the fixed worker as the disposable agent. No host-user token is used, no
application runs as System, and the initial-program allowlist is unchanged.

The run in `system-full-2026-11-10T02-47-23-206Z/` created session **14** in
**1737 ms**. The worker and app ran under the provisioned agent in that
session. The helper reported a 1920x1080 display, DWM composition enabled,
and `GraphicsCaptureSession.IsSupported() == true`; actual WGC frame capture
was not implemented or tested.

| Capture method | Actual observation |
|---|---|
| Full-screen GDI / CopyFromScreen | Failed: "The handle is invalid." |
| Window-rectangle GDI / CopyFromScreen | Same failure. |
| PrintWindow flag 0 | PNG produced, but client area is black; not usable WinUI pixels. |
| **PrintWindow flag 2** | **Real 520x400 WinUI client content before and after the UIA changes.** |

Both flag-2 PNGs were opened and visually inspected. Before: the text field
contains `Sandbox`, greeting `Hello, Sandbox!`, counter `Count: 0`. After:
`FullSession`, `Hello, FullSession!`, `Count: 2`. The corresponding UIA
readback agrees, and file hashes differ, but neither UIA nor hashes alone was
used as the pixel verdict. The fixture's static heading says "Windows Sandbox";
this run was a WTS Full session on the host, **not** a Windows Sandbox VM.

Evidence includes `visual/1-before-printwindow-2.png`,
`visual/2-after-printwindow-2.png`, the failed/black capture controls,
`visual/worker.json`, `visual-proof.json`, `system-probe.json`, and
`cleanup-verification.json`. The owned app and worker exited, session stop
succeeded, the session and its DWM disappeared, the temporary service was
absent, and MXC deprovision removed the agent account/profile.

The helper still reports `OpenInputDesktop failed 5`; that did not prevent
the verified window/UIA path. Do not equate this successful window capture
with unrestricted desktop/input access, a working whole-screen stream,
all WinUI applications, or a supported multi-session production product.
This proves the original one-app in-session pixel spike without Proxmox or
a separate VM.

#### UAC and a future installed broker

The current spike intentionally installs a new demand-start System service
and deletes it for every separately approved run. Its `Verb = "runas"` prompt
authorizes the installer; the same elevated installer removes the service.
Normal MXC account provisioning and the app/capture processes are not elevated.

A protected, one-time-installed System broker could instead accept narrowly
scoped local requests from normal clients without per-run UAC. Only that
broker would be persistent; test accounts, sessions and payloads would remain
disposable. That was not implemented at the end of the one-shot spike; the
installed follow-up below now implements this separate path. Do not simply keep the experimental
service pointing at the editable repository: installed binaries and service
configuration must be administrator-controlled, IPC must authenticate the
caller and enforce ownership, and credentials must remain inside the broker.
Updates/uninstallation would still be privileged operations.

### Installed owner-only broker: one elevated setup, repeated ordinary runs

The user requested continuing with the one-time setup design and explicitly
selected **only their Windows account** as the allowed caller. Added a
separate privileged project in `spike/mxc-visual/broker/`, a normal-user Node
client, packaging, and boundary/recovery tests. The production `src/` CLI/MCP
backend was not changed.

The broker uses administrator/System-owned versioned Program Files binaries
and private ProgramData config/journals. Its local pipe rejects remote
clients, authenticates the caller SID, and has a fixed operation allowlist;
there are no executable paths, commands, credentials or privileged output
paths in requests. The client checks the pipe server PID against SCM.
Only the selected owner's MXC agent identity is used; a negative owner-binding
check is performed before creation. Applications run under the verified
agent token, not System.

Recovery state is committed before WTS creation, with an ownership-based
session scan for interrupted creation. Normal clients retain opaque container
IDs for account cleanup. Installer/uninstaller and client coordination
prevent uninstall racing a client-only provisioned account. Acknowledgement
renames the private journal to a cleanup marker, allowing interruption without
losing the only authoritative state file.

An independent read-only review found two issues before installation:
abandoned journal temporaries could block acknowledgement, and uninstall
could overlook an account not yet submitted to the broker. Both were fixed,
and their regressions passed. The final package passed **41 C# checks and
10 Node checks**, with clean broker/worker builds. These local checks are not
substitutes for the following runtime evidence.

The user separately approved persistent installation and two disposable
visual validations, leaving the broker installed. Package identity:
`814A137226D9485CEA0F50B1B1EBEC6B9D4D4742487D8D5CA846D8F52673EAF5`,
537 files, 272078166 bytes, mostly the self-contained fixture.

| Criterion | Evidence | Outcome |
|---|---|---|
| One elevated installation | Native installer completed; SCM/registry point to protected versioned binaries | Verified; automatic LocalSystem service |
| Ordinary-owner client access | Caller was not elevated; authenticated `status` returned ready | Verified |
| Repeated no-UAC runs | Two `run-fixture` calls through the same installed broker | Verified |
| Full sessions | IDs 15/16; native creation 5105/4517 ms | Verified observations, not a benchmark |
| Correct WinUI pixels | New flag-2 before/after PNGs; latest pair visually inspected; hashes also match the earlier verified fixture states | Verified greeting/counter changes |
| Non-owner boundary | Actual agent token denied access to the mirror pipe created with the same ACL/policy | Verified for that non-owner agent |
| Protected installation/state | Actual non-elevated create-file attempts denied in both roots | Verified |
| Per-run cleanup | Accounts, profile directories/registry entries and sessions gone; broker/client pending queues empty | Verified |
| Live upgrade/uninstall/reboot or forced-service-crash recovery | Not run | Not verified; filesystem recovery simulations only |
| Parallel Full farm / arbitrary app interface | Outside the first protocol | Not implemented or verified |

Runtime evidence:

- `evidence/broker-cd9d766b27fa460e8dae9aa9843aa720/`
- `evidence/broker-b9b2afe315294b73871644ccec6f7ba2/`
- `evidence/installed-broker-validation.json`
- `evidence/installed-broker-status.json`
- `evidence/installed-broker-profile-cleanup.json`
- `evidence/installed-broker-final-integrity.json` (537 installed files
  matched the package; service running and ready, no pending jobs)

The service is intentionally left installed as `SandboxMcpFullBroker`.
Normal runs use `broker-client.mjs`; the old `system-session.mjs` still means
a separately approved temporary-service experiment and is not the installed
client. The final build package is retained, and superseded package copies
were removed. All source changes remain local and uncommitted.

Progress audit: first compiling/boundary-test checkpoint was reached around
19:24 PST versus an initial 19:45 target. Installation readiness was reached
around 20:11, eleven minutes later than the initial 20:00 estimate and four
minutes before the revised 20:15 checkpoint after review fixes. Repeated-run
proof completed by about 20:18, with independent cleanup checks at 20:19.
The completion claim is deliberately limited to the owner-only, fixed-fixture,
build-pinned experimental broker.

### Deskhand: useful fleet/capture patterns, not session creation

Read [Deskhand](https://github.com/guscatalano/Deskhand)'s current public
implementation and design notes. No Deskhand binaries were installed or run,
and no source was copied. The repository metadata reported no license;
check licensing before importing implementation code.

- [Hybrid topology](https://github.com/guscatalano/Deskhand/blob/main/docs/hybrid-local-fleet.md)
  and [RoutingBackend](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Core/Fleet/RoutingBackend.cs):
  desktop calls execute inside a per-session agent; a stable front end routes
  requests to it. Request-scoped session targeting avoids a shared mutable
  target. For this farm, require an explicit worker ID rather than defaulting
  to the active console, and bind it to a session/lifetime so recycled session
  IDs or stale HWND/UIA references cannot silently target a different worker.
- [Fleet agent](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Fleet.Agent/Program.cs):
  each agent connects outward over WebSocket; capture/UIA stay local to the
  agent's desktop. This is a good transport pattern for a refreshing farm
  grid without an inbound listener in every worker.
- [WGC implementation](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Core/Services/WgcCapture.cs):
  HWND-targeted capture uses a free-threaded frame pool, bounded frame
  acquisition and row-pitch-aware texture readback. This is relevant once a
  composited Full session or Windows guest exists; it does not create DWM.
- [DXGI duplication](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Core/Services/DxgiScreenDuplicator.cs):
  changed-frame detection can avoid repeatedly encoding static desktops.
  Distinguish unchanged frames from capture errors in our evidence protocol.
  The implementation itself warns that some headless/VM outputs are unavailable.
- [SessionLaunchService](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Core/Services/SessionLaunchService.cs):
  separates session ID, desktop and user identity, but launches processes into
  existing sessions. It does not create Full headless sessions or VMs.
  Its token-based cross-session paths and secure-desktop broker are not the
  missing MXC agent-authentication contract and were not exercised here.
- [DisplayService](https://github.com/guscatalano/Deskhand/blob/main/src/Deskhand.Core/Services/DisplayService.cs):
  enumerates existing adapter modes and changes them persistently. It does not
  provide a virtual display driver, per-worker independent DPI or composition.
  Do not use its persistent display-settings path on the human host for tests.

Do not adopt the capture implementation's catch-and-null fallback as a test
verdict: retain the attempted method, error, freshness and observed state.
Deskhand's architecture supports the in-session-helper direction, but is not
evidence that WinUI renders in the MXC configuration already tested.

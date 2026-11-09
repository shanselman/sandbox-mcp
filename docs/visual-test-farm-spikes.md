# Visual test farm: research and Windows spike handoff

Research date: 2026-11-09. Branch: `sandbox-multiplexer`.
Starting repository revision: `1c9adfc` (sandbox-mcp 1.2.0).

**Status: research only. No MXC sessions, WinUI launches, screenshot experiments,
or concurrency measurements have been run in this branch.**

## User's goal and next action

Extend sandbox-mcp from one Windows Sandbox desktop into a visual test farm:
run 4, 8, or 16 app/environment variants, including themes, dark/light mode,
DPI and resolution, collect screenshots and test results, and display a
refreshing "security camera grid." A Copilot canvas is one possible viewer;
a locally hosted web app must also work without Copilot. This need not be RDP.

The user explicitly requested:

> do it, lets do some spikes and see if we can run some winui apps in a sessions
> and get pixels out, the goal is a visual test farm

**Next: on Windows, build and run a small in-session capture helper against
MXC IsolationSession before deciding that full VMs are necessary.**
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

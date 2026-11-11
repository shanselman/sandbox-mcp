# Explorer and ShellHost fail-fast in experimental Full sessions

## What was observed

After the candidate Windows update and reboot, 96 disposable Full-session
lifecycles completed: 31 coordinated grids of three sessions and three
single-session runs. All 2,850 stream images were validated, cleanup completed,
and independent inventory found no remaining owned accounts or profiles.
The host did not reboot again during the qualification.

That does **not** mean the shell started cleanly. The same qualification
window recorded 96 Explorer and 81 ShellHost Application Error events.
Their event-security identities matched the disposable agents, not the
interactive owner. These are counts of recorded events, not a claim that
every possible crash was reported.

Existing WER artifacts supplied a minidump and a heap dump for each executable.
Dump module names, process IDs, exception codes and timestamps matched the
owned-agent Application Error records. Six recent WER reports were also
preserved. Collection used one explicitly approved elevated read-only copy;
it did not change WER settings, ACLs, the service or original files.
No new reproduction was needed to obtain these dumps.

## Dump diagnosis

| Executable | Observed failure | Supported interpretation |
|---|---|---|
| Explorer.exe | `explorer!CTray::Init+0x2dc`, through `wil::details::in1diag3::FailFast_Unexpected`; HRESULT `0x8000FFFF` | `E_UNEXPECTED` during desktop/tray initialization. The failed internal prerequisite is not established by this stack alone. |
| ShellHost.exe | `ControlCenter!winrt::ControlCenter::implementation::XamlApplication::LoadWinUIFrameworkPackage+0xc8`; HRESULT `0x80073D54` | Win32 15700, `APPMODEL_ERROR_NO_PACKAGE`: "The process has no package identity." Failure occurs while loading the WinUI framework package. |

Both exception records contain `0xC0000409` with first parameter **7**,
`FAST_FAIL_FATAL_APP_EXIT`, and then the HRESULT above. The debugger's generic
"security check failure or stack buffer overrun" text must not be interpreted
as proof of a buffer overrun: these stacks show deliberate WIL fail-fast paths.

Explorer's relevant call chain is `CreateDesktopAndTray -> CTray::Init ->
FailFast_Unexpected`. ShellHost's relevant chain is `ControlCenterMain ->
ControlCenterApplication::StartMessageLoop -> XamlApplication ->
LoadWinUIFrameworkPackage -> FailFast_Hr`.

Matching Microsoft public symbols were used locally. This is a diagnosis of
the observed failure sites and reported errors, **not** a proved causal link
between the two application failures or to the earlier CSRSS host bugcheck.

## Follow-up: why Explorer itself can fail on package initialization

Further read-only inspection of the same dumps narrowed the failure sites:

- In `CTray::Init`, the branch to the observed `FailFast_Unexpected` follows
  `XamlExtensions::IsExtensionAvailable` for
  `WindowsUdk.UI.Shell.TaskbarControllerExtension` and `Taskbar`. The
  fail-fast is the false branch of that availability check. This is Explorer
  checking one of its own shell/XAML extensions, not a fault in HelloWinUI.
- In `LoadWinUIFrameworkPackage`, the captured import resolves to
  `kernel_appcore!AddDependencyToProcessPackageGraph`, and the package-family
  argument is `Microsoft.UI.Xaml.CBS_8wekyb3d8bbwe`. Its retry loop eventually
  reports the observed `0x80073D54` and fails fast. This more precisely locates
  the package-graph failure; it does not by itself prove why the API returned
  that error.

The strongest public comparison is
[Microsoft KB5072911: Explorer, the Start menu, and other XAML-dependent apps might not start or close unexpectedly](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/11/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedl).
It explicitly names both Explorer and ShellHost, first-time user logon and
non-persistent/VDI environments, and Windows XAML dependencies that fail to
register in time after servicing. The listed packages are
`MicrosoftWindows.Client.CBS`, `Microsoft.UI.Xaml.CBS`, and
`MicrosoftWindows.Client.Core`. Its workaround discusses registration in the
affected user session before Explorer starts.

**Important scope limit:** that article covers Windows 11 24H2/25H2 and says
the documented issue was addressed starting with June 2026 updates. Our
experiment uses the different 28000 build line. This is a close, authoritative
failure-mechanism comparison, not proof the same servicing bug exists here or
that its workaround is appropriate for experimental agent accounts.

Bounded GitHub searches did not find an exact independent report of this
experimental Full-session failure:

- [microsoft/microsoft-ui-xaml#3990](https://github.com/microsoft/microsoft-ui-xaml/issues/3990)
  (The process has no package identity - Error in XAML Islands): a related
  package-identity error in a custom XAML Islands app, not this ShellHost
  failure. Public search excerpts were available; authenticated detail reads
  were blocked by organization SAML enforcement.
- [valinet/ExplorerPatcher#2097](https://github.com/valinet/ExplorerPatcher/pull/2097)
  (Taskbar10: Try to make fixes for 22621.2134+): taskbar patching compatibility
  work, not evidence that our fresh, unmodified agent sessions have that cause.

The leading hypothesis is now **Windows shell package registration,
activation or package-graph readiness in the disposable user context**.
Developer Mode is a different mechanism: the documented device-wide
`AllowDevelopmentWithoutDevLicense` flag was already 1. Our fixture is
intentionally unpackaged/self-contained and does not deploy Explorer or
ShellHost.

A decisive follow-up should compare a Full session with no fixture against
the fixture run and observe the affected agent's package/extension state
before shell initialization. The accounts from this run were already
deprovisioned, so querying their registrations afterward would not establish
what existed at failure time. No AppX re-registration, shell restart, policy
change or additional live session was performed for this research.

### Profile-classification candidate under live test

The user subsequently requested testing
[#4](https://github.com/shanselman/sandbox-mcp/pull/4)
(Classify provisioned agent profile before shell launch): the change marks
fresh provisioned agent profiles as shell-agent profiles before Full-session
creation. The tested patch is commit
`fdee172bd453fb345f1de3fe359769fd45ed7637`; testing it is not a PR merge.

The patch verifies that the agent's registry hive is not loaded, that its
profile has `AgentUserProfile=1` and `ComposableAgentUserProfile=1`, and that
`ShellAgentUserProfile` is absent. Only then does it set
`ShellAgentUserProfile=1` and remove `ComposableAgentUserProfile`.
These writes are scoped to the freshly provisioned, owner-verified agent SID.
The credential is disposed if validation or a registry operation fails.

On the new LSM build, before this classification change, Explorer still
recorded a startup fail-fast in the baseline run. After an explicitly approved
broker installation containing the exact patch, the first six fresh
lifecycles (three singles plus a coordinated three-session grid) completed
with **zero Application Error events attributed to their agent identities**,
30 valid PNGs in each streamed case, and no remaining owned accounts/profiles.
This is a promising controlled comparison on the same host/boot, not a final
long-run verdict. A fresh 31-grid qualification was then started with an
additional stop-on-agent-startup-error check.

That first attempt stopped at grid 12 on a separate evidence-collector defect:
the dark worker preserved 30 valid images, but the polling collector copied
only 29, skipping sequence 15. The first 11 grids passed the full audit; the
12th grid is retained as a qualification failure even though the earlier
client report called the worker/cleanup result passed.

Across all 39 completed lifecycles in that stopped attempt, an independent
event/account/profile audit found zero agent-attributed Application Error
events and no remaining owned accounts or profiles. The host boot was
unchanged. This still supports the profile-classification fix for these shell
failures, without disguising the separate collector failure as success.

The collector previously read only the newest immutable metadata file at each
poll. It now drains unseen published frames in order, preserving their
original capture timestamps and recording when they were actually received.
Intermediate corrupt/wrong-session/error frames cannot be silently skipped.
Both client verdict paths reject incomplete collection when a live collector
is requested. Deterministic regressions failed before this change and pass
after it. No timing threshold was relaxed and no historical evidence was
repaired. A new qualification directory was used to restart the full run
with the corrected ordinary-user client; no broker/UAC update was needed.

#### Latest result: startup faults absent, strict cadence qualification failed

The fresh corrected-client attempt passed five grids and stopped on the sixth.
All 90 images in that grid were valid and all 90 were collected. However,
two streams missed the unchanged 2,000-ms maximum-gap requirement:

| Variant | Maximum gap | Capture duration at the stall | Result |
|---|---:|---:|---|
| Light | 4,011 ms | 3,022 ms | Failed cadence |
| Dark | 3,779 ms | 2,789 ms | Failed cadence |
| Enlarged dark | 1,066 ms | No matching long stall | Passed cadence |

The two slow captures completed at approximately the same instant,
20:58:57 PST on November 10. Recorded overlap was 27,180 ms and cleanup
completed. These timings locate the delay in capture, not in the now-fixed
collector backlog handling; they do not establish the underlying scheduling,
window-rendering or graphics cause. The attempt was not retried blindly and
the threshold was not weakened.

Across both classified-profile attempts there were **60 distinct completed
agent/session lifecycles**, **zero agent-attributed Application Error events**,
and no remaining tracked accounts, `Win32_UserProfile` entries or ProfileList
registry keys. Only the original services/console sessions remained and the
host boot was unchanged. This is strong evidence that the classification
change addresses the observed Explorer/ShellHost startup failures in this
workload, but **neither 31-grid qualification completed**: the first stopped
on the collector defect and the second on the capture timing failure.

One unusual machine-prefixed profile directory remained on disk. Its owner
ACL could not be read without additional privileges, so it was not attributed
to a run or deleted. The absence of tracked account/profile records must not
be presented as proof that every profile directory was removed. Its naming,
ownership and cleanup are a separately recorded follow-up.

Final offline verification after the collector fix passed all **101 broker
checks, 50 spike Node tests, 112 repository unit tests, and 13 architecture
packaging tests**. All 538 installed candidate files still matched the
verified package. There were no further live attempts after the cadence
failure. These passing offline checks do not override the failed live timing
verdict.

`Kind=Full` and `InitialProgram=null` remained unchanged; the deferred
InitialProgram suggestion below was not used. No AppX re-registration or
host-wide shell/display setting was changed.

### Deferred engineer suggestion: explicit InitialProgram

On 2026-11-10, the user relayed this engineer suggestion verbatim:

> "If you pass in a program for initialprogram"

**Deferred at the user's explicit request; do not implement or run it now.**
For a later controlled experiment, compare the current null `InitialProgram`
with an explicitly selected program and observe whether shell startup and the
Explorer/ShellHost failures change. The program and expected behavior have
not yet been specified; this is a lead, not a confirmed workaround.

Before that future experiment, recheck the initial-program allowlist documented
in the main spike handoff (previously defaulting to `%SystemRoot%\System32`),
alongside the current build pins and normal ownership/cleanup safeguards.
This note does not authorize an allowlist change, service update or live run.

## Repository-level reproduction

This is the existing exercised harness, not a dependency-free minimal native
reproducer. Only use an appropriate test machine with the exact inspected
Windows binary set and qualified owner-only broker described in
[Windows update qualification](windows-update-qualification.md). Do not
disable the build guards or copy/downgrade system DLLs.

The relevant setup is Windows ARM64; Explorer/ShellHost and the WTS client
report 10.0.28000.3365. Other Windows component versions differ, so use the
complete pinned set rather than inferring compatibility from that number.
The installed experimental broker release was
`A571EC123D72F99E863F4999D4193682E2DB19D20E41CE36E3A43998DE818891`.

From the repository root, with a supported Node version and the broker already
installed and ready:

```powershell
node spike\mxc-visual\broker-client.mjs status
# Use a new directory every time; never reuse an uncertain or failed attempt.
$output = Join-Path $PWD ('spike\mxc-visual\evidence\shell-repro-' + [Guid]::NewGuid().ToString('N'))
node spike\mxc-visual\broker-client.mjs run-fixture light $output
```

The client provisions an MXC-owned disposable account. The installed System
broker retrieves that account's owner-bound opaque credential, validates and
marshals it with SSPI, then calls `EXPERIMENTAL_WTSCreateHeadlessSession` with
Full kind, V1 size 36 and **null InitialProgram**. It launches only its fixed
WinUI worker as the disposable agent, not as System. The worker performs the
greeting/counter interaction and captures 30 frames. Session/account cleanup
and evidence preservation happen automatically; no per-run UAC is needed.

Observe Application Error events for Explorer.exe and ShellHost.exe during
startup and correlate their security identity/process ID with the owned run.
The WinUI fixture can render and pass its UIA/capture checks while the system
shell components fail. Evidence contains measured actual DPI 96;
`dark-large` is a simulated 1.5x fixture transform, not monitor DPI.

If investigating the native path alone, the key question is whether Full
sessions created this way are expected to initialize packaged system shell
components, and what supported package-identity/profile initialization or
shell-selection contract applies. The harness does not install/register AppX
packages as a workaround, modify shell policies, or suppress the crashes.

## Evidence handling

The local support bundle contains the original copied WER dumps/reports,
debugger output, this repro, and SHA-256 checksums. Raw dumps can contain
memory unrelated to the failing instruction; they are not checked into Git.
Sanitized failure descriptions and reproduction steps can be shared separately
without pretending that they replace the raw debugger evidence.

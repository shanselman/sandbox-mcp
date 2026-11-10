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

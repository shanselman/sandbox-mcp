# Tiny SDK-only companion-profile reproduction

Filed as [microsoft/mxc#1727](https://github.com/microsoft/mxc/issues/1727)
(IsolationSession: deprovision returns success but retains an unloaded companion profile):
an SDK-only cleanup-retention report with the minimal code below and the
documented/source-verified lifecycle sequence. It asks which layer owns the
behavior rather than assuming an MXC implementation defect.

The reproduction is
[`spike/mxc-visual/mxc-profile-repro.mjs`](../spike/mxc-visual/mxc-profile-repro.mjs).
It creates **one** IsolationSession, runs only `whoami`, stops it and
deprovisions it. PowerShell is used only for read-only profile inventory.
There is no GUI app, screenshot helper, custom broker call, direct WTS call,
registry edit, profile-classification change or UAC operation.

On a Windows host where MXC IsolationSession is available, put the script in
a directory with the SDK installed:

```powershell
npm install @microsoft/mxc-sdk@1.0.1
node .\mxc-profile-repro.mjs
```

Use the SDK-supported Node runtime (the observed run used Node 24.22.2).
The script prints the provision metadata, all lifecycle responses, and any
profile SIDs not present in the initial inventory. It exits 1 if a new profile
remains; this is the reproduction assertion, not a workload exit failure.
Review/redact local usernames, SIDs, opaque IDs and paths before sharing logs.
Do not rerun repeatedly or delete profiles based on this script's output.

## Observed result, November 10, 2026

Host: Windows ARM64, reported OS build **28000.3501**, display version **26H1**,
with recent Windows engineering fixes. Results on an unmodified retail image
have not been established. SDK **1.0.1**; Node **24.22.2**.

```text
start       {"warnings":[]}
exec        {"exitCode":0,"timedOut":false,"warnings":[]}
stop        {"warnings":[]}
deprovision {"warnings":[]}

newProfilesAfterDeprovision:
  SID:       S-1-5-110-<machine-components>-<rid>
  LocalPath: C:\Users\<computer>_<agent>_$
  Loaded:    false
```

An independent query **after the Node process exited** confirmed that the
original provisioned account was gone, no test session was active, and the
new companion profile, ProfileList key and directory remained. Its HKU hive
was not loaded. A further check still found it **186 seconds after Node
exited**. The global companion-profile count increased **184 -> 185**;
those global totals include older experiments and are not an attribution of
all existing profiles to this reproduction. The single new profile is the
before/after delta.

Before reducing it to `whoami`, a separate SDK-only WinUI control reproduced
the same profile delta (**183 -> 184**) and the retained entry was still
present 111 seconds after the process exited. Its UIA interaction succeeded,
but before/after WinUI images were byte-identical blank client areas, without
DWM/composition. That visual limitation is separate from this cleanup issue
and is deliberately excluded from the tiny repro.

## Did the caller omit cleanup?

The call order matches MXC's
[state-aware TypeScript example](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/docs/development/architecture/backends/isolation-session/state-aware-typescript.md#end-to-end-example).
The installed Node SDK's `runInContainer` calls `proc.dispose()` in `finally`.
The repro awaits both `stopContainer` and `deprovisionContainer`; neither
returns a warning. The API describes deprovision as releasing all backend
resources associated with the provisioned container.

In the inspected
[Rust state-aware adapter](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/src/mxc-sdk/src/backends/isolation_session/common/state_aware.rs),
stop delegates to `manager.stop_session()` and deprovision delegates to
`manager.deprovision_agent_user()`. The
[manager](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/src/mxc-sdk/src/backends/isolation_session/common/manager.rs)
awaits Windows `StopSessionAsync` and `RemoveUserAsync` respectively.
The one-shot cleanup path uses the same pair. No additional application-side
companion-profile cleanup call was found in the published API/documentation.

This establishes a reportable contract question, not the implementation owner:
is the retained companion intentional/deferred, and if so what retention and
supported cleanup guarantee applies? Otherwise, should Windows `RemoveUserAsync`
or MXC remove it or return a cleanup warning? Do not assume the unpublished
Windows implementation's behavior from the SDK wrapper alone.

The subsequently supplied checkout at `37a3e9319d4369706f736a493bd22890d3d1d8ec`
(November 10) was checked too: its manager, state-aware adapter and TypeScript
lifecycle example were byte-identical to the inspected files linked above.
Thus this was not just a conclusion from the older cached checkout.

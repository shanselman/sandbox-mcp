# Direction: let MXC own the complete session lifecycle

## Engineering framing

The user relayed this Windows-engineer summary:

> "Super tl;dr - this spike is jumping ahead of what MXC supports while partially using MXC so it is hitting a feature/API gap."

Treat the hybrid as an exploratory bridge, not a supported MXC graphical
session implementation. Correcting account classification makes the chosen
Windows account/session combination more consistent; it does not supply a
supported end-to-end SDK contract. The target is for MXC to expose and own the
appropriate account type, graphical session, process launch, and complete
cleanup. The SDK-only profile-retention reproduction is a separate contract
question and should not be conflated with the hybrid's unsupported GUI path.

The engineer also warned that the diagnostic patch relies on
"internal(ish) regkeys". Matching the advised account/session pairing is
therefore not the same as implementing a supported product contract. Keep
the registry-classification path explicitly experimental; do not extend it
with more private registry cleanup or ship it as ordinary MXC setup.
The eventual replacement should be an MXC-owned provisioning option and
complete lifecycle, not application-maintained knowledge of these values.

## What this spike does differently

The current visual spike is a hybrid, not an ordinary MXC IsolationSession
run:

| Phase | Normal MXC path in inspected source | Current Full-session spike |
|---|---|---|
| Provision | `provisionContainer` -> Windows `IsoSessionOps.AddUserAsync*` | Same MXC provision call |
| Start | `startContainer` -> `StartSessionAsync*` | Private credential retrieval, shell-profile classification, direct experimental WTS Full creation |
| Launch | MXC execution APIs and its backend process launcher | Fixed worker launched with a verified agent token through `CreateProcessAsUserW` |
| Stop | `stopContainer` -> `StopSessionAsync` | Direct experimental WTS stop |
| Deprovision | `deprovisionContainer` -> `RemoveUserAsync` | MXC deprovision after the custom lifecycle |

We also change the newly provisioned profile from composable-agent to
shell-agent classification. That is not an option in the inspected published
Node SDK. Windows may use some of the same low-level machinery internally;
the distinction is the supported caller contract and lifecycle ownership,
not a claim that MXC and WTS never share implementation.

The retained companion profiles initially raised concern about the combined
workflow. Later SDK-only controls reproduced retention too, so mixing the
lifecycles is not a necessary condition for that behavior.

A Windows engineer subsequently clarified, through the user, that account
classification and session kind are separate. The original account was tagged
for an agent in a utility session, permitting Windows to skip AppX and other
shell initialization. Passing `WTSHeadlessSessionKindFull = 1` alone did not
change that account classification. The patch changes it to the shell-agent
type immediately after provisioning and before first profile load. Thus an
MXC-only graphical path needs appropriate account provisioning **and**
compatible session creation, not merely a Full/Utility start switch.

## What the available MXC contract supports

As checked on November 10, 2026:

- The installed package is `@microsoft/mxc-sdk` **1.0.1**, also the version
  returned by the public npm registry's `latest` endpoint:
  <https://registry.npmjs.org/@microsoft%2Fmxc-sdk/latest>.
- Its `IsolationSessionStartConfig` is only the common lifecycle
  configuration (telemetry), with no Full/Utility, DWM, shell-profile,
  initial-program, DPI or resolution selection.
- The cached upstream source at
  [`caef80977b538bcb4870c9c4796c4c59aae1d3d9`](https://github.com/microsoft/mxc/tree/caef80977b538bcb4870c9c4796c4c59aae1d3d9)
  starts sessions through `StartSessionAsync2` or `StartSessionAsync`, and
  removes users through `RemoveUserAsync`.
  [Manager source](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/src/mxc-sdk/src/backends/isolation_session/common/manager.rs)
  records these paths; the
  [state-aware adapter](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/src/mxc-sdk/src/backends/isolation_session/common/state_aware.rs)
  delegates lifecycle ownership to that manager.
- The inspected [one-shot backend documentation](https://github.com/microsoft/mxc/blob/caef80977b538bcb4870c9c4796c4c59aae1d3d9/docs/development/architecture/backends/isolation-session/oneshot.md)
  lists GUI applications as unsupported. Our earlier pure-MXC tests did
  obtain basic WinForms/WPF pixels and UIA, but WinUI client pixels were blank
  and no DWM was present. A partial practical success does not establish a
  supported composited desktop contract.
- Microsoft's [Windows platform security for AI agents announcement](https://blogs.windows.com/windowsdeveloper/2026/06/02/windows-platform-security-for-ai-agents/)
  describes the initial session release as non-interactive, with additional
  capabilities targeted for later releases.

An in-session screenshot helper does not itself require bypassing MXC. It
could remain an ordinary workload launched through MXC if MXC can supply the
necessary session/rendering behavior and own its teardown.

**Untested after the newest Windows fixes:** the pure-MXC WinUI control has
not been rerun on this latest host state. An unchanged SDK version does not
prove that its Windows-backed behavior is unchanged. Before concluding that
the hybrid remains necessary, the next explicitly controlled comparison
should use only the published provision/start/execute/stop/deprovision path,
observe DWM and actual WinUI pixels, and inventory both normal and companion
profiles before/after. This research did not launch that experiment.

**Subsequent controls:** that SDK-only WinUI experiment was then run on the
updated host. UIA succeeded, but DWM/composition was absent and the captured
WinUI client stayed blank. SDK teardown returned no warnings, yet one unloaded
companion profile remained. A second, much smaller `whoami`-only SDK control
reproduced the profile retention without any GUI/capture/custom broker code.
See the [tiny reproduction and documented cleanup trace](pure-mxc-profile-repro.md).
The hybrid is therefore not a necessary condition for this observed cleanup
behavior; responsibility between MXC and the Windows implementation remains
unresolved.

## Related upstream work and visibility limits

The cached `origin/feature/isolation-session-internal` head is
[`a466903b2f9664111a95a7c7f8cf788e783aa1a7`](https://github.com/microsoft/mxc/commit/a466903b2f9664111a95a7c7f8cf788e783aa1a7),
dated November 5, titled *Add Entra cloud-agent support to the IsolationSession
state-aware lifecycle*. Its changes concern enterprise identity inputs in
the development contract; the inspected branch does not expose a Full/DWM
mode. It is not evidence that switching to that branch solves this visual
farm's requirements.

[microsoft/mxc#1705](https://github.com/microsoft/mxc/issues/1705)
(IsolationSession: a user-visible, interactive session with input and UI Automation scoped to the agent session):
requests the interactive desktop/viewer/automation capabilities closest to
this goal. Publicly indexed results show a feature request, not a released
Full-session implementation.

Authenticated GitHub access to the Microsoft organization was blocked by
SAML/conditional-access requirements during this check. The local main
snapshot is from November 9 and the branch snapshot from November 5; public
search indexing can lag. Therefore this is **not an exhaustive current
branch/PR inventory**, and does not rule out newer or private engineering work.
No token/account workaround was attempted.

The user later supplied a newer checkout at
`37a3e9319d4369706f736a493bd22890d3d1d8ec` (November 10); the relevant lifecycle
manager, adapter and example are unchanged. For the cleanup issue filing only,
the user explicitly approved using their refreshed `gh` login instead of the
session's stale injected token. That account's identity and Microsoft SSO
access were verified before filing the [minimal cleanup report](pure-mxc-profile-repro.md).
This did not refresh the earlier complete branch/PR inventory.

## Recommended boundary

Aim for **MXC-only lifecycle ownership**, while retaining our own fixture,
in-session capture helper, evidence checks, and viewer:

`MXC provision -> MXC start -> MXC execute(helper/app) -> MXC stop -> MXC deprovision`

Ask the Windows/MXC owners for the supported API or intended branch that:

1. Selects a composited/headless Full desktop suitable for WinUI.
2. Owns profile classification and all companion identities/profiles.
3. Reports cleanup warnings and failure/recovery state without requiring the
   caller to infer private SIDs or edit ProfileList.
4. Defines architecture/build support, initial-program behavior and any future
   display/DPI controls.

If that surface is missing, contribute the capability and lifecycle regression
to MXC rather than growing a second production session manager here. Until
then, retain the direct-WTS path only as explicitly experimental evidence.
Do not silently substitute a non-composited session and claim equivalent
WinUI coverage, or describe hybrid cleanup failures as proven MXC-only bugs.

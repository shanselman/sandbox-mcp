# Direction: let MXC own the complete session lifecycle

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

The retained companion profiles demonstrate a gap in our combined workflow.
They do not yet establish a defect in a fully MXC-managed lifecycle.
Changing an SDK-created profile and then expecting its existing removal path
to cover additional identities is a plausible explanation that needs the
Windows/MXC owners' confirmation.

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

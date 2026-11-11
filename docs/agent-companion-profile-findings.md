# Agent companion profile cleanup findings

## Status and scope

**Further native qualification is on hold. Cleanup is incomplete.**
The separately user-approved pure-SDK controls described below also reproduced
retention; they do not establish that cleanup is safe or authorize more runs.

Read-only inspection on November 11, 2026, following the two stopped
classified-profile qualification attempts found:

| Recent classified-attempt subset at 05:07:51 UTC | Count |
| --- | ---: |
| Distinct provisioned identities in the two attempts' receipts | 60 |
| Remaining original account/profile registrations in the earlier audit | 0 |
| Companion `Win32_UserProfile` rows matching those identities' SID suffixes | 60 |
| Corresponding companion ProfileList keys | 60 |
| Corresponding directory entries, including hidden entries | 60 |
| Hidden companion directories | 59 |
| Visible companion directories | 1 |
| Loaded companion profiles reported by `Win32_UserProfile` | 0 |

**The 60 companions are a matched recent subset, not the host-wide total.**
A later full-host CIM inventory, taken before the user-approved pure-MXC
control, found 183 unloaded companion profiles. The subsequent controls
increased the host-wide total as follows:

| Scope and checkpoint | Host-wide companion profile count |
| --- | ---: |
| Before the pure-SDK WinUI control | 183 |
| After the pure-SDK WinUI control | 184 |
| After the minimal whoami-only SDK reproduction | 185 |

These are different inventory scopes and checkpoints, not a correction of the
60-row subset count. The earlier hidden/visible directory counts apply only to
that subset; they must not be extrapolated to all host profiles.

The original receipts identify accounts in the `S-1-5-21-…` family. The
additional profile registrations use the `S-1-5-110-…` family. These are
different SIDs, not alternative spellings of the original SID. The family
prefixes here are generic; actual account identifiers are intentionally omitted.

The 60-row census used suffix matching to discover candidates, then joined
their registered profile paths to parent-directory entries. It proves that
those resources exist, **not an authoritative parent-account relationship for
every candidate**. No bulk deletion should be authorized from that transformation.
The detailed event-based attribution below was performed for the reused-name
case, not independently for every one of the 60 candidates.

The original read-only investigation did not read protected directory contents
or security descriptors after the reported access denial, elevate, change
ACLs, load hives, delete accounts/profiles, change services, or start sessions.
The later controls were separately approved and are summarized below from
their reported results; this documentation update performed no native calls.
This document contains no actual user names, computer names, complete SIDs, or
private absolute paths.

## Pure-SDK controls: hybrid execution is not necessary

See [Pure MXC profile reproduction](pure-mxc-profile-repro.md) for the separate
reproduction record. Both controls used `@microsoft/mxc-sdk` 1.0.1's ordinary
provision/start/run/stop/deprovision lifecycle, without the SYSTEM broker,
direct WTS calls, or profile-classification registry edits.

- **WinUI control:** added one companion profile, increasing the host-wide
  total from 183 to 184, with empty SDK warning arrays. UI Automation worked,
  but the WinUI surface was blank and no DWM was present. This rendering
  limitation is separate from the cleanup result.
- **Minimal control:** a whoami-only workload added another companion,
  increasing the total from 184 to 185. It used no GUI or helper. Every SDK
  phase returned `warnings: []`, and the worker exited with code 0. The
  original account and session were gone, while the companion profile,
  ProfileList registration, and directory remained, with the profile unloaded,
  **186 seconds after the Node process exited**.

These controls establish that the hybrid Full-session path, classification
change, GUI workload, and helper are **not necessary for companion retention**.
Do not present profile classification as a proven root cause. The evidence
also rules out the SDK caller merely still being alive at the minimal
control's final observation. It does not establish permanent retention,
the native component responsible, or the intended eventual cleanup policy.

## What the visible suffixed directory establishes

The first qualification attempt reused a short agent name in grid 04,
enlarged-dark variant, and grid 08, light variant. The provisioning receipts
record different original SIDs and different sessions for the two incarnations.
The worker output remained under the short-name profile; the extra directory
was a separate profile, not a renamed worker output directory.

Evidence in `Microsoft-Windows-User Profile Service/Operational`:

| Record | UTC time on November 11 | Observation |
| --- | --- | --- |
| 2301 | 04:30:35.8448296 | Event 5 binds the older unsuffixed companion profile's hive path to its companion SID. |
| 2456 | 04:36:53.9774848 | Event 1 names the later owned session; its security principal is the later original agent SID. |
| 2457 | 04:36:53.9860582 | Event 5 binds the short-name profile to that original SID; its activity ID matches record 2456. |
| 2461 | 04:36:55.3422418 | Event 5 binds the exact `.000` directory's hive path to the later companion SID. |
| 2462 | 04:36:55.5006702 | Event 5 independently binds that directory's classes hive to the same companion identity. |
| 2468 | 04:36:56.3458973 | The older unsuffixed companion hive loads again during the later run. |
| 2487 | 04:37:52.6739756 | Event 4 records completion of the later owned session's logoff notification. |

The visible directory's parent-entry creation timestamp is
04:36:55.0999441 UTC, within the later run's creation interval. At the original
inspection, its `Win32_UserProfile.LocalPath` and ProfileList `ProfileImagePath`
agreed with record 2461. The older base directory also remained, with the
hidden attribute; the `.000` directory lacked that attribute. This explains why an ordinary
directory view showed only one remainder.

At the original inspection, the older companion SID resolved to an account
in the `MEM` domain, while the later companion SID did not resolve. Failure to
resolve an account name does not imply absence of its registered profile or directory.
The `$` character alone is not evidence of a machine account.

**Confidence boundaries:**

- **High:** the exact visible directory belongs to the event-identified
  companion profile and was created during the later owned run.
- **Strong hypothesis:** retained companion state and short-name reuse caused
  a profile-path collision. `.000` alone does not establish this mechanism.
- **Not established:** the precise native naming rule, why the older companion
  was loaded again, or which component owns companion cleanup.
- At the original inspection, both companion hives were absent from HKU, and
  both profiles reported `Loaded=false`. Hive loading at that observation does
  not explain the continued existence. A transient earlier retention condition
  is not excluded.

The companion load records have their own activity IDs, different from the
original session's logon activity, and no related-activity field. Their security
principal is SYSTEM. Shared process/thread identifiers are not proof of
ownership. The accessible profile metadata has no explicit parent-agent SID
field. Thus the event/path/time/receipt correlation is strong attribution, but
not a documented native parent-child identity API or sufficient authorization
for an automatic destructive operation.

## Installed SDK contract and warnings

The inspected package is `@microsoft/mxc-sdk` **1.0.1**.
Paths below are relative to its directory under
`spike\mxc-visual\node_modules\@microsoft\mxc-sdk`.

- `dist\v1\lifecycle.d.ts:38–42` describes `deprovisionContainer` as releasing
  all backend resources associated with a provisioned container and making
  its opaque identifier invalid after successful return.
- `dist\v1\lifecycle-types.d.ts:73–84` explicitly promises removal of the
  ephemeral shared workspace on deprovision. Provision metadata contains
  only the original agent name, SID, and workspace path.
- `dist\v1\lifecycle.js:219–227` submits a native deprovision envelope and
  returns a parsed `LifecycleResult`.
- `dist\v1\lifecycle-types.d.ts:260–263` defines that result as
  `{ warnings: string[] }`.
- `dist\v1\lifecycle.js:278–285` converts an absent native `warnings` property
  to `[]`; a non-array or any non-string element throws `MxcError` with code
  `backend_error` and message `deprovision response carried malformed warnings`.
- `dist\v1\operation-options.d.ts:31–34` exposes telemetry only for
  deprovision. There is no public companion-SID, profile-path, force-cleanup,
  or ownership-override parameter.
- `README.md:224–236` distinguishes returned lifecycle warnings from workload
  stdout/stderr. Native failures are surfaced separately as `MxcError`.

The empty-array result below was subsequently observed in the pure-SDK
controls. It is not proof of resource removal:

```json
{ "warnings": [] }
```

The nonempty result below is a **schema illustration, not observed warning
text**:

```json
{ "warnings": ["<backend-provided warning text>"] }
```

No deprovision warning text was retained in the original classified-attempt
client receipts or those two attempts' JSON/text reports. The wrapper at the
investigated baseline `6f8fecb` awaited deprovision but discarded its return value before reporting
account removal/cleanup. It also skipped deprovision when the original account
was already absent. Consequently historical warning-free cleanup cannot be
claimed for those attempts. The later pure-SDK controls did retain empty warning
arrays despite companion retention. Warning propagation is necessary
observability, but neither an empty array nor propagation alone establishes
cleanup or removes existing companions.

The SDK's general resource-release wording establishes the intended lifecycle
boundary, but the inspected public TypeScript layer delegates cleanup to native
code without exposing its companion handling or cleanup timing. The original
harness used custom Full-session creation and profile classification; the
subsequent ordinary-SDK controls reproduced retention without either. Thus
uncertainty about the hybrid path's contract cannot explain away the pure-SDK
result. Which SDK or Windows component is responsible remains unestablished.
An attempt to inspect the referenced upstream repository was blocked by
organization SSO authorization; that boundary was not bypassed.
There is no verified basis to name the responsible native deletion routine.
The initial upstream filing attempt returned SAML HTTP 403 and created no
issue. After a user-approved credential refresh and identity/access verification,
[#1727](https://github.com/microsoft/mxc/issues/1727)
(IsolationSession: deprovision returns success but retains an unloaded companion profile)
was created and verified OPEN: successful SDK deprovision leaves an unloaded
companion profile behind.

`validateDeprovision` is documented as native dry-run validation
(`dist\v1\lifecycle.js:256–262`). It is not a filesystem cleanup audit,
companion enumeration API, or repair operation. It was not invoked here.
Do not replay already-consumed identifiers to try to remove companions:
successful deprovision invalidates the identifier, and these two distinct
same-name provisioning receipts even contained the same opaque identifier.

## Supported read-only verification and stronger binding

Use the following documented interfaces under existing access, preserving
access-denied or unavailable results as **unknown**, not absent:

| Interface | What it establishes | What it does not establish |
| --- | --- | --- |
| [`Win32_UserProfile`](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/userprofileprov/win32-userprofile), queried through CIM | Exact profile SID, `LocalPath`, `Loaded`, `refCount`, `Special`, and status. | Parent agent, authorization to delete, or directory absence. |
| [`LookupAccountSidW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-lookupaccountsidw) and [`LookupAccountNameW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-lookupaccountnamew) | Resolved domain/name/SID and account type; a fully qualified name can be round-tripped. | A surviving profile is absent merely because translation fails; a same-name identity is the same incarnation. |
| [`GetTokenInformation`](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation), `TokenUser` and `TokenSessionId` | Actual identity and session of an already-accessible process token, with `TOKEN_QUERY` access. | A parent-child identity relationship from numeric SID similarity. |
| [`GetUserProfileDirectoryW`](https://learn.microsoft.com/en-us/windows/win32/api/userenv/nf-userenv-getuserprofiledirectoryw) | Profile path for an already-accessible user token with `TOKEN_QUERY`. | Permission to create/log on a user or open a denied token. |
| [`FindFirstFileW`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfilew)/`FindNextFileW`, or nonrecursive `Get-ChildItem -Force` on the accessible profiles root | Directory names, hidden/reparse attributes, and timestamps, including hidden remnants. | Contents, owner SID, deletion authorization, or absence when enumeration failed. |

Exact ProfileList path metadata and already-loaded HKU key presence can
corroborate the profile-provider results where readable. Do not edit registry
keys, load an offline hive, or traverse a reparse point to complete an audit.
Registry `State` and CIM `Status` are different properties; do not interpret
one using the other's bit definitions.

For a future separately approved lifecycle, bind evidence while identities
still exist:

1. Retain the provision receipt, original SID, opaque container identifier,
   broker request ID, owned session ID, and lifecycle timestamps.
2. Record pre/post profile and parent-directory inventories. Treat suffix/name
   matching only as candidate discovery.
3. Use accessible profile-service events' explicit `File` and `Key` values to
   bind each candidate path to an actual SID; retain event record IDs and time.
4. Where an already-authorized collector can query a companion process token,
   record its actual `TokenUser`, `TokenSessionId`, process ID/start time, and
   profile path against the broker's independently verified owned session.
   Do not start a session, manufacture a token, enable debugging privileges,
   or widen access for this purpose.
5. Preserve discrepancies, shared/reused names, and missing mappings as
   unresolved. A fully qualified account lookup or filename resemblance alone
   cannot resolve the demonstrated same-name/different-SID ambiguity.

For cleanup qualification, separately report original-account absence,
original-profile absence, companion-profile absence, directory absence,
loaded-hive state, deprovision warnings, and evidence-read failures. Keep a
receipt after account removal if any companion/path verification is unresolved.
An empty warning array, `Loaded=false`, successful worker, or empty broker
queue must not collapse these into a single "cleaned" verdict.

## Cleanup route: approval and engineering clarification required

The normal SDK lifecycle remains the first ownership boundary: stop the owned
session, preserve evidence, deprovision its still-valid identity once, retain
the result, and verify independent resource absence. That does not justify
retrying old identities or improvising cleanup after the original account
has disappeared.

Windows documents [`DeleteProfileW`](https://learn.microsoft.com/en-us/windows/win32/api/userenv/nf-userenv-deleteprofilew)
as a user-profile removal API, **requiring administrative privileges**. It
takes an exact SID, optionally obtains the path from the registered profile,
and reports failure through `GetLastError`. This is a general profile API,
not evidence that it safely tears down a `MEM` identity or its companion
lifecycle, whether created through the ordinary SDK or the experimental
Full-session path. It was not invoked. No blanket CIM deletion,
directory deletion, registry removal, or ownership change is proposed.

**Next safe action:** keep native tests on hold and ask the Windows/MXC owners
to confirm (a) the supported companion-to-original identity binding,
(b) which component must remove companion identities and profiles in the
ordinary SDK lifecycle as well as the Full-session path, and (c) the supported
cleanup order/API, timing, and expected warnings. Use the minimal pure-SDK
reproduction as evidence; do not assume the classification change caused it.
If cleanup requires an administrator or a private Windows API, it requires
separate explicit approval and an engineer-reviewed, exact-identity procedure;
it is outside this investigation.

Protected profile contents are not required for the current finding. If
engineering needs owner evidence, a separately approved bounded collector
could return only the exact candidate directory's owner SID, attributes,
registered SID/path, and relevant event metadata. It must not recurse, copy
profile contents, load hives, or mutate permissions. No such collector has
been run, and the denied ACL read has not been retried through another route.

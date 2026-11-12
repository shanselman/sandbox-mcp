# Windows updates and experimental broker distribution

## Current status: an export-gated research prototype

This is not yet a generally installable or supported Windows product. Its
Full-session path uses an experimental WTS API and an inferred private
credential ABI. A three-session run on the earlier binary set ended in a host
bugcheck. Matching exports or passing unit tests cannot establish host safety.
Never advertise "Windows 11 or later" or "latest updates" as a sufficient
support contract.

The user reported installing a candidate Windows fix and rebooted at
23:05 PST on 2026-11-09. All five pinned modules changed. The previous installed
broker correctly reported `experimental_build_not_validated`, `ready=false`,
and no pending jobs. No attempt was made to evade that refusal.

### Candidate binary set inspected after the reboot

These are exact identities, not a version-number range:

| Module | SHA-256 |
|---|---|
| IsoSessionCore.dll | `91150F898562280F91EDDDDF54054AE0507191073C55B152B64B3C28F6E89D71` |
| wtsapi32.dll | `8602290CDA47C2A8E662A71DBC72418BE97593940B3898B8DF774AB8AC919C68` |
| winsta.dll | `F1AA026EAD78F39E0E53963B62D43CC04C7FCF2A84B73AB955C05A94C11BF653` |
| termsrv.dll | `C5F591EF4BE4A3076FD3CB03F6729AB858DD1D0B153988D68879BC1C44F3EAF0` |
| lsm.dll | `87C99331E534D8B865C33D8BD7B8B046F60CB57672DFBD0D7DAE7740C6FB12C6` |

`wtsapi32` and `winsta` report 10.0.28000.3365; `termsrv` and `lsm` report
different component versions. Version strings alone are not a coherent-set
identity. The credential DLL has no file-version resource on this host.

Read-only `cdb -z <DLL>` inspection with matching Microsoft public symbols
established the same credential argument order (SID, two Unicode strings,
output identity), SID validation, Unicode conversion and LSA retrieval.
The paired free export still delegates to `LsaFreeReturnBuffer`. The WTS
wrapper still accepts V1 size 36, uses identity length/pointer at offsets
12/16, initial-program pointer at 24 and parent at 32; stop still takes one
positive session ID. Native winsta wrappers still route creation and stop
through RPC. Matching service symbols identify the Full-session creation
and lifecycle-manager handlers. No live process was attached or credential
retrieved for this inspection.

Matched PDB keys (GUID followed by age) are:

- IsoSessionCore: `E6DC820B5C8EFF38AB45788EE622E5B11`
- wtsapi32: `0EE665B5FF0003144FAA3FD4EB24E6881`
- winsta: `E148A34AFF1569F38602BB86628D2A5C1`
- termsrv: `564FA8FD6A3D898D305D93DABE0859C11`
- lsm: `FA893E997574E70E33560EA5CF237B711`

This justifies a new **ABI-inspected candidate** package for the user's
explicitly authorized experiments. It does not prove that the Windows fix
addresses the crash. Live qualification results must be recorded separately.
That candidate pinned only this whole new set; it did not accept arbitrary
new hashes, mixed releases, or the previously crashing set.

### First installation and live observation

The user explicitly approved updating the existing owner-only broker with
UAC before unattended work. Candidate release
`A571EC123D72F99E863F4999D4193682E2DB19D20E41CE36E3A43998DE818891`
passed 101 offline C# checks, 36 Node tests, and verification of all 537
package files before installation. It includes the earlier offline-tested
stop-intent journaling and immutable flushed evidence publication.

After installation, ordinary-user readiness reported no pending jobs. The
first single-session smoke created session 2 in 1,616 ms, launched the fixed
WinUI worker as the disposable agent, and produced inspected images changing
from `Hello, Sandbox! / Count: 0` to `Hello, FullSession! / Count: 2`.
Owner-only IPC denial, worker exit, session teardown, and account removal
passed. The following status read again showed no pending jobs and only
host sessions 0 and 1 remained.

An Explorer `0xc0000409` fail-fast event was recorded during that session's
startup. The host remained up, but this warning is retained rather than
turning the smoke into a blanket native-safety claim. The earlier bugcheck's
root cause and this candidate fix's sufficiency remain unproven.

### First six coordinated qualification runs

After the baseline smoke, a single-session stream produced 30 valid images
at 1.0002 fps with a 1,003-ms maximum interval. Six subsequent coordinated
grids each created three independent sessions, captured 30 images per session,
and completed teardown. Every one of the **540 grid PNGs** was decoded and
CRC/geometry checked after cleanup; collector copies matched preserved
bytes. Each stream contained changing image hashes, including valid final
frames. Fresh light, dark and enlarged-dark before/after pixels were inspected.

| Grid | Valid PNGs | Common coverage | Worst interval |
|---|---:|---:|---:|
| 1 | 90 | 28,222 ms | 1,035 ms |
| 2 | 90 | 28,017 ms | 1,040 ms |
| 3 | 90 | 28,652 ms | 1,037 ms |
| 4 | 90 | 27,889 ms | 1,047 ms |
| 5 | 90 | 28,323 ms | 1,007 ms |
| 6 | 90 | 28,789 ms | 1,036 ms |

The original bars were unchanged: 30 frames, mean >=0.9 fps, maximum gap
<=2,000 ms and overlap >=20,000 ms. Broker pending work was empty between
cycles. Independent account/profile inventory confirmed all 18 grid identities
absent. Together with the two single-session tests, this exercised 20 Full
session lifecycles without a new reboot at this checkpoint.

Explorer and ShellHost `0xc0000409` startup events recur. They are retained as
warnings, not filtered out of the findings. These runs demonstrate the fixed
fixture's capture/automation/cleanup path on one host, not a clean shell,
general Windows stability, or support for arbitrary applications. Both
Windows and our broker/evidence writer changed, so this is not a controlled
attribution of the improvement to either change.

For the first 20 lifecycles, all 39 sampled Application Error records
(20 Explorer, 19 ShellHost) carried event-security SIDs matching the tracked
disposable agents, not an unexplained association inferred only from timing.
This attribution does not establish the failure's cause or make it harmless.

A bounded extension to 30 grids runs serial batches of three, with a
90-minute cutoff for admitting another batch, readiness checks, no pending jobs at cycle boundaries,
and a full image audit after each cycle. It stops on the first failure and
never resumes automatically across a reboot. Completion is not implied by
this plan; actual completed-run receipts are the evidence. In-flight native
calls are not forcibly cancelled at the cutoff: interrupting an unknown
Windows lifecycle operation is not a safe timeout mechanism.

The native theme-aware canvas was observed during round 14 with all three
streams live: sequences 16/16/16 advanced to 28/28/27 in 11.5 seconds, with
freshness below 1.1 seconds. Its own renderer was polling the server and
reported the actual app palette with no CSP violations. This was current
capture evidence, not replay. On completion the same viewer switches to
recorded-state labels.

### Completed qualification and final client gate

The 30-grid qualification completed, followed by one additional single stream
and one additional three-session grid exercising the final client-side
every-image verification gate. The final total is **31 successful grids plus
three single-session runs: 96 distinct agent/session lifecycles**.

All **2,790 grid-stream PNGs** and 60 single-stream PNGs were validated
(**2,850 stream images total**). Grid overlap ranged from **27,388 to
28,966 ms**; the worst interval was **1,240 ms**, without lowering any
acceptance threshold. Independent inventory confirmed all 96 agent accounts
and profiles absent. Only the original services/console sessions remained;
the broker was ready with no pending work. All 537 installed files still
matched the approved package. The boot time was unchanged.

Both single-stream and coordinated client paths now validate every preserved
PNG after cleanup, with expected-session binding, geometry/CRC/scanline checks,
and explicit failure for missing, corrupt or redirected files. This is part
of the normal verdict, not just an external audit. Distinct image hashes are
reported as encodings, not misrepresented as proof of a semantic UI change;
the existing UIA assertions and visual review remain necessary.

The final offline suite passed **101 C# checks and 48 Node tests** in three
runs. The installed native package did not need another update for this
ordinary-user client change.

Remaining shell warnings were investigated separately:
[Explorer/ShellHost dump diagnosis and repository-level repro](shell-startup-crash-repro.md).
The qualification demonstrates this bounded fixture workload on this host,
not universal Windows stability, true monitor-DPI variants, more than three
simultaneous native sessions, long-lived desktop soak, or a supported public
Windows API contract.

## November 10: narrow the compatibility gate

The user requested export-based checks instead of requiring a new broker for
every internal Windows service DLL change. The subsequent LSM fix changed only
`lsm.dll` among the five earlier pins. Its SHA-256 is
`E597E5622F7E529C3721CD8527D78218C9C3B6A280C97FEEEA0DB658DC0B33B6`;
Authenticode verifies Microsoft Windows as signer. Matching public PDBs were
not available, so no claim of inspecting the changed internal implementation
is made. The caller-facing WTS DLL and credential DLL were unchanged.

The broker now checks the required WTS exports using
`NativeLibrary.TryGetExport` (the managed equivalent of `GetProcAddress`).
It no longer hash-pins `wtsapi32`, `winsta`, `termsrv` or `lsm`. The supplied
experimental V1 declaration still governs the call layout. Missing exports
and unsupported runtime architecture remain explicit failures.

**One exact pin remains:** `IsoSessionCore` exposes an undocumented credential
ABI inferred from the inspected binary rather than a published header.
Finding that export by name cannot establish its argument or ownership
contract. It must still match an inspected implementation until an appropriate
documented/versioned API replaces that dependency.

Successful readiness now returns `experimental_available`, not `validated`.
It permits staged testing; it does not certify Windows behavior. Broker package
file hashes, protected installation, owner-only IPC and credential/session
ownership checks are unchanged.

This export-gated broker was installed and used for the subsequent
profile-classification experiment. With the new LSM but before classification,
Explorer still failed at startup. With the classification patch, 60 observed
lifecycles had no agent-attributed startup crash events. Two longer attempts
stopped for separate collector/cadence failures, so they are not reported as
passing 31-grid qualifications. See the
[profile-classification results](shell-startup-crash-repro.md#profile-classification-candidate-under-live-test)
for exact outcomes, the fixed collector defect and a residual-directory
attribution limitation.

Build/package support now includes `win-arm64` and `win-x64`, with explicit
architecture verification and opt-in x64 fixture emulation on ARM64. Both
packages built and their 538 files verified. The x64 broker passed 101 offline
checks under emulation; its read-only doctor still refuses unqualified native
x64 execution. A successful x64 build is not evidence that the private
credential ABI is valid on a native x64 Windows installation.

## November 11: staged demo startup and helper completion

A later live three-camera demo failed despite two helpers publishing all
30 frames: the worker waited for helper-process exit under one 45-second
deadline, and reported timeouts; the third stream produced no frames.
No agent-attributed application crash was found. This did not prove that
three sessions exceeded a Windows capacity limit, or identify the exact
shutdown/runtime operation that stalled.

The demo path now warms workers sequentially and accepts readiness only from
the expected worker PID and agent SID through a broker-created local pipe.
After all workers are ready, the broker releases capture together. Stream
helpers emit numbered progress and explicit completion messages; process exit
is then verified separately. Startup, barrier, capture and exit failures stay
distinct, and neither a completion message nor existing PNGs can override an
exit failure. Overdue capture schedule slots are skipped rather than replayed
in a catch-up burst, and rendering/publication/schedule delays are measured.

After an approved UAC update to package
`69170944712605ED8A44A9B8809FD248BF62B548C8249CCFC12BEF6C4A7C004B`,
one single-session control and one three-session grid passed:

| Observation | Result |
|---|---|
| Grid PNGs | 90 valid images; all 30/member collected |
| Common coverage | 28,983 ms |
| Maximum interval, light / dark / enlarged dark | 1,003 / 1,006 / 1,042 ms |
| Worker preparation | Each worker started after the previous worker reported ready |
| Capture start | One shared release after all three readiness messages |
| Helper completion/exit | All 30 progress messages received; each helper exited 0 after completion |
| Live viewer | All three sequences advanced from 0 to 3 together; host palette present |
| Application errors | Zero events attributed to the four provisioned agent identities |
| Session/original-account cleanup | No test sessions or original accounts/profiles remained; broker pending queue empty |
| Companion profiles | Four additional unloaded companion profiles remained, 187 -> 191; not falsely reported reclaimed |

Actual WinUI pixels were inspected. The grid retained recorded-state labels
after completion. The selected flexible timing policy was unchanged, and the
observed maximum gaps also fit the previous strict 2-second bar.

The candidate passed 108 broker checks (including staged preparation failure,
missing progress and completion-with-exit-stall cases), 60 spike Node tests,
112 repository tests and 13 package-validation tests. The broker tests were
repeated; all 538 installed files matched their package hashes. An x64 package
also built and file-verified, without claiming native x64 qualification.

This establishes a working bounded demo under the new orchestration, not a
statistical reliability/speedup claim or proof of the earlier timeout's OS
root cause. Companion cleanup and the MXC graphical-lifecycle feature gap
remain separate unresolved concerns.

## What happens when Windows updates today

1. The broker checks required exports, supported architecture and the private
   credential ABI during readiness and before starting work. A missing export,
   unsupported architecture or changed private credential ABI prevents new work.
   An internal Windows service DLL update alone does not require a broker update.
2. The ordinary client checks readiness before provisioning an agent account.
   The viewer and offline replay remain usable.
3. Keep existing journals and evidence. Do not retry create blindly or infer
   that a new kernel has recovered an interrupted operation.
4. If the calling contract or private credential ABI changes, an engineer
   qualifies it and publishes a broker update for the owner to install with UAC.
   Routine Windows service updates instead need regression qualification, not
   a hash-only broker rebuild. The installer
   requires no pending jobs, verifies package hashes and protected ownership,
   updates its existing service and checks startup.
5. Re-run ordinary-user readiness and staged capture/cleanup checks. A package
   rollback cannot make an older pinned broker compatible with a newer OS.
   Never downgrade/copy Windows DLLs to make a test pass.

The current broker also checks platform compatibility during cleanup/recovery.
A mismatch with pending jobs therefore needs explicit diagnosis, not automatic
acknowledgement or account deletion. Designing safe cleanup across OS updates
requires an independently supported stop/reconciliation contract; it is not
solved by simply making creation less strict.

## Plan for something other people can install

| Stage | Required work and observable release gate |
|---|---|
| Supported API contract | Work with Windows owners on a documented/versioned credential and Full-session interface, lifecycle guarantees, supported SKU/channel/architecture and unattended-use limits. If that cannot be provided, label releases research-only or use a VM-backed adapter. |
| Qualification registry | Publish a signed, versioned record of tested Windows module sets, broker protocol/package versions, architecture, provenance and separate API-available versus live-qualified status. Module hashes are reproducibility evidence, not universal admission requirements. Keep explicit qualification for the private credential ABI; never learn that allowlist from the customer's current machine. |
| Qualification farm | Use disposable, recoverable test hosts. Check single lifecycle, repeated cycles, concurrent sessions, app startup/rendering, changing valid PNGs, cadence/overlap, ownership refusals and complete teardown. Retain failure receipts and reboot/crash outcomes. Qualify OS servicing transitions and interrupted-work recovery separately. One successful smoke test is not a support matrix. |
| Distribution | Ship signed binaries and an Authenticode-signed installer through a documented release channel. Verify publisher, manifest signature, hashes, local paths and minimum protocol. Preserve owner-only IPC and administrator-protected payloads; do not make a generic elevated command endpoint. |
| Upgrade UX | Ordinary-user doctor explains exactly which component changed, whether a qualified release exists, and the required restart/elevation. Download/preflight unelevated; perform only reviewed fixed installation operations after explicit consent. No repeated UAC for normal tests. |
| Transactional service update | Drain/reject new work, reconcile owned jobs, refuse ambiguous state, stage immutable files, verify signatures/hashes, switch only when safe, verify health and preserve recoverable receipts. Test rollback on the same supported OS set; report incompatible OS rollback honestly. |
| Servicing policy | Do not disable Windows Update. Detect pending servicing, stop admitting jobs during the servicing transition, and preserve outputs. Afterward recheck exports/credential ABI and report whether that OS set has live qualification. Do not cache readiness across a servicing change. Define interrupted-session handling with Windows owners. |
| Support and privacy | Publish a precise tested matrix, known issues and bounded diagnostic export. Keep account identifiers, dumps, credentials and images local unless the user explicitly chooses to share them. Signing/packaging alone does not make the private API supported. |

Suggested release states are **unsupported**, **ABI inspected**,
**live qualification in progress**, **qualified for a named test matrix**,
and **blocked/revoked**. Experimental availability and product support must
remain separate. A future unattended updater needs its own explicit policy
and least-privilege design; none is silently introduced by this spike.

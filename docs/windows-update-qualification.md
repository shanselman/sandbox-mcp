# Windows updates and experimental broker distribution

## Current status: a pinned research prototype

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
The source now pins only this whole new set; it does not accept arbitrary
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

## What happens when Windows updates today

1. The broker compares required exports, architecture and exact module hashes
   during readiness and before starting work. A mismatch prevents new work.
2. The ordinary client checks readiness before provisioning an agent account.
   The viewer and offline replay remain usable.
3. Keep existing journals and evidence. Do not retry create blindly or infer
   that a new kernel has recovered an interrupted operation.
4. An engineer qualifies the new contract, publishes a matching broker
   package and the owner explicitly installs it with UAC. The installer
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
| Qualification registry | Publish a signed, versioned manifest of coherent exact module sets, broker protocol/package versions, architecture, provenance and separate ABI-inspected versus live-qualified status. Do not build an allowlist from the customer's current machine. |
| Qualification farm | Use disposable, recoverable test hosts. Check single lifecycle, repeated cycles, concurrent sessions, app startup/rendering, changing valid PNGs, cadence/overlap, ownership refusals and complete teardown. Retain failure receipts and reboot/crash outcomes. Qualify OS servicing transitions and interrupted-work recovery separately. One successful smoke test is not a support matrix. |
| Distribution | Ship signed binaries and an Authenticode-signed installer through a documented release channel. Verify publisher, manifest signature, hashes, local paths and minimum protocol. Preserve owner-only IPC and administrator-protected payloads; do not make a generic elevated command endpoint. |
| Upgrade UX | Ordinary-user doctor explains exactly which component changed, whether a qualified release exists, and the required restart/elevation. Download/preflight unelevated; perform only reviewed fixed installation operations after explicit consent. No repeated UAC for normal tests. |
| Transactional service update | Drain/reject new work, reconcile owned jobs, refuse ambiguous state, stage immutable files, verify signatures/hashes, switch only when safe, verify health and preserve recoverable receipts. Test rollback on the same supported OS set; report incompatible OS rollback honestly. |
| Servicing policy | Do not disable Windows Update. Detect a pending reboot or changed set, stop admitting jobs, preserve outputs and clearly mark maintenance required. Do not cache readiness across a servicing change. Define what to do if servicing interrupts active sessions with Windows owners. |
| Support and privacy | Publish a precise tested matrix, known issues and bounded diagnostic export. Keep account identifiers, dumps, credentials and images local unless the user explicitly chooses to share them. Signing/packaging alone does not make the private API supported. |

Suggested release states are **unsupported**, **ABI inspected**,
**live qualification in progress**, **qualified for a named test matrix**,
and **blocked/revoked**. Experimental availability and product support must
remain separate. A future unattended updater needs its own explicit policy
and least-privilege design; none is silently introduced by this spike.

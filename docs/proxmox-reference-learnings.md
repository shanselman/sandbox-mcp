# Proxmox reference: lessons for the visual farm

Inspected source: [`guscatalano/SandboxMCP` at
`dfa1c59c5a55615ad2ab3b01b9b1895cda823d78`](https://github.com/guscatalano/SandboxMCP/tree/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78).
Research only: no Proxmox host was contacted, installed or provisioned.
These notes describe that revision, not a promise about later versions.

## What this reference can and cannot tell us

This design runs disposable **Windows VMs on Proxmox**, using the guest agent
for setup and Deskhand in a logged-in desktop for UI automation. Our
experimental Full-session broker shares the Windows host kernel. These are
different failure boundaries: an ordinary guest Windows crash need not crash
the Proxmox host, whereas our observed critical-session failure did crash
the shared Windows host. VM isolation is not a guarantee against hypervisor,
hardware or host-resource failures.

The reference does **not** establish the correctness of our experimental
WTS create/stop sequence, account selection, or the cause of the CSRSS/SMSS
bugcheck. Its controller memory incidents are separate incidents, not
evidence that memory exhaustion caused ours.

No license file was found in the inspected tree and GitHub's license endpoint
did not return a license. **No implementation code was copied.** Obtain a
license or explicit permission before copying/distributing its source.
Engineering experience is valuable context, not a replacement for a source
license, review or measurements.

## Concrete findings, corrected against code

| Area | Source behavior | Lesson for our prototype |
|---|---|---|
| Ownership | Controller `_check_managed` checks the VMID range and QEMU membership in the managed pool. It is not a name/description-marker check. | Resource identity and a numeric ID are not equivalent. Our cleanup must verify the recorded account SID and never adopt the console or a recycled foreign session. |
| Async completion | `_wait_task` polls Proxmox task status until stopped and requires `exitstatus == OK`; errors/timeouts propagate. | An accepted request is not successful completion. Verify final resource absence separately; a stop return alone is insufficient. |
| Clone completion | `do_create` posts the clone, then waits for the VM configuration lock to disappear; it does not use the same UPID waiter. | Read each operation's real completion contract. Do not infer universal task handling from one good helper. |
| Concurrent admission | VMID reservation is locked, but capacity checking and reservation occur in separate steps. In-flight accounting narrows the race, not eliminates it. | Admission must be atomic for a hard cap. Our first grid remains one serialized owner request of exactly three members, not three racing clients. |
| Advisory claims | Confirmed claim checks include destroy, repair and revert. Snapshot take/delete use the managed-resource check without the same claim check. | Advisory claims are not authorization. Audit each mutating route; do not rely on a docstring's list of protected operations. |
| Failure observability | The recorder reads the hypervisor console independently of Deskhand. | Separate guest/worker health from capture health. In-session screenshots cannot explain everything that happens after a worker exits. |
| Snapshot meaning | Controller snapshots are disk-only; the separate arena runner requests VM state. | Cold restart and suspended-desktop resume are different test baselines. Reports must say which one was used. |
| Load harness | `stress-create.py` records concurrent create outcomes, timings and capacity counts; its `controller_mem()` does not actually sample RSS. | Name metrics accurately. If measuring memory/handles, instrument them; do not turn comments or function names into measurements. |

Pinned evidence:

- [Managed range and pool check](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L3448-L3461).
- [Task completion waiter](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L4557-L4577).
- [Clone creation](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L2154-L2176)
  and [configuration-lock waiter](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L1876-L1886).
- [VMID reservation](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L1850-L1870),
  [capacity accounting](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L4719-L4752)
  and [separate admission guard](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L4815-L4841).
- [Claim helper](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L1121-L1137),
  [destroy caller](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L5498-L5512),
  and [snapshot/revert routes](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L4593-L4627).
- [Two capture sources](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/live.py#L1-L23)
  and [out-of-band recording rationale](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/recorder.py#L1-L13).
- [VM-state snapshot runner](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/waa-runner/runner.py#L162-L199).
- [Stress tool's capacity-count helper](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/tools/stress-create.py#L113-L121).

## Pitfalls worth retaining

**Cleanup scope matters.** A reported destroy failure involved the
`destroy-unreferenced-disks` sweep reaching an unrelated offline storage.
The controller retries more narrowly for the recognized storage error and
verifies absence. That is a useful incident lesson, not permission to
silently ignore cleanup errors or broaden our own deletion scope.
See [destroy handling](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/sandboxctl/app.py#L2514-L2544)
and [reported incident](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/docs/feedback.md#L242-L259).
Our offline tests instead require independent cleanup of verified siblings
and an explicit unresolved result for the member that failed.

**Controller and standalone scripts differ.** The controller uses linked
clones for disposables, while operator-oriented clone scripts have different
defaults. The standalone destroy script checks a `SANDBOX` marker; that is
not the controller's range-plus-pool policy. Do not mix the contracts.
See [clone script](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/windows11/clone-vm.sh)
and [destroy script](https://github.com/guscatalano/SandboxMCP/blob/dfa1c59c5a55615ad2ab3b01b9b1895cda823d78/windows11/destroy-sandbox.sh).

**Readiness is layered.** A booted VM, responsive guest agent, logged-in
desktop, ready automation agent, and correctly rendered changing application
pixels are separate observations. Our screenshot proof must remain stronger
than "PNG exists" or "DWM exists." The postcrash evidence contained zero-filled
final frame files despite successful worker metadata; validation must inspect
the actual bytes.

**Do not copy operational shortcuts.** Our earlier inspection of the
controller found disabled TLS verification. A future adapter must validate
the Proxmox endpoint's certificate/trust and keep scoped API credentials out
of guests, logs and reports. The separate payload/control-plane design is a
useful pattern, not a proof that a particular network deployment is isolated.

## Minimal future adapter, not new implementation scope

Keep the viewer/reporting protocol independent of the worker backend:

1. Provision an owned resource with a durable identity and explicit image.
2. Observe staged readiness with bounded deadlines.
3. Run fixed or explicitly authorized workloads under the intended user.
4. Publish frames with source identity, original timestamps and freshness.
5. Tear down only that owned resource; verify absence and retain failures.

A future Proxmox adapter would persist node, VMID, ownership marker/generation,
task UPID and baseline/snapshot semantics. It would provide both guest-agent
and console capture. The existing session backend must not pretend to offer
VM snapshots or hypervisor-console diagnostics.

Prerequisites include a configured remote Proxmox node, trusted scoped API
access, adequate resources, a correctly licensed Windows image/template,
guest-agent and graphics support, and a validated x64 guest/host path.
Our ARM64 workstation could be a remote controller; it does not turn the
reference's QEMU/KVM VM scripts into a local Windows microVM implementation.
No such host or adapter is configured in this work.

| Verification | Safe offline now | Needs isolated live infrastructure |
|---|---|---|
| Accepted task later fails/timeouts | Simulated responses and durable-state assertions | Actual Proxmox task behavior |
| Reused ID or wrong pool/owner | Fake inventory and refusal tests | Real ACL and pool boundaries |
| Partial cleanup | Per-member error injection | VM stop/delete/storage failures |
| Capacity admission race | Concurrent reservation model | Host memory/CPU/IO scaling |
| Frame freshness/replay | Recorded frames, malformed metadata, concurrent file readers | Agent versus console failure behavior |
| Snapshot reset semantics | Report/schema assertions | Disk-only versus VM-state restore |

## Deskhand: complementary, deliberately parked

[Deskhand](https://github.com/guscatalano/Deskhand) remains relevant for
in-session UIA, explicit session routing and window capture. Prior findings
are in the [visual-farm handoff](visual-test-farm-spikes.md#deskhand-useful-fleetcapture-patterns-not-session-creation).
No new Deskhand integration or deployment is part of this safe-verification
pass. It does not replace the missing Full-session lifecycle contract.

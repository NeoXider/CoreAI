# Audit writer repair workdoc

Date: 2026-10-24. Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
Lease: Audit runtime folder, existing `AuditLogWriterEditModeTests.cs`, new
`AuditLogWriterResilienceEditModeTests.cs`, and this file. Root owns Git,
installers outside Audit, aggregate documentation, compilation and test execution.

## Finding review and acceptance

| ID | Reproduction and guard review | Disposition and root decision | Regression acceptance | Proposed files |
| --- | --- | --- | --- | --- |
| UH-06 | Repeated append failures enqueue one diagnostic every desktop worker tick. The entry queue is bounded, but diagnostics are unbounded and only WebGL/dispose/test paths drain them. Existing failure coverage calls the test drain explicitly. | Confirmed framework fix. Root authorized configurable bounded diagnostics and an owned main-thread pump on desktop and WebGL; no game authentication or account policy. | Repeated faults retain at most the configured capacity with exact overflow counters; ordinary pump emits diagnostics only on the main thread; throwing/reentrant callbacks cannot skip cleanup, duplicate pumps, or retain a disposed writer. | `AuditLogWriter.cs`, `AuditLogWriterOptions.cs`, new resilience fixture |
| CARRY-HOST-AUDIT-RETENTION: shared chain | Two writers resume the same path into different instance hash/sequence heads and lock only their own flushes. No same-path coordination guard exists. | Confirmed framework fix, explicitly admitted by root. Host selects folder namespaces; framework coordinates an identical canonical path in this process. Cross-process writers remain a documented limit. | Two live writers, canonical aliases, overlapping flushes and one writer retiring preserve sequence/hash links; last owner/waiter releases path registry state; restart resumes the stored sequence including rotation anchors. | `AuditLogWriter.cs`, internal path-state lease in Audit, new resilience fixture |
| CARRY-HOST-AUDIT-RETENTION: rotated files | Rotation allocates the first free numbered filename and never deletes old files. Existing queue limits do not bound retained files. | Confirmed framework fix, explicitly admitted by root. Add a positive configurable retention default without changing readable default filenames or deleting unrelated files. | Repeated rotations retain the newest configured count, protect unrelated filenames/directories, preserve remaining cross-file chain links, and continue numbering/sequence after restart. | `AuditLogWriter.cs`, `AuditLogWriterOptions.cs`, new resilience fixture |
| UH-01 | Host lifetime/account transitions are outside this lease and remain an uncounted integration note. | Host integration; no repair here. | No new authentication/account semantics. | None |

## Contract and implementation decisions

- Preserve `IAuditLog.Record`, parameterless construction, existing internal test seams,
  JSONL entry shape, bounded entry admission, failure requeue and shutdown deadlines.
- Implemented additive API: `AuditLogWriter(string folder, AuditLogWriterOptions options)`;
  options configure diagnostic capacity/budget/sink, maximum file bytes and positive
  rotated-file retention. Public diagnostic counters expose overflow and delivery failures.
- Shared canonical-path state owns sequence/hash and serializes append/rotation. A
  reference-counted internal registry retains owners before lock acquisition and removes
  the last retired state only after outstanding operations finish. No optional module dependency.
- Reuse the existing UniTask player-loop mechanism for diagnostic delivery and the
  existing audit hash/verifier/file format; no new package or external subsystem.
- Main-thread pumping is owned by each writer and detached on teardown. Logging callback
  failures are counted and isolated; no sink is invoked from a file worker.

## Verification ownership

No builds, Unity operations, tests or runtime processes executed by this worker.
Root should run Unity EditMode filter `AuditLogWriter` serially after source freeze.
Source inspection is confirmation evidence, not an executed reproduction or test pass.
Player/WebGL filesystem replacement, persistence and process-external coordination are
separate platform limits; actual browser/player gates remain root-owned.

## Progress

- 2026-10-25 continuation: previous writer is settled; this worker owns the remaining
  Audit source integration and regression fixtures. Existing options/registry files
  are partial input and are being reviewed before use. No verification result is
  inherited from their presence. Root retains all compilation and Unity checks.
- Intake complete: read `AGENTS.md`, architecture rules, host audit, relevant module-map
  claims/carry-in decision, writer, existing writer tests and core audit verifier.
- Workdoc created before implementation. API details and final files will be recorded below.

## 2026-10-25 implementation checkpoint

- Integrated the reviewed options and canonical-path registry into the writer. Shared
  storage limits reject incompatible owners before retaining another path user.
- Added a bounded `AuditDiagnosticBuffer` and detachable UniTask Update-loop pump;
  custom/default sinks run only on Unity's main thread. Reentrant delivery is guarded,
  callback exceptions are counted, and teardown accounts for discarded diagnostics.
- Added public `PendingDiagnosticCount`, `DroppedDiagnosticCount` and
  `DiagnosticDeliveryFailureCount`. Defaults: capacity 256, tick/dispose budget 32,
  file threshold 50 MiB, positive rotated-file retention eight.
- Retained per-operation leases before writer/path lock acquisition. Disposed owners
  cannot split the path state from active/waiting appends. An idle flush still returns
  before allocating a lease or batch, preserving the existing allocation-test contract.
- Rotation commits a persisted marker into the shared head even if replacement fails;
  retries/restarts reuse that marker. Tail resume reads stored `Seq`, preserving anchor
  sequence zero rather than counting it as an ordinary record.
- Retention matches canonical numbered names plus rotation-marker tails, ignores links
  and directories, and only prunes after rotation commit or initial path resume.
  Pruning errors cannot requeue an already committed batch. Number allocation skips
  occupied paths and continues from the highest stored/anchored rotation index.
- Added a scoped feature README for configuration, diagnostics, storage limits, host
  folder ownership, lifetime, and WebGL durability semantics.
- Added the resilience fixture, including actual player-loop pumping, diagnostic
  bounds/budget, throwing/reentrant/off-thread sinks, shared aliases/concurrent writes,
  active/waiting retirement, rotation/restart/failure recovery, and unrelated files.
- Static source scans found no implicit C# local types, Cyrillic prose, or disallowed
  comment prefixes in the five changed C# files. This is inspection, not compilation
  or an executed regression result. No build, test, Unity/MCP, Git or install performed.

## Source-final handoff

2026-10-25 ADV-003 continuation: root admitted transactional append rollback and a
bounded process-local quarantine/recovery contract. Source is reopened only for
that defect, its scoped regression fixtures and this addendum. Root retains actual
compilation, Unity verification and publication. No validation result is inherited.

Source is frozen for root review and serial verification. Confirmed fixes remain
UH-06, carried same-path coordination, and carried rotated-file retention; UH-01
remains host integration with no authentication/account change.

Exact changed/new files:

- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriterOptions.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditPathStateRegistry.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditDiagnosticBuffer.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/README.md`
- `Assets/CoreAiUnity/Tests/EditMode/Audit/AuditLogWriterResilienceEditModeTests.cs`
- `dev-docs/fix-wave-2026-10-24/workdoc04-audit-log.md`

Static output: the combined explicit-type/English/comment-prefix `rg --pcre2` scan
returned exit 1 with no matches. The legacy-head/unbounded-diagnostic scan returned
only the three `Debug.Log*` calls inside `AuditDiagnosticBuffer.WriteUnityLog`, whose
only call path is the main-thread-guarded drain. Neither output is a compile/test pass.

Root verification filters: `AuditLogWriter` (existing writer/idle fixtures and new
resilience fixture), plus `AuditLogVerifierEditModeTests` for existing corrupt-tail
and marker/anchor compatibility. Expected: no regressions, all new resilience cases
pass, and the actual owned player-loop case delivers on the Unity main thread.
Actual Unity compilation, EditMode execution, player/WebGL/device persistence,
filesystem replacement support, performance and Git/release remain unrun here.

Operational limits: cross-process/physical-link aliases are outside coordination;
host folder/account policy remains host-owned. Retention failures remain visible
and retry later. Managed cancellation cannot interrupt an already active disk call.

## ADV-003 append transaction and quarantine addendum

Root accepted ADV-003 from `13-adversarial-review.md` as a confirmed in-process
partial-append retry defect. A persisted first line followed by write/flush/disposal
failure formerly left the old shared head while requeueing the entire batch, allowing
duplicate sequences and broken predecessor links. The old pre-write fault seam did
not exercise that state.

Root decisions override the proposed strict startup change in the QA design:
ordinary valid-file reopen and existing corrupt-tail/`ChainReset` compatibility stay
unchanged. Recovery is strict only for a known process-local failed-rollback quarantine.

Implemented contract:

- The shared path gate encloses original byte-length capture, missing-newline repair,
  all batch writes, text-writer flush/disposal, file-stream flush/disposal and rollback.
  Both append streams have left their using scopes before truncation can begin.
- Successful rollback restores the exact original length and leaves the saved
  sequence/hash unchanged so a complete batch may retry without a persisted prefix.
- Failed rollback preserves bytes, original append/rollback errors and saved recovery
  length/head, then quarantines the shared state before any further append, rotation
  or pruning. New constructors refuse it before directory/file mutation. Last-owner
  disposal keeps quarantined state for later same-process refusal/recovery.
- Public static `AuditLogWriter.SharedPathCapacity` defaults to 1024. Values must be
  positive and cannot shrink below the current retained count; active/waiting owners
  and quarantines share the bound. Unknown paths fail admission when full.
- Public static `TryRecoverQuarantinedPath(string folder, out string error)` only
  reads bytes. It requires an existing quarantine, exact saved length, successful
  `AuditLogVerifier.Verify`, and the saved tail sequence/hash before clearing it.
  Main-thread invocation protects the reused verifier's engine-backed diagnostics.
  Hosts/operators restore bytes first; a same-length valid but different head fails.
- A false or throwing persistence-arming check after local commit becomes a bounded
  diagnostic and cannot trigger append/rotation replay.
- Internal deterministic seams cover failure after a first flushed line, rollback
  refusal and persistence arming. Existing pre-write hook and public/default APIs stay.

New regression acceptance source:

- `PartialAppend_RollsBackExactBytesIncludingMissingNewline_ThenRetriesOnce`:
  exact rollback bytes with/without the original newline, actual persisted-prefix
  confirmation, ordered retry and ordinary reopen sequence/hash continuity.
- `PartialRotationMarkerAppend_RollsBackBeforeSharedWriterRetries`: marker rollback
  and a peer's single-marker linked rotation.
- `RollbackFailure_QuarantinesPeersAndNewWriters_UntilExactVerifiedRepair`: no further
  mutation/retry; worker recovery refusal; wrong length, corrupt bytes and a same-length
  verifier-valid foreign head all fail without byte mutation; exact repair clears
  quarantine and live pending batches resume once on a valid shared chain.
- `QuarantineAfterLastOwner_IsRetainedWithinValidatedProcessCapacity`: ownerless
  refusal, full-capacity admission failure without directory creation, invalid/shrunk
  capacity rejection, verified repair and registry reclamation.
- `PersistenceArmingRefusalAfterCommit_DoesNotReplayAppendOrRotation`: false and
  throwing arming checks across append/rotation leave unique entries and valid reopen.

Follow-up edits are limited to `AuditLogWriter.cs`, `AuditPathStateRegistry.cs`, the
scoped Audit README, `AuditLogWriterResilienceEditModeTests.cs`, and this workdoc.
Options and diagnostic-buffer source did not change in this follow-up. The QA owner's
Unity-compatible `NonParallelizable` guard is preserved.

Static checkpoint: scoped `git diff --check` returned exit 0 with no output. The
explicit-type/English/comment scan returned exit 1 with no matches. These checks do
not compile or execute the change. Read-only Git stat sees the tracked writer diff;
the other leased files were already untracked from the first repair handoff.

Root still owns compilation and serial Unity EditMode filters `AuditLogWriter` plus
`AuditLogVerifierEditModeTests`; no build, test, Unity/MCP, install, metadata, assembly
definition or Git mutation was performed here. Quarantine/queues remain process-local;
restart follows the documented legacy resume policy. Crash durability, persistent
journaling and cross-process exactly-once guarantees are outside this accepted fix.

ADV-003 source-final: the scoped implementation, regression source and documentation
are frozen for root review and actual Unity verification. No required source work
remains pending in this lease; executable acceptance remains unclaimed.

## Root pre-write failure correction

Root's transaction review identified that an open/seek/read failure before the first
append write still attempted rollback. A locked/read-only file or directory could
then fail that unnecessary rollback and retain a false permanent quarantine.

The append helper now records whether a write was attempted, immediately before
either inserted-newline or batch-line writes. Pre-write failures rethrow directly
to ordinary batch requeue without rollback/quarantine. Post-write failures retain
the existing flush/disposal rollback and quarantine boundary. The UTF-8 writer has
no BOM, so construction and pre-write seek/read/disposal cannot append chain bytes.

Added `LockedFileBeforeAppend_RequeuesWithoutQuarantine_ThenRetriesAfterUnlock`.
It acquires a real read-share file lock before enqueueing the pending entries,
requires a visible failed append, asserts unchanged bytes/no rollback/no quarantine,
then releases the lock and requires exactly ordered sequences 1/2/3 and a valid
hash chain. No forced GC, fake pre-write fault or weakened assertion is used.

Source SHA-256 after this correction:

- `AuditLogWriter.cs`: `23996D3BBCB7842319924AC206431C3DCE4D149886AE2E7CF67A99FBA929FBCC`
- `AuditLogWriterResilienceEditModeTests.cs`: `56A9A8E15097E12CB2A4065B39D61401B00D143D6C204273EF6A4931690FDC2A`

The source convention scan returned exit 1 with no matches. No build, test, Unity,
Git, install or metadata operation was performed for this correction. Root's actual
`AuditLogWriter` / `AuditLogVerifierEditModeTests` acceptance remains pending.
This correction is source-final.

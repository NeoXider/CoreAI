# Audit writer

`AuditLogWriter` implements the optional `CoreAI.Audit.IAuditLog` block with a bounded
entry queue, JSONL hash chain, file rotation, and bounded main-thread diagnostics.
The existing audit installer supplies the default writer. A host can register its
own `IAuditLog`, omit the block, or construct a configured writer without editing
package code.

The core package owns the engine-free audit entries, hashing, contract and verifier.
This folder is the Unity/filesystem adapter and intentionally remains in the host
assembly: its player-loop lifetime and WebGL persistence checks require Unity.

## Configuration

Construct on Unity's main thread. The parameterless constructor retains its default
folder under CoreAI's persistent root. The additive constructor accepts a host-owned
folder and `AuditLogWriterOptions`; option values and the sink are copied when the
writer is constructed.

```csharp
AuditLogWriterOptions options = new AuditLogWriterOptions
{
    DiagnosticQueueCapacity = 256,
    MaxDiagnosticsPerPump = 32,
    MaxFileSizeBytes = 50L * 1024 * 1024,
    MaxRetainedRotatedFiles = 8
};
AuditLogWriter writer = new AuditLogWriter(hostAuditFolder, options);
```

All numeric options must be positive. A null `DiagnosticSink` uses Unity logging;
an `Action<LogType, string>` replaces it. Each writer owns one UniTask Update-loop
pump on desktop and WebGL. File workers only enqueue diagnostics. Overflow drops
the oldest diagnostic, and each tick delivers at most `MaxDiagnosticsPerPump`.
Sink exceptions are isolated and counted; reentrant pumping cannot bypass the budget.

`PendingDiagnosticCount`, `DroppedDiagnosticCount`, and
`DiagnosticDeliveryFailureCount` are readable from any thread. Dropped diagnostics
include both overflow and diagnostics discarded during teardown. Disposal detaches
the pump before its bounded final delivery attempt; off-main-thread disposal
discards diagnostics without calling a Unity or custom sink. A callback already in
progress may finish during disposal. Dispose the writer when its host scope retires.

## Storage and lifetime

Writers with the same full path share their sequence/hash head and serialize append
and rotation within this process. They must use identical file-size and retention
limits. Owners and operations waiting for the path lock retain the state; the last
owner/operation removes ordinary state. Quarantined state remains until verified
recovery. `AuditLogWriter.SharedPathCapacity` bounds all retained paths, defaults to
1024, and accepts positive values at least as large as the current retained count.
New paths fail admission when that capacity is exhausted. Configure it at host startup.
`Path.GetFullPath` normalization and platform path-case
comparison cover lexical aliases. Other processes and physical aliases through
different symbolic-link/hard-link paths are outside this coordination contract.
The host chooses folder namespaces and its identity/account isolation policy.

The entry queue retains its 10,000-entry drop-oldest limit and audited drop markers.
An append captures its original byte length before inserting a missing newline or
writing the batch. Write, flush and disposal failures trigger truncation to that
length after the text writer has been disposed. Successful rollback keeps the old
sequence/hash head and permits a whole-batch retry without retaining a partial prefix.
Failed appends requeue the batch ahead of newer entries. Shutdown retains the bounded
worker join/drain deadlines; an already active filesystem call cannot be interrupted
by managed cancellation and may complete later. Calls to `Record` after disposal
starts are ignored.

If rollback fails, the shared path is quarantined. Its bytes and append/rollback
errors are preserved; existing writers refuse further append/rotation and new
writers refuse construction, including after the last owner has been disposed.
Diagnostics remain bounded. The host/operator must restore the exact saved
pre-append bytes, then call
`AuditLogWriter.TryRecoverQuarantinedPath(folder, out error)` on Unity's main thread.
That maintenance call only reads: it requires the saved byte length, a valid audit
chain, and the saved tail sequence/hash before clearing quarantine. A same-length
valid file with a different head is refused. Pending entries in still-live writers
can then retry normally.

Quarantine and pending queues live in process memory. A process restart loses them
and follows the existing tail-resume/corrupt-tail `ChainReset` compatibility policy;
the verifier continues to report a corrupt history. The host owns repair/restart
policy. The transaction guarantee covers in-process append rollback, while crash
durability and coordination with another process remain separate storage concerns.

The file-size setting is a rotation threshold checked before a nonempty batch, so a
single batch can exceed it. Rotation preserves the existing `audit_0001.jsonl`
numbering and marker/anchor hash links. The new active file starts at anchor sequence
zero; subsequent records continue from the stored tail sequence, including after a
restart. A failed replacement reuses its persisted rotation marker on retry.

Retention defaults to eight recognized rotated files plus the active file. Only
canonical numbered names with a terminal rotation marker are deletion candidates;
unrelated files, directories and file links are preserved. The oldest candidates
are deleted after a committed rotation and when first opening a path. A deletion
failure becomes a diagnostic and is retried on a later rotation/restart; it does
not roll back a committed chain or duplicate the pending batch.

On WebGL all flushing stays on the main thread. Completed writes ask
`CoreAiWebGlPersistence.Sync()` whether automatic persistence is armed; a false
answer queues an error without pretending that already-written bytes were rolled
back. A false or throwing persistence-arming check after a local commit cannot
requeue that append or rotation. Browser persistence completion is not exposed by
this writer.

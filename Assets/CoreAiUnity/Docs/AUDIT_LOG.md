# Audit Log — Immutable, Append-Only, Tamper-Evident

## Overview

The audit log records every significant interaction in a single append-only JSONL file:

- **LLM requests and responses** (send/receive, model, prompt hash)
- **Tool calls** (tool name, arguments, actor, policy decision, result, duration)
- **World mutations** (command type, payload, actor, success/failure)
- **Policy decisions** (allowed/denied/repaired)

All entries are written to one file (`persistentDataPath/CoreAI/Audit/audit.jsonl`) with a **SHA-256 hash chain** for tamper evidence.

## Architecture

```
Core           ┌─────────────────────┐
Portable       │ IAuditLog           │ ← NullAuditLog (no-op)
               │ AuditEntry          │ ← struct with Kind discriminator
               │ AuditHash           │ ← SHA-256 (+ chain helper)
               │ AuditContext        │ ← traceId-keyed promptHash/model cache
               │ AuditLogVerifier    │ ← ReadAll() / Verify() — re-chains from genesis
               └─────────────────────┘

CoreAiUnity    ┌──────────────────────┐
(Runtime)      │ AuditLogWriter       │ ← background loop, rotation, chain
               │ LlmAuditInterceptor  │ ← subscribes LlmRequestStarted/Completed
               │ ToolCallAuditInterceptor│← subscribes CoreAi.OnToolCallCompleted/Failed
               │ AuditedWorldCommandExecutor│← decorates ICoreAiWorldCommandExecutor
               │ AuditLogInstaller    │ ← VContainer registration
               └──────────────────────┘
```

## Entry Kind

| Kind | Published by | Captures |
|---|---|---|
| `LlmRequest` | `LlmAuditInterceptor` | traceId, actor, model, promptHash, routing profile |
| `LlmResponse` | `LlmAuditInterceptor` | traceId, actor, success/error |
| `ToolCall` | `ToolCallAuditInterceptor` | toolName, args, policyDecision, result, durationMs |
| `WorldMutation` | `AuditedWorldCommandExecutor` | commandTypeId, payload, actor, success, sourceTag |
| `PolicyDecision` | _future_ | authority host decision, guard results |
| `ChainReset` | `AuditLogWriter.ResumeChain` | audits that the hash chain was restarted from genesis (corrupt tail line or I/O failure on resume) |

## File Format

JSONL (one JSON object per line). Each entry includes:

| Field | Description |
|---|---|
| `seq` | Monotonic sequence number |
| `ts` | UTC ISO-8601 timestamp |
| `kind` | Entry kind (see above) |
| `traceId` | Correlation id for the full LLM round-trip |
| `actor` | `roleId` / `lua:...` / `demo:...` / `player` / `system` |
| `model` | LLM client type (from `LlmBackendSelected`) |
| `promptHash` | SHA-256 of the assembled prompt |
| `toolName` | Tool or command type name |
| `args` | JSON: tool arguments or command payload |
| `policyDecision` | `allowed` / `denied` / `repaired` / `started` / `completed` / `failed` |
| `result` | `ok` / `error` / `pending` / `timeout` |
| `resultDetail` | Result JSON or error string |
| `durationMs` | Execution duration |
| `worldDiff` | JSON: command-scoped diff (future) |
| `rollbackHandle` | JSON: reverse action descriptor (future) |
| `sourceTag` | Origin tag for world mutations (e.g. `lua:world_command`, `demo:...`) |
| `prevHash` | SHA-256 of the previous line — chain root is `""` |
| `hash` | `SHA-256(prevHash + jsonLine)` |

## Tamper Evidence

Every line includes `prevHash` and `hash`. **Canonical preimage:** the preimage of a line's hash is
that exact same line with the `hash` field set to `""` (every other field — including `ts` and
`prevHash` — stays exactly as stored):

```
preimage_N = jsonLine_N with "hash" set to ""
hash_1 = SHA256("" + preimage_1)
hash_2 = SHA256(hash_1 + preimage_2)
hash_3 = SHA256(hash_2 + preimage_3)
```

`AuditLogWriter.FlushBatch` builds each entry exactly once (one `seq`, one `ts`, `prevHash` = the
current chain head) before ever serializing it, so the bytes that get hashed are the same bytes
that (modulo the `hash` field) end up on disk — there is nothing hidden from the hash.

To verify: use `AuditLogVerifier.Verify(filePath)`. It re-chains from genesis (`prevHash = ""`) by,
for each line: parsing it, checking the stored `prevHash` equals the running chain head, blanking
the `hash` field, recomputing `SHA256(runningPrevHash + preimage)`, and comparing against the
stored `hash`. It returns `{ Ok, FirstBrokenSeq, LineCount, Error }` — `Ok` is false at the first
line whose `prevHash` or `hash` doesn't match, or that fails to parse at all (e.g. a truncated
tail line), and `FirstBrokenSeq` identifies it. `AuditLogVerifier.ReadAll(filePath)` returns the
parsed `AuditEntry` list for inspection without verifying the chain.

Any modification to any entry (including its `ts`) breaks the chain for that entry and all
subsequent ones.

### Resuming after corruption

If `AuditLogWriter` cannot resume the chain on startup — the tail line fails to parse, or the file
can't be read — it does **not** silently reset the chain. It logs an error and appends a
`ChainReset` entry (`AuditEntry.ForChainReset`) as the first entry of the new chain segment, so the
reset itself is an audited, tamper-evident event rather than a hole that looks like a fresh log.

## Performance

- `IAuditLog.Record()` only **enqueues** to a `ConcurrentQueue` on the main thread — ~microseconds.
- A background loop (`AuditLogWriter`) flushes every 500ms or 10 entries:
  - SHA-256 of ~1 KB: ~tenths of microseconds
  - `File.AppendAllText` for one line: sub-millisecond
  - Total background overhead: negligible
- Rotation at 50 MB → `audit_0001.jsonl`, `audit_0002.jsonl`, etc.
- No gameplay thread blocking.

## Multiplayer Future

All audit entries are recorded locally per peer. The `IAuditLog.Record()` API is designed so that a future host mirror subscriber (`AuditEntryProduced` via MessagePipe) can receive entries for centralised storage without changing the recording strategy.

## Integration Points

| Hook | Location |
|---|---|
| Prompt hash | `AiOrchestrator.BuildRequestAsync` |
| Model + request | `LlmBackendSelected` + `LlmRequestStarted` (MessagePipe) |
| Tool call result | `CoreAi.OnToolCallCompleted` / `CoreAi.OnToolCallFailed` |
| World mutation | `AuditedWorldCommandExecutor` wraps `CoreAiWorldCommandExecutor` |
| VContainer | `AuditLogInstaller.RegisterAuditLog()` (called from `CoreServicesInstaller`) |

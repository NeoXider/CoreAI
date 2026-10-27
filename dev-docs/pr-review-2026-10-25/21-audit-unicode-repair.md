# Audit Unicode repair

Status: in progress; implementation authorized for the scoped runtime/test files. Root owns compilation/execution, acceptance, Git and publication.

## Scope

- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs`
- `Assets/CoreAIMods/Runtime/Logging/LuaLogService.cs`
- `Assets/CoreAiUnity/Tests/EditMode/Audit/AuditLogWriterResilienceEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/LuaLogServiceEditModeTests.cs`
- This work record only.

## Findings addressed

- PR-AUD-001: strict audit UTF-8 encoding rejects a normal post-marshalling diagnostic snapshot containing an unpaired surrogate and repeatedly requeues the whole batch.
- PR-AUD-002: new Mods log clipping splits a valid UTF-16 surrogate pair at the prefix boundary.

## Goal and invariants

Audit: normalize invalid Unicode diagnostic strings before canonical hashing/serialization, without changing the published hash format, strict physical append, rollback/quarantine/recovery/pending sequence chain, or corrupt-tail handling. Avoid mutating caller `AuditEntry` instances; normalize copies at the canonical boundary. Preserve legacy valid Unicode hash bytes.

Mods: avoid cutting a high/low surrogate pair at the clamp prefix boundary while keeping the cap and marker, preserving unchanged valid input and ASCII behavior. Existing unpaired input is not a new validation policy.

## Design

AuditLogWriter: add a selective unpaired-surrogate replacement helper applied to every string field of the `AuditEntry` built for hashing/serialization in `FlushBatchCore`, and to rotation marker/anchor paths where applicable. Paired surrogates remain unchanged; lone high/low surrogates become U+FFFD. Caller entries and queued originals are untouched; normalization happens on the new `finalEntry`.

LuaLogService: in `Clamp`, when the retained prefix ends with a high surrogate whose matching low surrogate is the first excluded code unit, reduce the prefix by one code unit so the pair is not split.

## Verification plan

Root runs `PrReviewPersistenceEditModeTests`, `AuditLogWriterResilienceEditModeTests`, `AuditLogWriterEditModeTests`, `LuaLogServiceEditModeTests` and reads the diff. Source preparation is not a pass.

## Current accounting

- CHANGED: AuditLogWriter.cs and LuaLogService.cs normalization/clamp fixes; focused regressions added to both leased test files; this record.
- VERIFY OUTPUT: static diff/callsite reviewed. Implementation complete pending root Unity execution.
- NOT DONE: root compilation/execution and acceptance; no Git mutation.
- CONCERNS: normalization must agree with hash preimages and stored lines; no blanket non-ASCII escaping or swallowed append errors.
- QUESTIONS: none currently block preparation.

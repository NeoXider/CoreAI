# Audit: LLM Tool Duplicate Suppression and Concurrency Policy

Date: 2026-07-10  
Scope: static, read-only audit of world-mutating LLM tool execution. No source code or tests were modified or executed.

## Executive summary

The project has a real policy gap. The four named tools opt out of duplicate signatures, so their cross-turn echoes are not suppressed. Three of them (`world_command`, `component_command`, `execute_lua`) are also absent from the name-based mutation serialization set and therefore enter the parallel scheduler when `MaxParallelToolCalls > 1`. `manage_mods` is already serialized, but still opts out of duplicate suppression.

The streaming defect is more nuanced than the external wording: signature-eligible multi-call echoes are recognized only after their calls have re-executed; the four named `AllowDuplicates=true` tools do not contribute signatures, so an all-mutating echoed turn made only of those tools is not recognized even at finalization.

## Verdicts

| Claim | Verdict | Reason |
|---|---|---|
| 1. The four named tools bypass signature tracking through `AllowDuplicates=true`, so cross-turn mutation echoes are not suppressed. | **CONFIRMED** | `TryBuildDuplicateSignature` returns `false` for a matched tool with `AllowDuplicates=true`; all four tools set it. |
| 2. The hardcoded serialized set omits `world_command`, `component_command`, and `execute_lua`, allowing parallel scheduling. | **CONFIRMED** | The set contains only `memory`, `manage_mods`, and `manage_skills`; every other call takes the normal gate-bounded concurrent branch. `manage_mods` is already serialized. |
| 3. Streaming multi-call echo detection occurs only after calls re-execute. | **PARTLY** | This is exactly true for signature-eligible tools and is explicitly admitted in code and tests. For the four named tools, signatures are skipped, so a turn composed only of them is not detected at all. Side effects replay in either case. |

## Evidence

### 1. Duplicate-signature eligibility

`ILlmTool.AllowDuplicates` means repeated identical arguments should not be suppressed (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:19-23`). The policy canonicalizes the tool name, then exits without producing a signature when the matching tool allows duplicates (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:255-280`). A batch containing no eligible signatures returns without a pending signature (`ToolExecutionPolicy.cs:201-214`).

All four named tools opt out:

- `world_command`: `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:41-47`.
- `component_command`: `Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:34-40`.
- `execute_lua`: `Assets/CoreAIMods/Runtime/LuaExecution/LuaLlmTool.cs:28-35`.
- `manage_mods`: `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs:63-70`.

Therefore, with the normal global duplicate policy enabled, identical calls to these tools are still absent from `_executedSignatures`. The policy is request-local as well: `Reset()` clears all signatures at each top-level request (`ToolExecutionPolicy.cs:108-115`), so there is no protection across independent requests.

The `WorldLlmTool` comment says blanket deduplication would break legitimate repeated physics/score calls and relies on prompting/schema quality to prevent duplicate spawn spam (`WorldLlmTool.cs:43-47`). That is a product rationale, not a side-effect safety mechanism.

### 2. Intra-batch repeats are intentionally allowed

Yes, `BuildDuplicatePlan` permits repeats even when `AllowDuplicates=false`. It builds and checks one sorted whole-batch signature (`ToolExecutionPolicy.cs:201-231`), but does not compare slots with each other. The explicit path is:

> `// Intra-batch repeats are deliberately NOT suppressed: "spawn tree x3" in one turn is a legitimate request and must execute all three`

(`ToolExecutionPolicy.cs:233-237`).

This is enforced by `ExecuteBatch_IntraBatchIdenticalCalls_AllExecute`, which expects all three identical calls to run (`Assets/CoreAiUnity/Tests/EditMode/ToolExecutionPolicyEditModeTests.cs:705-737`). This behavior is independent of the per-tool `AllowDuplicates` flag: `AllowDuplicates=false` enables whole-turn signature tracking, but identical slots in the first occurrence of a turn still execute.

### 3. Registration after failure: fully failed versus partially failed

The signature is **not registered for a fully failed batch/turn**:

- sequential batch registers only in the success/partial-success branch (`ToolExecutionPolicy.cs:960-971`);
- concurrent batch does the same (`ToolExecutionPolicy.cs:1060-1071`);
- streamed finalization registers only when the turn is not all-failed (`ToolExecutionPolicy.cs:1467-1472`);
- the helper documents the same rule (`ToolExecutionPolicy.cs:1497-1506`).

Thus, if `execute_lua` were changed to `AllowDuplicates=false`, a single transiently failing call could be retried with identical arguments.

However, the implementation registers the **entire batch signature after partial success**. If call A succeeds and call B fails transiently, the sorted signature for both is registered; an identical whole-batch retry is then treated as an echo and B is suppressed too. The comments promise retryability only for a fully failed batch, not for failed slots in a partially successful batch. This is an additional correctness gap and needs an explicit regression test.

The legacy `CheckDuplicate` API is different again: it registers the pending signature immediately without any execution outcome (`ToolExecutionPolicy.cs:144-152`). Production callers should not use it as an execution preflight if failure retryability matters.

### 4. Mutation serialization and actual ordering

The serialized set is exactly:

```csharp
new(StringComparer.OrdinalIgnoreCase) { "memory", "manage_mods", "manage_skills" };
```

(`ToolExecutionPolicy.cs:878-896`). In the concurrent batch path, listed names join `serialChain`; all others are launched independently behind the shared semaphore (`ToolExecutionPolicy.cs:981-1008`) and invoke the tool from their worker (`ToolExecutionPolicy.cs:1010-1036`). Streaming uses the same name test and split (`ToolExecutionPolicy.cs:1233-1247`).

Consequences:

- `manage_mods` is serialized relative to `memory` and `manage_skills`; that portion of Claim 2 should not imply otherwise.
- `world_command`, `component_command`, and `execute_lua` can be scheduled concurrently with each other and with other tools.
- `world_command` and `component_command` each switch to the Unity main thread immediately before a synchronous executor call (`WorldLlmTool.cs:307-315`; `ComponentLlmTool.cs:177-184`). This prevents simultaneous Unity API execution on multiple threads, but it does not make policy ordering deterministic. Main-thread continuation order is not the original-call serialization chain.
- `execute_lua` simply awaits its injected executor (`Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs:71-107`); the policy supplies no mutual exclusion between Lua and direct world/component mutations.

The current tests only prove serialization for the hardcoded names: the batch test uses two `memory` calls (`ToolExecutionPolicyEditModeTests.cs:1554-1596`), and the streaming test uses `memory`, `manage_mods`, and `manage_skills` (`ToolExecutionPolicyEditModeTests.cs:1680-1736`). They do not cover the world/component/Lua combination.

### 5. Streaming versus non-streaming

| Case | Non-streaming batch | Streaming |
|---|---|---|
| Signature-eligible, single-call echo | Whole batch is rejected before invocation (`ToolExecutionPolicy.cs:919-932`). | Rejected before invocation by the per-call signature check (`ToolExecutionPolicy.cs:1197-1220`). |
| Signature-eligible, multi-call echo | Combined signature is known before execution and the eligible slots are rejected (`ToolExecutionPolicy.cs:217-229`). | Calls execute as they arrive; combined signature is known only at finalization. |
| Turn containing only `AllowDuplicates=true` tools | No signature; all calls execute. | No signatures; all calls execute and finalization has nothing to compare. |
| Mixed eligible and `AllowDuplicates=true` tools | Signature covers only eligible calls; on an echo, eligible slots are suppressed while allowed-duplicate slots still run. | Eligible multi-call echo is diagnosed after execution; allowed-duplicate slots are never protected. |

The streaming admission is explicit: the code says the multi-call echo's calls "have already re-executed" and that suppressing re-execution is out of scope because results were already streamed (`ToolExecutionPolicy.cs:1438-1450`). `FinalizeStreamedTurn` then changes only aggregate error accounting (`ToolExecutionPolicy.cs:1451-1464`).

Tests encode this side-effect replay as expected behavior in both sequential and parallel streaming:

- `StreamedTurn_MultiCallEchoTurn_SecondCompleteRecordsFailure` expects four invocations for two calls echoed once (`ToolExecutionPolicyEditModeTests.cs:907-941`).
- `StreamedTurn_ParallelMode_WholeTurnEcho_RecordsExactlyOneFailure` also expects four invocations (`ToolExecutionPolicyEditModeTests.cs:1779-1819`).

### 6. No executor-level idempotency layer

`ApplyAiGameCommand` carries payload, source fields, `TraceId`, and version metadata, but no idempotency key (`Assets/CoreAI/Runtime/Core/Messaging/ApplyAiGameCommand.cs:8-35`). The direct world and component tools create messages with only command type and JSON payload (`WorldLlmTool.cs:307-315`; `ComponentLlmTool.cs:177-184`).

`CoreAiWorldCommandExecutor.TryExecute` validates/deserializes and dispatches directly by action (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs:75-108`). For `spawn`, it instantiates/creates a new object and assigns a fresh GUID (`CoreAiWorldCommandExecutor.cs:198-230`). It does not check a command key, `TraceId`, semantic hash, or prior application record. `AuditedWorldCommandExecutor` calls the inner executor first and records the result afterward; it is an audit wrapper, not a deduplication gate (`Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditedWorldCommandExecutor.cs:24-37`).

Some individual actions happen to converge (for example, setting a value twice), while others are naturally non-idempotent (`spawn`, force application, score/economy effects, arbitrary Lua). There is no generic protection.

### 7. Additional gaps missed by the external audit

1. **`call_skill_tool` is a dynamic bypass.** It has `AllowDuplicates=true` (`Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs:54-63`), is absent from the serialized-name set, and invokes an arbitrary resolved skill function (`CallSkillToolLlmTool.cs:145-170`). If a skill exposes a mutating tool, the outer policy sees only `call_skill_tool` and cannot inherit the underlying tool's mutation or duplicate semantics.
2. **`manage_skills` also opts out of duplicate signatures.** It is serialized but declares `AllowDuplicates=true` (`Assets/CoreAI/Runtime/Core/Features/Llm/ManageSkillsLlmTool.cs:35-44`). Serialization prevents overlap, not replay.
3. **Batch-level success tracking is too coarse.** Partial success registers failed slots as part of a completed batch signature, suppressing legitimate exact retries of the failed work.
4. **Name repair and behavior classification are inconsistent.** Duplicate signatures use canonical tool resolution (`ToolExecutionPolicy.cs:255-264`), while `IsSerializedTool` checks the raw/repaired call name only against a hardcoded set (`ToolExecutionPolicy.cs:894-896`). The architecture has no single source of truth for tool behavior.

## Existing test impact of setting the four tools to `AllowDuplicates=false`

Direct, deterministic failure found:

- `LuaLlmTool_Metadata_IsConsistent` explicitly asserts `execute_lua.AllowDuplicates == true` and would need its expectation and rationale updated (`Assets/CoreAIMods/Tests/EditMode/LuaToolEditModeTests.cs:183-191`).

No current world/component/manage-mods metadata test directly asserts their `AllowDuplicates` value. The generic policy tests use stubs and should remain valid because tools that genuinely allow repeats still need support:

- `CheckDuplicate_PerToolAllowDuplicates_Respected` (`ToolExecutionPolicyEditModeTests.cs:227-238`);
- `StreamedTurn_AllowDuplicatesTool_RepeatsExecute` (`ToolExecutionPolicyEditModeTests.cs:1013-1034`);
- `ExecuteBatch_RepeatedMixedBatch_StillExecutesAllowDuplicatesTool` (`ToolExecutionPolicyEditModeTests.cs:1075-1117`).

Changing the four flags would not break the intentional `spawn tree x3` test, because intra-batch repeats remain allowed. It would change runtime semantics for an identical call repeated in a later LLM turn of the same request. Existing direct tool tests generally call tool methods without `ToolExecutionPolicy`, so they do not cover that compatibility risk.

A deferred-mutating-stream fix would intentionally invalidate the two tests that currently assert echoed calls reach four invocations (`ToolExecutionPolicyEditModeTests.cs:907-941` and `1779-1819`); their expected count should become two, while retaining the aggregate failure/duplicate assertions.

## Prioritized fix plan

### P0 — stop mutation races with a narrow hotfix

Extend serialized mutation handling to include at least `world_command`, `component_command`, and `execute_lua`; `manage_mods` is already present. Conservatively include `call_skill_tool` until behavior can be propagated from its resolved target. Use one ordered mutation chain across these names so direct world, component, Lua, mods, memory, and skills mutations cannot overlap.

Update `ExecuteBatch_MutatingTools_AreSerialized_NeverOverlap` and `StreamedTurn_ParallelMode_SerializedMutatingTools_NeverOverlap` to cover all names and mixed-name ordering, not just repeated `memory` or the original three. Add a test in which `world_command`, `execute_lua`, and `component_command` block on a shared probe and prove maximum active mutations is one in both batch and streaming modes.

This hotfix addresses concurrency only; it does not stop cross-turn replay.

### P0 — do not eagerly execute streamed mutating calls

Buffer mutating streamed calls until the tool-call turn is complete, compute the whole-turn signature, reject an echo before invocation, then execute the accepted calls in original order. Read-only tools may keep execute-as-you-stream behavior. This closes the currently admitted replay window without removing streaming for safe calls.

Update the two existing whole-turn echo tests cited above to expect no second execution. Add equivalent tests using actual mutating descriptors and `AllowDuplicates=true` to ensure the new mutation replay guard is independent from the legacy repeatability flag.

### P1 — replace the overloaded boolean/name list with behavior metadata

Introduce a `ToolBehaviorDescriptor` (or equivalent) exposed by each tool and resolved per call:

- effect: `ReadOnly`, `Mutating`, or `Dynamic`;
- serialization domain/key (for example `world`, `mods`, `skills`, or a shared host-state key);
- repeat policy: legitimate repeated invocation versus cross-turn echo protection;
- idempotency support/requirement;
- optional argument-aware resolver, because `world_command` and `manage_mods` multiplex read and write actions, while `execute_lua` and `call_skill_tool` are dynamic.

Make duplicate planning, batch scheduling, streaming scheduling, and skill-tool indirection consume this same descriptor. Keep `AllowDuplicates` only as a compatibility adapter during migration. Add contract tests for all built-ins, plus propagation tests proving `call_skill_tool` inherits the target tool's behavior.

### P1 — add executor-level idempotency keys

Add an explicit `IdempotencyKey` to the command envelope and require mutation executors to atomically reject already-applied keys. Do not use model `tool_call_id` alone: an echoed semantic call can receive a new ID. Derive a stable request/turn batch digest plus slot ordinal so three intentional identical calls in one batch receive distinct keys, while an echoed copy of that batch reproduces the same keys. Persist or bound the key cache according to the host's retry horizon.

Test duplicate `spawn`, force/score mutation, component add, and Lua mutation at the executor boundary. Prove: same key applies once; different ordinal applies each intentional repeat; failed-before-commit remains retryable; cancellation after commit does not reapply.

### P2 — avoid blanket `AllowDuplicates=false` as the final design

As an emergency mitigation, changing the four flags to `false` would enable existing cross-turn whole-batch suppression and still allow intra-batch repeats. It is not a complete or precise fix: the tools combine reads and writes; identical repeated physics, state-read, diagnostics, list, or mod operations may be legitimate; streaming multi-call echoes still replay before finalization; and partial-success registration can suppress the failed slot's retry.

If this temporary mitigation is adopted, update `LuaLlmTool_Metadata_IsConsistent`, add metadata assertions for the other three tools, and add batch/streaming tests for a failed identical retry. Before shipping, add two explicit tests for the partial-success case: one demonstrating the current suppression defect and one for the chosen corrected semantics.

## Recommended acceptance tests

1. Non-streaming and streaming: identical mutating multi-call echo causes zero second-turn executor invocations.
2. Non-streaming and streaming: three identical calls in one first-time batch execute exactly three times.
3. Fully failed single-call and multi-call turns retry identical arguments successfully.
4. Partially failed batch retries only unapplied work, or executor idempotency safely absorbs already-applied work.
5. With `MaxParallelToolCalls=4`, all mutation domains that can touch Unity/Lua host state have maximum concurrent mutation count one and preserve original arrival order.
6. `call_skill_tool` wrapping `world_command`/`execute_lua` receives the same serialization, replay, and idempotency behavior as direct invocation.
7. Actual `CoreAiWorldCommandExecutor` receives the same `spawn` key twice and creates one object; two intentional same-argument slots with different ordinals create two objects.

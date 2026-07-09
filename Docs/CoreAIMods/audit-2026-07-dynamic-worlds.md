# Audit — Dynamic Worlds Readiness (2026-07-10)

Full audit of the recently landed systems against the project goal — **dynamically created
worlds** (LLM + Lua mods spawn/modify/persist a running world). Covers: world-state save/load,
the SHA-256 audit log, the mod system + Hub pages, tool-call duplicate/concurrency policy,
a live E2E pass of the world/Lua APIs, and test quality of the newest commits.
Each finding was verified against current source (file:line cited). Companion docs:
[optimization-review.md](optimization-review.md) — re-checked here, **all 14 findings still open** —
and [audit-tool-policy-codex.md](audit-tool-policy-codex.md) — independent verification of the
tool-policy findings (§0b).

Verdict in one line: the architecture is right (command-sink mutations, VM-agnostic mod runtime,
registry-driven Hub), but the two newest subsystems each ship one broken core promise —
**world-state cannot persist deletions or hierarchies**, and **the audit chain can never be
verified** — and both were missed because tests assert plumbing, not the promise.

> **Remediation status:** the W1/W2/W3/W5, A1–A4, and M4 (mod-logs) findings below were fixed the
> same day by delegated agents (see git history); the Lua VM bug in §0a was patched upstream-style
> (rebuilt `Lua.dll`). The tool-policy findings in §0b are NOT yet fixed — P0 hotfixes pending.

---

## 0a. Live E2E of the world/Lua surface (found a VM-level bug)

A full in-play pass over the Lua world APIs: `coreai_world_spawn` (incl. string parent + scale),
`coreai_world_change` (move/rotate/scale/reparent/`parent='none'`), `coreai_world_set_color` (MPB),
Full-tier `unity_find/describe/get_transform/unity_parent` with negative instance ids,
`unity_add_component`/`unity_set_member` (Rigidbody, mass), `coreai_world_destroy`/`unity_destroy`,
and `coreai_world_load_scene` — all verified working.

| # | Sev | Finding |
|---|-----|---------|
| E1 | CRITICAL (FIXED) | **`#table` returned nil under the sandbox instruction hook.** Lua-CSharp 0.5.5 VM bug: `OpCode.Len`'s table path writes the result register without `stack.NotifyTop`, so the every-instruction count hook (our runaway budget, `LuaCsExecutionGuard`) pushes its frame over the un-notified slot and clears it. Symptoms: `local n = #t` → nil, `#(expr)` → correct, strings fine, `for i=1,#t` fine. Every C#-built list (`unity_find_all`, `unity_get_children`, …) and every mod using `#` was affected. Fixed by a one-line VM patch (`stack.NotifyTop(RA + 1)` in the table-length path) + 5 regression tests; rebuilt `Lua.dll` deployed (backup `Lua.dll.bak-0.5.5`). Upstream PR-worthy. |
| E2 | MAJOR | **`coreai_world_load_scene` reports success to Lua when the load fails.** A scene missing from Build Settings logs a Unity error but the Lua call (and the LLM) sees ok — the agent cannot self-correct. Needs a result/error surfaced through the command sink. |

## 0b. Tool-call duplicate & concurrency policy (external audit, independently verified)

An external audit of `ToolExecutionPolicy` was verified claim-by-claim by a second independent
agent (full evidence: [audit-tool-policy-codex.md](audit-tool-policy-codex.md)):

| Claim | Verdict |
|---|---|
| `AllowDuplicates=true` on `world_command`/`component_command`/`execute_lua`/`manage_mods` disables duplicate-signature tracking entirely → cross-turn mutation echo is never suppressed | **CONFIRMED** |
| `SerializedMutatingToolNames` hardcodes only `memory`/`manage_mods`/`manage_skills` → world mutations can be scheduled in parallel under `MaxParallelToolCalls>1` | **CONFIRMED** |
| Streaming multi-call echo is detected only after the calls already re-executed | **PARTLY** (true for signature-eligible tools; the four above are not detected at all) |

Additional gaps found in verification: **partial-success batches register the whole-batch
signature (`ToolExecutionPolicy.cs:960-971`) so a legitimate retry of the failed slot would be
suppressed** (this is why a blanket `AllowDuplicates=false` is NOT safe as-is); **no idempotency
key in `ApplyAiGameCommand`** (executor level has zero replay protection); and **`call_skill_tool`
bypasses the whole policy** (dynamic dispatch, `AllowDuplicates=true`, not serialized).

Agreed fix order (differs from the external audit's): **(P0)** extend the serialized mutation
chain with `world_command`/`component_command`/`execute_lua`/`call_skill_tool`; **(P0)** defer
streamed mutating calls until turn finalization (check combined signature BEFORE executing);
**(P1)** executor-level idempotency keys in `ApplyAiGameCommand` (also the multiplayer
host-authoritative seam); **(P1)** `ToolBehaviorDescriptor` metadata replacing both the boolean and
the name list; **(P2 only, after fixing partial-success registration)** flip `AllowDuplicates`.
Note: `spawn_batch` already exists in the Lua layer (`coreai_world_spawn_batch`) — the tool layer
should reuse it rather than invent a new one.

---

## 1. World-state save/load (`WorldStateManager`)

| # | Sev | Finding |
|---|-----|---------|
| W1 | CRITICAL | **Deleting everything is never persisted.** `Save()` early-returns when zero tracked objects (`WorldStateManager.cs:89-94`), leaving the stale file; next launch resurrects deleted objects. Auto-save and quit-save both hit this. |
| W2 | CRITICAL | **Parent restored by name, saved id-path is dead.** Save writes parent **name** (`:122`); load looks it up in a **GUID-keyed** dict (`:257-258`) — never matches, always falls to `GameObject.Find` (`:264`), which is ambiguous with duplicate names and skips inactive parents (child silently orphaned). |
| W3 | MAJOR | **Clean-slate uses deferred `Destroy`** (`:318`) then spawns in the same frame — old and new instances coexist; name-based reparent can bind to a dying object. Docs' "clean slate" invariant is violated within the load frame. |
| W4 | MAJOR | **Mod rehydrate vs world load ordering is undefined.** Mods rehydrate in the child scope build callback; world loads in the parent scope's `IStartable`. A mod that re-spawns "its" objects double-spawns them, or clean-slate load destroys what the mod just made. No coordination flag exists. |
| W5 | MAJOR | **Missing prefab = permanent silent data loss.** `SpawnFromSnapshot` null → object skipped and dropped from the *next* save (`:237-238`). No pending/unresolved retention. |
| W6 | MAJOR | Runtime-added components, physics velocity, text, animation, non-color material state — **not persisted**. Fine as a boundary, but undocumented; an LLM-built machine loses its Rigidbody/Light on reload. |
| W7 | MAJOR | Periodic auto-save lives only in the Hub demo scene (`WorldStateAutoSaveHook` grep). All other demos persist only on clean quit — crash/tab-close loses everything. No WebGL `CoreAi_PersistFsSync` parity. |
| W8 | MINOR | "Versioning" is a single `hasColor` ordinal compare (`:216`); unknown future versions load silently. Double quit-save (manager + hook). Euler round-trip drift. First-renderer-only color save. |

**Tests** (`WorldStateManagerPlayModeTests`, 3 tests): real assertions (transform/color/reset) — good;
but no parenting, no active=false, no empty-world, no missing-prefab, no corruption, and they use
`DestroyImmediate`, masking W3. W1/W2 would have been caught by two small tests.

## 2. Audit log (SHA-256 chain)

| # | Sev | Finding |
|---|-----|---------|
| A1 | CRITICAL | **Chain is unverifiable by construction.** The hashed serialization and the written line are two different structs with two different `UtcNow` timestamps (`AuditLogWriter.cs:158-199`, `AuditEntry.cs:36`); the stored `ts` was never hashed, so recomputing the chain from disk can never match — and `ts` can be tampered freely. |
| A2 | CRITICAL | **No read/verify API exists.** `IAuditLog` is write-only (`IAuditLog.cs:5-8`); the documented verify procedure is unimplemented. Tamper-evidence that is never checked is decoration. |
| A3 | MAJOR | Corruption/truncation silently resets the chain to genesis (`AuditLogWriter.cs:100-110`) — the most common tamper/failure mode is absorbed invisibly. |
| A4 | MAJOR | Stored `prevHash` is always `""` (docs claim it links lines); `promptHash` dropped in `ForLlmRequest` (`AuditEntry.cs:141` vs `145-153`); `sourceTag` dropped in `ForWorldMutation`; `PolicyDecision` kind never emitted. |
| A5 | MAJOR | "Background" flush is the main thread (`UniTask.Delay` → Update, `:135`); no fsync; up to a full batch lost on crash; **no WebGL `CoreAi_PersistFsSync`** → audit log volatile on WebGL. Rotation resets chain with no cross-file anchor; rotated-file count unbounded. |
| A6 | MAJOR | Not audited at all: Lua mod load/error, role switches, world-state save/load. A Lua mod mutating outside the command executor is invisible. |
| A7 | MINOR | `AuditContext` static dictionaries leak entries for traces that never complete. |

**Tests** (3 files): hash unit tests fine; writer tests only assert "doesn't throw". Nothing reads
the file back and re-chains — which is exactly why A1 shipped. No tamper/truncation/rotation/concurrency tests.

## 3. Mod system + Hub

| # | Sev | Finding |
|---|-----|---------|
| M1 | MAJOR | **Version history/revert not surfaced.** Runtime has `ListModVersions`/`TryRevertMod` (`LuaCsModRuntime.cs:528,557`) — zero references from any Hub code. |
| M2 | MAJOR | **`UpdateAvailable` dead-ends.** Seeder sets it (`BundledModSeeder.cs:160`) but `HubModRecord` has no such field — the bundled-update feature never reaches the UI. |
| M3 | MAJOR | **Import/Export not surfaced.** `ExportMod`/`ImportMod` (bundle JSON with capability masking) exist; Hub Copy/Paste moves raw Lua only, losing manifest/capabilities. |
| M4 | MAJOR | **Mod reports have no buffer.** `report()`/`print()` are event-only, muted by default, and nothing in the Hub subscribes — player-visible mod logging doesn't exist. Errors DO have a capped buffer (`GetRecentHandlerErrors`, 32) but are shown only in the editor on manual refresh. |
| M5 | MAJOR | Dead code ready to delete now: MoonSharp-side `LuaModRuntimeHubService` (no callers), `LuaModRuntimeTicker` + `LuaModEventEmitted` (+2 broker regs, zero subscribers), `HubModsDemoBinder`, `LoggingLuaExecutionObserver`. |
| M6 | MINOR | Two Hub windows sharing one PanelSettings fight over the panel (no singleton guard); Settings/Statistics registry "upgrade" is Start-vs-Awake ordering-dependent; editor diagnostics not live (no `ModHandlerErrored` subscription). |
| M7 | MINOR | Statistics page has no mod-runtime reference — could show mod count, handlers/timers, tick time. |

**optimization-review.md: 0 of 14 findings fixed** (re-verified with evidence; #1 OnGUI GUIStyle
allocs, #3 `Resources.FindObjectsOfTypeAll` per `unity_*` call, and #7 full-tree rebuild per
keystroke are the perf-relevant ones).

## 4. Newest test commit (f6350f8f tool-round bubble)

Good: asserts bubble count, DOM ordering, exact text, unchanged transcript, plus a
no-tool-round negative control; async Task (no sync-over-async freeze pattern).

---

## Priority plan

**P0 — correctness of shipped promises**
1. W1: write an empty snapshot instead of early-return.
2. W2: persist parent `persistentId` (name fallback) + key reparent on id; add parenting test.
3. A1+A2: single canonical serialization (one `ts`, real `prevHash`, hash the exact written line
   minus the hash field) + `AuditLogVerifier.Verify()`/`ReadAll()`; on resume, verify instead of
   silently resetting (A3).
4. W4: explicit mod↔world load ordering contract + a Play Mode double-spawn test.

**P1 — durability & reach**
5. W7/A5: auto-save + audit flush on all scenes; WebGL `CoreAi_PersistFsSync` parity; fsync per batch.
6. W5: retain unresolved-prefab snapshots across saves.
7. A6: audit mod load/error + world save/load events.

**P2 — Hub features (player-facing)**
8. **Mod Logs page** (owner request): add a report ring buffer mirroring `_recentHandlerErrors`
   (+`GetRecentReports`/clear + per-mod `LogReports` toggle via `IHubModService`), one merged
   error+report feed with per-mod filter, live via `ModHandlerErrored`/`ModReportEmitted`.
9. **Audit Log page**: entry list + chain-integrity badge from the new verifier.
10. M1/M2/M3: History/Revert in the editor, UpdateAvailable badge + apply, Import/Export buttons.

**P3 — cleanup & perf**
11. M5 deletions; optimization-review #1/#3/#7.
12. Test backfill: world-state (parenting/empty/missing-prefab/corruption), audit
    (tamper/truncation/rotation/concurrency round-trip).

# Session work audit — benchmark hardening, Lua Full, demos

Date: 2026-06-30. Branch: `feat/game-creation-benchmark`. Scope of this audit: the work done in this
session, why each change exists, and how it was verified.

## Test status (verified)

| Suite | Result |
|-------|--------|
| EditMode (all) | **1332 / 1332 passing** |
| PlayMode FastNoLlm | **46 / 46 passing** |
| PlayMode LlmVerification (live, on qwen3.5-4b-mtp) | **53 passed, 0 failed, 1 ignored** |

All green. No regressions introduced by this session's changes.

## Benchmark fixes (root-cause driven)

These were found by actually running the suite and reading transcripts, not by inspection.

1. **Tool-call roundtrip cap was silently 10.** The cap is read by `SmartToolCallingChatClient` from the
   settings the HTTP client was built with, not the per-run benchmark settings. So `SetMaxToolCallRoundtrips`
   on the orchestrator settings did nothing — every run was throttled at 10. G6 (a 24+ object castle) could
   never pass its mandatory ">=12 spawns" gate, so it scored 0/100 across the whole sweep. Fixed by wiring
   `COREAI_BENCHMARK_ROUNDTRIPS` into `BuildBehaviorSettings`. Verified: qwen3.5-0.8b went 0/100 → 98/100.

2. **spawn rotation/scale were dropped.** The executor read `fx/fy/fz`/`scale` from the envelope, but
   `WorldLlmTool.CreateSpawnCommand` only forwarded x/y/z — so the model's rotation/scale args were
   discarded (this is why castles showed no rotation/sizing — not the model's fault). Fixed end to end,
   plus the **tool schema** now documents that `spawn` accepts inline rotation/scale (the model had no way
   to know otherwise).

3. **Tool-call history truncation caused duplicate spawns.** `maxToolCallHistoryMessages` defaulted to 20,
   so a 30+ step build forgot the first ~15 objects it placed and re-spawned them. This is the real cause
   of the "model loops / duplicates" behavior, not just weak models. Default is now **0 = unlimited**
   everywhere (Programmer role, orchestrator, benchmark), with summarization + overflow-retry still
   bounding truly long sessions.

4. **Throughput metric was mislabeled.** It was called "decode tok/s, comparable to LM Studio" but divides
   by the whole provider call (prefill + decode). LM Studio reports decode-only, so our number reads lower.
   Relabeled "provider-call tok/s (prefill+decode)"; added a live TTFT-based `TokensPerSecondPlayModeTests`
   that isolates true decode-only tok/s the way LM Studio does (verified on 2b: decode 167.8 > provider-call
   137.7, the expected direction).

5. **Difficulty disagreed between UI and history.** The editor RUN tab hardcoded 1–10 values; scenarios
   used a separate 1–5 scale. Unified on one source `BenchmarkInfo.GroupDifficulty10`; UI, ordering, and
   progress all read it now.

6. **Time handling.** Added a SOFT 5-min suite budget that still writes the report/screenshots (vs the
   NUnit `[Timeout]` hard-abort that writes nothing), and the model is now told its time budget with a
   live "X s left" countdown appended to each spawn result so it can pace itself.

7. **Provenance on the hero image.** The free-build hero now bakes a stats line: tool-calls · spawns ·
   gen-seconds · gen-tokens · tok/s. spawn results echo the applied transform so transcripts show whether
   the model used rotation/scale.

8. **Readable spawn names.** Unnamed spawns were named with a GUID hash (unreadable hierarchy); now
   `cube_1`, `Enemy_2`, etc.

## Lua Full

- Closed coercion gaps (Rect, Bounds, Color32, numeric widths, enum-by-number, **Unity object references
  by instance id**) so a mod can wire references, not just set value types.
- Added `unity_add_component` and `unity_destroy` (the create/remove gap the capability audit flagged).
- 16 EditMode cases cover all of the above; see `LUA_FULL_CAPABILITIES_AUDIT.md` and
  `LUA_FULL_VS_RUNTIMEINSPECTOR_AUDIT.md`.

## Demos

- Moved every loose `.cs` into per-demo `Scripts/` subfolders via `git mv` (GUIDs preserved, scenes
  verified intact). See `DEMO_FOLDER_STRUCTURE_AUDIT.md`.
- The FullAccess demo now also showcases `unity_list_members` + member coercion.

## Known blocker (not a code issue)

The 7-model G6 castle re-sweep is **blocked by an LM Studio model-config issue**, not by the benchmark
code: on the current load of `qwen3.5-4b-mtp`, the model reasons (in its think block) that it "has no
tool-execution capability" and emits zero tool calls, hitting the token limit on reasoning. A prior load
of the same model built a 111-tool castle fine, so this is a load/template configuration to resolve on the
LM Studio side. The benchmark harness itself is verified working (EditMode + PlayMode green).

## Follow-ups (tracked, not blocking)

- Make the benchmark's manually-built orchestrator turn-trace visible in the Agent Session Inspector
  (today it only resolves a trace reader from a scene DI scope).
- Optional: a subagent pass to confirm, once a model emits tool calls, whether it uses inline
  rotation/scale now that the schema documents it.

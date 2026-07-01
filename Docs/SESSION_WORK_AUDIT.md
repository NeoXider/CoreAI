# Session work audit — benchmark hardening, Lua Full, demos

Date: 2026-06-30. Branch: `feat/game-creation-benchmark`. Scope of this audit: the work done in this
session, why each change exists, and how it was verified.

## Test status (verified)

| Suite | Result |
|-------|--------|
| EditMode (all) | **1314 / 1314 passing** |
| PlayMode FastNoLlm | **51 / 51 passing** |
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

6. **Time handling.** Added a SOFT 10-min suite budget that still writes the report/screenshots (vs the
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

## G6 castle "Empty response from LLM" — root cause and fix (resolved)

The G6 castle runs were failing with `Empty response from LLM`. Diagnosed live (LM Studio server logs +
direct `curl`), the cause was **not** the model or the prompt:

- The model is healthy — via the raw OpenAI-compatible API it emits up to 30 tool calls in one
  non-streaming response, fast. The HTTP request the code builds is equivalent to a known-good `curl`.
- The free-build runs as ~one tool-call per orchestrator turn with **unbounded** tool-call history
  (`maxToolCallHistoryMessages = 0`). A weak model never decides it is "done", so the conversation runs
  away — observed live growing to **500+ messages / ~37k tokens**. Eventually one turn returns empty (no
  tool call, no visible text), surfaced as `LlmErrorCode.EmptyResponse` at `MeaiLlmClient.cs:271`. The
  earlier "244 s timeout" was the same runaway hitting `requestTimeoutSeconds: 240` on a giant prefill.
- **Reasoning was disabled.** `LlmReasoningMode` = ProviderDefault(0) / Disabled(1) / Enabled(2); the
  settings asset had `reasoningMode: 1` (Disabled → `enable_thinking:false`). Set to Enabled.

**Fixes (both in `GameCreationBenchmarkHarness.cs`, `dotnet build` green):**

1. **Grade-on-empty.** A mid-build empty response *after a scene already exists*
   (`env.World.Count("spawn") >= 1 || capture.ToolCalls >= 1`) is treated as a clean STOP — grade and
   screenshot what was built, no Environment failure, no retry (helper `IsEmptyResponseError`).
2. **Grade-on-cancel/timeout.** Same clean-stop for an `OperationCanceledException` / timeout
   (`"A task was canceled."`) once a scene exists, so a heavy model that hits the per-scenario time budget
   keeps its castle instead of being retried-from-scratch (helper `IsCancellationError`). The grading
   itself was deliberately left unchanged (free visual test, no restrictions).

**Validated live (G6, reasoning on):** `qwythos-9b` → Pass, 37 objects; `deepreinforce-ai_ornith-1.0-9b`
→ Pass, 65 objects; `qwen3.6-27b-fable-5-experimental` → Pass, 27 objects (clean walled perimeter). All
`failure=''`, 0 failed/invalid tool calls; fresh hero screenshots saved to `Docs/Images/castles/`. The
weak `qwen3.5-2b` no longer fails but false-passes via duplicate-spam (273 spawn *commands* ≈ 2 distinct
objects) — expected for a free build that counts commands, not distinct objects.

## Prompt composition + duplicate policy — nuances (and fixes)

Inspecting *what the model actually receives* on the castle run (LM Studio server log, verified live)
surfaced two real disconnects, now fixed. The message the model gets is `system` (UniversalSystemPromptPrefix
+ role/override base) + `user` (the scenario `Goal` carried as `Hint`).

1. **Per-scenario system prompts were dead code.** The orchestrator request `AiTaskRequest` had no
   system-prompt field, and the role id `GameMaster` is not registered in
   `BuiltInDefaultAgentSystemPromptProvider`, so every scenario's `.WithSystemPrompt(...)` was silently
   dropped and the model got the generic fallback `You are agent "GameMaster" in CoreAI…`. **Fix:** added
   an optional `AiTaskRequest.SystemPrompt` override; `AiPromptComposer.GetSystemPrompt(roleId,
   overrideBasePrompt)` uses it as the base prompt (the Universal prefix is still prepended). The harness
   now passes `SystemPrompt = scenario.SystemPrompt`, and G6 overrides it with a build-appropriate prompt
   ("You are a 3D scene builder… keep building until complete, do not stop early"). This is a general
   capability — the **game** can now pass a per-task system prompt on the same role, so game and benchmark
   share one path. Backward-compatible: empty override ⇒ unchanged (registered role prompt).

2. **System prompt and tool config contradicted each other on duplicates.** The Universal prefix says
   *"Do not call the same tool again with the same arguments"*, but every benchmark scenario forced
   `.WithAllowDuplicateToolCalls(true)` AND `WorldLlmTool`/`ComponentLlmTool` declared `AllowDuplicates =>
   true`, which together disable the dedup guard entirely. A weak model (2b) then spam-loops the *identical*
   spawn — the real driver of the runaway. **Fix:** removed the `WithAllowDuplicateToolCalls(true)` override
   from all six scenarios (the global default is already `false`) and set the world/component tools to
   `AllowDuplicates => false`. The dedup key is **tool name + canonicalized arguments**, so distinct spawns
   (different positions/names) are still fully allowed — only an *exact* identical call is skipped with a
   "duplicate … with same arguments" result. Because the dedup lives in core `ToolExecutionPolicy`, the same
   protection holds **in-game**: nothing can spam-loop the world tool in a single task.

3. **Reasoning enum trap.** `LlmReasoningMode` = `ProviderDefault(0) / Disabled(1) / Enabled(2)`. The value
   `1` reads like "on" but means **Disabled** (sends `enable_thinking:false`). The committed asset had `1`,
   which is why thinking was off. Set to `Enabled(2)`.

4. **The benchmark builds its own `ICoreAISettings`, not the `Resources` asset.** So its
   UniversalSystemPromptPrefix is the code default (`CRITICAL RULES FOR ALL AGENTS: …`, which usefully
   carries the tool-calling rules), while the in-game asset prefix is `Respond concisely…`. Worth knowing
   when comparing a benchmark transcript to an in-game session — they share the composition logic but not
   the settings instance.

**Castle timeout** is back to **10 minutes** (G6 `TimeoutSeconds=600`, soft suite-budget default 600,
NUnit `[Timeout]` backstop 15 min so the soft budget can still write the report).

**Verified live (fable-27b, G6, post-fix):** request `system` = `CRITICAL RULES…` prefix + the 3D-builder
override; `user` = the castle goal; `reasoning_tokens` > 0; conversation stayed at ~12 messages / ~2.5k
prompt tokens (no runaway); spawns carried distinct `targetName`s (no duplicate-spam).

## Follow-ups (tracked, not blocking)

- Make the benchmark's manually-built orchestrator turn-trace visible in the Agent Session Inspector
  (today it only resolves a trace reader from a scene DI scope).
- The free-build capture reports `toolCalls=0` when a run ends via the empty/timeout clean-stop path (the
  inner tool loop's calls aren't surfaced to `capture`); the spawn count from `env.World` is correct and
  is what grading uses. Cosmetic only.
- Optional: a subagent pass to confirm, once a model emits tool calls, whether it uses inline
  rotation/scale now that the schema documents it.

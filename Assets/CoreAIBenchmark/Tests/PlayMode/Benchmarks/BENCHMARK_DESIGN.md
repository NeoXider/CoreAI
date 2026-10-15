# CoreAI Game-Creation Benchmark

A scored benchmark that measures how well a model **builds a game** with CoreAI — driving the
real `execute_lua` and `world_command` tools — rather than how well it chats. Each scenario is
graded **0..100** (with a bounded correctness bonus that can push the displayed total above 100),
so runs are comparable across models and over time.

## Scoring model (portable core)

The scoring math lives in the shipped core at `Assets/CoreAI/Runtime/Core/Features/Benchmarking/`
(`GoalScore`, `ScenarioResult`, `BenchmarkReport`, `BenchmarkReportFormatter`) and is unit-tested
under dotnet (`GoalScoreEditModeTests`). Summary:

- A scenario declares weighted **checkpoints**; weights are normalized to 100.
- Runtime **penalties** subtract, flooring at 0: failed tool calls and invalid world commands
  (harness-level), plus scenario-specific penalties (over-building, disallowed actions).
- **Hard caps**: incomplete run (timeout/fault) or final-state failure → cap 60; tool never fired
  (prose only) → cap 40.
- **Bonus** 0..20, **gated on base ≥ 90** (only solvers earn it): the scenario's requested
  *correctness/robustness* bonus — being **more right** than the pass bar. **Correctness only.**
  Token/time efficiency is **reported as a tok/s metric, never scored**, so a merely-faster model
  cannot look smarter than an equally-correct but slower one. Total bonus is capped at 20.
- `Total = Base + Bonus`; suites are compared on **Base** only.
- Verdict: **PASS** (base ≥ 90 and all mandatory checkpoints) / **PARTIAL** (50–89) / **FAIL** (< 50).
- Non-finite inputs are sanitized so a single NaN can never poison a score.
- `FailureAttribution` separates `Framework` / `Model` / `Environment` (classified by exception type)
  so a harness bug is never mistaken for a weak model. The runner **never throws** on a model
  timeout/fault — it records the failure and still grades + reports.
- Repetitions: each scenario can run N times; the suite scorecard uses the **per-scenario mean (average)**
  base (robust to a single noisy run on a small local model) and reports the spread.
- **Tiers** (suite 2.0): a scenario with `IsExtreme` belongs to the extreme tier (`ScenarioResult.Extreme`).
  `BenchmarkReport.SuiteBaseScore` still averages every graded scenario of both tiers;
  `RegularBaseScore` and `ExtremeBaseScore` average the per-scenario means of each tier (with
  `RegularScenarioCount`, `ExtremeScenarioCount`, `ExtremePassCount`, `HasExtreme`). Each `GroupScore`
  carries the both-tier mean plus `RegularMeanBase`/`RegularCount` and
  `ExtremeMeanBase`/`ExtremeCount`/`ExtremePassCount`. Environment/Framework/NotGraded results are
  excluded from every tier, as from the suite score.
- **Real metrics:** a session-capturing `ILlmClient` decorator records true per-turn token usage
  (provider `usage` when available, else a labeled BPE estimate), tool-call counts (incl. failures),
  and the **full model session**, which is appended to the end of every report.

## Summary dimensions & report

One suite score is split into comparable **dimensions** (`BenchmarkDimension`), each fed by the
checkpoints tagged with it:

1. **Tool correctness** — used the right tools, valid args, no failed/invalid calls.
2. **Intent & sequence** — right intent and ordering (discovery before action, exact instruction-following).
3. **Task completion** — the goal was actually achieved (final state / slot behavior on hidden samples).
4. **Determinism** — identical inputs give identical outputs.
5. **Reasoning** — correctness on *derived* inputs the model had to work out itself (piecewise logic,
   recursion, multi-condition math, constraint satisfaction). The "intelligence" axis that separates a
   model that follows instructions from one that actually reasons.
6. **Instruction adherence** — obeying explicit constraints under a subtractive score (prohibitions,
   exact counts, forbidden tools, ordering, tool budgets). Each violation costs points.

From these dimensions (+ generation speed) the report derives a **game-fitness** verdict: a 0..10 fit
rating per game-dev role (NPC, Mechanic/GameMaster, Scene/Tool Operator, Programmer, Orchestrator/Director,
QA) with role-specific weights and gates, plus an overall score and the best-fit role. A tiny model that
cannot call tools reads clearly as 'Not suitable for agentic roles'. Shown in the report and the window.

The Markdown report is structured for quick reading and reuse:
**G6 hero image** (when a G6 build produced one) → **model card** (`_modelcard.png`, or the
`_modelcard.svg` twin when no PNG was rendered) → **SVG results card** → **scorecard** (grade, verdict
counts, suite / regular-tier / extreme-tier scores, per-group line, best/worst, cost, model setup, run)
→ **Scores by group** table (scenarios, mean base, pass count, extreme result) → **Extreme tier**
section (sub-score and one row per extreme scenario) → **dimension bars + a Mermaid chart** →
**game-fitness** → **tool-call statistics** (counts / failures / invalid) → **scenario means** (with
repetitions) → **scenario scores** → **failed checkpoints** → **full model session** (the complete
per-turn transcript) → links to the `.tools.jsonl` trace and the G6 Lua replay → other scene
screenshots.

The cards are engine-free display lists (`BenchmarkCardScene`) built by `BenchmarkReportFormatter`:
`ResultsCardScene` / `ToSvg` for the results card and `ModelCardScene` / `ToModelCardSvg` for the
1600x900 model card (header, suite / extreme / verdict / game-fit tiles, one row per group with the
extreme scenario as a diamond, a dimension dot plot on a 50-100 axis, role bars, a tokens and speed
footer). The SVG output carries a `prefers-color-scheme: dark` override. `BenchmarkModelCard`
(benchmark package) rasterizes the same scene into a dark 3200x1800 PNG when a graphics device exists;
the SVG twin is written with every report, so headless runs still get a model card.

Speed is reported honestly as **generation tokens/sec** (completion tokens ÷ wall-clock); prompt tokens
are excluded because they are prefilled far faster than generated. Speed is **reported, never scored** —
a token/time figure in the report, not points in the bonus — so a faster model never looks smarter.

Reports are machine-readable too: the JSON carries the summary, per-dimension scores, tool stats, and
per-checkpoint dimensions. Since suite 2.0 the summary also has `scenarios`, `regularBaseScore`,
`regularScenarios`, `extremeBaseScore`, `extremeScenarios`, `extremePass` and a `groups` object (per
group: `meanBase`, `scenarios`, `pass`, `regularBase`, `regularScenarios`, `extremeBase`,
`extremeScenarios`, `extremePass`), and every result has an `extreme` flag. **`Build Model Comparison
Report`** (`ModelComparison`) parses many runs and emits `COMPARISON.md` (ranking table with suite,
extreme and regular columns, per-dimension columns, scores by group, extreme-tier ranking, a Mermaid
chart, best model per dimension and for the extreme tier), `COMPARISON.svg` (suite ranking with
extreme diamonds, models x groups heatmap with an Extreme column, extreme ranking),
`COMPARISON_groups.svg` and `COMPARISON_extreme.svg`. Models rank by suite score; ties go to the higher
extreme score. `tools/benchmark_chart.py` builds the leaderboard SVG and Markdown rows from the archived
run JSONs; it shows the extreme column when the reports carry the `extreme` flag and charts pre-2.0
suites as before.

**Extending:** add a new scenario group (its scenarios just set `Group => "G9"` and are listed in
`BenchmarkSuiteCatalog`) and, if needed, a new `BenchmarkDimension` value; the aggregation, bars,
chart, JSON, and comparison adapt automatically. Mark a scenario `IsExtreme` to put it in the extreme
tier. `BenchmarkInfo.Groups`, `GroupTitles` and `GroupDifficulty10` give a group its card order, title
and difficulty; an unlisted group is shown last, under its id, at difficulty 5.

Only **G6** captures a screenshot: its built scene is the deliverable and becomes the report hero. Every
other group, G1 and G7 included, sets `CaptureScene => false` and is graded from world state, logic
slots and the tool-call trace. The capture is skipped headlessly when no GPU is present.

## Implemented: G1 – G8

### G2 — Runtime mechanic authoring (deterministic core)
Pure-Lua tasks. The game declares named **logic slots** (`LuaLogicSlots`: `calculate_damage`,
`spawn_rate`, `score_formula`, `win_condition`, …) with a C# default. The model must install Lua
overrides via `execute_lua` to satisfy a behavioral spec. **Graded by executing the slot** on a
battery of inputs (e.g. `damage(level=1)==10`, `damage(level=5)==50`, monotonic), not by inspecting
prose. Direct generalization of the existing `LuaDynamicGameMechanicsTests`. Almost zero flakiness;
also runnable against a scripted LLM to prove the harness.

### G1 — Build-a-game from a spec (world + lua)
A natural-language brief lists explicit requirements; the model builds the mini-game with
`world_command` (spawn/move/score UI) plus `execute_lua` (rules). Graded deterministically on the final
scene the recorded world commands leave behind (`RecordedScene`: a later `change` counts, a destroyed
object does not) **and** by executing the resulting logic slots on hidden samples. Spread is checked
pairwise (centres at least 1 m apart in x/z, as the goals state). Score = weighted % of requirements
met. Example: a coin-collector — spawn player + N coins + goal zone; `score_formula` adds 1 per coin;
`win_condition` true at N.

### G3 — Reasoning & design (no spoon-fed code)
The prompt does **not** give the Lua code: the model must work out the mechanic itself — quadratic
scoring, piecewise tiered pricing, two-sided clamping, n-th Fibonacci (recursion/iteration), boolean
composition with negation, and a constraint-satisfaction task (four distinct in-range HP values summing
to exactly 400). Graded by executing the resulting slot on **derived** inputs (tagged Reasoning). This
is where a small model that only transcribes starts to fail.

### G4 — Playable game (simulated playthrough)
The model designs a small **rule system** as several interacting logic slots; the harness then
**simulates a real playthrough** by driving state through those slots and asserting the trajectory
step-by-step: a turn-based fight resolves over the exact right turns (HP 20→12→4→0), a shopping session
ends with the exact gold after a rejected purchase, a crafting chain transitively reaches `table` from
raw wood. The showcase tier and the hardest discriminator — every slot must be mutually consistent and
handle the edge cases, or the simulation diverges and the run fails. This is the multi-step / agentic
tier; further multi-step and agentic groups will extend it.

### G5 — Strict instruction-following (subtractive)
A small task with explicit constraints; scored **from 100 down**. Each constraint is a compliance
checkpoint (the InstructionAdherence dimension) that fails when violated, plus a per-occurrence penalty
for repeated violations; a mandatory core task prevents "do nothing" from scoring 100. Constraints:
never touch a protected object, spawn-only (no other action), exactly N actions, a forbidden tool
(no `execute_lua`), a tool-call budget, and an exact spawn order. Violations are detected
deterministically from the captured tool-call trace and world commands. Since suite v1.8 the recording
executor expands a `spawn_batch` into one `spawn` per item (named `item.name`, else `targetName_i`, else
`prefab_i`, exactly as the production executor does), so a batch satisfies the spawn-only constraint and
its items count as spawns in every group; under v1.7 a batch was one opaque non-spawn command.

### G6 — Free-build castle (visual)
Open-ended: the model designs and places a whole castle scene through the Roblox API — `execute_lua`
over a live `RbxWorldHost`, `Instance.new('Part')` with `Size`/`CFrame`/`Material`/`Color`/`Shape` — with
model-authored positions (since 7.5.0; before that, `world_command` primitives with no materials and four
shapes). The grade reads every part back through the same Lua surface (`G6BuildAnalysis.ReadParts`) and
rasterizes the build from above (`G6BuildAnalysis.Analyze`). Besides tool hygiene, part count (40+, 60+),
distinct names, the build volume, 12+ distinct `Enum.Material` values and all five `Enum.PartType`
shapes, suite 2.0 scores the geometry (`G6BuildAnalysis.Checks`):

- castle structure (default castle goal only): curtain walls enclosing at least 400 square studs of open
  courtyard, a ground-level gate 3-16 studs wide, towers touching the curtain and rising at least 4 studs
  above the wall crest (2 and 4 towers are separate checkpoints), a free-standing keep, at least 3
  structures roofed with Wedge/CornerWedge/Ball parts, at least 5 objects inside the walls and 4 beyond;
- spread: parts 2+ studs above the ground cover at least 1500 square studs and span at least 48 x 48;
- construction quality: at least 95% of parts grounded, no duplicates, at most 10% of parts in
  z-fighting overlaps, at most 5% hidden or paper-thin, at most 5% squashed round shapes.

The goal's WHAT IS MEASURED and CONSTRUCTION paragraphs quote these thresholds, and tests pin the wording
to the constants. A `COREAI_BENCHMARK_FREEBUILD_SUBJECT` build replaces the castle checks with
subject-neutral ones (spread, at least 16 studs of height, at least 6 separate objects). Colour is not
scored, and the 1.x `material_breadth` (20+ materials) checkpoint is gone. A world that holds parts but
cannot be read back throws, so the run is attributed to the framework, not the model. G6 is one scenario
plus its image-feedback twin and has no extreme tier; its 2.0 scores are not comparable with 1.x. The
screenshot preserves the model's layout (no grid normalisation, no ghosts) and is embedded as the **hero
image at the top of the report**.

### G7 — Comprehensive integration
A key-and-gate puzzle combines exact world construction with Lua logic in one model session. The harness
checks Player/Gate/Key placement (x/z zones and y = 1), a proximity-based `key_found` slot sampled along
x, z and diagonals, and consistency between the spawned world and the logic that refers to it. It takes
no screenshot.

### G8 — Described-state conditional selection
The prompt provides a textual snapshot of named existing objects and asks the model to mutate only the
matching subset: remove two junk objects, scale only undersized towers, or encode a described wave rule.
The recorder seeds the described objects, so `list_objects` lists them (with their described positions
where the scenario gives them). Type, faction, hp and tags exist only in the prompt. Grading validates
exact targets, rejects position/rotation/parent pollution (including a single axis), and charges every
extra mutation. This is a described-state reasoning test; it does not measure sustained multi-turn
recovery.

### Extreme tier (suite 2.0)
Every group except G6 has one scenario with `IsExtreme => true`, `Difficulty => 5` and a 480-second
timeout. The rules are stated exactly; the difficulty is the number of interacting steps. Checkpoints
are split by rule family for partial credit, and hidden samples come from independent C# references
(`DamagePipelineReference`, `RouteReference`, `BattleReference`, the G1/G7 canonical layouts).

- `g1_extreme_fortress` — 27-object octagonal fortress derived from a coordinate spec, plus
  `wall_for_bearing` (hidden bearings) and `nearest_tower`, which must read the live world: the grader
  loads a reference fortress (`VirtualScene`), moves and removes towers, and calls it again.
- `g2_extreme_damage_pipeline` — a nine-step multi-hit `resolve_attack` slot, 69 hidden attacks.
- `g3_extreme_dungeon_route` — cheapest route with swamps, keys/doors, one-way cells and teleporters,
  fewest actions on cost ties, 46 hidden grids.
- `g4_extreme_auto_battler` — four slots of a stateful team auto-battler, 22 hidden battles compared
  turn by turn.
- `g5_extreme_work_order` — ten interacting rules on a seeded scene, one rule per checkpoint, and a
  prompt injection in a seeded object name.
- `g7_extreme_dungeon` — 25-object keyed 3x3 dungeon with rule-derived key rooms and five world-reading
  slots, re-checked after the grader moves keys, the Exit and the Player and removes a door.
- `g8_extreme_siege_director` — 48-object described scene seeded with positions; three rules and two
  tags select exactly ten mutations.

### Recorder fidelity (suite 2.0)
The recording executor answers `list_objects` with each object's real position (seeded with
`SeedObject(name, position)`, or the last recorded spawn/move/change) instead of the origin. A
`spawn_batch` item inherits the batch-level rotation, scale, parent and `worldPositionStays` when it sets
none, exactly like `CoreAiWorldCommandExecutor.TrySpawnBatch`; at most `MaxSpawnBatchSize` items are
recorded.

## Recorded ideas (not yet implemented)

### Incremental build (multi-turn, regression-aware)
Build the game over several turns (world → score → win → enemy). After each turn re-run **all** prior
checkpoints to detect whether a new feature broke an earlier one. Exercises long-horizon coherence and
context management. Cost: longer runs, sensitive to model memory.

### Reproduce a reference game (functional equivalence)
A golden reference (hand-authored world commands + logic slots) passes a behavior battery; the model
sees only the brief and is graded by how many of the golden behavior tests it also passes. Cost:
maintaining golden references.

### Future game jam (open-ended, judge + deterministic floor)
"Make a fun one-screen mini-game." Deterministic floor first (runs without Lua errors? ≥3 interactive
objects? ≥1 callable win/lose slot?), then an LLM-judge scores creativity/coherence with a capped
contribution (deterministic stays ≥ 50% per the scoring model's judge-blend clamp). Flakiest, needs a
judge, hardest to reproduce on a small local model.

## Running

The PlayMode entry test (`GameCreationBenchmarkPlayModeTests`) is `[Explicit]` + `[Category("Benchmark")]`,
so it never runs in a normal "Run All" — only a deliberate launch executes it. It is gated on a
configured live model via `PlayModeProductionLikeLlmFactory.TryCreate` / `PlayModeOpenAiTestConfig`
(env vars `COREAI_TEST_BASE_URL` / `COREAI_TEST_API_KEY` / `COREAI_TEST_MODEL`, or the local config
file); when unconfigured it `Assert.Ignore`s.

`COREAI_BENCHMARK_REASONING_EFFORT` or batchmode `-coreAiBenchmarkReasoningEffort` selects
`none`, `low`, `medium`, `high`, or `xhigh`; unset leaves the provider default. The effective effort
is recorded as `reasoningEffort` in report JSON metadata and in the Markdown header, which shows
`provider default` when no effort is sent.

Reports are written to `TestResults/CoreAI/Benchmarks/BENCHMARK_<yyyyMMdd_HHmmss>_<model>.{md,json}` —
date and model in the filename, so runs are self-identifying and never overwrite. Beside them: the
results card `.svg`, `_modelcard.svg` (always) and `_modelcard.png` (with a GPU), the `.tools.jsonl`
trace, and for G6 the hero PNG and `_g6_replay.lua`.

### Time limits
`COREAI_BENCHMARK_TIMEOUT` overrides the model timeout for non-extreme scenarios, including G6;
extreme scenarios retain their own timeout. G6 is capped at 570 seconds even when the override is
higher, reserving the rest of its budget for capture/export. Zero or unset uses each scenario's
default (subject to the G6 cap).

The NUnit `[Timeout]` is a 6-hour hard backstop that writes nothing. The real terminator is the soft
suite budget (`COREAI_BENCHMARK_SUITE_BUDGET`): a scenario repetition starts only when its worst case
(every retry attempt at the full timeout plus the cancel grace, and for G6 the capture reserve) still
fits, otherwise the run stops and writes its report. The default budget is 6000 seconds for a single
model and the full ceiling for a multi-target run; the ceiling is the NUnit timeout minus a report
margin of 300 seconds plus 30 seconds per extra target. The launcher watchdog (6 h 10 min) sits above
the NUnit timeout.

### Parallel multi-model runs
With `COREAI_BENCHMARK_TARGETS` set (or `-coreAiBenchmarkTargets`, or enabled rows in the window's
**Parallel targets** foldout), the same test runs several targets at once
(`GameCreationBenchmarkParallelSuite.cs`). `BenchmarkTargetList` (core) parses a JSON array, compact
`label|model|baseUrl|apiKey|effort` entries separated by `;` or new lines, or `@file`; `env:VAR` keys are read
from the environment, and duplicate display names get `#2`, `#3` labels. `COREAI_BENCHMARK_PARALLEL`
(`-coreAiBenchmarkParallel`) caps concurrency; empty or `0` runs all targets. Each target gets its own
HTTP client (`PlayModeProductionLikeLlmFactory.TryCreateForEndpoint`), settings asset, report, progress
handle (`BenchmarkProgress.BeginRun`, with a per-target stop) and artifacts named
`BENCHMARK_<runId>_<model>[__<label>]`; all targets share the run id and the suite clock.

The optional fifth compact field is the target effort. Leave the key field empty when no key is
needed: `local|model|http://127.0.0.1:8831/v1||high` (`baseUrl||high`); `baseUrl|high` is ambiguous
and rejected. JSON targets accept `reasoningEffort` and `extraBody` (a JSON object or a string
holding one). Target body fields merge over the shared body for the same endpoint; explicit target
`reasoningEffort` is applied last. Empty target effort inherits the suite default.

Sampling temperature is optional and JSON-only: `temperature` (alias `temp`) accepts a number from 0
to 2; omitted or `null` means provider/model default. There is no sixth compact field. Both the classic
single-model run and targets without a temperature send no temperature override. The target value is
passed through `ResolveForEndpoint` to the HTTP profile's opt-in override; the benchmark sets no other
typed sampling values, so `extraBody` fields such as `top_p`, `top_k`, `min_p`, `presence_penalty`,
`repeat_penalty` and `chat_template_kwargs` reach the wire unchanged. For MiniCPM-V 4.7, use
`temperature: 0.7` and `extraBody: {"top_p":0.95,"top_k":100,"min_p":0,"presence_penalty":0,
"repeat_penalty":1.03,"chat_template_kwargs":{"enable_thinking":false}}` in the target object.
The window saves its optional Temperature field with the other non-secret row fields; empty means
provider default. Run metadata and model summaries carry a nullable temperature; JSON uses `null`
and the Markdown header says `temperature provider default` when unset, or the explicit value when set.

`CooperativeScheduler` (`ParallelBenchmarkScheduler.cs`) steps the target loops inside the one
`[UnityTest]` coroutine. Scenarios with `UsesSharedScene` (by default `CaptureScene`, so G6) and the
model-card captures take turns on one `BenchmarkVisualLane`: Rbx parts are bound at the model's own
world coordinates, the agent camera service finds its camera by a global search, and captures change
global lighting. All other scenarios of all targets run in parallel. A scenario that uses
`env.RbxWorld` or `env.CameraTool` without `CaptureScene` must override `UsesSharedScene`; the harness
logs a warning when a parallel run builds an Rbx world without it. Lifecycle tool-call records without
a trace id are ignored in parallel runs, because every target uses the same role id. Without a target
list the run is the single-model path it always was.

Parallel runs poll provider tasks against each scenario's wall clock, the target Stop flag and the
shared soft budget. Cancellation has a bounded grace window; a provider that ignores its token cannot
hold the coroutine indefinitely. The scheduler also checks Stop/budget outside nested waits, releases
visual-lane waiters on disposal, and limits nested coroutine work per step. Benchmark retries stop at
the configured attempt count, Stop or elapsed budget. Each parallel PlayMode regression has a
15-second NUnit watchdog.

Editor batch mode never resumes `WaitForEndOfFrame`, even with a graphics device. Report cards and
scene captures therefore read the previous rendered frame on the next update in batch mode; the
scheduler uses the same fallback for nested render waits. Interactive rendering retains its normal
end-of-frame wait. These guards require cooperative main-thread code: a synchronous `MoveNext` or
provider call that never returns cannot be preempted by a coroutine.
Prefer an open Editor for published runs so captures retain the normal end-of-frame timing.

### Launching (convenient)
`GameCreationBenchmarkLauncher` (editor-only test assembly) drives the suite via `TestRunnerApi`:
- Menu **CoreAI ▸ Benchmarks ▸ Run Game-Creation Benchmark** (one click; opens the report when done).
- Menu **CoreAI ▸ Benchmarks ▸ Benchmark Window…** — Run/History/Models/Compare tabs: pick
  model/connection, G1–G8 groups and repetitions, optional parallel targets, browse past runs, a
  sortable model leaderboard, and build the cross-model comparison report.
- Batchmode: `-executeMethod CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunFromCli`
  with `-coreAiBenchmarkModel` / `-coreAiBenchmarkGroups` / `-coreAiBenchmarkReps` /
  `-coreAiBenchmarkReasoningEffort`, or
  `-coreAiBenchmarkTargets` / `-coreAiBenchmarkParallel` for a parallel run. An invalid target list
  exits with code 2.

The suite honors `COREAI_BENCHMARK_GROUPS` (CSV of group ids) and `COREAI_BENCHMARK_REPS` (per-scenario
repetitions; the report keeps the per-scenario mean). For a single local server that loads one model at
a time, a multi-model **matrix** is still a shell loop over `-coreAiBenchmarkModel`; each invocation
writes its own date+model report.

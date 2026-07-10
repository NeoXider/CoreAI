# CoreAI Game-Creation Benchmark

A scored benchmark that measures how well a model **builds a game** with CoreAI — driving the
real `execute_lua` and `world_command` tools — rather than how well it chats. Each scenario is
graded **0..100** (with a bounded efficiency bonus that can push the displayed total above 100),
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
- **Bonus** 0..20, **gated on base ≥ 90** (only solvers earn it), composed of:
  - *correctness/robustness* (the scenario's requested bonus), and
  - *efficiency* — **fewer tokens** (up to 6) and **less time** (up to 6) than the scenario's budget;
    the further under budget, the more points. Total bonus is capped at 20.
- `Total = Base + Bonus`; suites are compared on **Base** only.
- Verdict: **PASS** (base ≥ 90 and all mandatory checkpoints) / **PARTIAL** (50–89) / **FAIL** (< 50).
- Non-finite inputs are sanitized so a single NaN can never poison a score.
- `FailureAttribution` separates `Framework` / `Model` / `Environment` (classified by exception type)
  so a harness bug is never mistaken for a weak model. The runner **never throws** on a model
  timeout/fault — it records the failure and still grades + reports.
- Repetitions: each scenario can run N times; the suite scorecard uses the **per-scenario mean (average)**
  base (robust to a single noisy run on a small local model) and reports the spread.
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
**embedded SVG results card** → **scorecard** (model, suite score, grade, verdict, generation tok/s,
speed/efficiency bonus) → **dimension bars + a Mermaid chart** → **tool-call statistics** (counts /
failures / invalid, before the session) → **scenario scores** → **failed checkpoints** → **full model
session** (the complete per-turn transcript, at the end).

Speed is reported honestly as **generation tokens/sec** (completion tokens ÷ wall-clock); prompt tokens
are excluded because they are prefilled far faster than generated. The efficiency bonus is split into a
**token** part and a **time** part so the reward for being fast and cheap is explicit.

Reports are machine-readable too: the JSON carries the summary, per-dimension scores, tool stats, and
per-checkpoint dimensions, so **`Build Model Comparison Report`** can parse many runs and emit
`COMPARISON.md` — a ranking table, per-dimension columns, suite-score bars, a Mermaid chart, and the
best model per dimension.

**Extending to G3+ :** add a new scenario group (its scenarios just set `Group => "G3"`) and, if needed,
a new `BenchmarkDimension` value; the aggregation, bars, chart, JSON, and comparison adapt automatically
— no core changes required.

For **G1 build-a-game** scenarios (where the scene is the deliverable) the model's `world_command` output
drives a real Unity scene (primitive GameObjects with name labels, tinted green/red by expected name),
and a **screenshot is captured and embedded** in the report (and shown inline in the History window).
Other groups skip the screenshot (their scene is incidental); skipped headlessly when no GPU is present.

## Implemented: G1 – G6

### G2 — Runtime mechanic authoring (deterministic core)
Pure-Lua tasks. The game declares named **logic slots** (`LuaLogicSlots`: `calculate_damage`,
`spawn_rate`, `score_formula`, `win_condition`, …) with a C# default. The model must install Lua
overrides via `execute_lua` to satisfy a behavioral spec. **Graded by executing the slot** on a
battery of inputs (e.g. `damage(level=1)==10`, `damage(level=5)==50`, monotonic), not by inspecting
prose. Direct generalization of the existing `LuaDynamicGameMechanicsTests`. Almost zero flakiness;
also runnable against a scripted LLM to prove the harness.

### G1 — Build-a-game from a spec (world + lua)
A natural-language brief lists explicit requirements; the model builds the mini-game with
`world_command` (spawn/move/score UI) plus `execute_lua` (rules). Graded deterministically by
inspecting the captured world commands **and** executing the resulting logic slots. Score = weighted
% of requirements met. Example: a coin-collector — spawn player + N coins + goal zone; `score_formula`
adds 1 per coin; `win_condition` true at N.

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
deterministically from the captured tool-call trace and world commands.

### G6 — Free-build castle (bonus, visual)
Open-ended: the model designs and places a whole castle scene via `world_command` (towers, walls, a gate,
flags, …) with model-authored positions. Graded leniently on scale (12+ objects) and variety (20+); the
screenshot preserves the model's layout (no grid normalisation, no ghosts) and is embedded as the **hero
image at the top of the report**. Purpose is a vivid, comparable visual of what each model builds — not a
precise score.

## Recorded ideas (not yet implemented)

### Incremental build (multi-turn, regression-aware)
Build the game over several turns (world → score → win → enemy). After each turn re-run **all** prior
checkpoints to detect whether a new feature broke an earlier one. Exercises long-horizon coherence and
context management. Cost: longer runs, sensitive to model memory.

### Reproduce a reference game (functional equivalence)
A golden reference (hand-authored world commands + logic slots) passes a behavior battery; the model
sees only the brief and is graded by how many of the golden behavior tests it also passes. Cost:
maintaining golden references.

### G8 — Game jam (open-ended, judge + deterministic floor)
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

Reports are written to `TestResults/CoreAI/Benchmarks/BENCHMARK_<yyyyMMdd_HHmmss>_<model>.{md,json}` —
date and model in the filename, so runs are self-identifying and never overwrite.

### Launching (convenient)
`GameCreationBenchmarkLauncher` (editor-only test assembly) drives the suite via `TestRunnerApi`:
- Menu **CoreAI ▸ Benchmarks ▸ Run Game-Creation Benchmark** (one click; opens the report when done).
- Menu **CoreAI ▸ Benchmarks ▸ Benchmark Window (UITK)…** — Run/History/Models/Compare tabs: pick
  model/connection, G1–G6 groups and repetitions, browse past runs, a sortable model leaderboard, and
  build the cross-model comparison report.
- Batchmode: `-executeMethod CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunFromCli`
  with `-coreAiBenchmarkModel` / `-coreAiBenchmarkGroups` / `-coreAiBenchmarkReps`.

The suite honors `COREAI_BENCHMARK_GROUPS` (CSV of group ids) and `COREAI_BENCHMARK_REPS` (per-scenario
repetitions; the report keeps the per-scenario mean). Multi-model **matrix** runs are just a shell
loop over `-coreAiBenchmarkModel`; each invocation writes its own date+model report.

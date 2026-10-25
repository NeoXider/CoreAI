# G6 golden-input integration repair

Date: 2026-10-25. Model: `gpt-6.1-sol`; reasoning effort: `max`.
Checkout: `D:/Git/CoreAI`. Status: fixture correction source-final; root PlayMode acceptance pending.

## Final root execution acceptance - 2026-10-25

`artifacts/fix-wave-20261025/unity-playmode-fake-final.xml` confirms all 34 G6 cases Passed. The complete selected gate has 386 Passed, zero Failed and two batch-mode end-of-frame skips, exit zero. The restored 60-part golden castle satisfies every unchanged checkpoint and the heap negative control still passes. Root accepts this fixture correction; historical source-stage pending statements below predate this receipt. No live-model quality or built-player acceptance follows from it.

## Goal, scope and invariants

Restore a valid positive castle control while retaining full rotated-part bounds, every G6
grading threshold, the all-checkpoints golden assertion and the heap's negative composition
control. This is fixture geometry repair, not a change to benchmark quality or historical scores.

Only `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysis.cs`,
`G6BuildAnalysisPlayModeTests.cs`, and this receipt are leased. Current evidence justifies changing
only the fixture. Adjacent scenario, harness, world adapter, other tests, metadata and archived
assets are read-only. Root owns all execution and Git; this worker runs no tests/builds/Unity/MCP.

## Recorded failure and actual contract

- Root Windows PlayMode receipt `artifacts/fix-wave-20261025/unity-playmode-windows-final.xml`:
  **389 total, 382 passed, five failed, two skipped**; companion exit receipt records code 2.
- The G6 fixture has **34 cases: 33 passed, one failed, zero skipped**. The failure is
  `LiveWorld_GradePassesTheCoherentCastleAndFailsTheHeap`, line 384: `scene_depth: 59 parts`.
  `windows-playmode-failures.json` records that exact assertion, with no dimension/ratio failure.
- `GameCastleScenarioG6.cs:376` grants the scene-depth checkpoint only when `scene.Parts >= 60`,
  weight two. The published goal says at least 40 parts, ideally 60+. The existing golden test
  intentionally requires every checkpoint to pass. The checkpoint is not currently marked
  BenchmarkCheckpoint.Mandatory: `ScenarioGrading.Add` defaults that flag to false. Preserve
  the predicate, flag and golden assertion; do not mislabel or weaken this contract.
- The correct adapter volume check uses rotated Size/CFrame half-extents with 0.001 tolerance,
  x/z within -64..64 and y within 0..96. A part's centre alone is insufficient. No adapter or
  analyzer defect is established by this failure.

## Numerical source proof and approved correction

`CoherentCastle()` contains 60 original parts: 24 initial parts, 12 tower/roof/finial parts,
14 merlons, six tree parts and four shrubs. Its existing `CastleGround` is a
124 x 1 x 124 foundation centred at y=-0.5, so it occupies y=-1..0. Correct extents reject
that foundation. The prior fixture correction removes it, preserving 59 structural parts but
making the 60-part scene-depth checkpoint impossible to pass.

Root-approved fixture-only correction for the live-world golden input: retain the original foundation
and translate the entire original 60-part castle upward by exactly one stud. Preserve all names,
roles, counts, shapes, materials, sizes, rotations, x/z placements and pairwise relative positions.
All 59 existing structural parts stay together on the foundation's top surface, without adding
padding parts or modifying the heap. The analyzer and scenario thresholds remain unchanged.

| Measurement | Original 60-part castle | Removed-foundation input | Corrected translated original |
| --- | --- | --- | --- |
| Part count | 60 | 59 | 60 original parts |
| Foundation y interval | -1..0 | Absent | 0..1 |
| Structural minimum y | 0 | 0 | 1 |
| Complete x interval | -62..62 | -53..52 | -62..62 |
| Complete z interval | -62..62 | -62..47.5 | -62..62 |
| Complete y interval | -1..30 | 0..30 | 0..31 |
| Rotated full-volume check | Foundation fails | No foundation violation | All extents inside the volume |
| Scene-depth count predicate | 60 >= 60 | 59 < 60 | 60 >= 60 |

The common translation preserves part overlaps, support contacts, widths and proportions.
The foundation remains grounded at y=0; the inferred scene ground becomes y=1, so height
above ground and plan-view composition retain their original meaning. These are source-derived
numerical expectations, not an executed geometry/test pass. Root explicitly adjudicated and
approved restoration of the existing foundation: a literal total of 59 conflicts with the existing
60-part criterion, while the original 59 structural parts and their relative layout are preserved.

## Completed source checkpoint and verification

- Changed only `G6BuildAnalysisPlayModeTests.cs` at the live-world positive input. Removed the
  foundation deletion, retained all 60 original parts, added an explicit original-count assertion,
  and reconstructed each immutable PartSpec with y+1 while copying every other field unchanged.
- The existing all-checkpoint loop, total-weight-100 assertion, gate assertion and every heap
  negative assertion remain. No Ignore, baseline, bypass, padding part, relaxed extent check,
  weight change or threshold change was introduced. The common synthetic CoherentCastle helper
  and Heap helper themselves are unchanged.
- Current fixture SHA-256: `c7f8902d674178f597c2a175e4579dcb80c809a1942e024a5c8a98388df3b690`.
  The analyzer, scenario and world-adapter hashes still equal the initial read-only values below.
- Read the actual full fixture diff and adjacent support/ground/extent/grading contracts.
  `git diff --check` exits 0; direct English/trailing-whitespace checks are clean and the added
  C# uses explicit types. The root was notified that C# is frozen for its executable checks.

Expected next result: the same original 60 parts read back as `scene_depth: 60 parts`, all rotated
full extents remain valid, the castle earns every unchanged checkpoint, and the unchanged heap
still earns less than half of the grade. Those are acceptance expectations, not recorded passes.
Root must rerun all 34 G6 PlayMode cases, all 249 fake benchmark cases and the full fake-only
PlayMode gate. Require fresh positive XML with fixture presence and retain this negative run.
No source-only pass or publication claim is made.

## Initial source hashes

| Source | SHA-256 before this worker's changes |
| --- | --- |
| `G6BuildAnalysis.cs` | `dc7f1a3b84cc05974dbb94e6de8f767670cb0cc741b0d380615da2d137dabb16` |
| `G6BuildAnalysisPlayModeTests.cs` | `6c449afbd82271a59c9eacda2e71f9fbd47c5afec105551498b4d6a4462b5dc7` |
| `GameCastleScenarioG6.cs` (read-only) | `9a8ff18320b1d8e373fc239a2c45bf24520501567ad3720dfe44cb6fdefba4f3` |
| `RbxBenchmarkWorld.cs` (read-only) | `23483150285b05b571e34cbd6d83366d1ba40cb64af033908871752ece838e73` |

All source paths in this table are relative to `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks`.

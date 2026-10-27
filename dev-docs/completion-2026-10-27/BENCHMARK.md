# Benchmark inactivity-attribution PR audit — 2026-10-27

Status: source review complete; root acceptance pending. Reviewer: native gpt-6.1-sol, reasoning effort max. Baseline: `demo` at published `bc3fd74f` / 7.54.0; tracked and untracked working-tree changes are the review surface.

## Goal, invariants and scope

Review the module as a replaceable framework block, preserve unrelated authorized changes and the frozen 2026-10-23 audits, and report only actionable confirmed defects. No implementation, asset, metadata, test, build, Unity, provider, Git or service mutation was performed. The owner explicitly requested these retained per-module reports. Saved receipts were parsed, not rerun.

## Review result

No additional confirmed behavior defect in this module's current diff. The PR-BENCH-001 repair captures a current-turn admission timestamp, resets provider activity for that turn, freezes both at `Seal`, and uses the latest of current-turn start, provider activity and tool activity for inactivity attribution. A fresh quiet continuation therefore does not inherit the whole scenario's age. The original no-baseline fallback still uses the finite scenario age.

The injectable UTC clock is confined to the capturing harness; production defaults remain the existing UTC time source. The elapsed measurement clock remains separate. Source review checked both buffered and streaming admission, sealed reads, delayed response/timeout evidence, negative gap clamping, the inactivity threshold and environment-retry decisions. This does not change model score policy or create a new provider call.

## Regression evidence read

`artifacts/pr-review-20261025/unity-repair-play1.xml` contains **45 Passed** rows for `CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests`, including:

- `ProviderInactivityGap_FreshQuietContinuationUsesCurrentTurnStart`: prior turn at time zero, new turn at 520 seconds, deadline at 570 seconds; the 50-second current-turn gap stays below 120 seconds and is attributed to Model without environment retry.
- `ProviderInactivityGap_QuietContinuationBeyondThresholdIsEnvironment`: the same new turn at 520 seconds, deadline at 700 seconds; the 180-second gap exceeds the threshold and allows environment retry.

The fake clock tests do not wait those wall-clock durations. Their stream has opened provider-response evidence and no useful continuation content; sealed state remains the grading source.

## Acceptance limits

The complete saved PlayMode selection is **390 Passed, 1 Failed, 2 Skipped** out of 393 cases. The failure is `FullAccessDemoScenePlayModeTests.CommandInputField_ConstructsAndInitializesWithInstalledTmp`, outside the Benchmark fixture. `unity-repair-console-play2.xml` later has 2 Passed isolated console cases; it does not turn the old full selection green. Do not describe the full PlayMode gate as passed until root supplies the fresh run.

No real-model benchmark or performance/quality comparison was run by this reviewer. All newly authorized real API PlayMode checks use OpenRouter; old local-model benchmark artifacts are historical and cannot satisfy that gate.

## Reviewed source snapshot

| Path | SHA-256 |
| --- | --- |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs` | `2c35b89d864fee5a62cb4512f3429cba4f0e50dc1e75d699753e2f3e41a85098` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs` | `acc82765a841833ce0e4f1a457fc78ff5b78d725c766ee94b7220076c5fe1aa1` |

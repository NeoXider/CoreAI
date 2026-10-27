# PR-BENCH-001: inactivity classification repair

Status: implemented (worker), pending root verification.

## Finding

Resetting `_lastProviderActivityUtc` in `SessionCapturingLlmClient.BeginTurn` correctly prevents prior-turn generation evidence from being reused, but `ProviderActivityGapSeconds` then fell back to scenario age or an older tool timestamp. A freshly accepted quiet continuation could therefore be classified as an Environment provider stall.

## Fix

- `SessionCapturingLlmClient` now keeps a separate current-turn admission inactivity baseline, `_currentTurnStartedUtc`, initialized in `BeginTurn` using a controllable UTC clock (`_utcNow`, optional constructor seam).
- The baseline is sealed atomically with the other evidence in `Seal()` as `_sealedCurrentTurnStartedUtc`.
- Exposed as `CurrentTurnStartedUtc` (plus `CurrentTurnStartUtc` / `TurnStartedUtc` aliases), sealed-aware, so late callbacks cannot mutate it.
- All `DateTime.UtcNow` activity timestamps now use `_utcNow()` for controllable, deterministic clocks.
- `ProviderActivityGapSeconds` gained an optional `currentTurnStartedUtc` parameter; when present it becomes the floor for the last-activity timestamp, so inactivity is measured from the current turn's admission instead of scenario start or an older tool call.
- `RunScenario` passes `capture.CurrentTurnStartedUtc` into `ProviderActivityGapSeconds`.

## Regression tests

Added to `GameCreationBenchmarkPlayModeTests.cs`:

- `ProviderInactivityGap_FreshQuietContinuationUsesCurrentTurnStart`: two accepted turns, first active until 520s, quiet continuation reaching a 570s deadline; gap < 120s, classified Model, not retryable, sealed baseline unchanged.
- `ProviderInactivityGap_QuietContinuationBeyondThresholdIsEnvironment`: same shape but scenario end at 700s; gap >= 120s, classified Environment, retryable.

No live provider wait, no deadline weakening, no classifier policy change.

## Scope

Only `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs`,
`Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs`, and this document changed.

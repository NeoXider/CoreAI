# EditMode Test Audit

## 2026-06-14

### Passed checks
- [`Assets/CoreAiUnity/Tests/EditMode/LiveMechanicsModsChatDemoSceneEditModeTests.cs`](C:/Git/CoreAI/Assets/CoreAiUnity/Tests/EditMode/LiveMechanicsModsChatDemoSceneEditModeTests.cs): removed a brittle demo check that tested incidental UI and prompt content details rather than gameplay/runtime contract.
  - Removed method: `LiveMechanicsModsChatDemo_HasAutoRepairPersistenceAndUserFacingPrompts`.
  - Left contract check: `WaveAutoBattlerModsDemo_HasFullLuaEnabled`.
- A4 (reflection) hardening: removed `System.Reflection` on `CoreAISettingsAsset` private fields across EditMode tests, replaced with public configuration API (`ConfigureFallbackBackend`, `ConfigureOffline(bool,…)`, `SetOrchestratorTimeoutSeconds`, `SetApiBaseUrl`, `SetModelResolution`, `ApplyOptions`). Behavior preserved; verified by `dotnet build CoreAI.Tests.csproj`.

### Recommendation
- For EditMode UI-geometry checks, prefer stable invariants (min/max bounds) over exact floats unless the exact value is part of the contract.
- Test settings through the public configuration surface; do not reach into private serialized fields by name.

### TODO
- `Assets/CoreAiUnity/Tests/EditMode/WorldToolEditModeTests.cs`:
  - Stale TODO note about `play_sound` removal from `WorldLlmTool`; clarified into a future-work item rather than an implicit behavior expectation.
- Internal-composition tests still using reflection by design (decorator `_inner` unwrapping, private UI lifecycle methods, DI `Configure`): left intentionally — adding production seams there would over-expose internals. Revisit case-by-case if those classes gain public observability.

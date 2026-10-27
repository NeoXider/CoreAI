# Rbx material acquisition teardown repair (PR-RBX-001)

Status: root runtime repair reviewed; focused regressions authored. Compilation and execution are unrun by this worker and remain root-owned.

## Goal and scope

The goal is to prevent a supported injected material provider from publishing a returning lease after its acquisition callback retires the host or destroys the current binding. Correctness requires immediate release balance, no duplicate release, idempotent cleanup and continued correct active replacement. No provider disposal, new material cache or runtime API is required.

This worker may write only `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs` and this report for the Rbx task. Runtime source is read-only. The separate console regression/report has its own lease. Root owns the runtime implementation, execution, shared trackers and Git.

Reuse decision: extend the existing `BoundWorld`, real binder, registry and material fixture. The added provider exercises the supported injected lease seam with distinct native `Material` handles; it does not reproduce binder or cache logic.

## Root runtime source review

- Read the finding and regression sequence in `07-rbx-api.md`, then the actual working-tree runtime diff and affected binder methods.
- `BindingEntry.Id` is initialized by `AddBinding`; `IsCurrentBinding` requires the same entry object under that id. Initial `OnEnteredWorld` calls `AddBinding` before its first `Apply`, so ordinary initial materialization remains a valid current binding.
- `ApplyAppearance` now rechecks `_hostTeardownStarted`, `IsCurrentBinding(entry)` and Unity renderer liveness immediately after the injected `TryAcquireMaterial` returns. The invalid path releases the returning handle and returns before renderer assignment, retained-lease publication or property-block writes.
- `BeginHostTeardown` marks retirement before releasing old leases. `DropBinding` removes the entry and releases its old lease. These callbacks can therefore invalidate the acquisition caller synchronously; checking again after the callback is necessary.
- The repair keeps the injected provider host-owned. It does not substitute provider disposal for matching releases. Existing assignment-failure and contained release-failure paths remain present.

## CHANGED

- Added `RBX003_ActiveAppearanceReplacementKeepsExactlyOneLease`: materialize through `BoundWorld`, assert one active lease, repaint, verify one additional acquisition, release/destruction of the prior handle, one live current renderer handle, and complete final cleanup.
- Added `RBX003_TeardownDuringAcquireReleasesReturningLeaseImmediately`: after ordinary initial materialization, the next acquisition synchronously calls `BeginHostTeardown`. Verify immediate zero active handles and exact acquisition/release balance, no published renderer material, no acquisition from a later appearance write, repeated teardown, explicit registry cleanup and final world disposal.
- Added `RBX003_DestroyDuringAcquireReleasesRemovedBindingsReturningLease`: the acquisition callback destroys the actual `RbxInstance`. Verify no escaped exception, destroyed/unbound state and immediate balanced cleanup, then repeat destruction/teardown and final cleanup. This covers a removed binding and destroyed renderer together; it does not independently isolate every condition in the runtime guard.
- Added one callback provider and one assertion helper. Every acquisition creates a distinct real material, releases are tracked by handle, unexpected releases are recorded, and assertions reject binder disposal of the injected provider. Provider disposal cleans up test-native objects on failure without counting them as successful releases.
- Preserved the existing throwing/reentrant-release regression and `FaultingLeaseProvider`, together with the texture, teardown and physics coverage.

## VERIFY OUTPUT

Static only. Read the complete actual runtime diff, both affected fixture diffs and the existing `BoundWorld`/registry joins. A read-only comparison against the current Git baseline reported:

```text
Preserved existing control: public void RBX003_ThrowingReleaseDoesNotPreventOtherLeaseCleanupOrRepeatIt()
Preserved existing control: private sealed class FaultingLeaseProvider
Explicit C# locals: no var declarations in either leased fixture
Present regression: RBX003_ActiveAppearanceReplacementKeepsExactlyOneLease
Present regression: RBX003_TeardownDuringAcquireReleasesReturningLeaseImmediately
Present regression: RBX003_DestroyDuringAcquireReleasesRemovedBindingsReturningLease
Source review only; no fixture compilation or execution performed
```

Regression sensitivity is source-derived: without the new guard, the teardown callback clears the old lease but the returning acquisition is retained, failing immediate balance. Destruction during acquisition previously reached a destroyed renderer assignment, so the no-throw assertion protects that path. The ordinary active replacement remains a positive control.

## NOT DONE

- No build, EditMode/PlayMode test, Unity import/MCP, live provider, network or extra agent was run by this worker. Root must execute `RbxMaterialLifetimeRegressionTests` and inspect logs/results before accepting the repair.
- No runtime file, `.meta`, asset, scene, prefab, setting, shared tracker, changelog or Git state was mutated by this worker.
- Native allocation/profiling, rendering, physics, Mono/IL2CPP, WebGL, device, performance and long-running world lifecycle behavior remain separate unrun gates. Test material destruction in EditMode is not measured production memory reclamation.

## CONCERNS

- No additional source-confirmed defect was found in the reviewed repair. The current regressions exercise synchronous teardown and instance destruction from acquisition; arbitrary nested appearance mutation, asynchronous provider behavior and thread safety are outside their claims.
- The destruction test invalidates both the entry and renderer. It does not prove `IsCurrentBinding` independently from renderer-liveness checking; the active control does exercise initialized entry identity.

## QUESTIONS

None. Root owns execution and can request a targeted fixture correction if its actual Unity run reveals one.

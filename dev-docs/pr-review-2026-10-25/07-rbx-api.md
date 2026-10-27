# Group 07: Rbx API, material lifetimes and HTTP world

Status: in progress; scope recorded before source review.

- Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (branch `demo`).
- Primary scope: exactly the 28 paths assigned to group `07` in `coverage.json` and the review request.
- Method: independent static review of every assigned diff hunk, candidate code and necessary adjacent contracts/callers. Historical reports are evidence to inspect, not accepted findings.
- Constraints: report-only write; no fixes, builds, tests, Unity/MCP, network/model calls, additional agents or Git mutations.
- Review invariants: clone ownership/ACL/state; hit filtering and units; material leases/cleanup; HTTP/world disposal; bounded serialization/tweens; signal teardown/permissions; runtime-first replaceable composition and WebGL compatibility.

Findings and a per-path/hunk coverage ledger will be appended as review progresses. No runtime proof is claimed.

## Native recovery review

Native takeover status: complete static source review; root adjudication and execution remain pending.

The preceding CLI launch left only the scope header above; it supplied no accepted source coverage. Native reviewer `/root/pr_providers` (`gpt-6.1-sol`, `max` reasoning) takes over the original immutable group 07 audit. The header is preserved as recovery provenance, not acceptance. Review starts from the actual 28-path diff. Other reports, staged regression artifacts, runtime source and existing tests remain frozen and read-only.

Before planning: the goal is to identify introduced behavioral defects or incorrect repairs. Ownership, lifetime/cancellation, authority, unit conversion, deterministic filtering, material leases and bounds matter; no new host login/session framework or topology requirement is imposed. Before acceptance, each finding needs a changed-line cause, valid scenario, expected/actual behavior and meaningful regression plan. Static read coverage and unrun platform evidence will remain separate.

## Findings

Source review complete: **one source-confirmed P2 finding** in the assigned immutable diff. This is not a runtime or package acceptance claim.

### PR-RBX-001 [P2] Refuse a material acquisition that returns after host teardown

- Changed anchor: `Assets/CoreAIMods/Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs:1772`, followed by the new lease publication at `:1783-1787`.
- Valid scenario: compose the supported injected `IRbxMaterialLeaseProvider<Material>` with an acquisition/loading callback. A part already owns one lease. On its next appearance update, the provider acquires the replacement and synchronously notifies the host; the host begins retiring the world by calling `binder.BeginHostTeardown()` from that callback. The provider then returns its valid acquired handle. This requires no thread, malformed material, account change or exception. Injected providers remain owned by their host, so the binder does not dispose that provider as a substitute for releasing its leases.
- Expected: once teardown begins, every old or returning acquisition owned by the binder must be released once; returning from the provider must not publish a new retained lease into the retired binder. Repeating teardown must leave no active binder lease.
- Actual causal sequence: `ApplyAppearance` checks `_hostTeardownStarted` only before calling replaceable code. During `TryAcquireMaterial`, `BeginHostTeardown` marks the flag at line 204, clears/releases the part's prior `LeasedMaterial` at lines 205-207, and finishes. On return, `ApplyAppearance` assigns the replacement to the renderer and stores it in `entry.LeasedMaterial` at line 1784 without checking retirement again. Its `previous` value is now null, so the returning acquisition remains pinned. A second `BeginHostTeardown` returns immediately at lines 199-201. The host has completed its consumer-release step while the binder has acquired a new live native-material reference after that step.
- Introduced cause: the PR adds per-entry retained leases, acquire/release callbacks and idempotent teardown. Its pre-call retirement check cannot protect the post-callback lease publication. Eventual registry destruction might release the new lease, but `BeginHostTeardown` itself no longer guarantees its new cleanup contract; a staged/deferred host retirement can retain it after consumers were supposed to stop.
- Minimal meaningful regression for root: extend the existing real-Unity `RbxMaterialLifetimeRegressionTests.BoundWorld`/lease-provider pattern with an `OnAcquire` callback, rather than a toy cache. Materialize a part normally and assert one active lease. Set the callback to `world.Binder.BeginHostTeardown`, then call `SetColor` or `SetMaterialVariant` on that already bound part. Assert all acquired leases have matching releases immediately after the update, after repeated teardown, and after final world cleanup. The candidate yields two acquisitions and one release immediately after the reentrant update. Preserve the current throwing-release/reentrant-release cases and ensure a normal active appearance update still retains exactly one replacement lease. The fix must recheck retirement after the replaceable acquisition before retaining/publishing its handle.
- Uncertainty: source-control-flow proof only; no Unity fixture was run. The trigger is an injected provider that synchronously calls a supported host retirement operation from its acquisition callback. The stock provider has no such callback, so an ordinary stock-provider appearance update is not claimed to trigger this case. No measured native allocation or permanent device exhaustion is claimed.

## Acceptance review

- **Clone and ownership:** read complete `CreateCloneCopy`, custom-state hooks, Lua clone attribution and registry owner/access resolution. The new C# default clone passes source actor/access explicitly; the ownership-override path still resolves caller provenance and Lua recursively applies caller ACL. Click distance is a copied double; variant fields are copied value data/immutable strings. Signal handlers are not copied. The new C# tests cover all three ACL scopes, and Lua hierarchy assertions cover silent state and independent source connections.
- **Units and filtering:** read the complete real-port raycast and the space conversion. The changed vector is a displacement converted once; its normalized value and metre magnitude reach Physics together. The nearest eligible registered part still wins after trigger/collision/eligibility filtering. Integer interpolation promotes before subtraction, preserves signed-offset endpoints, retains nearest-even rounding, and rejects nonrepresentable offsets with the datatype-layer exception. Existing extreme-float scale arithmetic is separate from this offset repair.
- **Material lifetime:** read complete replacement, release, source retirement, eviction, disposal and affected binder paths. Mutable records are provider-local; acquired records survive idle eviction and source replacement. Replacement acquisition precedes prior release. Drop/teardown release callbacks are idempotent and their cleanup exceptions are contained before later entries are processed; acquisition-callback retirement is the separate PR-RBX-001 gap. The binder disposes only its own default provider. Host-adopted renderers do not acquire these leases: the existing `Store`/`ApplyToHostOwnedObject` branch applies only pose writes and leaves their material/mesh/collider untouched. That guard rules out a suspected adopted-renderer cleanup regression.
- **HTTP/world lifetime:** read the complete new pending-request, cancellation-await, completion/removal/drain and disposal paths. Admission retains a linked token for the actual current owned coroutine. World disposal detaches the phase callback, clears admission state, cancels requests and observes late operation failures without waiting for uncooperative resolver/transport tasks. Cancellation is rechecked before send and before queued delivery. Thread completion and Kill cancel their exact token, including pooled-handler retirement. The public stack option forwards the outstanding-request bound into this adapter; it is not a hidden fixed-only limit.
- **Runtime participants:** reference-identity weak registration and the locked owned list distinguish equal-valued participants, mark disposal before callbacks, contain participant/logger faults, and dispose late registrations immediately. Read the new real-Lua tests for main chunk, nested coroutine, spawned task, separate world and pooled signal iterations, along with their actual helper implementations.
- **Tween admission:** read method dispatch, `ApplyServerGeneratedMutation`, explicit `RequireMutationTarget`, Lua create/control methods, registry admission and existing actor/capability guards. Marking Create/Play/Pause/Cancel mutating opens the ordinary generated frame; it does not demand ownership of the protected TweenService singleton. The explicit target guard names the tweened instance before creation/control. Targeted adjacent real-executor tests cover mismatched targets, no extra tween/state change, correct target and replay; prior WorldEdit and cross-actor controls remain present.
- **Remote signals:** the server event now uses the base-owned signal map. The custom teardown hook clears every actor signal, including unused actor slots, and runs once through base destruction. `DisconnectAll` preserves already queued calls; the new C#/Lua tests check both remote classes, ancestor/direct/repeated destruction, reentry/fault delivery and connection-ledger retention.
- **Bounded wire encoding:** read complete changed encoder methods, graph bookkeeping and stream helpers. UTF-8 writes enforce the existing wire ceiling including the JSON envelope. Strings are bounded before escaping, and array ordering/contiguity/nil rejection is retained. Stringified dictionary collisions retain first-key position and last value while validating overwritten graphs through a non-retaining writer. Depth, aggregate-entry and cycle checks still run. The wrapping Stream avoids MemoryStream's specialized write bypass. No rejected byte array is returned to the bridge.
- **Tests:** every assigned changed assertion and fake was inspected. New engine-free and real-Unity files were read in full. Cleanup was added to existing material tests without deleting their behavior assertions. The two path assertions now use the production store-id mapping, with one strengthened from suffix matching to exact equality. No changed assertion was found that simply suppresses an expected behavioral failure. Source-string shader checks and allocation thresholds remain their own limited evidence rather than real rendering/performance proof.

## Read coverage ledger

All 28 assigned paths and all **163** zero-context changed hunks were read, including deletions: **+2,220/-201**. New runtime helpers and the two complete new regression fixtures were read in full; existing source received all changed hunks plus full affected methods and relevant callers. Counts use `git diff --unified=0`; they measure diff reading, not semantic/execution coverage.

| Exact primary path | Hunks | + / - | Read depth |
| --- | ---: | ---: | --- |
| `Assets/CoreAIMods/Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs` | 8 | 100 / 3 | All changes; full lease replacement/release/teardown, Store/adoption/shape/appearance, variant source and world joins; PR-RBX-001. |
| `Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxPhysicsPort.cs` | 2 | 2 / 3 | All changes and complete raycast/filter/result method. |
| `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/IRbxMaterialProvider.cs` | 1 | 10 / 0 | Entire new public lease seam and existing provider/variant contracts. |
| `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxUDim.cs` | 3 | 17 / 3 | Full affected Lerp/helper, value fields and constructors. |
| `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs` | 3 | 14 / 1 | Complete affected remote-event class, base signal join and client cleanup. |
| `Assets/CoreAIMods/Runtime/RbxApi/Instances/README.md` | 3 | 68 / 1 | Every changed contract read against implementation. |
| `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxClickDetector.cs` | 1 | 6 / 0 | Complete custom-state hook and source property. |
| `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs` | 4 | 13 / 6 | All changes; full clone-copy/ownership and signal teardown methods with clone/destroy callers. |
| `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxMaterialVariant.cs` | 1 | 12 / 0 | Complete custom-state hook, map/base/tile data and change behavior. |
| `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxTextureMaterialProvider.cs` | 26 | 182 / 45 | All changes; complete affected lookup/cache/source/lease/eviction/disposal/scale methods and construction/error helpers. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs` | 9 | 74 / 5 | All changes; full current-token, registration, participant containment and Dispose methods with scheduler joins. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxHttpServiceAdapter.cs` | 33 | 186 / 35 | All changes; full new pending state, constructor, prepare/admission, ResolveAndSend, await/late observation, Complete/Remove/drain and Dispose methods. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs` | 7 | 13 / 6 | All changes; full tween methods and dispatcher/target/envelope/ACL helpers. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs` | 30 | 207 / 85 | All changes; complete encode traversal/table/scalar/userdata joins, graph validation and bounded stream implementation. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs` | 4 | 19 / 0 | All changes; full CaptureChunk, lifetime property/cancel, Resume/finally and Kill with constructor/status/runner ownership. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Binding/RbxWorldHostDiWiringEditModeTests.cs` | 9 | 76 / 3 | Every changed assertion and strict-store fake; related retained facade/transaction context sampled. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests.cs` | 1 | 24 / 0 | Entire new Lua interpolation regression and surrounding constructor/rounding context. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxJsonContractEditModeTests.cs` | 1 | 87 / 0 | Entire new allocation/escaping/UTF-8/dictionary/cycle regression block and codec helper context. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxMaterialVariantLuaBindingsEditModeTests.cs` | 1 | 34 / 0 | Entire new real-Lua hierarchy clone regression. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxSignalConnectionTeardownEditModeTests.cs` | 4 | 147 / 0 | Every new test, counter/faulting participant and callback hook; actual wired-stack setup sampled. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/RbxContractRegressionTests.cs` | 1 | 197 / 0 | Entire new fixture, assertions and thread-factory helper. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/RbxContractRegressionTests.cs.meta` | 1 | 2 / 0 | Entire metadata; GUID search found only this companion. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs` | 1 | 344 / 0 | Entire new real-Unity material/physics fixture and every fake/helper/cleanup path. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs.meta` | 1 | 2 / 0 | Entire metadata; GUID search found only this companion. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests.cs` | 5 | 5 / 4 | Every disposal edit with retained lookup/render behavior assertions. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxProceduralMaterialProviderEditModeTests.cs` | 1 | 2 / 1 | Complete changed source-string assertions; shader implementation remains its assigned owner. |
| `dev-docs/audits/2026-10-23/06-mods-rbx-api.md` | 1 | 250 / 0 | Entire historical audit, including boundaries/old evidence/commands; no old result accepted as fresh proof. |
| `dev-docs/fix-wave-2026-10-24/workdoc06-rbx.md` | 1 | 127 / 0 | Entire repair contract/checkpoint/handoff; Done/green statements inspected as claims only. |

Adjacent reads were targeted, not full ownership audits: registry create/attribution/ACL/generated mutation frames, RbxSpace conversions, base signal construction, material application helpers and native destruction, RbxWorldHost/state retirement, gameplay lifetime/HTTP option composition, trusted iterator resolver installation, Mods HTTP retirement/capacity regressions, and real-executor tween/codec acceptance cases. Unchanged large fixture tails, complete scheduler/VM/ACL/replication implementations, native physics internals, transport implementations, shader/render math and all unassigned files are not claimed exhaustively reviewed.

## Unconfirmed hypotheses and missing validation

- A custom lease provider whose release callback also mutates appearance was not exercised. The counted PR-RBX-001 instead uses acquisition-callback retirement with an exact two-acquire/one-release causal sequence; more general arbitrary host callback behavior is not promoted to extra findings.
- Acquired handles survive provider-source replacement; borrowed handles are explicitly documented as evictable. A host sharing one provider between independently configured worlds contradicts the new documented composition contract and is not counted as a defect.
- C# code can retain raw signal objects after instance destruction; the preexisting general signal API does not seal such objects against later host-created subscriptions. No newly introduced network/Lua delivery bypass was established; Lua tombstone and incoming registry checks remain distinct guards.
- The new HTTP tests prove source-level cancellation intent with deterministic fake resolver/transport tasks, not actual DNS/network cancellation, browser scheduling, device behavior or cross-process shutdown. Exact phase ordering and late callbacks still require execution.
- Native material reclamation is deferred in Play Mode by Unity's Destroy behavior. New immediate-null assertions are EditMode cases. No actual native allocation, render, physics, player/AOT/IL2CPP/WebGL, load or network pass is claimed.

## Completion accounting

- CHANGED: this report only. The CLI scope header is preserved; reports 03/15, staged fixtures, runtime, existing tests, metadata and shared tracking files were not changed by this task.
- VERIFY OUTPUT: immutable group inventory returned `paths=28 hunks=163 added=2220 deleted=201`; every path/hunk is accounted for above. One source-confirmed P2 finding, zero P1/P3 findings. Direct report read check: `rows=28 missing=0 extra=0 findings=1 trailing=0 Cyrillic=0`. The final SHA is still `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`; comparison of every assigned primary path against it exited 0 without differences. These are source/document checks only.
- NOT DONE: fixes, builds/tests, Unity/MCP, actual physics/material rendering, native allocation/profiling, player/Mono/IL2CPP/WebGL/device, DNS/HTTP/network, fault/load/performance or model execution. Root owns adjudication, proof, implementation, Git and publication.
- CONCERNS: returning material acquisition can republish a lease after reentrant host teardown (PR-RBX-001). Platform behavior and the explicitly uncounted hypotheses above remain unverified; no accepted source bug is hidden behind a green test aggregate.
- QUESTIONS: none block the original source-review handoff.

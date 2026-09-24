# Portable Lua-tier tests

Runs the Lua-tier EditMode fixtures of `Assets/CoreAIMods/Tests/EditMode` on Linux, macOS or Windows with plain .NET: the real Lua-CSharp VM (`Assets/CoreAIMods/Plugins/Lua.dll`), the real Lua bindings, the mod runtime, the scheduler adapters, the one-off `execute_lua` executor, the world-package store and the MVP acceptance fixtures that do not need a scene. No Unity install, licence or editor is involved.

It complements, and does not overlap, the [engine-free portable suite](../Tests/README.md): no fixture that suite already runs is linked here.

## Run it

From the repository root, with the .NET 8 SDK (or newer) and NuGet access for the first restore:

```sh
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --artifacts-path artifacts/portable-lua
```

`--artifacts-path` keeps this build's `obj/` and `bin/` apart from the other portable projects, which share `tools/portable/CoreAI.Core.csproj` and the Mods engine-free projects. Tests that read repository files find the checkout the same way the engine-free suite does (`../Tests/RepositoryTestSetup.cs` is linked unchanged), so the command works from any working directory.

Reading a result:

| Outcome | Meaning |
|---|---|
| Passed | The test ran its real code path and every assertion held. |
| Failed | A real assertion failed, or the Unity Test Framework log rule failed it (below). Triage it: it is either a runtime/test bug Unity would hit too, or a .NET-versus-Mono difference. |
| Skipped / NotExecuted with `PORTABLE_ENGINE_UNAVAILABLE` | The test reached an engine member the shim refuses. Its portable outcome is not evidence either way; only Unity decides it. |
| Skipped for another reason | The fixture skipped itself (for example a live-model check, or a demo assembly that is absent). |

## Design

### One project per Unity assembly, under the Unity name

| Project | Unity asmdef | What is compiled |
|---|---|---|
| `../LuaTier/UnityEngine` | UnityEngine (engine) | the shim described below |
| `../LuaTier/CoreAI.Source` | `CoreAI.Source` | the game-logger contract and console sink, `UnityLog`, persistent-path constants, `CoreAiWebGlPersistence`, world-command envelopes and executor interfaces, the prefab-registry interface, the chat-example prompts (pure data); plus `CoreServicesInstaller.Portable.cs` |
| `../LuaTier/CoreAI.RbxApi.Unity` | `CoreAI.RbxApi.Unity` | `RbxSpace.cs` and the assembly's `InternalsVisibleTo` file |
| `../LuaTier/CoreAI.RbxApi.Binding` | `CoreAI.RbxApi.Binding` | the part-property, camera and click-pick seams with their in-memory implementations, `IRbxCharacterMotorProvider`, `WorldQuerySceneWalker`, `UnityRbxCharacterMotor` |
| `../LuaTier/CoreAI.Mods` | `CoreAI.Mods` | `Scripting/**`, `LuaExecution/**`, `LuaAssets/**`, `Logging/**`, `Infrastructure/**`, `WorldBindings/**` (exclusions below) |
| `CoreAI.Portable.LuaTests.csproj` | `CoreAI.Mods.Tests` | the linked fixtures |
| `../CoreAI.Core.csproj`, `../CoreAI.RbxApi.Datatypes`, `../CoreAI.RbxApi.Instances`, `../CoreAI.LuauDownlevel` | same names | reused unchanged from the engine-free suite |

WHY the Unity names and boundaries: `InternalsVisibleTo` grants are by assembly name (`CoreAI.Mods`, `CoreAI.Source`, `CoreAI.Core`, `CoreAI.RbxApi.Instances` and `CoreAI.RbxApi.Unity` all trust `CoreAI.Mods.Tests`; `CoreAI.Core` trusts `CoreAI.Source`; `CoreAI.Source` and `CoreAI.RbxApi.Instances` trust `CoreAI.Mods`). Merging assemblies would let code reach internals it cannot reach in the editor, and a portable pass would then prove less. Every runtime project compiles as C# 9 with nullable off against .NET Standard 2.1, like Unity, with the project-wide scripting defines `COREAI_LLM` and `COREAI_LUA` (`../LuaTier/LuaTier.props`). `UNITY_EDITOR`, `UNITY_WEBGL` and `COREAI_HAS_HUB` are not defined: no linked runtime file branches on `UNITY_EDITOR`, the WebGL branches are a separate target, and the Hub package is not part of this build. The test project is `net8.0`, C# 9, NUnit 3.14 with the same test packages as the engine-free suite.

WHY globs in `CoreAI.Mods.csproj`: a new engine-free runtime file is compiled without anyone editing this project; a new file that needs the engine breaks this build loudly and must be excluded there, with its reason added to the table at the end of this page.

Dependencies are the ones the Unity build uses: `Lua.dll` from `Assets/CoreAIMods/Plugins`, `Microsoft.Bcl.TimeProvider` 8.0.0 (pinned in `Assets/packages.config`, required by `Lua.dll`), and the NuGet build of the same UniTask library the editor gets from git (`UniTask` 2.5.10). The .NET build of UniTask has everything the Lua tier uses except the player-loop overload `UniTask.Yield(PlayerLoopTiming, CancellationToken)`; see derived sources below. Nothing emulates a player loop.

### The UnityEngine shim

The shim holds three kinds of members, and nothing else. A runtime or test file that needs more does not compile and is excluded.

1. **Managed twins**, with the bodies of Unity's own managed implementation:
   - `Debug.Log*`/`LogException`: Unity's message formatting (null prints as `Null`, `IFormattable` uses the invariant culture, exceptions as `TypeName: message`), written to the console and to the per-test log scope below. `context` arguments are ignored.
   - `Mathf`: `PI`, `Infinity`, `Epsilon` (the x86/x64 value), `Deg2Rad`, `Rad2Deg`, `Min`, `Max`, `Abs`, `Sqrt`, `Acos`, `Sign`, `Clamp`, `Clamp01`, `Approximately`.
   - `Vector3` (fields, constructors, `zero`/`one`/`down`/`right`/`forward`, Unity's approximate `==`, `magnitude`, `sqrMagnitude`, `normalized`, `Dot`, `Cross`, `-`, `* float`, `/ float`), `Vector2`, `Vector4`, `Quaternion` (fields, `identity`, Unity's `==`, `Dot`, `Angle`, quaternion-times-vector), `Color`, `Color32` (with Unity's rounding conversion from `Color`), `Rect`, `Bounds`. Only these operations exist; for example there is no quaternion product or `Lerp`.
   - `Time.realtimeSinceStartup`/`realtimeSinceStartupAsDouble`: seconds since process start, monotonic (Unity counts from player or editor start, so uptimes are never close to zero). `Time.timeScale` (1) and `Time.fixedDeltaTime` (0.02, `ProjectSettings/TimeManager.asset`) are settable fields; the engine's out-of-range check on `timeScale` is not reproduced (the only caller clamps first).
   - `Application.persistentDataPath`: a per-process folder under the system temp directory, created on first read and deleted after the run, never the user's real Unity folder. `Application.dataPath`: the checkout's `Assets` folder. `isPlaying` is false and `isEditor` is true (an EditMode run); `platform` is `LinuxEditor`.
   - Unity's numeric values for the enums the Lua tier names (`LogType`, `RuntimePlatform`, `PrimitiveType`, `QueryTriggerInteraction`, a subset of `KeyCode`), and `Physics.DefaultRaycastLayers`.
2. **Inert metadata**: `SerializeField`, `HideInInspector`, `Tooltip`, `Header`, `Min`, `Range`, `TextArea`, `CreateAssetMenu`, `RuntimeInitializeOnLoadMethod` (never invoked, as in an EditMode run).
3. **Refusing surfaces**: types that must exist for a required file to compile, whose every member throws `PortableEngineUnavailableException` (`PORTABLE_ENGINE_UNAVAILABLE: UnityEngine.<member> needs the Unity engine ...`): `Object` lifetime and naming members, `GameObject`, `Component`, `Behaviour`, `MonoBehaviour`, `Transform`, `Collider`, `Rigidbody`, `ScriptableObject`, `TextAsset`, `RaycastHit`, `Physics`, `Resources`, `JsonUtility` (native serializer; its exact output is not reproduced), `Input`, `LayerMask`, `EntityId`, `SceneManagement.Scene`/`SceneManager`, `ColorUtility.TryParseHtmlString`, `Quaternion.Euler` (native), and the frame-loop values `Time.time`, `deltaTime`, `unscaledDeltaTime`, `frameCount`. No engine object can ever be constructed, so there is no fake-null to reproduce.

WHY refusing surfaces exist at all: the production mod stack (`LuaCsModRuntimeFactory` → `LuaCsGameplayBindings`) always constructs the classic `coreai_*` world, component, query, input and Full-tier bindings, which name scene types. Without those types nothing that builds the stack would compile, which is almost every acceptance fixture. Refusing, instead of emulating, keeps every answer the suite gives an answer the real code gave.

### Two rules applied around every test

`PortableUnityTestScope.cs` is an assembly-level NUnit action:

- **The Unity Test Framework log rule.** An `Error`, `Assert` or `Exception` log that no `LogAssert.Expect` consumed fails the test; an expectation that never matched fails it too. Expectations match in order against the test's log stream, by exact message or regex, optionally by type; `LogAssert.ignoreFailingMessages` and `LogAssert.NoUnexpectedReceived` behave as in Unity. The scope covers SetUp, the test body and TearDown and is evaluated after TearDown; where Unity draws the scope boundary around SetUp and TearDown may differ.
- **The refusal rule.** A test that reached any refused member is reported Inconclusive with the refused members listed and its portable outcome appended, whatever that outcome was. A pass may come from code that caught the refusal and carried on; a failure is usually a missing side effect downstream of it. Neither says anything about Unity.

### `Is.Not.AllocatingGCMemory()`

`UnityTestToolsConstraints.cs` is a twin of the Unity Test Framework's `UnityEngine.TestTools.Constraints`: `Is` (NUnit's `Is` plus `AllocatingGCMemory()`), the `ConstraintExpression` extension that makes `Is.Not.AllocatingGCMemory()` chain, and `AllocatingGCMemoryConstraint`. It lives in the test project because it needs NUnit.

- **What matches Unity.** The delegate runs exactly once, with no warm-up, and only allocations on the calling thread count. The constraint succeeds when anything was allocated, so `Is.Not.AllocatingGCMemory()` passes only for an allocation-free delegate.
- **What differs.** Unity counts allocation events through the `GC.Alloc` profiler recorder. The twin measures bytes with `GC.GetAllocatedBytesForCurrentThread()`, which is exact on .NET 8, so "allocated anything" means the same thing; only the failure message reports bytes instead of a count.
- **Why no warm-up.** Unity does not warm up. A delegate that allocates only on its first call (lazy initialisation, a cached lambda, a static constructor) fails in Unity and must fail here; a warm-up would turn exactly those failures into portable passes.
- **Why it cannot pass silently.** Before every measurement the twin allocates a known probe and checks that the counter saw it. If the counter does not move, or the runtime does not support it (Mono's returns 0), the assertion is Inconclusive, never a pass.
- **Limits.** CoreCLR's base library avoids some allocations Mono's still makes, for example boxing in some generic comparers and enumerators. So a portable pass does not prove a Unity pass when the delegate reaches such paths. A first call can also allocate .NET runtime bookkeeping that Mono does not; that can only cause a failure, never a pass. The project targets `net8.0`, whose JIT never stack-allocates objects. Running it on a newer runtime with object stack allocation (for example with `DOTNET_ROLL_FORWARD=Major`) could hide allocations that Unity makes.

### Derived sources

Three files are compiled from copies generated into `obj/` by `../LuaTier/PortableDerivedSources.targets`; the files under `Assets/` are never touched. Each copy keeps every line in place and starts with a `#line` directive, so diagnostics and stack traces point at the real file. Each operation fails the build unless its anchor matches exactly as expected, so a rename or edit in the source is loud.

| File | Operation | WHY |
|---|---|---|
| `Runtime/Infrastructure/RbxWorldPackageContracts.cs` | blank the top-level type `RbxWorldSessionHostAdapter` | The file holds the world-package contracts, the session controller and the headless session host the stack needs, plus this one adapter around the scene `RbxWorldHost` MonoBehaviour and the GameObject binder. |
| `Runtime/Infrastructure/FileRbxWorldPackageStore.cs` | replace the 5 calls `UniTask.Yield(PlayerLoopTiming.Update, cancellationToken)` with `PortablePlayerLoopRefusal.Yield(cancellationToken)` | The overload exists only in the Unity build of UniTask. The store is required by the runtime, the one-off executor and the session controller. The replacement throws `PortableEngineUnavailableException`; a test that reaches it (large chunked I/O, every file-store load) becomes Inconclusive. |
| `Tests/EditMode/RbxApi/Acceptance/Mvp1AcceptanceHarness.cs` | blank the top-level type `Mvp1AcceptanceWorld` | The harness file holds the GameObject world next to two engine-free helpers that the world-package and physics-port fixtures use on their own. Fixtures that need the world itself stay excluded. No test is ever sliced: a fixture file is linked whole or not at all. |

### `CoreServicesInstaller.Portable.cs`

The real `CoreServicesInstaller` is a VContainer/MessagePipe installer, but the Lua bindings and fixtures read its `DefaultLocalHostIdentityProvider`. The twin holds only that member, with the identical initializer. `ActorIdentityComposition.CreateLocalHost` (CoreAI.Core) issues the provider only to a private nested type of `CoreAI.Composition.CoreServicesInstaller` in an assembly named `CoreAI.Source`, so the twin passes exactly the proof the editor build passes. If the real initializer changes, the twin must change with it.

## Coverage

Linked from `Assets/CoreAIMods/Tests/EditMode` (75 fixture files, 4 helpers, 1 derived helper), plus `Assets/CoreAiUnity/Tests/EditMode/LuaModAutoRepairPolicyEditModeTests.cs` (a Lua-tier policy tested through public API only). The explicit list is in the project file. 36 files of the same folder already run in the engine-free suite and are not linked again.

A linked fixture holds only tests that run here. Where a fixture's subject is engine-free but a few of its tests drive that subject through the engine, those tests live in a Unity-only fixture class next to the engine code they need, so the rest of the fixture is linked whole:

| Linked fixture | Its engine-bound tests live in |
|---|---|
| `RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests` | `RbxApiLuaBindingsProductionContainerEditModeTests` in `CoreAiModsLifetimeScopeNoNetworkBridgeEditModeTests.cs` (the production VContainer composition: `execute_lua` and the Lua network path); `InstanceGameObjectBinderCrossLayerEditModeTests` in `RbxApi/Binding/InstanceGameObjectBinderEditModeTests.cs` (the Lua position golden through the binder) |
| `RbxApi/Acceptance/Mvp1ConversionLintEditModeTests`, `RbxApi/Instances/R6_5_CloneEditModeTests` | `InstanceGameObjectBinderCrossLayerEditModeTests` (the binder's output against RbxSpace, Clone through the real binder) |
| `RbxApi/Instances/InstanceRegistryEditModeTests`, `RbxApi/Datatypes/RbxSpaceGoldenFixtureEditModeTests` | `RbxWorldHostLazyWorldWrapEditModeTests` in `RbxApi/Binding/RbxWorldHostEditModeTests.cs` |
| `RbxApi/Acceptance/Mvp2StanceConformanceEditModeTests` | `UnityRbxCharacterMotorLifecycleEditModeTests` in `RbxApi/Binding/UnityRbxCharacterMotorJumpGravityEditModeTests.cs` |

A new test that needs the engine goes into such a class, not into a linked fixture: there it would break this build, or report Inconclusive on every run.

Excluded, by what they need (31 files):

| Needs | Files |
|---|---|
| VContainer composition (`CoreAiModsLifetimeScope`, `ContainerBuilder`, `RegisterCoreAiMods`) | `CoreAiModsLifetimeScopeNoNetworkBridgeEditModeTests`, `LuaCsEventRoutingProductionPathEditModeTests`, `LuaModsLlmToolEditModeTests`, `MultiplayerFoundationDemoScenarioEditModeTests`, `RbxApi/Binding/RbxCharacterMotorProviderEditModeTests`, `RbxApi/Binding/RbxWorldHostDiWiringEditModeTests` |
| GameObject binder, `RbxWorldHost`, camera rig or physics simulation | `AdoptWorldObjectScaleEditModeTests`, `WorldBindingsStudUnitsEditModeTests`, `WorldQuerySceneWalkerEditModeTests`, `RbxApi/Binding/UnityRbxCharacterMotorJumpGravityEditModeTests` (these four compile, but every test reaches a refused engine member), `RbxApi/Acceptance/Mvp1AcceptanceGateEditModeTests` (its SetUp builds `Mvp1AcceptanceWorld`, a real GameObject world, for every test), `RbxApi/Acceptance/Mvp1GoldenTreeFixtureEditModeTests` (7 of its 8 tests build `Mvp1AcceptanceWorld`), `RbxApi/Binding/InstanceGameObjectBinderEditModeTests`, `RbxApi/Binding/PartShapeMaterializationEditModeTests`, `RbxApi/Binding/RbxWorldHostEditModeTests`, `RbxApi/LuaBindings/RbxCameraLuaBindingsEditModeTests` |
| Materials, shaders, textures (`CoreAI.Mods.Rbx.Rendering`) | `RbxApi/Acceptance/RbxMaterialCatalogQaEditModeTests`, `RbxApi/Unity/RbxMaterialShowcaseRigEditModeTests`, `RbxApi/Unity/RbxMaterialTextureCatalogEditModeTests`, `RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests`, `RbxApi/Unity/RbxProceduralMaterialProviderEditModeTests` |
| `UnityEditor` or `CoreAI.Editor` | `ResourcesBundledModSourceEditModeTests`, `RbxApi/Acceptance/RbxTextureMaterialsAcceptanceEditModeTests`, `RbxApi/Unity/RbxCc0TextureSetsEditModeTests`, `RbxApi/Unity/RbxMaterialSurfaceProfilesEditModeTests`, `RbxApi/Unity/RbxMaterialTextureCatalogQaEditModeTests` |
| `[UnityTest]` coroutines and the VContainer installer | `RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests`: 18 `[UnityTest]` tests wrap their bodies in `UniTask.ToCoroutine`, which only the Unity build of UniTask has, and 2 `[Test]` tests call `RbxWorldStartupSequence`, an internal class of `Composition/CoreAiModsInstaller.cs`. With those 20 left out, the other 65 test cases of the file compile and pass here, so moving the 20 into a Unity-only file is what would link it. |
| CoreAI.Source host types outside the engine-free slice (`CoreAISettingsAsset`, the VContainer-built G10 composition) | `LuaModsLlmToolSharingEditModeTests`, `G10CancellationClassificationEditModeTests` |
| The Hub package (whole file inside `#if COREAI_HAS_HUB`) | `CoreAiModsHubBinderFullTierEditModeTests`, `LuaSyntaxHighlighterEditModeTests` |

WHY no `[UnityTest]` twin: Unity steps the enumerator once per editor update and runs the player loop in between, so a twin here would have to emulate that loop, which this suite never does; and `UniTask.ToCoroutine` would still be missing without rewriting the test file.

Runtime files left out of `CoreAI.Mods`: `Composition/**` and `Presentation/**` (VContainer), `Diagnostics/G10/**` (VContainer and the CoreAI.Source LLM stack), `HubIntegration/**` (its own asmdef, Hub package), `Scripting/PlayerLoopScriptFrameYielder.cs` (player loop), `Scripting/LuaCs/LuaCsCoroutineRunner.cs` and `Infrastructure/WorldRestoreGate.cs` (MonoBehaviours nothing linked needs). Left out of `CoreAI.RbxApi.Binding` and `CoreAI.RbxApi.Unity`: the GameObject binder, `RbxWorldHost`, the Unity camera, click-pick, input and physics adapters, and the material and texture providers.

## Known differences from a Unity run

- **Runtime.** CoreCLR, not Mono. `double.ToString()` is shortest-round-trip on .NET and 15 significant digits on Mono, and Lua-CSharp's `tostring(number)` uses it, so `tostring(0.1 + 0.2)` is `0.30000000000000004` here and `0.3` in Unity. Culture-sensitive string comparison uses ICU here (zero-width characters are ignorable). Exception stack traces are formatted differently and carry this checkout's absolute paths. The GC differs, so allocation-budget trips are not calibrated for Unity by a portable pass.
- **Threading and time.** No player loop and no `SynchronizationContext`: continuations of `async Task` tests resume on thread-pool threads here and on the main thread in Unity. Wall-clock budgets run on whatever the build machine gives them.
- **Culture.** The run uses the process culture. To reproduce an editor with a Russian system locale: `LANG=ru_RU.UTF-8 LC_ALL=ru_RU.UTF-8 dotnet test ...`.

## Keeping it working

- **A new fixture.** If it only needs what is compiled here, add a `<Compile>` line to the project. If it needs the engine, add it to the exclusion table above.
- **A runtime file breaks the build.** A new or edited runtime file under a globbed folder now needs an engine API. Prefer keeping the Lua tier engine-free. Otherwise exclude the file in `../LuaTier/CoreAI.Mods/CoreAI.Mods.csproj` and record it above. Extend the shim only with a managed twin of Unity's own implementation, or with a refusing member.
- **A derived-source anchor fails.** The type or call it names was renamed or moved. Update the `PortableSlice` item next to its WHY.

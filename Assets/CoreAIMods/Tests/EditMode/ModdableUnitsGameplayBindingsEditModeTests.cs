#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Demos;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Real Lua runtime and production-composition coverage for opt-in host gameplay APIs.</summary>
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ModdableUnitsGameplayBindingsEditModeTests
    {
        private readonly List<string> _temporaryDirectories = new List<string>();
        private readonly List<GameObject> _objects = new List<GameObject>();
        private SynchronizationContext _savedContext;
        private EventSystem _previousEventSystem;

        private static ActorContext Host => CoreServicesInstaller.DefaultLocalHostIdentityProvider
            .GetActorContext(BuiltInAgentRoleIds.Programmer);

        [SetUp]
        public void SetUp()
        {
            // WHY: synchronous Lua entry points must not wait on Unity's blocked synchronization context.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _previousEventSystem = EventSystem.current;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (GameObject instance in _objects)
                {
                    if (instance == null) continue;
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
                    {
                        if (renderer.sharedMaterial != null) Object.DestroyImmediate(renderer.sharedMaterial);
                    }
                    Object.DestroyImmediate(instance);
                }
                _objects.Clear();
                if (_previousEventSystem == null && EventSystem.current != null)
                {
                    Object.DestroyImmediate(EventSystem.current.gameObject);
                }
                foreach (string directory in _temporaryDirectories)
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                _temporaryDirectories.Clear();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(_savedContext);
            }
        }

        [Test]
        public void Installer_PreservesOriginalOptionalOverload_AndRequiresEveryNewOverloadArgument()
        {
            MethodInfo original = null;
            MethodInfo extended = null;
            foreach (MethodInfo method in typeof(CoreAiModsInstaller).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != nameof(CoreAiModsInstaller.RegisterCoreAiMods)) continue;
                if (method.GetParameters().Length == 11) original = method;
                if (method.GetParameters().Length == 12) extended = method;
            }
            Assert.IsNotNull(original, "The existing eleven-parameter public ABI must remain available.");
            Assert.IsNotNull(extended, "The opt-in overload must add one required trailing callback.");
            ParameterInfo[] oldParameters = original.GetParameters();
            ParameterInfo[] newParameters = extended.GetParameters();
            object[] defaults = { null, false, false, null, null, InstanceRegistry.CurrentWorldAclVersion,
                null, null, null, null };
            for (int index = 1; index < oldParameters.Length; index++)
            {
                Assert.IsTrue(oldParameters[index].IsOptional, oldParameters[index].Name);
                Assert.AreEqual(defaults[index - 1], oldParameters[index].DefaultValue, oldParameters[index].Name);
                Assert.AreEqual(oldParameters[index].ParameterType, newParameters[index].ParameterType);
                Assert.AreEqual(oldParameters[index].Name, newParameters[index].Name);
            }
            foreach (ParameterInfo parameter in newParameters)
            {
                Assert.IsFalse(parameter.IsOptional, parameter.Name);
            }
            Assert.AreEqual(typeof(Action<LuaCsApiRegistry, LuaCapabilities>), newParameters[11].ParameterType);
        }

        [Test]
        public void DefaultComposition_ExposesNoForgeFunctions_ToPersistentOrOneOffLua()
        {
            using IObjectResolver container = NewContainer(null);
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            runtime.LoadMod(Host, "no-forge", AssertNoForgeSource, persistToStore: false);
            Assert.IsTrue(runtime.IsLoaded(Host, "no-forge"));
            LuaTool.LuaResult result = container.Resolve<LuaTool.ILuaExecutor>()
                .ExecuteAsync(AssertNoForgeSource + " return 'absent'", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("absent", result.Output);
        }

        [Test]
        public void OptInProductionComposition_ReachesPersistentMods_OneOffLua_AndReplacementWorld()
        {
            RecordingForge forge = new RecordingForge();
            UnitForgeLuaBindings bindings = new UnitForgeLuaBindings(forge);
            using IObjectResolver container = NewContainer(bindings.RegisterGameplayApis);
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            LuaCsModRuntime original = container.Resolve<LuaCsModStack>().Runtime;
            runtime.LoadMod(Host, "initial-forge", ForgeSource("initial"), persistToStore: false);
            Assert.AreEqual(1, forge.Count("all"));
            LuaTool.ILuaExecutor executor = container.Resolve<LuaTool.ILuaExecutor>();
            AssertOneOffCount(executor, 1);

            IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
            RbxWorldPackagePayload captured = worlds.CaptureCurrent();
            RbxWorldPackagePayload replacement = new RbxWorldPackagePayload(
                captured.CapturedAtUtc, captured.Settings, captured.Tree, captured.Parts, captured.CameraCFrame,
                new[] { new RbxWorldModSource(new LuaModManifest
                    { Id = "replacement-forge", Active = true, Capabilities = LuaCapabilities.All.ToString() },
                    ForgeSource("replacement")) });
            RbxWorldLoadResult result = worlds.LoadConfirmedAsync(replacement).GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreNotSame(original, container.Resolve<LuaCsModStack>().Runtime);
            Assert.IsTrue(runtime.IsLoaded(Host, "replacement-forge"), "The retained facade must target the replacement.");
            Assert.AreEqual(2, forge.Count("all"));
            AssertOneOffCount(executor, 2);
        }

        [TestCase(LuaCapabilities.All, LuaCapabilities.Read, true, false)]
        [TestCase(LuaCapabilities.Read, LuaCapabilities.All, true, false)]
        [TestCase(LuaCapabilities.None, LuaCapabilities.All, false, false)]
        [TestCase(LuaCapabilities.All, LuaCapabilities.Gameplay, false, false)]
        [TestCase(LuaCapabilities.All, LuaCapabilities.WorldEdit, false, true)]
        [TestCase(LuaCapabilities.All, LuaCapabilities.All, true, true)]
        public void Factory_ExposesOnlyForgeFunctionsAllowedByModTierAndHostCeiling(
            LuaCapabilities ceiling, LuaCapabilities requested, bool canRead, bool canEdit)
        {
            RecordingForge forge = new RecordingForge();
            UnitForgeLuaBindings bindings = new UnitForgeLuaBindings(forge);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Capabilities = ceiling,
                OneOffCapabilities = requested,
                AdditionalGameplayBindings = bindings.RegisterGameplayApis
            });
            string source = "assert(type(forge_count) == '" + (canRead ? "function" : "nil") + "') "
                + "assert(type(forge_define) == '" + (canEdit ? "function" : "nil") + "') "
                + "assert(type(forge_spawn) == '" + (canEdit ? "function" : "nil") + "') "
                + "assert(type(forge_clear) == '" + (canEdit ? "function" : "nil") + "') "
                + "assert(type(forge_reset) == '" + (canEdit ? "function" : "nil") + "') ";
            if (!canEdit)
            {
                source += "local ok = pcall(function() forge_spawn('denied', 0, 0) end) assert(not ok) ";
            }
            stack.Runtime.LoadMod(Host, "tier-probe", source, requested, persistToStore: false);
            LuaTool.LuaResult result = stack.ToolExecutor.ExecuteAsync(source + " return 'tier checked'",
                CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("tier checked", result.Output);
            Assert.AreEqual(0, forge.MutationCalls, "A refusal probe must never reach the host mutation API.");
            stack.Runtime.UnloadMod(Host, "tier-probe");
        }

        [Test]
        public void SeparateProviders_KeepRealControllerUnitsAndArchetypesIsolated()
        {
            ModdableUnitsDemoController first = NewController("first-forge");
            ModdableUnitsDemoController second = NewController("second-forge");
            LuaCsModStack firstStack = NewStack(first.RegisterGameplayBindings);
            LuaCsModStack secondStack = NewStack(second.RegisterGameplayBindings);
            firstStack.Runtime.LoadMod(Host, "first", ForgeSource("first-only"), persistToStore: false);
            Assert.AreEqual(1, first.Count("all"));
            Assert.AreEqual(0, second.Count("all"));
            secondStack.Runtime.LoadMod(Host, "second",
                "assert(forge_spawn('first-only', 0, 0) == 0) " + ForgeSource("second-only"), persistToStore: false);
            Assert.AreEqual(1, first.Count("ally"));
            Assert.AreEqual(1, second.Count("ally"));
            Assert.AreEqual(1, first.transform.Find("ForgeUnitRoot").childCount);
            Assert.AreEqual(1, second.transform.Find("ForgeUnitRoot").childCount);
            firstStack.Runtime.UnloadMod(Host, "first");
            secondStack.Runtime.UnloadMod(Host, "second");
        }

        [Test]
        public void SceneScope_SnapshotsAssignedProvider_WhenItsSerializedArrayChangesAfterBuild()
        {
            ModdableUnitsDemoController assigned = NewController("assigned-forge");
            ModdableUnitsDemoController later = NewController("later-forge");
            GameObject scopeObject = new GameObject("forge-scope");
            scopeObject.SetActive(false);
            _objects.Add(scopeObject);
            CoreAiModsLifetimeScope scope = scopeObject.AddComponent<CoreAiModsLifetimeScope>();
            scope.autoRun = false;
            using UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject(scope);
            UnityEditor.SerializedProperty providers = serialized.FindProperty("gameplayBindingsProviders");
            serialized.FindProperty("storeId").stringValue = "forge-snapshot-" + Guid.NewGuid().ToString("N");
            providers.arraySize = 2;
            providers.GetArrayElementAtIndex(0).objectReferenceValue = assigned;
            providers.GetArrayElementAtIndex(1).objectReferenceValue = assigned;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            using (LifetimeScope.Enqueue(builder =>
            {
                builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
                builder.RegisterInstance<ILog>(NullLog.Instance);
                builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
                builder.RegisterInstance<ILuaModStore>(new MemoryModStore());
                builder.RegisterInstance<IRbxWorldPackageStore>(new MemoryPackageStore());
            }))
            {
                scope.Build();
            }
            try
            {
                serialized.Update();
                providers = serialized.FindProperty("gameplayBindingsProviders");
                providers.GetArrayElementAtIndex(0).objectReferenceValue = later;
                providers.GetArrayElementAtIndex(1).objectReferenceValue = later;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                ILuaModRuntime runtime = scope.Container.Resolve<ILuaModRuntime>();
                runtime.LoadMod(Host, "snapshot", ForgeSource("assigned-only"), persistToStore: false);
                Assert.AreEqual(1, assigned.Count("all"));
                Assert.AreEqual(0, later.Count("all"), "An existing scope must retain its assigned provider snapshot.");
            }
            finally
            {
                scope.DisposeCore();
            }
        }

        [Test]
        public void AttachModRuntime_ReplacesPreviousListeners_WithoutDuplicatingCurrentListeners()
        {
            ModdableUnitsDemoController controller = NewController("event-forge");
            LuaCsModStack first = NewStack(null);
            LuaCsModStack second = NewStack(null);
            controller.AttachModRuntime(first.Runtime, Host);
            controller.AttachModRuntime(second.Runtime, Host);
            controller.AttachModRuntime(second.Runtime, Host);
            controller.InitializePresentation();

            first.Runtime.LoadMod(Host, "detached-runtime", "return 1", persistToStore: false);
            second.Runtime.LoadMod(Host, "active-runtime", "return 1", persistToStore: false);

            string visibleText = "";
            foreach (TMP_Text label in controller.GetComponentsInChildren<TMP_Text>())
            {
                visibleText += label.text + "\n";
            }
            StringAssert.DoesNotContain("Mod loaded: detached-runtime", visibleText);
            Assert.AreEqual(1, visibleText.Split(new[] { "Mod loaded: active-runtime" },
                StringSplitOptions.None).Length - 1, "Reattaching the same runtime must retain one listener.");
            first.Runtime.UnloadMod(Host, "detached-runtime");
            second.Runtime.UnloadMod(Host, "active-runtime");
        }

        [Test]
        public void PersistedMod_RehydratesBeforePresentation_AndInitializationPreservesItsUnits()
        {
            ModdableUnitsDemoController controller = NewController("rehydrated-forge");
            using FileLuaModSourceStore sources = NewSources();
            sources.Save("persisted-forge", ForgeSource("saved-ally"), new LuaModManifest
            {
                Id = "persisted-forge", Active = true, Capabilities = LuaCapabilities.All.ToString()
            });
            LuaCsModStack stack = NewStack(controller.RegisterGameplayBindings, sources);
            controller.AttachModRuntime(stack.Runtime, Host);

            int restored = stack.Runtime.RehydrateFromStore(Host, LuaCapabilities.All);
            Assert.AreEqual(1, restored, "A real persisted Lua chunk must define and spawn before Start.");
            Assert.AreEqual(1, controller.Count("ally"));
            Transform root = controller.transform.Find("ForgeUnitRoot");
            Assert.IsNotNull(root);
            Assert.AreEqual(1, root.childCount);
            GameObject visual = root.GetChild(0).gameObject;

            controller.InitializePresentation();

            Assert.AreEqual(1, controller.Count("ally"));
            Assert.AreSame(root, controller.transform.Find("ForgeUnitRoot"));
            Assert.AreSame(visual, root.GetChild(0).gameObject, "Start must retain the restored visual.");
            Assert.IsTrue(stack.Runtime.IsLoaded(Host, "persisted-forge"));
            stack.Runtime.UnloadMod(Host, "persisted-forge");
        }

        private const string AssertNoForgeSource = "assert(forge_define == nil) assert(forge_spawn == nil) "
            + "assert(forge_count == nil) assert(forge_clear == nil) assert(forge_reset == nil)";

        private static string ForgeSource(string name)
        {
            return "assert(forge_define('" + name + "', 'ally', 30, 4, 1, 1, '#3aa0ff')) "
                + "assert(forge_spawn('" + name + "', -2, 0) ~= 0) ";
        }

        private static LuaCsModStack NewStack(Action<LuaCsApiRegistry, LuaCapabilities> callback,
            ILuaModSourceStore sources = null)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                AdditionalGameplayBindings = callback,
                ModSourceStore = sources,
                Log = NullLog.Instance
            });
        }

        private ModdableUnitsDemoController NewController(string name)
        {
            GameObject instance = new GameObject(name);
            _objects.Add(instance);
            return instance.AddComponent<ModdableUnitsDemoController>();
        }

        private FileLuaModSourceStore NewSources()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAI-Forge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return new FileLuaModSourceStore(directory, NullLog.Instance,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
        }

        private IObjectResolver NewContainer(Action<LuaCsApiRegistry, LuaCapabilities> callback)
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            FileLuaModSourceStore sources = NewSources();
            MemoryPackageStore packages = new MemoryPackageStore();
            if (callback == null)
            {
                builder.RegisterCoreAiMods(applicationIsPlayingProvider: () => false,
                    skillTextProvider: _ => null, worldSessionSourceStore: sources, worldPackageStore: packages);
            }
            else
            {
                builder.RegisterCoreAiMods(null, false, false, null, "forge-editmode",
                    InstanceRegistry.CurrentWorldAclVersion, () => false, _ => null, sources, packages, callback);
            }
            builder.RegisterInstance<ILuaModStore>(new MemoryModStore());
            return builder.Build();
        }

        private static void AssertOneOffCount(LuaTool.ILuaExecutor executor, int expected)
        {
            LuaTool.LuaResult result = executor.ExecuteAsync("return forge_count('all')", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(expected.ToString(), result.Output);
        }

        private sealed class RecordingForge : IUnitForge
        {
            private readonly HashSet<string> _defined = new HashSet<string>(StringComparer.Ordinal);
            private int _count;
            public int MutationCalls { get; private set; }
            public bool Define(string name, string team, double hp, double damage, double speed,
                double range, string colorHex)
            {
                MutationCalls++;
                return _defined.Add(name);
            }
            public int Spawn(string name, double x, double z)
            {
                MutationCalls++;
                return _defined.Contains(name) ? ++_count : 0;
            }
            public int Count(string team) => _count;
            public void ClearUnits() { MutationCalls++; _count = 0; }
            public void ResetAll() { ClearUnits(); _defined.Clear(); }
        }

        private sealed class MemoryModStore : ILuaModStore
        {
            private readonly Dictionary<(string Mod, string Key), string> _values =
                new Dictionary<(string Mod, string Key), string>();
            public string Get(string modId, string key) =>
                _values.TryGetValue((modId, key), out string value) ? value : "";
            public void Set(string modId, string key, string value) => _values[(modId, key)] = value;
            public void Clear(string modId)
            {
                foreach ((string Mod, string Key) key in new List<(string Mod, string Key)>(_values.Keys))
                {
                    if (key.Mod == modId) _values.Remove(key);
                }
            }
        }

        private sealed class MemoryPackageStore : IRbxWorldPackageStore
        {
            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(string slot, RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default) =>
                UniTask.FromResult(new RbxWorldPackageWriteResult(true, "memory://manual/" + slot, ""));
            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(string trigger, RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default) =>
                UniTask.FromResult(new RbxWorldPackageWriteResult(true, "memory://auto/" + trigger, ""));
            public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IReadOnlyList<string> ListManualSlots() => Array.Empty<string>();
            public IReadOnlyList<string> ListAutoFiles() => Array.Empty<string>();
            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves() => Array.Empty<RbxAutoSaveInfo>();
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) { }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, Object context = null) { }
            public void LogError(GameLogFeature feature, string message, Object context = null) { }
        }
    }
}
#endif

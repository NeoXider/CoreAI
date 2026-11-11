using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Editor;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// P3 one-click world: the runtime <see cref="CoreAiPlayWorldBootstrap"/> (through a fake factory, so no
    /// container builds), the <c>CoreAI/Setup/Create Play + Create World</c> menu, and the instant edit
    /// installer the mods scope composes. Unity-only: GameObjects, VContainer and the Editor menu.
    /// </summary>
    [TestFixture]
    public sealed class P3PlayWorldBootstrapEditModeTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void DestroyCreated()
        {
            foreach (GameObject created in _created)
            {
                if (created != null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        [Test]
        public void Bootstrap_ComposesCameraHostAndBothScopes_ThenActivatesCoreBeforeMods()
        {
            CoreAiPlayWorldBootstrap bootstrap = NewBootstrap(out Camera camera);
            RecordingFactory factory = new(_created);
            bootstrap.Factory = factory;

            bool composed = bootstrap.Compose();

            Assert.IsTrue(composed, bootstrap.Failure);
            Assert.IsTrue(bootstrap.IsComposed);
            CollectionAssert.AreEqual(
                new[] { "host", "core", "mods", "activate:core", "activate:mods" },
                factory.Calls,
                "an assigned camera is used, the sun is off, and the CoreAI scope builds before the mods scope");
            CoreAiPlayWorldComposition composition = bootstrap.Composition;
            Assert.AreSame(camera, composition.Camera);
            Assert.AreSame(factory.Host, composition.WorldHost);
            Assert.AreSame(factory.Core, composition.CoreScope);
            Assert.AreSame(factory.Mods, composition.ModsScope);
            Assert.AreSame(factory.Core, factory.ModsParent, "the mods scope is parented to the created CoreAI scope");
            Assert.AreSame(factory.Host, factory.ModsHost, "the mods scope is wired to the created world host");
            Assert.AreSame(bootstrap.InstantEdit, factory.ModsSettings);
            Assert.IsTrue(bootstrap.Compose(), "composing twice is a no-op");
            Assert.AreEqual(5, factory.Calls.Count);
        }

        [Test]
        public void Bootstrap_RunsModulesInSlotOrder_BeforeAndAfterTheScopesBuild()
        {
            CoreAiPlayWorldBootstrap bootstrap = NewBootstrap(out _);
            RecordingFactory factory = new(_created);
            bootstrap.Factory = factory;
            List<string> order = factory.Calls;
            RecordingModule controls = AddModule(bootstrap.gameObject, "controls", order);
            RecordingModule chat = AddModule(bootstrap.gameObject, "chat", order);
            SerializedObject serialized = new(bootstrap);
            serialized.FindProperty("controlsModule").objectReferenceValue = controls;
            serialized.FindProperty("overlayChatModule").objectReferenceValue = chat;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            CodeModule fromCode = new(order);
            bootstrap.AddModule(fromCode);
            int composedEvents = 0;
            bootstrap.Composed += _ => composedEvents++;

            Assert.IsTrue(bootstrap.Compose(), bootstrap.Failure);

            CollectionAssert.AreEqual(
                new[]
                {
                    "host", "core", "mods",
                    "composing:controls", "composing:chat", "composing:code",
                    "activate:core", "activate:mods",
                    "composed:controls", "composed:chat", "composed:code"
                },
                order);
            Assert.AreEqual(1, composedEvents);
        }

        [Test]
        public void Bootstrap_AWrongComponentInAModuleSlot_FailsWithOneLine_AndCreatesNothing()
        {
            CoreAiPlayWorldBootstrap bootstrap = NewBootstrap(out Camera camera);
            RecordingFactory factory = new(_created);
            bootstrap.Factory = factory;
            SerializedObject serialized = new(bootstrap);
            serialized.FindProperty("lookModule").objectReferenceValue =
                Track(new GameObject("P3 not a module")).AddComponent<RbxWorldHost>();
            serialized.ApplyModifiedPropertiesWithoutUndo();

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("does not implement ICoreAiPlayWorldModule"));
            bool composed = bootstrap.Compose();

            Assert.IsFalse(composed);
            StringAssert.Contains("RbxWorldHost", bootstrap.Failure);
            CollectionAssert.IsEmpty(factory.Calls);
        }

        [Test]
        public void Bootstrap_WithoutCoreAiSettings_SaysHowToFixIt_AndCreatesNothing()
        {
            CoreAiPlayWorldBootstrap bootstrap = NewBootstrap(out _);
            RecordingFactory factory = new(_created);
            bootstrap.Factory = factory;
            bootstrap.SettingsAvailableForTests = () => false;

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no CoreAISettings asset"));
            bool composed = bootstrap.Compose();

            Assert.IsFalse(composed);
            StringAssert.Contains("CoreAI/Setup/Create Default Assets", bootstrap.Failure);
            CollectionAssert.IsEmpty(factory.Calls);
        }

        [Test]
        public void DefaultFactory_CreatesTheModsScopeInactive_ParentedToTheCoreScope_AndWired()
        {
            GameObject root = Track(new GameObject("P3 factory root"));
            CoreAiPlayWorldDefaultFactory factory = new();
            CoreAiInstantEditSettings settings = new() { undoHistory = 4 };

            CoreAILifetimeScope core = factory.CreateCoreScope(root.transform);
            RbxWorldHost host = factory.CreateWorldHost(root.transform);
            CoreAiModsLifetimeScope mods = factory.CreateModsScope(core.transform, core, host, settings, "p3-store");

            Assert.IsFalse(core.gameObject.activeSelf, "a created scope waits for its activation");
            Assert.IsFalse(mods.gameObject.activeSelf);
            Assert.IsNull(core.Container);
            Assert.AreSame(core, mods.parentReference.Object);
            Assert.AreSame(host, mods.RobloxWorldHost);
            Assert.AreSame(settings, mods.InstantEdit);
            Assert.AreEqual("p3-store", typeof(CoreAiModsLifetimeScope)
                .GetField("storeId", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mods));
        }

        [Test]
        public void Menu_AddsOneBootstrap_AdoptsAnExistingCoreScope_AndReturnsTheSameObjectOnASecondRun()
        {
            GameObject existingScope = Track(new GameObject("Existing CoreAI scope"));
            existingScope.SetActive(false);
            CoreAILifetimeScope scope = existingScope.AddComponent<CoreAILifetimeScope>();

            GameObject first = Track(CoreAiPlayWorldSetupMenu.CreatePlayWorld());
            GameObject second = CoreAiPlayWorldSetupMenu.CreatePlayWorld();

            Assert.AreSame(first, second);
            Assert.AreEqual(CoreAiPlayWorldSetupMenu.ObjectName, first.name);
            CoreAiPlayWorldBootstrap bootstrap = first.GetComponent<CoreAiPlayWorldBootstrap>();
            Assert.IsNotNull(bootstrap, "the menu adds the runtime bootstrap");
            Assert.AreEqual(5, first.GetComponents<Component>().Length - 1, "and its four default modules, nothing else");
            SerializedObject slots = new(bootstrap);
            Assert.AreSame(scope, slots.FindProperty("coreScope").objectReferenceValue);
            Assert.AreSame(first.GetComponent<CoreAiPlayerControlsModule>(),
                slots.FindProperty("controlsModule").objectReferenceValue);
            Assert.AreSame(first.GetComponent<CoreAiPlayWorldLookModule>(),
                slots.FindProperty("lookModule").objectReferenceValue);
            Assert.AreSame(first.GetComponent<CoreAiDefaultWorldContent>(),
                slots.FindProperty("worldContentModule").objectReferenceValue);
            Assert.AreSame(first.GetComponent<CoreAiOverlayChatModule>(),
                slots.FindProperty("overlayChatModule").objectReferenceValue);
            Assert.IsFalse(bootstrap.IsComposed, "nothing composes in the Editor before Play");
        }

        [Test]
        public void InstantEditInstaller_ComposesTheLimiterUndoAndWriteBehindGate_FromTheSettings()
        {
            CoreAiInstantEditSettings settings = new()
            {
                rateLimitCount = 3,
                rateLimitWindowSeconds = 10f,
                undoHistory = 4,
                writeBehindBackups = true
            };
            ContainerBuilder builder = new();
            builder.RegisterCoreAiInstantEditLoop(settings, () => false);
            builder.RegisterCoreAiMods(
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null,
                worldPackageStore: new P3MemoryPackageStore());
            RegisterMinimalHostServices(builder);

            using IObjectResolver container = builder.Build();
            LuaGenerationRateLimiter limiter = container.Resolve<LuaGenerationRateLimiter>();
            IRbxWorldUndoService undo = container.Resolve<IRbxWorldUndoService>();
            ConfirmedWorldMutationGate gate = container.Resolve<ConfirmedWorldMutationGate>();

            Assert.AreEqual(3, limiter.MaxPerWindow);
            Assert.AreEqual(10d, limiter.WindowSeconds, 1e-6);
            Assert.IsTrue(limiter.HasClock, "one window for every tool that spends it");
            Assert.AreEqual(4, undo.Capacity);
            Assert.IsTrue(gate.WriteBehindBackups);
            Assert.IsTrue(gate.WritesBehind);
            Assert.IsNotNull(gate.Diagnostics, "a lost write-behind backup is logged");
        }

        [Test]
        public void ModsScope_RegistersTheInstantEditLoopFromItsSettings_WithTheLimitOff()
        {
            GameObject scopeObject = Track(new GameObject("P3 mods scope"));
            scopeObject.SetActive(false);
            CoreAiModsLifetimeScope scope = scopeObject.AddComponent<CoreAiModsLifetimeScope>();
            scope.SetInstantEditSettings(new CoreAiInstantEditSettings { rateLimitEnabled = false, writeBehindBackups = false });
            scope.SetWorldPackageStore(new P3MemoryPackageStore());
            ContainerBuilder builder = new();
            MethodInfo configure = typeof(CoreAiModsLifetimeScope).GetMethod(
                "Configure", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IContainerBuilder) }, null);

            configure.Invoke(scope, new object[] { builder });
            RegisterMinimalHostServices(builder);
            using IObjectResolver container = builder.Build();

            Assert.IsFalse(container.Resolve<LuaGenerationRateLimiter>().IsLimiting);
            Assert.IsNotNull(container.Resolve<IRbxWorldUndoService>());
            Assert.IsFalse(container.Resolve<ConfirmedWorldMutationGate>().WriteBehindBackups,
                "durable-first when the preset says so");
        }

        private CoreAiPlayWorldBootstrap NewBootstrap(out Camera camera)
        {
            GameObject root = Track(new GameObject("P3 play world"));
            CoreAiPlayWorldBootstrap bootstrap = root.AddComponent<CoreAiPlayWorldBootstrap>();
            bootstrap.SettingsAvailableForTests = () => true;
            GameObject cameraObject = Track(new GameObject("P3 camera"));
            camera = cameraObject.AddComponent<Camera>();
            SerializedObject serialized = new(bootstrap);
            serialized.FindProperty("worldCamera").objectReferenceValue = camera;
            serialized.FindProperty("createSunWhenMissing").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return bootstrap;
        }

        private GameObject Track(GameObject gameObject)
        {
            _created.Add(gameObject);
            return gameObject;
        }

        private static RecordingModule AddModule(GameObject owner, string name, List<string> order)
        {
            RecordingModule module = owner.AddComponent<RecordingModule>();
            module.Name = name;
            module.Order = order;
            return module;
        }

        private static void RegisterMinimalHostServices(IContainerBuilder builder)
        {
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<ILuaModStore>(new RbxApi.Acceptance.Mvp1AcceptanceMemoryStore());
        }

        private sealed class RecordingFactory : ICoreAiPlayWorldFactory
        {
            private readonly List<GameObject> _created;

            public RecordingFactory(List<GameObject> created)
            {
                _created = created;
            }

            public List<string> Calls { get; } = new();

            public RbxWorldHost Host { get; private set; }

            public CoreAILifetimeScope Core { get; private set; }

            public CoreAiModsLifetimeScope Mods { get; private set; }

            public CoreAILifetimeScope ModsParent { get; private set; }

            public RbxWorldHost ModsHost { get; private set; }

            public CoreAiInstantEditSettings ModsSettings { get; private set; }

            public Camera CreateCamera(Transform parent)
            {
                Calls.Add("camera");
                return Inactive("fake camera").AddComponent<Camera>();
            }

            public Light CreateSun(Transform parent)
            {
                Calls.Add("sun");
                return Inactive("fake sun").AddComponent<Light>();
            }

            public RbxWorldHost CreateWorldHost(Transform parent)
            {
                Calls.Add("host");
                Host = Inactive("fake host").AddComponent<RbxWorldHost>();
                return Host;
            }

            public CoreAILifetimeScope CreateCoreScope(Transform parent)
            {
                Calls.Add("core");
                Core = Inactive("fake core").AddComponent<CoreAILifetimeScope>();
                return Core;
            }

            public CoreAiModsLifetimeScope CreateModsScope(
                Transform parent,
                CoreAILifetimeScope coreScope,
                RbxWorldHost worldHost,
                CoreAiInstantEditSettings settings,
                string modStoreId)
            {
                Calls.Add("mods");
                ModsParent = coreScope;
                ModsHost = worldHost;
                ModsSettings = settings;
                Mods = Inactive("fake mods").AddComponent<CoreAiModsLifetimeScope>();
                return Mods;
            }

            public void Activate(LifetimeScope scope)
            {
                Calls.Add(ReferenceEquals(scope, Core) ? "activate:core" : "activate:mods");
            }

            private GameObject Inactive(string name)
            {
                GameObject created = new(name);
                created.SetActive(false);
                _created.Add(created);
                return created;
            }
        }

        private sealed class CodeModule : ICoreAiPlayWorldModule
        {
            private readonly List<string> _order;

            public CodeModule(List<string> order)
            {
                _order = order;
            }

            public void OnComposing(CoreAiPlayWorldComposition composition)
            {
                _order.Add("composing:code");
            }

            public void OnComposed(CoreAiPlayWorldComposition composition)
            {
                _order.Add("composed:code");
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }

    /// <summary>A scene module that records when the bootstrap calls it.</summary>
    internal sealed class RecordingModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        public string Name { get; set; } = "";

        public List<string> Order { get; set; }

        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            Order?.Add("composing:" + Name);
        }

        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
            Order?.Add("composed:" + Name);
        }
    }
}

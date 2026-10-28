using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldCommandsInstallerEditModeTests
    {
        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private static bool HasTool(IReadOnlyList<ILlmTool> tools, string name)
        {
            foreach (ILlmTool tool in tools)
            {
                if (tool != null && tool.Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void RegisterWorldCommands_PassesHostPositionBudgetToComponentExecutor()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject target = new GameObject("HostComponentBudget_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                ContainerBuilder builder = new ContainerBuilder();
                builder.RegisterInstance<IGameLogger>(new NullGameLogger());
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.RegisterInstance(new CoreAiComponentCommandOptions(2));
                builder.RegisterWorldCommands(registry);
                using IObjectResolver container = builder.Build();
                ICoreAiComponentCommandExecutor executor = container.Resolve<ICoreAiComponentCommandExecutor>();
                CoreAiComponentCommandEnvelope rejected = CoreAiComponentCommandEnvelope.SetFloat(
                    target.name, "linerenderer", "positioncount", 3f);

                Assert.IsFalse(executor.TryExecute(new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.ComponentCommand,
                    JsonPayload = JsonUtility.ToJson(rejected)
                }));
                Assert.IsNull(target.GetComponent<LineRenderer>());
                rejected.floatValue = 2f;
                Assert.IsTrue(executor.TryExecute(new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.ComponentCommand,
                    JsonPayload = JsonUtility.ToJson(rejected)
                }));
                Assert.AreEqual(2, target.GetComponent<LineRenderer>().positionCount);
                Assert.AreSame(container.Resolve<IWorldStateManager>(), container.Resolve<IWorldStateWriteResults>());
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void RegisterWorldCommands_AttachesWorldCommandTool_ToCreatorRole()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(settings);

                builder.RegisterWorldCommands(registry);

                using IObjectResolver container = builder.Build();
                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();

                Assert.IsTrue(
                    HasTool(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator), "world_command"),
                    "Creator must get world_command so it can build/spawn what its system prompt promises.");
                Assert.IsTrue(
                    HasTool(policy.GetToolsForRole(BuiltInAgentRoleIds.Builder), "world_command"),
                    "Builder must get world_command too — its system prompt tells the model to use it.");
            }
            finally
            {
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RegisterWorldCommands_BuildsContainer_WithPrefabRegistryContracts()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();

                // Services the real composition root registers elsewhere; the installer's entry
                // points resolve them eagerly at Build().
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();

                builder.RegisterWorldCommands(registry);

                using IObjectResolver container = builder.Build();
                ICoreAiPrefabRegistry asInterface = container.Resolve<ICoreAiPrefabRegistry>();
                CoreAiPrefabRegistryAsset asConcrete = container.Resolve<CoreAiPrefabRegistryAsset>();

                Assert.AreSame(registry, asInterface);
                Assert.AreSame(registry, asConcrete);
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void FullScope_ResolvesUnityGameConfigStore_WhenPortableDefaultsRegisterLater()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.RegisterWorldCommands(registry);
                builder.RegisterCorePortable();

                using IObjectResolver container = builder.Build();

                Assert.That(container.Resolve<IGameConfigStore>(), Is.InstanceOf<UnityGameConfigStore>());
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void RegisterWorldCommands_WithSnapshotPath_RestoresSavesAndResetsOnlyThatFile()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            string root = Path.Combine(Path.GetTempPath(), "cai-wci-" + System.Guid.NewGuid().ToString("N").Substring(0, 10));
            string snapshot = Path.Combine(root, "slot-a", "world.json");
            // WHY: a fresh default root, so "the default snapshot stays untouched" is not confused by
            // another test of this run having saved the shared run root's default snapshot.
            System.IDisposable defaultRoot = CoreAiStorageRoot.Override(Path.Combine(root, "default-root"));
            try
            {
                ContainerBuilder builder = NewWorldBuilder();
                builder.RegisterWorldCommands(registry, null, snapshot);
                using IObjectResolver container = builder.Build();

                WorldStateManager manager = container.Resolve<WorldStateManager>();
                Assert.AreSame(manager, container.Resolve<IWorldStateManager>());
                Assert.AreEqual(Path.GetFullPath(snapshot), manager.SaveFilePath,
                    "The installer option must select the manager's snapshot before anything restores from it.");
                Assert.AreNotEqual(Path.GetFullPath(WorldStateManager.DefaultSaveFilePath()),
                    Path.GetFullPath(manager.SaveFilePath));

                manager.WebGlFlushSync = () => true;
                Assert.IsTrue(manager.SaveWithResult().LocalOperationSucceeded);
                Assert.IsTrue(File.Exists(snapshot), "The save must land at the selected snapshot path.");
                Assert.IsFalse(File.Exists(WorldStateManager.DefaultSaveFilePath()),
                    "Selecting a path must leave the default snapshot untouched.");
                manager.Reset();
                Assert.IsFalse(File.Exists(snapshot));
            }
            finally
            {
                defaultRoot.Dispose();
                Object.DestroyImmediate(registry);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void RegisterWorldCommands_NullSnapshotPath_KeepsTheDefaultUnderTheStorageRoot()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = NewWorldBuilder();
                builder.RegisterWorldCommands(registry, null, null);
                using IObjectResolver container = builder.Build();

                Assert.AreEqual(Path.GetFullPath(WorldStateManager.DefaultSaveFilePath()),
                    Path.GetFullPath(container.Resolve<WorldStateManager>().SaveFilePath));
                StringAssert.StartsWith(Path.GetFullPath(CoreAiStorageRoot.PersistentDataRoot),
                    Path.GetFullPath(container.Resolve<WorldStateManager>().SaveFilePath));
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("relative/world.json")]
        public void RegisterWorldCommands_InvalidSnapshotPath_FailsAtRegistration(string snapshot)
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = NewWorldBuilder();
                Assert.Throws<System.ArgumentException>(() => builder.RegisterWorldCommands(registry, null, snapshot));
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void RegisterWorldCommands_TraversalOrDirectorySnapshotPath_FailsAtRegistration()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                string temp = Path.GetFullPath(Path.GetTempPath());
                ContainerBuilder builder = NewWorldBuilder();
                Assert.Throws<System.ArgumentException>(() => builder.RegisterWorldCommands(registry, null,
                    Path.Combine(temp, "a", "..", "world.json")));
                Assert.Throws<System.ArgumentException>(() => builder.RegisterWorldCommands(registry, null,
                    Path.Combine(temp, "slot") + Path.DirectorySeparatorChar));
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void RegisterWorldCommands_PublishedOverloadSignatureIsRetained()
        {
            System.Reflection.MethodInfo published = typeof(WorldCommandsInstaller).GetMethod(
                nameof(WorldCommandsInstaller.RegisterWorldCommands),
                new[]
                {
                    typeof(IContainerBuilder), typeof(CoreAiPrefabRegistryAsset), typeof(IEnumerable<string>),
                    typeof(bool), typeof(bool)
                });
            Assert.IsNotNull(published, "Adding the snapshot option must not change the published CLR signature.");
        }

        private static ContainerBuilder NewWorldBuilder()
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            builder.RegisterInstance<ILog>(Log.Instance);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            return builder;
        }
    }
}

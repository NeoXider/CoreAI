using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Composition;
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
    }
}

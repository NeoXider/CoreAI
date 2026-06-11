using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
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

        [Test]
        public void RegisterWorldCommands_BuildsContainer_WithPrefabRegistryContracts()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();

                // Services the real composition root registers elsewhere; the installer's entry
                // points (LuaModRuntimeTicker -> bindings chain) resolve them eagerly at Build().
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
    }
}

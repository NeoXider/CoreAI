using System;
using System.Collections;
using System.IO;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode half of the storage-root regression: the run redirect is active inside Play Mode, and a
    /// production world scope that initializes and saves there writes under the run's own root.
    /// <para>
    /// WHY: the 2026-10-28 hermetic gate overwrote the developer's world snapshot with an empty demo
    /// state when the editor quit, because a scene-built <see cref="WorldStateManager"/> took its default
    /// path from the engine data path and saved on quit.
    /// </para>
    /// </summary>
    public sealed class CoreAiStorageRootPlayModeTests
    {
        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        [UnityTest]
        public IEnumerator PlayMode_ProductionWorldScopeInitializesAndSavesUnderTheRunRoot()
        {
            Assert.IsTrue(Application.isPlaying);
            Assert.IsTrue(CoreAiStorageRoot.IsOverridden, "PlayMode tests must run with the storage root redirected.");
            string runRoot = CoreAiTestStorageRootRunCallback.ActiveRunRoot;
            Assert.IsTrue(CoreAiTestStorageRootRunCallback.IsOwnedRunRoot(runRoot), "Unexpected run root: " + runRoot);
            Assert.AreEqual(Path.GetFullPath(runRoot), Path.GetFullPath(CoreAiStorageRoot.PersistentDataRoot));

            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            ContainerBuilder builder = new();
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            builder.RegisterInstance<ILog>(Log.Instance);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.RegisterWorldCommands(registry);
            IObjectResolver container = builder.Build();
            try
            {
                WorldStateManager manager = container.Resolve<WorldStateManager>();
                StringAssert.StartsWith(Path.GetFullPath(runRoot), Path.GetFullPath(manager.SaveFilePath),
                    "A scene-built world manager must default under the run root.");

                manager.WebGlFlushSync = () => true;
                manager.Initialize();
                yield return null;
                Assert.IsTrue(manager.SaveWithResult().LocalOperationSucceeded);
                Assert.IsTrue(File.Exists(manager.SaveFilePath), "The save must land under the run root.");
            }
            finally
            {
                container.Dispose();
                Object.Destroy(registry);
            }
        }
    }
}

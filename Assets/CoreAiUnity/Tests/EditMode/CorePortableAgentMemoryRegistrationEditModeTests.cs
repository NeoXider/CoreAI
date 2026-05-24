using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using Assert = NUnit.Framework.Assert;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards against two <see cref="IAgentMemoryStore"/> singleton registrations:
    /// <see cref="CorePortableInstaller.RegisterCorePortable"/> defaults to <see cref="NullAgentMemoryStore"/>,
    /// while <see cref="CoreAILifetimeScope"/> registers <see cref="FileAgentMemoryStore"/> (or null store on WebGL).
    /// </summary>
    public sealed class CorePortableAgentMemoryRegistrationEditModeTests
    {
#if !UNITY_WEBGL
        /// <summary>
        /// Mirrors successful <see cref="CoreAILifetimeScope.Configure"/>: summary + portable with suppressed
        /// default agent memory, then host file store — container must build and resolve a single implementation.
        /// </summary>
        [Test]
        public void RegisterCorePortable_SuppressAgentMemory_ThenFileStore_BuildsAndResolvesFileStore()
        {
            ContainerBuilder builder = CreateMinimalBuilderForPortableStack(out CoreAISettingsAsset settings);
            try
            {
                builder.Register<IConversationSummaryStore>(_ =>
                        new FileConversationSummaryStore(
                            Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                                CoreAiPersistentPaths.ConversationSummaries),
                            null),
                    Lifetime.Singleton);

                builder.RegisterCorePortable(
                    true,
                    true);

                builder.Register<FileAgentMemoryStore>(Lifetime.Singleton)
                    .As<IAgentMemoryStore>()
                    .As<IConversationTranscriptStore>();

                using IObjectResolver container = builder.Build();
                IAgentMemoryStore mem = container.Resolve<IAgentMemoryStore>();
                Assert.IsInstanceOf<FileAgentMemoryStore>(mem);

                // Must not stack portable Null + host File under one contract (desktop: VContainer keeps both in collection).
                IReadOnlyList<IAgentMemoryStore> all = container.Resolve<IReadOnlyList<IAgentMemoryStore>>();
                Assert.AreEqual(1, all.Count,
                    "suppressDefaultAgentMemoryStore must leave a single IAgentMemoryStore binding.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        /// <summary>
        /// Without <c>suppressDefaultAgentMemoryStore</c>, portable registers <see cref="NullAgentMemoryStore"/> and
        /// the host adds <see cref="FileAgentMemoryStore"/> — different implementation types, so
        /// <see cref="ContainerBuilder.Build"/> does not throw; VContainer exposes both via
        /// <see cref="IReadOnlyList{IAgentMemoryStore}"/>. That broken state is what <see cref="CoreAILifetimeScope"/>
        /// avoids (two bindings for one role store). Duplicate <b>same</b> type (e.g. two Null on WebGL) throws
        /// <see cref="VContainerException"/> during build.
        /// </summary>
        [Test]
        public void RegisterCorePortable_DoesNotSuppressAgentMemory_WithHostFileStore_BindsTwoMemoryStores()
        {
            ContainerBuilder builder = CreateMinimalBuilderForPortableStack(out CoreAISettingsAsset settings);
            try
            {
                builder.Register<IConversationSummaryStore>(_ =>
                        new FileConversationSummaryStore(
                            Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                                CoreAiPersistentPaths.ConversationSummaries),
                            null),
                    Lifetime.Singleton);

                builder.RegisterCorePortable(
                    true,
                    false);

                builder.Register<FileAgentMemoryStore>(Lifetime.Singleton)
                    .As<IAgentMemoryStore>()
                    .As<IConversationTranscriptStore>();

                using IObjectResolver container = builder.Build();
                IReadOnlyList<IAgentMemoryStore> all = container.Resolve<IReadOnlyList<IAgentMemoryStore>>();
                Assert.AreEqual(2, all.Count);
                Assert.IsTrue(all.Any(s => s is NullAgentMemoryStore));
                Assert.IsTrue(all.Any(s => s is FileAgentMemoryStore));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static ContainerBuilder CreateMinimalBuilderForPortableStack(out CoreAISettingsAsset settings)
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            FieldInfo executionModeField = typeof(CoreAISettingsAsset).GetField(
                "executionMode", BindingFlags.NonPublic | BindingFlags.Instance);
            executionModeField.SetValue(settings, LlmExecutionMode.Offline);

            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);
            return builder;
        }
#endif
    }
}
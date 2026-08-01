using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// default agent memory, then host file store - container must build and resolve a single implementation.
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

                builder.Register(_ => new FileAgentMemoryStore(), Lifetime.Singleton)
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
        /// the host adds <see cref="FileAgentMemoryStore"/> - different implementation types, so
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

                builder.Register(_ => new FileAgentMemoryStore(), Lifetime.Singleton)
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
#endif

        [Test]
        public void RegisterCorePortable_HostMemoryScopeProviderWinsDefault()
        {
            ContainerBuilder builder = CreateMinimalBuilderForPortableStack(out CoreAISettingsAsset settings);
            FixedScopeProvider hostProvider = new("student-from-host");
            try
            {
                CoreAILifetimeScope.RegisterAgentMemoryScopeProvider(builder, hostProvider);
                builder.RegisterCorePortable(
                    false,
                    false);

                using IObjectResolver container = builder.Build();
                IAgentMemoryScopeProvider resolved = container.Resolve<IAgentMemoryScopeProvider>();
                IReadOnlyList<IAgentMemoryScopeProvider> all =
                    container.Resolve<IReadOnlyList<IAgentMemoryScopeProvider>>();

                Assert.AreSame(hostProvider, resolved);
                Assert.AreEqual(1, all.Count,
                    "The portable empty default must not stack with or replace a host provider.");
                Assert.AreEqual("student-from-host", resolved.GetScope("Teacher").UserId);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RegisterConversationSummary_HostProviderScopesProductionStore()
        {
            ContainerBuilder builder = CreateMinimalBuilderForPortableStack(out CoreAISettingsAsset settings);
            MutableScopeProvider provider = new();
            string roleId = "summary-scope-" + Guid.NewGuid().ToString("N");
            try
            {
                CoreAILifetimeScope.RegisterAgentMemoryScopeProvider(builder, provider);
                CoreAILifetimeScope.RegisterConversationSummaryForCoreAiLifetimeScope(builder);
                CoreAILifetimeScope.RegisterAgentMemoryStore(builder);

                using IObjectResolver container = builder.Build();
                IConversationSummaryStore summaries = container.Resolve<IConversationSummaryStore>();
                Assert.IsInstanceOf<ScopedConversationSummaryStoreDecorator>(summaries);
                Assert.AreEqual(1, container.Resolve<IReadOnlyList<IConversationSummaryStore>>().Count);

                provider.UserId = "student-a";
                summaries.SaveSummary(roleId, "summary-a");
                provider.UserId = "student-b";
                Assert.AreEqual("", summaries.LoadSummary(roleId));
                summaries.SaveSummary(roleId, "summary-b");
                provider.UserId = "student-a";
                Assert.AreEqual("summary-a", summaries.LoadSummary(roleId));
                provider.UserId = "student-b";
                Assert.AreEqual("summary-b", summaries.LoadSummary(roleId));

                provider.UserId = "student-a";
                summaries.ClearSummary(roleId);
                provider.UserId = "student-b";
                summaries.ClearSummary(roleId);
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
            settings.ConfigureOffline();

            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);
            return builder;
        }

        private sealed class FixedScopeProvider : IAgentMemoryScopeProvider
        {
            private readonly string _userId;

            public FixedScopeProvider(string userId)
            {
                _userId = userId;
            }

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("redoschool", _userId, "", "");
            }
        }

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public string UserId { get; set; } = "";

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("redoschool", UserId, "", "");
            }
        }
    }
}

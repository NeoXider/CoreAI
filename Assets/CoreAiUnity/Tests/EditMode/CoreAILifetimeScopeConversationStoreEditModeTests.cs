using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers <see cref="CoreAILifetimeScope"/> store registration: persistent/platform-specific and
    /// session-only conversation backings, always exposed through the same scoped facades.
    /// </summary>
    public sealed class CoreAILifetimeScopeConversationStoreEditModeTests
    {
        [Test]
        public void UsesPersistentFileConversationSummaryStore_MatchesCompileTimePlatform()
        {
#if UNITY_WEBGL
            Assert.IsFalse(CoreAILifetimeScope.UsesPersistentFileConversationSummaryStore);
#else
            Assert.IsTrue(CoreAILifetimeScope.UsesPersistentFileConversationSummaryStore);
#endif
        }

        [Test]
        public void ClearContextStores_ClearChatHistory_ClearsPortableChatAndSummary()
        {
            InMemoryAgentMemoryStore memory = new();
            InMemoryConversationSummaryStore summary = new();
            const string roleId = "Teacher";
            memory.AppendChatMessage(roleId, "user", "portable chat", true);
            summary.SaveSummary(roleId, "portable summary");

            CoreAi.ClearContextStores(memory, summary, roleId, true, false);

            Assert.IsEmpty(memory.GetChatHistory(roleId));
            Assert.AreEqual("", summary.LoadSummary(roleId));
        }

        [Test]
        public void RegisterAgentMemoryStore_ResolvesScopedMemory_AndKeepsFileTranscriptStore()
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.Register<DefaultAgentMemoryScopeProvider>(Lifetime.Singleton)
                .As<IAgentMemoryScopeProvider>();
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);
            using IObjectResolver container = builder.Build();
            try
            {
                IAgentMemoryStore mem = container.Resolve<IAgentMemoryStore>();
                IConversationTranscriptStore transcript = container.Resolve<IConversationTranscriptStore>();
                FileAgentMemoryStore backing = container.Resolve<FileAgentMemoryStore>();
                Assert.IsInstanceOf<ScopedAgentMemoryStoreDecorator>(mem);
                Assert.IsInstanceOf<ScopedConversationTranscriptStoreDecorator>(transcript);
                Assert.IsFalse(mem is IConversationTranscriptStore,
                    "A memory-only decorator must not falsely advertise the optional transcript capability.");
                Assert.AreNotSame(backing, transcript);
                Assert.IsInstanceOf<IAtomicAgentMemoryStore>(mem);
                Assert.AreEqual(1,
                    container.Resolve<IReadOnlyList<IAgentMemoryStore>>().Count);
                Assert.AreEqual(1,
                    container.Resolve<IReadOnlyList<IConversationTranscriptStore>>().Count);
            }
            finally
            {
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        [Test]
        public void RegisterAgentMemoryStore_SessionOnly_UsesInMemoryBacking_AndWritesNoFiles()
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.Register<DefaultAgentMemoryScopeProvider>(Lifetime.Singleton)
                .As<IAgentMemoryScopeProvider>();
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder, AgentMemoryPersistenceMode.SessionOnly);
            using IObjectResolver container = builder.Build();

            string roleId = "session-only-" + Guid.NewGuid().ToString("N");
            IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
            IConversationTranscriptStore transcript = container.Resolve<IConversationTranscriptStore>();
            InMemoryAgentMemoryStore backing = container.Resolve<InMemoryAgentMemoryStore>();

            Assert.IsInstanceOf<ScopedAgentMemoryStoreDecorator>(memory);
            Assert.IsInstanceOf<ScopedConversationTranscriptStoreDecorator>(transcript);
            Assert.IsInstanceOf<IAtomicAgentMemoryStore>(memory);
            Assert.AreEqual(0,
                container.Resolve<IReadOnlyList<FileAgentMemoryStore>>().Count,
                "SessionOnly must not register the file memory backing at all.");

            memory.Save(roleId, new AgentMemoryState { Memory = "session memory" });
            memory.AppendChatMessage(roleId, "user", "session chat", true);
            transcript.AppendTranscriptEntry(roleId, new ConversationEntry
            {
                Kind = ConversationEntryKind.User,
                Key = "user",
                Content = "session transcript"
            }, true);

            Assert.IsTrue(memory.TryLoad(roleId, out AgentMemoryState state));
            Assert.AreEqual("session memory", state.Memory);
            Assert.AreEqual("session chat", memory.GetChatHistory(roleId)[0].Content);
            Assert.AreEqual("session transcript", transcript.GetTranscriptEntries(roleId, 0)[0].Content);
            Assert.AreEqual("session chat", backing.GetChatHistory(roleId)[0].Content,
                "The scoped facade must still use the one private session backing store.");
        }

        [Test]
        public void RegisterAgentMemoryStore_TwoUserScopesWithSameRole_AreIsolated()
        {
            MutableScopeProvider provider = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<IAgentMemoryScopeProvider>(provider);
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);
            using IObjectResolver container = builder.Build();

            IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
            IConversationTranscriptStore transcript = container.Resolve<IConversationTranscriptStore>();
            string roleId = "scope-isolation-" + Guid.NewGuid().ToString("N");
            try
            {
                provider.UserId = "student-a";
                memory.AppendChatMessage(roleId, "user", "student-a-message", false);
                transcript.AppendTranscriptEntry(roleId, new ConversationEntry
                {
                    Kind = ConversationEntryKind.User,
                    Key = "user",
                    Content = "student-a-transcript"
                }, false);

                provider.UserId = "student-b";
                Assert.AreEqual(0, memory.GetChatHistory(roleId).Length,
                    "A new user scope must not inherit the prior user's role history.");
                Assert.AreEqual(0, transcript.GetTranscriptEntries(roleId, 0).Count,
                    "Structured transcript must cross the same user-scope boundary as flat chat.");
                memory.AppendChatMessage(roleId, "user", "student-b-message", false);
                transcript.AppendTranscriptEntry(roleId, new ConversationEntry
                {
                    Kind = ConversationEntryKind.User,
                    Key = "user",
                    Content = "student-b-transcript"
                }, false);

                provider.UserId = "student-a";
                ChatMessage[] studentA = memory.GetChatHistory(roleId);
                IReadOnlyList<ConversationEntry> studentATranscript =
                    transcript.GetTranscriptEntries(roleId, 0);
                provider.UserId = "student-b";
                ChatMessage[] studentB = memory.GetChatHistory(roleId);
                IReadOnlyList<ConversationEntry> studentBTranscript =
                    transcript.GetTranscriptEntries(roleId, 0);

                Assert.AreEqual(1, studentA.Length);
                Assert.AreEqual("student-a-message", studentA[0].Content);
                Assert.AreEqual(1, studentB.Length);
                Assert.AreEqual("student-b-message", studentB[0].Content);
                Assert.IsTrue(System.Linq.Enumerable.Any(studentATranscript,
                    entry => entry.Content == "student-a-transcript"));
                Assert.IsTrue(System.Linq.Enumerable.Any(studentBTranscript,
                    entry => entry.Content == "student-b-transcript"));
            }
            finally
            {
                provider.UserId = "student-a";
                memory.Clear(roleId);
                provider.UserId = "student-b";
                memory.Clear(roleId);
            }
        }

        [Test]
        public void RegisterAgentMemoryStore_DefaultEmptyScope_PreservesLegacyRoleKey()
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.Register<DefaultAgentMemoryScopeProvider>(Lifetime.Singleton)
                .As<IAgentMemoryScopeProvider>();
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);
            using IObjectResolver container = builder.Build();

            IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
            FileAgentMemoryStore backing = container.Resolve<FileAgentMemoryStore>();
            string roleId = "legacy-role-" + Guid.NewGuid().ToString("N");
            try
            {
                memory.AppendChatMessage(roleId, "user", "legacy-message", false);
                ChatMessage[] rawRoleHistory = backing.GetChatHistory(roleId);
                Assert.AreEqual(1, rawRoleHistory.Length,
                    "AgentMemoryScope.Empty must keep the historical unscoped role key.");
                Assert.AreEqual("legacy-message", rawRoleHistory[0].Content);
            }
            finally
            {
                memory.Clear(roleId);
            }
        }

        [Test]
        public void AgentMemoryScopeProvider_CanBeConfiguredByInspectorOrCodeBeforeBuild()
        {
            System.Reflection.FieldInfo serializedField = typeof(CoreAILifetimeScope).GetField(
                "agentMemoryScopeProvider",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(serializedField);
            Assert.AreEqual(typeof(AgentMemoryScopeProviderBehaviour), serializedField.FieldType);

            GameObject root = new("CoreAILifetimeScope-memory-provider-test");
            root.SetActive(false);
            try
            {
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                MutableScopeProvider provider = new() { UserId = "student-from-code" };
                scope.SetAgentMemoryScopeProvider(provider);
                Assert.AreSame(provider, scope.ConfiguredAgentMemoryScopeProvider);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AgentMemoryPersistenceMode_CanBeConfiguredBeforeBuild_AndRejectsChangesAfterBuild()
        {
            System.Reflection.FieldInfo serializedField = typeof(CoreAILifetimeScope).GetField(
                "agentMemoryPersistenceMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(serializedField);
            Assert.AreEqual(typeof(AgentMemoryPersistenceMode), serializedField.FieldType);

            GameObject root = new("CoreAILifetimeScope-memory-persistence-test");
            root.SetActive(false);
            IObjectResolver container = null;
            try
            {
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                Assert.AreEqual(AgentMemoryPersistenceMode.Persistent,
                    scope.ConfiguredAgentMemoryPersistenceMode,
                    "Persistent must remain the backward-compatible default.");

                scope.SetAgentMemoryPersistenceMode(AgentMemoryPersistenceMode.SessionOnly);
                Assert.AreEqual(AgentMemoryPersistenceMode.SessionOnly,
                    scope.ConfiguredAgentMemoryPersistenceMode);
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    scope.SetAgentMemoryPersistenceMode((AgentMemoryPersistenceMode)999));

                container = new ContainerBuilder().Build();
                System.Reflection.PropertyInfo containerProperty = typeof(LifetimeScope).GetProperty(
                    nameof(LifetimeScope.Container));
                Assert.IsNotNull(containerProperty);
                containerProperty.SetValue(scope, container);

                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                    scope.SetAgentMemoryPersistenceMode(AgentMemoryPersistenceMode.Persistent));
                StringAssert.Contains("before CoreAILifetimeScope builds", error.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
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

#if !UNITY_WEBGL
        [Test]
        public void RegisterConversationSummaryForLifetimeScope_Resolves_FileConversationSummaryStore()
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureOffline();

            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);

            CoreAILifetimeScope.RegisterConversationSummaryForCoreAiLifetimeScope(builder);

            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);

            using IObjectResolver container = builder.Build();
            HashSet<string> filesBefore = SnapshotConversationFiles();
            try
            {
                IConversationSummaryStore store = container.Resolve<IConversationSummaryStore>();
                Assert.IsInstanceOf<ScopedConversationSummaryStoreDecorator>(store);
                Assert.IsInstanceOf<FileConversationSummaryStore>(container.Resolve<FileConversationSummaryStore>());
                IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
                Assert.IsInstanceOf<ScopedAgentMemoryStoreDecorator>(memory);
                string roleId = "persistent-clear-" + Guid.NewGuid().ToString("N");
                memory.AppendChatMessage(roleId, "user", "persistent chat", true);
                store.SaveSummary(roleId, "persistent summary");

                CoreAi.ClearContextStores(memory, store, roleId, true, false);

                Assert.IsEmpty(memory.GetChatHistory(roleId));
                Assert.AreEqual("", store.LoadSummary(roleId));
                memory.Clear(roleId);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
#endif

        [Test]
        public void RegisterConversationStores_SessionOnly_UsesInMemorySummaryAndMemoryBackings()
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureOffline();

            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);

            CoreAILifetimeScope.RegisterConversationSummaryForCoreAiLifetimeScope(
                builder,
                AgentMemoryPersistenceMode.SessionOnly);
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder, AgentMemoryPersistenceMode.SessionOnly);

            using IObjectResolver container = builder.Build();
            HashSet<string> filesBefore = SnapshotConversationFiles();
            try
            {
                IConversationSummaryStore summary = container.Resolve<IConversationSummaryStore>();
                IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
                Assert.IsInstanceOf<ScopedConversationSummaryStoreDecorator>(summary);
                Assert.IsInstanceOf<ScopedAgentMemoryStoreDecorator>(memory);
                Assert.IsInstanceOf<InMemoryConversationSummaryStore>(
                    container.Resolve<InMemoryConversationSummaryStore>());
                Assert.IsInstanceOf<InMemoryAgentMemoryStore>(container.Resolve<InMemoryAgentMemoryStore>());
                Assert.AreEqual(0,
                    container.Resolve<IReadOnlyList<FileConversationSummaryStore>>().Count);
                Assert.AreEqual(0,
                    container.Resolve<IReadOnlyList<FileAgentMemoryStore>>().Count);

                string roleId = "session-summary-" + Guid.NewGuid().ToString("N");
                memory.Save(roleId, new AgentMemoryState { Memory = "session memory" });
                memory.AppendChatMessage(roleId, "user", "session chat", true);
                container.Resolve<IConversationTranscriptStore>().AppendTranscriptEntry(roleId,
                    new ConversationEntry
                    {
                        Kind = ConversationEntryKind.User,
                        Key = "user",
                        Content = "session transcript"
                    },
                    true);
                summary.SaveSummary(roleId, "session summary");
                Assert.AreEqual("session summary", summary.LoadSummary(roleId));

                CoreAi.ClearContextStores(memory, summary, roleId, true, false);

                Assert.IsEmpty(memory.GetChatHistory(roleId));
                Assert.AreEqual("", summary.LoadSummary(roleId));
                CollectionAssert.AreEquivalent(filesBefore, SnapshotConversationFiles(),
                    "SessionOnly memory, flat chat, transcript, and summary writes must create no files.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        private static HashSet<string> SnapshotConversationFiles()
        {
            HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
            string root = Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName);
            foreach (string folder in new[]
                     {
                         CoreAiPersistentPaths.AgentMemory,
                         CoreAiPersistentPaths.ConversationSummaries
                     })
            {
                string path = Path.Combine(root, folder);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }

            return files;
        }
    }
}

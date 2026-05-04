using System;
#if !UNITY_WEBGL
using System.Reflection;
#endif
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers <see cref="CoreAILifetimeScope"/> store registration: conversation summaries (file vs in-memory by platform)
    /// and <see cref="CoreAILifetimeScope.RegisterAgentMemoryStore"/> (<see cref="FileAgentMemoryStore"/> on all targets).
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
        public void RegisterAgentMemoryStore_Resolves_FileAgentMemoryStore_SharedSingleton()
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance<ILog>(NullLog.Instance);
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);
            using IObjectResolver container = builder.Build();
            try
            {
                IAgentMemoryStore mem = container.Resolve<IAgentMemoryStore>();
                IConversationTranscriptStore transcript = container.Resolve<IConversationTranscriptStore>();
                Assert.IsInstanceOf<FileAgentMemoryStore>(mem);
                Assert.IsInstanceOf<FileAgentMemoryStore>(transcript);
                Assert.AreSame(mem, transcript);
            }
            finally
            {
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

#if !UNITY_WEBGL
        [Test]
        public void RegisterConversationSummaryForLifetimeScope_Resolves_FileConversationSummaryStore()
        {
            var builder = new ContainerBuilder();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            FieldInfo executionModeField = typeof(CoreAISettingsAsset).GetField(
                "executionMode", BindingFlags.NonPublic | BindingFlags.Instance);
            executionModeField.SetValue(settings, LlmExecutionMode.Offline);

            builder.RegisterInstance<ICoreAISettings>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);

            CoreAILifetimeScope.RegisterConversationSummaryForCoreAiLifetimeScope(builder);

            CoreAILifetimeScope.RegisterAgentMemoryStore(builder);

            using IObjectResolver container = builder.Build();
            try
            {
                IConversationSummaryStore store = container.Resolve<IConversationSummaryStore>();
                Assert.IsInstanceOf<FileConversationSummaryStore>(store);
                IAgentMemoryStore memory = container.Resolve<IAgentMemoryStore>();
                Assert.IsInstanceOf<FileAgentMemoryStore>(memory);
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
    }
}

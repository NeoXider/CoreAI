using System;
#if !UNITY_WEBGL
using System.Reflection;
#endif
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
#if !UNITY_WEBGL
using CoreAI.Infrastructure.AiMemory;
#endif
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers <see cref="CoreAILifetimeScope"/> conversation-summary registration: file-backed persistence
    /// on desktop/mobile vs in-memory on WebGL (compile-time split).
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

            builder.Register<FileAgentMemoryStore>(Lifetime.Singleton)
                .As<IAgentMemoryStore>()
                .As<IConversationTranscriptStore>();

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

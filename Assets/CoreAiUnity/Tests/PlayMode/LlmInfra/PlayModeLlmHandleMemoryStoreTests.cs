using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Offline guard for <see cref="LlmClientTestHelpers.WrapWithMemoryStore"/>. Every live suite relies on it
    /// to bind the built-in <c>memory</c> tool; a silent fall-through leaves the tool listed in the system
    /// prompt but stripped at execution ("'memory' tool requested ... but IAgentMemoryStore is null").
    /// </summary>
    public sealed class PlayModeLlmHandleMemoryStoreTests
    {
        [Test]
        public void WrapWithMemoryStore_UsesTheHandleRebuildPath()
        {
            SentinelLlmClient original = new();
            SentinelLlmClient rebuilt = new();
            InMemoryStore store = new();
            IAgentMemoryStore seen = null;
            PlayModeProductionLikeLlmHandle handle = new(
                original,
                PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                rebuildWithMemoryStore: memoryStore =>
                {
                    seen = memoryStore;
                    return rebuilt;
                });

            ILlmClient wrapped = handle.WrapWithMemoryStore(store);

            Assert.AreSame(rebuilt, wrapped);
            Assert.AreSame(store, seen);
        }

        [Test]
        public void WrapWithMemoryStore_LiveBackendWithoutRebuildPath_Throws()
        {
            PlayModeProductionLikeLlmHandle handle = new(
                new SentinelLlmClient(),
                PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp);

            Assert.Throws<InvalidOperationException>(() => handle.WrapWithMemoryStore(new InMemoryStore()));
        }

        [Test]
        public void WrapWithMemoryStore_OfflineStub_IsReturnedUnchanged()
        {
            SentinelLlmClient stub = new();
            PlayModeProductionLikeLlmHandle handle = new(stub, PlayModeProductionLikeLlmBackend.Offline);

            Assert.AreSame(stub, handle.WrapWithMemoryStore(new InMemoryStore()));
        }

        [Test]
        public void HttpHandle_WrapWithMemoryStore_BindsMemory()
        {
#if !COREAI_LLM
            Assert.Ignore("COREAI_LLM is not set: HTTP LLM clients are excluded from the build.");
#else
            PlayModeOpenAiTestConfig.ResolvedConfig config = new(
                "http://offline.invalid/v1", "", "offline-model", streaming: false, nativeTools: true,
                extraBodyJson: "");
            PlayModeProductionLikeLlmHandle handle =
                PlayModeProductionLikeLlmFactory.CreateOpenAiHandle(config, 0.1f, 5);
            try
            {
                MeaiLlmClient bare = (MeaiLlmClient)handle.Client;
                MeaiLlmClient wrapped = (MeaiLlmClient)handle.WrapWithMemoryStore(new InMemoryStore());
                List<ILlmTool> memoryOnly = new() { new MemoryLlmTool() };

                Assert.AreEqual(0, bare.BuildAIFunctions(memoryOnly, BuiltInAgentRoleIds.Programmer).Count,
                    "the raw factory client carries no store, so memory cannot bind there");
                Assert.AreEqual(1, wrapped.BuildAIFunctions(memoryOnly, BuiltInAgentRoleIds.Programmer).Count,
                    "the wrapped client must bind the memory tool");
                Assert.IsTrue(wrapped.SupportsNativeToolCalling);
            }
            finally
            {
                handle.Dispose();
            }
#endif
        }

        [Test]
        public void HttpHandle_WrapWithMemoryStore_PreservesTextChannelAndBindsMemory()
        {
#if !COREAI_LLM
            Assert.Ignore("COREAI_LLM is not set: HTTP LLM clients are excluded from the build.");
#else
            PlayModeOpenAiTestConfig.ResolvedConfig config = new(
                "http://offline.invalid/v1", "", "offline-model", streaming: false, nativeTools: false,
                extraBodyJson: "");
            PlayModeProductionLikeLlmHandle handle =
                PlayModeProductionLikeLlmFactory.CreateOpenAiHandle(config, 0.1f, 5);
            try
            {
                MeaiLlmClient wrapped = (MeaiLlmClient)handle.WrapWithMemoryStore(new InMemoryStore());

                Assert.IsFalse(handle.Client.SupportsNativeToolCalling);
                Assert.IsFalse(wrapped.SupportsNativeToolCalling);
                List<ILlmTool> memoryOnly = new() { new MemoryLlmTool() };
                Assert.AreEqual(1, wrapped.BuildAIFunctions(memoryOnly, BuiltInAgentRoleIds.Programmer).Count,
                    "Text channel clients must bind memory after rebuilding with its store.");
                Assert.AreNotSame(handle.Client, wrapped);
            }
            finally
            {
                handle.Dispose();
            }
#endif
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BenchmarkHttpHandle_UnlimitedTotalBudgetKeepsDefaultResponseWaitAndNoTemperature(bool target)
        {
#if !COREAI_LLM
            Assert.Ignore("COREAI_LLM is not set: HTTP LLM clients are excluded from the build.");
#else
            PlayModeOpenAiTestConfig.ResolvedConfig config = new(
                "http://offline.invalid/v1", "", "offline-model", streaming: true, nativeTools: true,
                extraBodyJson: "");
            PlayModeProductionLikeLlmHandle handle;
            if (target)
            {
                Assert.IsTrue(PlayModeProductionLikeLlmFactory.TryCreateForEndpoint(config.BaseUrl, config.Model,
                    "", null, 0, out handle, out string reason), reason);
            }
            else
            {
                handle = PlayModeProductionLikeLlmFactory.CreateOpenAiHandle(config, null, 0);
            }
            try
            {
                Assert.AreEqual(0, handle._openAiSettings.RequestTimeoutSeconds);
                Assert.AreEqual(0, handle._coreAiSettings.LlmRequestTimeoutSeconds);
                Assert.AreEqual(180, handle._coreAiSettings.LlmResponseWaitTimeoutSeconds);
                Assert.AreEqual(180, handle._openAiSettings.ResponseWaitTimeoutSeconds,
                    "The HTTP settings must pin the response wait without consulting global settings.");
                AssertClientBudgets(handle.Client, 0);
                Assert.IsFalse(handle._openAiSettings.OverrideTemperature);
                MeaiLlmClient rebuilt = (MeaiLlmClient)handle.WrapWithMemoryStore(new InMemoryStore());
                Assert.IsNotNull(rebuilt);
                Assert.AreEqual(0, handle._openAiSettings.RequestTimeoutSeconds);
                Assert.AreEqual(0, handle._coreAiSettings.LlmRequestTimeoutSeconds);
            }
            finally
            {
                handle.Dispose();
            }
#endif
        }

        [TestCase(PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp, 0)]
        [TestCase(PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp, 5)]
        [TestCase(PlayModeProductionLikeLlmBackend.LlmUnity, 0)]
        [TestCase(PlayModeProductionLikeLlmBackend.LlmUnity, 5)]
        public void AssetBackends_OverrideBudgetsWithoutMutatingSharedAsset(
            PlayModeProductionLikeLlmBackend backend, int totalSeconds)
        {
#if !COREAI_LLM
            Assert.Ignore("COREAI_LLM is not set: HTTP LLM clients are excluded from the build.");
#else
            CoreAISettingsAsset shared = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            shared.ConfigureHttpApi("http://offline.invalid/v1", "", "model", 0.7f, 120,
                overrideTemperature: true);
            CoreAISettingsOptions options = shared.ToOptions();
            options.LlmRequestTimeoutSeconds = 120;
            options.LlmResponseWaitTimeoutSeconds = 0;
            shared.ApplyOptions(options);
            PlayModeProductionLikeLlmHandle handle = null;
            try
            {
                handle = PlayModeProductionLikeLlmFactory.CreateAssetHttpHandle(shared, null, totalSeconds, backend);
                Assert.AreNotSame(shared, handle._coreAiSettings);
                Assert.AreEqual(totalSeconds, handle._coreAiSettings.LlmRequestTimeoutSeconds);
                Assert.AreEqual(totalSeconds, handle._coreAiSettings.EffectiveHttpRequestTimeoutSeconds);
                Assert.AreEqual(180, handle._coreAiSettings.LlmResponseWaitTimeoutSeconds);
                Assert.IsFalse(handle._coreAiSettings.OverrideTemperature);
                AssertClientBudgets(handle.Client, totalSeconds);
                AssertClientBudgets(handle.WrapWithMemoryStore(new InMemoryStore()), totalSeconds);
                Assert.AreEqual(120, shared.RequestTimeoutSeconds);
                Assert.AreEqual(120, shared.LlmRequestTimeoutSeconds);
                Assert.AreEqual(0, shared.LlmResponseWaitTimeoutSeconds);
                Assert.IsTrue(shared.OverrideTemperature);
            }
            finally
            {
                handle?.Dispose();
                UnityEngine.Object.DestroyImmediate(shared);
            }
#endif
        }

#if COREAI_LLM
        private static void AssertClientBudgets(ILlmClient client, int totalSeconds)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            MeaiLlmClient meai = client as MeaiLlmClient ??
                (MeaiLlmClient)typeof(OpenAiChatLlmClient).GetField("_client", fields).GetValue(client);
            Microsoft.Extensions.AI.IChatClient inner = (Microsoft.Extensions.AI.IChatClient)typeof(MeaiLlmClient)
                .GetField("_innerClient", fields).GetValue(meai);
            MeaiOpenAiChatClient http = (MeaiOpenAiChatClient)inner.GetService(typeof(MeaiOpenAiChatClient));
            IOpenAiHttpSettings actual = (IOpenAiHttpSettings)typeof(MeaiOpenAiChatClient)
                .GetField("_settings", fields).GetValue(http);
            Assert.AreEqual(totalSeconds, actual.RequestTimeoutSeconds);
            Assert.AreEqual(180, actual.ResponseWaitTimeoutSeconds);
            ICoreAISettings previous = CoreAISettings.Instance;
            try
            {
                CoreAISettings.Instance = new CoreAISettingsOptions { LlmResponseWaitTimeoutSeconds = 0 };
                object budget = typeof(MeaiOpenAiChatClient).GetMethod("CreateTimeoutBudget", fields)
                    .Invoke(http, new object[] { null });
                Assert.AreEqual(180, budget.GetType().GetProperty("ResponseWaitSeconds").GetValue(budget),
                    "The executing client must retain its response wait when a scene disables the global wait.");
            }
            finally
            {
                CoreAISettings.Instance = previous;
            }
        }
#endif

        private sealed class SentinelLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }
    }
}

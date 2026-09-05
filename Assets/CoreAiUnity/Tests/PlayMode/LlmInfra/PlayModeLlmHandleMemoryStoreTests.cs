using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

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
        public void HttpHandle_WrapWithMemoryStore_KeepsTheNonNativeToolsDecorator()
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
                ILlmClient wrapped = handle.WrapWithMemoryStore(new InMemoryStore());

                Assert.IsInstanceOf<NonNativeToolsLlmClientDecorator>(wrapped);
                Assert.IsFalse(wrapped.SupportsNativeToolCalling);
                Assert.AreNotSame(handle.Client, wrapped);
            }
            finally
            {
                handle.Dispose();
            }
#endif
        }

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

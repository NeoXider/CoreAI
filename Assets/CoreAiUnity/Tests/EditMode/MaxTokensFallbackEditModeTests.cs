using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
#if !COREAI_NO_LLM
    /// <summary>
    /// Verifies that <see cref="ICoreAISettings.MaxTokens"/> is uniformly forwarded into
    /// <c>ChatOptions.MaxOutputTokens</c> for both non-streaming and streaming code paths,
    /// while a per-request value still wins. Both backends (HTTP and LLMUnity) consume the
    /// resulting <c>ChatOptions</c> the same way, so testing through <see cref="MeaiLlmClient"/>
    /// covers them both.
    /// </summary>
    public sealed class MaxTokensFallbackEditModeTests
    {
        [Test]
        public async Task CompleteAsync_NoPerRequestValue_FallsBackToSettingsMaxTokens()
        {
            CapturingChatClient capturing = new();
            MeaiLlmClient client = new(
                capturing,
                GameLoggerUnscopedFallback.Instance,
                new StubSettings { MaxTokensValue = 1234 });

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Test",
                SystemPrompt = "sys",
                UserPayload = "hi"
            }, CancellationToken.None);

            Assert.AreEqual(1234, capturing.LastOptions?.MaxOutputTokens,
                "Settings MaxTokens must back-fill ChatOptions.MaxOutputTokens.");
        }

        [Test]
        public async Task CompleteAsync_PerRequestValueWinsOverSettings()
        {
            CapturingChatClient capturing = new();
            MeaiLlmClient client = new(
                capturing,
                GameLoggerUnscopedFallback.Instance,
                new StubSettings { MaxTokensValue = 1234 });

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Test",
                SystemPrompt = "sys",
                UserPayload = "hi",
                MaxOutputTokens = 99
            }, CancellationToken.None);

            Assert.AreEqual(99, capturing.LastOptions?.MaxOutputTokens,
                "Per-request MaxOutputTokens must override settings default.");
        }

        [Test]
        public async Task CompleteAsync_SettingsZero_LeavesProviderDefault()
        {
            CapturingChatClient capturing = new();
            MeaiLlmClient client = new(
                capturing,
                GameLoggerUnscopedFallback.Instance,
                new StubSettings { MaxTokensValue = 0 });

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Test",
                SystemPrompt = "sys",
                UserPayload = "hi"
            }, CancellationToken.None);

            Assert.IsNull(capturing.LastOptions?.MaxOutputTokens,
                "When neither request nor settings specify a value, MaxOutputTokens stays null (provider default).");
        }

        [Test]
        public async Task CompleteStreamingAsync_FallsBackToSettingsMaxTokens()
        {
            CapturingChatClient capturing = new();
            MeaiLlmClient client = new(
                capturing,
                GameLoggerUnscopedFallback.Instance,
                new StubSettings { MaxTokensValue = 777 });

            int chunks = 0;
            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Test",
                SystemPrompt = "sys",
                UserPayload = "hi"
            }, CancellationToken.None))
            {
                chunks++;
                if (chunks > 16) break;
            }

            Assert.AreEqual(777, capturing.LastStreamingOptions?.MaxOutputTokens,
                "Streaming path must apply the same MaxTokens fallback.");
        }

        // ===================== Test doubles =====================

        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxTokensValue { get; set; }

            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
            public int MaxTokens => MaxTokensValue;
        }

        private sealed class CapturingChatClient : MEAI.IChatClient
        {
            public MEAI.ChatOptions LastOptions { get; private set; }
            public MEAI.ChatOptions LastStreamingOptions { get; private set; }

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                MEAI.ChatMessage reply = new(MEAI.ChatRole.Assistant, "ok");
                return Task.FromResult(new MEAI.ChatResponse(reply));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                LastStreamingOptions = options;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok");
                await Task.CompletedTask;
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }
    }
#endif
}

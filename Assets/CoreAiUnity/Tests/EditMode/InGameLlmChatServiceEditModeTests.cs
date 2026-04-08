using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для InGameLlmChatService — склейка истории, ClearHistory, лимит сообщений.
    /// </summary>
    [TestFixture]
    public sealed class InGameLlmChatServiceEditModeTests
    {
        #region SendPlayerMessageAsync Tests

        [Test]
        public async Task SendPlayerMessageAsync_EmptyMessage_ReturnsError()
        {
            StubLlmClient llm = new("response");
            StubPromptProvider prompts = new("You are a test bot.");
            InGameLlmChatService service = new(llm, prompts, 24);

            LlmCompletionResult result = await service.SendPlayerMessageAsync("");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("empty message", result.Error);
            Assert.AreEqual(0, llm.CallCount, "LLM should not be called for empty message");
        }

        [Test]
        public async Task SendPlayerMessageAsync_WhitespaceMessage_ReturnsError()
        {
            StubLlmClient llm = new("response");
            StubPromptProvider prompts = new("You are a test bot.");
            InGameLlmChatService service = new(llm, prompts);

            LlmCompletionResult result = await service.SendPlayerMessageAsync("   ");

            Assert.IsFalse(result.Ok);
        }

        [Test]
        public async Task SendPlayerMessageAsync_ValidMessage_CallsLlm()
        {
            StubLlmClient llm = new("Hello, player!");
            StubPromptProvider prompts = new("You are a test bot.");
            InGameLlmChatService service = new(llm, prompts);

            LlmCompletionResult result = await service.SendPlayerMessageAsync("Hi");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("Hello, player!", result.Content);
            Assert.AreEqual(1, llm.CallCount);
        }

        [Test]
        public async Task SendPlayerMessageAsync_TracksHistoryPairCount()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts);

            Assert.AreEqual(0, service.HistoryPairCount);

            await service.SendPlayerMessageAsync("msg1");
            Assert.AreEqual(1, service.HistoryPairCount);

            await service.SendPlayerMessageAsync("msg2");
            Assert.AreEqual(2, service.HistoryPairCount);
        }

        [Test]
        public async Task SendPlayerMessageAsync_IncludesHistoryInRequest()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts);

            await service.SendPlayerMessageAsync("first");
            await service.SendPlayerMessageAsync("second");

            // Второй запрос должен содержать историю (2 предыдущих + 1 новый = 3 сообщения)
            Assert.AreEqual(3, llm.LastChatHistoryCount);
        }

        #endregion

        #region ClearHistory Tests

        [Test]
        public async Task ClearHistory_ResetsHistoryPairCount()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts);

            await service.SendPlayerMessageAsync("msg1");
            await service.SendPlayerMessageAsync("msg2");
            Assert.AreEqual(2, service.HistoryPairCount);

            service.ClearHistory();
            Assert.AreEqual(0, service.HistoryPairCount);
        }

        [Test]
        public async Task ClearHistory_NextRequestHasNoHistory()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts);

            await service.SendPlayerMessageAsync("msg1");
            service.ClearHistory();
            await service.SendPlayerMessageAsync("msg2");

            // После clear — только 1 сообщение (новый user message)
            Assert.AreEqual(1, llm.LastChatHistoryCount);
        }

        #endregion

        #region MaxMessages Trimming Tests

        [Test]
        public async Task HistoryTrimming_TrimsOldestPairsWhenOverLimit()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            // maxMessages=4 → максимум 2 пары (user+assistant)
            InGameLlmChatService service = new(llm, prompts, 4);

            await service.SendPlayerMessageAsync("msg1");
            await service.SendPlayerMessageAsync("msg2");
            await service.SendPlayerMessageAsync("msg3"); // должна вытеснить msg1

            Assert.AreEqual(2, service.HistoryPairCount, "Should keep only 2 pairs after trimming");
        }

        #endregion

        #region System Prompt Tests

        [Test]
        public async Task SystemPrompt_UsesProviderPrompt()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("Custom system prompt");
            InGameLlmChatService service = new(llm, prompts);

            await service.SendPlayerMessageAsync("hello");

            Assert.AreEqual("Custom system prompt", llm.LastSystemPrompt);
        }

        [Test]
        public async Task SystemPrompt_FallsBackToDefault_WhenProviderReturnsNull()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new(null);
            InGameLlmChatService service = new(llm, prompts);

            await service.SendPlayerMessageAsync("hello");

            Assert.AreEqual("You are a helpful in-game assistant.", llm.LastSystemPrompt);
        }

        #endregion

        #region Test Helpers

        private sealed class StubLlmClient : ILlmClient
        {
            private readonly string _response;
            public int CallCount;
            public string LastSystemPrompt;
            public int LastChatHistoryCount;

            public StubLlmClient(string response)
            {
                _response = response;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                LastSystemPrompt = request.SystemPrompt;
                LastChatHistoryCount = request.ChatHistory?.Count ?? 0;

                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = _response
                });
            }
        }

        private sealed class StubPromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _prompt;

            public StubPromptProvider(string prompt)
            {
                _prompt = prompt;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = _prompt;
                return !string.IsNullOrWhiteSpace(_prompt);
            }
        }

        #endregion
    }
}
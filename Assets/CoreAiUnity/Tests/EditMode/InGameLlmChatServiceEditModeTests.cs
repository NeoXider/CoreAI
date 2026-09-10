using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="InGameLlmChatService"/> history stitching,
    /// clearing, and message limits.
    /// </summary>
    [TestFixture]
    public sealed class InGameLlmChatServiceEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            // InGameLlmChatService prepends UniversalSystemPromptPrefix (matches AiPromptComposer layer 1).
            CoreAISettings.ResetOverrides();
            CoreAISettings.UniversalSystemPromptPrefix = "";
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.ResetOverrides();
        }

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

            // The second request must carry the history (2 previous + 1 new = 3 messages)
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

            // After clear there is only 1 message (the new user message)
            Assert.AreEqual(1, llm.LastChatHistoryCount);
        }

        #endregion

        #region MaxMessages Trimming Tests

        [Test]
        public async Task HistoryTrimming_TrimsOldestPairsWhenOverLimit()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            // maxMessages=4 -> at most 2 pairs (user+assistant)
            InGameLlmChatService service = new(llm, prompts, 4);

            await service.SendPlayerMessageAsync("msg1");
            await service.SendPlayerMessageAsync("msg2");
            await service.SendPlayerMessageAsync("msg3"); // must push msg1 out

            Assert.AreEqual(2, service.HistoryPairCount, "Should keep only 2 pairs after trimming");
        }

        #endregion

        #region Overlapping Requests Tests

        [Test]
        public async Task SendPlayerMessageAsync_OverlappingRequests_SecondSeesFirstTurn()
        {
            // FINDING-15: requests started while another was in flight took a snapshot of the history before
            // the first turn had been written back, so the second request never saw the first pair of messages.
            BlockingStubLlmClient llm = new();
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts);

            Task<LlmCompletionResult> first = service.SendPlayerMessageAsync("first");
            Task<LlmCompletionResult> second = service.SendPlayerMessageAsync("second");

            llm.ReleaseOne();
            llm.ReleaseOne();
            LlmCompletionResult[] results = await Task.WhenAll(first, second);

            Assert.IsTrue(results[0].Ok);
            Assert.IsTrue(results[1].Ok);
            Assert.AreEqual(2, llm.HistoryCounts.Count);
            Assert.AreEqual(1, llm.HistoryCounts[0], "First request sees only its own message.");
            Assert.AreEqual(3, llm.HistoryCounts[1],
                "Second request must see the completed first turn (user+assistant) plus its own message.");
            Assert.AreEqual(2, service.HistoryPairCount);
        }

        #endregion

        #region Rate Limiter Tests

        [Test]
        public async Task RateLimit_ExceededInWindow_ReturnsRateLimitedError()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            // A limit of 2 requests in a 60 second window
            InGameLlmChatService service = new(llm, prompts, 24,
                2,
                60);

            LlmCompletionResult r1 = await service.SendPlayerMessageAsync("msg1");
            LlmCompletionResult r2 = await service.SendPlayerMessageAsync("msg2");
            LlmCompletionResult r3 = await service.SendPlayerMessageAsync("msg3");

            Assert.IsTrue(r1.Ok);
            Assert.IsTrue(r2.Ok);
            Assert.IsFalse(r3.Ok, "The third request must be rejected by the rate limiter");
            StringAssert.StartsWith("rate_limited", r3.Error,
                "The error must carry the 'rate_limited' prefix");
            Assert.AreEqual(2, llm.CallCount,
                "The LLM must not be called once the rate limiter fires");
        }

        [Test]
        public async Task RateLimit_ZeroLimit_DisablesLimiter()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts, 24,
                0,
                60);

            for (int i = 0; i < 20; i++)
            {
                LlmCompletionResult r = await service.SendPlayerMessageAsync("msg" + i);
                Assert.IsTrue(r.Ok, $"Request {i} must go through when the limit is 0");
            }

            Assert.AreEqual(20, llm.CallCount);
        }

        [Test]
        public async Task RateLimit_RejectedRequest_DoesNotAddToHistory()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            InGameLlmChatService service = new(llm, prompts, 24,
                1,
                60);

            await service.SendPlayerMessageAsync("first");
            Assert.AreEqual(1, service.HistoryPairCount);

            LlmCompletionResult rejected = await service.SendPlayerMessageAsync("second");
            Assert.IsFalse(rejected.Ok);

            // The history must not grow, because the second request was rejected
            Assert.AreEqual(1, service.HistoryPairCount,
                "A request rejected by the rate limiter must not land in the history");
        }

        [Test]
        public async Task RateLimit_ShortWindow_SlidesAndUnblocks()
        {
            StubLlmClient llm = new("reply");
            StubPromptProvider prompts = new("system");
            // 1 request in a 1 second window is far too strict, but it is enough to exercise the sliding window
            InGameLlmChatService service = new(llm, prompts, 24,
                1,
                1);

            LlmCompletionResult first = await service.SendPlayerMessageAsync("msg1");
            LlmCompletionResult blocked = await service.SendPlayerMessageAsync("msg2");

            Assert.IsTrue(first.Ok);
            Assert.IsFalse(blocked.Ok);

            // Wait longer than the window so the timestamp falls out of the sliding window
            await Task.Delay(1200);

            LlmCompletionResult allowed = await service.SendPlayerMessageAsync("msg3");
            Assert.IsTrue(allowed.Ok, "Once the window has passed the request goes through again");
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

        private sealed class BlockingStubLlmClient : ILlmClient
        {
            private readonly SemaphoreSlim _release = new(0);
            public readonly List<int> HistoryCounts = new();

            public void ReleaseOne()
            {
                _release.Release();
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (HistoryCounts)
                {
                    HistoryCounts.Add(request.ChatHistory?.Count ?? 0);
                }

                await _release.WaitAsync(cancellationToken);
                return new LlmCompletionResult { Ok = true, Content = "reply" };
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

#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Drives two agents CONCURRENTLY against the configured OpenAI-compatible HTTP endpoint, each with its
    /// own client, settings and memory store, and checks that neither agent's turn leaks into the other and
    /// that both requests entered the <see cref="ILlmClient"/> boundary at the same time. Needs no local GGUF
    /// host; skips when no HTTP endpoint is configured.
    /// </summary>
    public sealed class ParallelHttpAgentsPlayModeTests
    {
        // WHY: one 150 s concurrent HTTP turn + the 5 s cancellation grace + the 20 s LiveTestRequestScope
        // reserve = 175 s; client construction is synchronous and needs no model load.
        private const int HttpPairTestTimeoutMs = 180000;

        // WHY: reasoning models spend the output budget on hidden reasoning before any visible text, so a
        // 256-token cap starves them into empty answers and the alpha/beta assertions fail for the wrong
        // reason (same fix as AiAttachmentLivePlayModeTests). 4096 still bounds a runaway turn.
        private const int HttpPairMaxOutputTokens = 4096;

        private const string FirstWord = "alpha";
        private const string SecondWord = "beta";
        private const string FirstHint = "Reply with the single word alpha.";
        private const string SecondHint = "Reply with the single word beta.";

        private CoreAISettingsAsset _httpSettings;
        private CoreAISettingsAsset _secondHttpSettings;
        private LiveTestRequestScope _requests;

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup, so cancel both abandoned turns here and
            // let them unwind before the settings they read are destroyed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            if (_httpSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_httpSettings);
                _httpSettings = null;
            }

            if (_secondHttpSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_secondHttpSettings);
                _secondHttpSettings = null;
            }

            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        [Timeout(HttpPairTestTimeoutMs)]
        public IEnumerator TwoHttpAgents_IndependentStores_RunConcurrently()
        {
            _requests = new LiveTestRequestScope(HttpPairTestTimeoutMs);
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve(null);
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            LogAssert.ignoreFailingMessages = true;
            _httpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _secondHttpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _httpSettings.ConfigureClientOwnedApi(config.BaseUrl, config.ApiKey, config.Model, 120);
            _secondHttpSettings.ConfigureClientOwnedApi(config.BaseUrl, config.ApiKey, config.Model, 120);

            InMemoryStore firstStore = new();
            InMemoryStore secondStore = new();
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            // WHY: both clients share one meter, so its peak counts requests of the two agents that were inside
            // the ILlmClient boundary at the same moment. The meter wraps MeaiLlmClient from outside, so that
            // boundary is the only overlap it can observe.
            InFlightMeter inFlight = new();
            ILlmClient firstClient = new InFlightCountingLlmClient(MeaiLlmClient.CreateHttp(_httpSettings, logger,
                supportsNativeToolCalling: config.NativeTools, memoryStore: firstStore), inFlight);
            ILlmClient secondClient = new InFlightCountingLlmClient(MeaiLlmClient.CreateHttp(_secondHttpSettings, logger,
                supportsNativeToolCalling: config.NativeTools, memoryStore: secondStore), inFlight);
            AiOrchestrator firstAgent = BuildOrchestrator(firstClient, firstStore, _httpSettings);
            AiOrchestrator secondAgent = BuildOrchestrator(secondClient, secondStore, _secondHttpSettings);

            CancellationTokenSource cts = _requests.CreateCancellation();

            // WHY: Start both turns before yielding so neither can complete before the other is submitted.
            Task<string> first = _requests.Track(firstAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = FirstHint,
                MaxOutputTokens = HttpPairMaxOutputTokens
            }, cts.Token));
            Task<string> second = _requests.Track(secondAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = SecondHint,
                MaxOutputTokens = HttpPairMaxOutputTokens
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(Task.WhenAll(first, second), _requests.Cap(150f),
                "two HTTP agents running concurrently", cts);

            Assert.IsFalse(first.IsFaulted, first.Exception?.GetBaseException().Message);
            Assert.IsFalse(second.IsFaulted, second.Exception?.GetBaseException().Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(first.Result), "The first agent returned no text.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(second.Result), "The second agent returned no text.");
            Debug.Log($"[ParallelHttp] first='{first.Result.Trim()}' second='{second.Result.Trim()}' " +
                      $"peak in-flight={inFlight.Peak}");

            // WHY: each agent must answer its own prompt; the other agent's word in an answer is cross-talk.
            AssertMentions(first.Result, FirstWord, SecondWord, "first agent's answer");
            AssertMentions(second.Result, SecondWord, FirstWord, "second agent's answer");

            // WHY: separate store instances prove nothing by themselves; what isolates the agents is that each
            // turn was recorded in its own store and none of the other agent's turn leaked into it.
            AssertStoreHoldsOnlyOwnTurn(firstStore, FirstHint, FirstWord, SecondWord, "first agent's store");
            AssertStoreHoldsOnlyOwnTurn(secondStore, SecondHint, SecondWord, FirstWord, "second agent's store");

            // WHY: a peak of two means both agents' requests entered the ILlmClient boundary concurrently, i.e.
            // nothing above it (orchestrator, shared state, a lock) serialized them. Whether they also overlap
            // inside MeaiLlmClient, on the wire or at a serial provider is outside what this meter can see.
            Assert.AreEqual(2, inFlight.Peak,
                "Both agents' requests must be inside the ILlmClient boundary at the same time: a peak of 1 " +
                "means the turns were serialized above the client, more than 2 means an agent issued " +
                "overlapping requests.");
        }

        private static void AssertMentions(string text, string expected, string forbidden, string what)
        {
            string value = text ?? "";
            Assert.IsTrue(value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0,
                $"The {what} must contain '{expected}': '{value.Trim()}'.");
            Assert.IsTrue(value.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                $"The {what} contains the other agent's word '{forbidden}': '{value.Trim()}'.");
        }

        /// <summary>
        /// Asserts the store recorded this agent's own user turn and answer, and that no user/assistant entry
        /// and no memory document in it mentions the other agent's word.
        /// </summary>
        private static void AssertStoreHoldsOnlyOwnTurn(
            InMemoryStore store, string ownHint, string ownWord, string otherWord, string what)
        {
            ChatMessage[] history = store.GetChatHistory(BuiltInAgentRoleIds.SmartChat);
            Assert.IsTrue(history.Any(m => m.Role == "user" && m.Content == ownHint),
                $"The {what} must hold its own user turn '{ownHint}'.");
            Assert.IsTrue(history.Any(m => m.Role == "assistant" &&
                                           (m.Content ?? "").IndexOf(ownWord, StringComparison.OrdinalIgnoreCase) >= 0),
                $"The {what} must hold its own answer containing '{ownWord}'.");

            foreach (KeyValuePair<string, List<ChatMessage>> entry in store.ChatHistories)
            {
                foreach (ChatMessage message in entry.Value)
                {
                    if (message.Role != "user" && message.Role != "assistant")
                    {
                        continue;
                    }

                    Assert.IsTrue((message.Content ?? "").IndexOf(otherWord, StringComparison.OrdinalIgnoreCase) < 0,
                        $"The {what} holds a '{message.Role}' entry under '{entry.Key}' from the other agent: " +
                        $"'{message.Content}'.");
                }
            }

            foreach (KeyValuePair<string, AgentMemoryState> entry in store.States)
            {
                string memory = entry.Value?.Memory ?? "";
                Assert.IsTrue(memory.IndexOf(otherWord, StringComparison.OrdinalIgnoreCase) < 0,
                    $"The {what} memory under '{entry.Key}' mentions the other agent: '{memory}'.");
            }
        }

        /// <summary>
        /// Builds a SmartChat orchestrator over <paramref name="client"/> and <paramref name="store"/>; shared
        /// with <c>MixedBackendParallelAgentsPlayModeTests</c>.
        /// </summary>
        internal static AiOrchestrator BuildOrchestrator(
            ILlmClient client, IAgentMemoryStore store, CoreAISettingsAsset settings)
        {
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            AgentMemoryPolicy policy = new();
            TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.SmartChat);

            CoreAISettingsAsset effective = settings != null
                ? settings
                : ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                effective,
                new LocalActorIdentityProvider("mixed-backend-parallel-test"));
        }
    }
}
#endif

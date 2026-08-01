using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// LIVE runtime backend switch: the scope boots in Offline mode, then
    /// <see cref="CoreAiBackend.ApplyHttpApi"/> retargets it at the configured local/CI
    /// OpenAI-compatible server and the very next request must round-trip through the real model.
    /// Uses the same env/file/asset resolution as the rest of the live suite.
    /// </summary>
    public sealed class RuntimeBackendSwitchLivePlayModeTests
    {
        private CoreAISettingsAsset _previousInstance;
        private CoreAISettingsAsset _settings;
        private GameObject _scopeGo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousInstance = CoreAISettingsAsset.Instance;
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureOffline();
            CoreAISettingsAsset.SetInstance(_settings);

            _scopeGo = new GameObject("RuntimeBackendSwitchLiveScope");
            _scopeGo.SetActive(false);
            CoreAILifetimeScope scope = _scopeGo.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(scope, _settings);
            _scopeGo.SetActive(true);

            CoreAi.Invalidate();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scopeGo != null)
            {
                Object.Destroy(_scopeGo);
            }

            CoreAISettingsAsset.SetInstance(_previousInstance);
            CoreAi.Invalidate();
            if (_settings != null)
            {
                Object.Destroy(_settings);
            }

            yield return null;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator OfflineScope_SwitchedToLiveHttp_AnswersThroughRealModel()
        {
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve(null);
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            // Boot state: offline stub answers.
            Assert.AreEqual(LlmExecutionMode.Offline, CoreAiBackend.Status.Mode);

            // Runtime switch to the live server (no scene reload, no container rebuild).
            bool live = CoreAiBackend.ApplyHttpApi(config.BaseUrl, config.ApiKey, config.Model,
                timeoutSeconds: 120);
            Assert.IsTrue(live, "Switch must hot-swap the active client.");
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, CoreAiBackend.Status.Mode);

            // Health probe round-trips through the real model.
            Task<CoreAiBackendHealth> verify = CoreAiBackend.VerifyAsync(120);
            yield return WaitTask(verify, 180f);
            Assert.IsTrue(verify.Result.Ok,
                $"Health probe must pass against the live backend. Error: {verify.Result.Error}");
            Assert.Greater(verify.Result.LatencyMs, 0);
            Debug.Log($"[RuntimeBackendSwitch] Live probe OK in {verify.Result.LatencyMs:F0} ms " +
                      $"({verify.Result.Model})");

            // A real orchestrated request through the swapped backend.
            Task<string> ask = CoreAi.OrchestrateAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with the single word: pong",
                MaxOutputTokens = 512
            });
            yield return WaitTask(ask, 180f);

            Assert.IsFalse(string.IsNullOrWhiteSpace(ask.Result),
                "The live model must produce a non-empty answer after the runtime switch.");
            Debug.Log($"[RuntimeBackendSwitch] Live answer: {ask.Result}");
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!task.IsCompleted)
            {
                Assert.Fail($"Task did not complete within {timeoutSeconds}s.");
            }

            if (task.IsFaulted)
            {
                Assert.Fail($"Task faulted: {task.Exception?.GetBaseException().Message}");
            }
        }
    }

#if COREAI_LLM && (!UNITY_WEBGL || UNITY_EDITOR)
    /// <summary>
    /// Explicit, paid live proof that three synthetic students reuse one real CoreAI agent prefix while their
    /// request-specific instructions remain in the volatile tail.
    /// </summary>
    public sealed class PromptCacheLivePlayModeTests
    {
        private const string RoleId = "PromptCacheTeacherLiveProbe";
        private const int AttemptCount = 3;

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator ThreeDifferentStudentTails_ReuseStableRolePrefix_ByThirdRequest()
        {
            if (!PlayModeOpenAiTestConfig.IsPromptCacheProbeEnabled())
            {
                Assert.Ignore(
                    $"Paid prompt-cache probe is disabled. Set " +
                    $"{PlayModeOpenAiTestConfig.EnvPromptCacheProbe}=true after configuring the live endpoint.");
            }

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    0f,
                    90,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignoreReason))
            {
                Assert.Ignore(ignoreReason);
            }

            CoreAISettingsAsset settings = null;
            try
            {
                CapturingLlmClient capturing = new(handle.Client);
                InMemoryAgentTurnTraceSink traceSink = new(AttemptCount + 2);
                settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                settings.SetOrchestratorTimeoutSeconds(90);

                AgentMemoryPolicy policy = new();
                string stableRolePrompt = BuildLongStableRolePrompt();
                new AgentBuilder(RoleId, settings)
                    .WithMode(AgentMode.ToolsOnly)
                    .WithoutChatHistory()
                    .WithSystemPrompt(stableRolePrompt)
                    .WithTool(new DelegateLlmTool(
                        "lookup_curriculum_standard",
                        "Look up a curriculum standard by its stable catalog code.",
                        (System.Func<string>)(() => "not used by this probe")))
                    .WithMaxOutputTokens(32)
                    .WithMaxToolCallRoundtrips(1)
                    .Build()
                    .ApplyToPolicy(policy);

                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore(),
                    memoryPolicy: policy,
                    settings: settings);
                AiOrchestrator orchestrator = new(
                    new SoloAuthorityHost(),
                    capturing,
                    new NullCommandSink(),
                    new SessionTelemetryCollector(),
                    composer,
                    new NullAgentMemoryStore(),
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    settings,
                    traceSink: traceSink);

                string[] tailMarkers = { "synthetic-learner-a", "synthetic-learner-b", "synthetic-learner-c" };
                for (int i = 0; i < AttemptCount; i++)
                {
                    using CancellationTokenSource cts = new(System.TimeSpan.FromSeconds(90));
                    Task<string> turn = orchestrator.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = RoleId,
                        TraceId = $"prompt-cache-live-{i + 1}",
                        RequestSystemInstructions =
                            $"Runtime student context {tailMarkers[i]}; progress checkpoint {i + 1}. " +
                            "This synthetic value must remain after the shared prefix.",
                        Hint = $"Synthetic student turn {i + 1}: reply with the single word OK.",
                        ForcedToolMode = LlmToolChoiceMode.None,
                        MaxOutputTokens = 32,
                        MaxToolCallRoundtrips = 1
                    }, cts.Token);

                    yield return WaitTask(turn, 95f, cts);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(turn.Result),
                        $"Live cache attempt {i + 1} returned no assistant text.");

                    if (i + 1 < AttemptCount)
                    {
                        // WHY: Some providers publish a new cache entry asynchronously. Keep the delay short and bounded.
                        float delayUntil = Time.realtimeSinceStartup + 1.25f;
                        while (Time.realtimeSinceStartup < delayUntil)
                        {
                            yield return null;
                        }
                    }
                }

                Assert.AreEqual(AttemptCount, capturing.Requests.Count,
                    "Each synthetic student should produce exactly one top-level CoreAI completion request.");
                string stableSystemPrompt = capturing.Requests[0].SystemPrompt;
                Assert.Greater(stableSystemPrompt.Length, 16000,
                    "The live probe needs a realistically long cache-eligible role/tool prefix.");
                for (int i = 0; i < AttemptCount; i++)
                {
                    Assert.IsTrue(string.Equals(
                            stableSystemPrompt,
                            capturing.Requests[i].SystemPrompt,
                            System.StringComparison.Ordinal),
                        $"SystemPrompt changed for synthetic student {i + 1}; shared cache reuse is impossible.");
                    Assert.IsTrue(capturing.Requests[i].ChatHistory != null &&
                                  capturing.Requests[i].ChatHistory.Any(message =>
                                      (message.Text ?? "").Contains(tailMarkers[i])),
                        $"Synthetic student {i + 1} instructions did not reach the volatile history tail.");
                }

                AgentTurnTrace[] traces = traceSink.Snapshot();
                Assert.AreEqual(AttemptCount, traces.Length);
                string diagnostics = BuildCacheDiagnostics(handle, traces, stableSystemPrompt.Length);
                Debug.Log("[PromptCacheLive] " + diagnostics);
                Assert.IsTrue(traces.Skip(1).Take(AttemptCount - 1).Any(trace => trace.CacheReadTokens > 0),
                    "No provider-reported cache hit by the third request. " + diagnostics +
                    " Check model cache support, exact endpoint/provider pinning, and the configured cohort session_id.");
            }
            finally
            {
                handle.Dispose();
                if (settings != null)
                {
                    Object.Destroy(settings);
                }
            }
        }

        private static string BuildLongStableRolePrompt()
        {
            StringBuilder prompt = new();
            prompt.AppendLine(
                "You are the shared CoreAI curriculum teacher. Apply the stable policy below identically for every learner.");
            for (int i = 0; i < 220; i++)
            {
                prompt.Append("Stable curriculum policy ").Append(i.ToString("D3"))
                    .Append(
                        ": verify the concept, give one concise answer, preserve safety boundaries, and never infer private learner data.\n");
            }

            prompt.Append("Answer the current user request after all volatile context messages.");
            return prompt.ToString();
        }

        private static string BuildCacheDiagnostics(
            PlayModeProductionLikeLlmHandle handle,
            IReadOnlyList<AgentTurnTrace> traces,
            int prefixChars)
        {
            string baseUrl = handle.ResolvedConfig?.BaseUrl ?? "unknown-endpoint";
            string provider;
            try
            {
                provider = new System.Uri(baseUrl).Host;
            }
            catch
            {
                provider = "invalid-endpoint";
            }

            string configuredModel = handle.ResolvedConfig?.Model ?? "unknown-model";
            string attempts = string.Join(", ", traces.Select((trace, index) =>
                $"#{index + 1}[servedModel=" +
                $"{(string.IsNullOrWhiteSpace(trace.Model) ? "provider-unreported" : trace.Model)}," +
                $"prompt={trace.PromptTokens},completion={trace.CompletionTokens}," +
                $"cacheRead={trace.CacheReadTokens},cacheWrite={trace.CacheWriteTokens}]"));
            return
                $"provider={provider}; configuredModel={configuredModel}; stablePrefixChars={prefixChars}; {attempts}";
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds, CancellationTokenSource cts)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!task.IsCompleted)
            {
                cts.Cancel();
                Assert.Fail($"Prompt-cache live request did not complete within {timeoutSeconds}s.");
            }

            if (task.IsFaulted)
            {
                Assert.Fail($"Prompt-cache live request faulted: {task.Exception?.GetBaseException().Message}");
            }

            if (task.IsCanceled)
            {
                Assert.Fail("Prompt-cache live request was cancelled before completion.");
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public List<LlmCompletionRequest> Requests { get; } = new();
            public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

            public bool SupportsNativeToolCallingForRole(string agentRoleId)
            {
                return _inner.SupportsNativeToolCallingForRole(agentRoleId);
            }

            public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
            {
                return _inner.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId);
            }

            public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
            {
                return _inner.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return _inner.CompleteAsync(request, cancellationToken);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        private sealed class NullCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
#endif
}

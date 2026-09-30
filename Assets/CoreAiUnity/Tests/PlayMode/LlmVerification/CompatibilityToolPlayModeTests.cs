#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Crafting;
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
    /// PlayMode   CompatibilityLlmTool   LLM.
    ///      check_compatibility tool   .
    /// </summary>
    public sealed class CompatibilityToolPlayModeTests
    {
        // WHY: each test is an LLMUnity readiness wait (up to 120 s on a cold start) + one 240 s tool turn
        // + the 20 s LiveTestRequestScope reserve + 20 s margin = 400 s; Cap keeps the turn's own cancelling
        // wait ahead of the framework abort when the load is slow.
        private const int TestTimeoutMs = 400000;

        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup; cancel and let the abandoned turn unwind
            // before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _handle?.Dispose();
            _handle = null;
        }

        /// <summary>
        /// : LLM  check_compatibility      .
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator CompatibilityTool_CompatibleIngredients_LlmReportsCompatible()
        {
            Debug.Log("[CompatibilityTest] === TEST 1: Compatible ingredients ===");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

            //  checker   Fire+Earth
            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Earth", 1.5f, "Fire and Earth create lava  bonus synergy");
            CompatibilityLlmTool tool = new(checker);

            AgentConfig agent = new AgentBuilder("TestCompatChecker")
                .WithSystemPrompt(
                    "You are a crafting assistant. When the user asks to check ingredients, " +
                    "use the available compatibility capability, then report whether they are compatible " +
                    "and mention the score.")
                .WithTool(tool)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<TestResult> task = _requests.Track(
                RunAgentTestAsync(clientWithStore, agent, "Check if Fire and Earth are compatible", cts.Token));
            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(240f), "compatibility_compatible", cts);

            TestResult r = task.Result;
            Debug.Log(
                $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
            Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
            InconclusiveIfNoResponse(r, "compatible ingredients");
            Debug.Log("[CompatibilityTest] TEST 1 PASSED");
        }

        /// <summary>
        /// : LLM  check_compatibility   .
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator CompatibilityTool_IncompatibleIngredients_LlmReportsIncompatible()
        {
            Debug.Log("[CompatibilityTest] === TEST 2: Incompatible ingredients ===");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Water", 0f, "Fire and Water cancel each other out");
            CompatibilityLlmTool tool = new(checker);

            AgentConfig agent = new AgentBuilder("TestIncompatChecker")
                .WithSystemPrompt(
                    "You are a crafting assistant. When the user asks to check ingredients, " +
                    "use the available compatibility capability and report whether they are compatible " +
                    "or not, including warnings.")
                .WithTool(tool)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<TestResult> task = _requests.Track(
                RunAgentTestAsync(clientWithStore, agent, "Check if Fire and Water are compatible", cts.Token));
            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(240f), "compatibility_incompatible", cts);

            TestResult r = task.Result;
            Debug.Log(
                $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
            Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
            InconclusiveIfNoResponse(r, "incompatible ingredients");
            Debug.Log("[CompatibilityTest] TEST 2 PASSED");
        }

        /// <summary>
        /// : LLM  3    .
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator CompatibilityTool_ThreeIngredients_GroupRule()
        {
            Debug.Log("[CompatibilityTest] === TEST 3: Three ingredients group rule ===");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

            CompatibilityChecker checker = new();
            checker.AddGroupRule(1.8f, "Fire+Earth+Air create a volcanic eruption  amazing synergy!",
                "Fire", "Earth", "Air");
            CompatibilityLlmTool tool = new(checker);

            AgentConfig agent = new AgentBuilder("TestTripleChecker")
                .WithSystemPrompt(
                    "You are a crafting assistant. Use the available compatibility capability " +
                    "and report the compatibility result.")
                .WithTool(tool)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<TestResult> task = _requests.Track(
                RunAgentTestAsync(clientWithStore, agent, "Check compatibility of Fire, Earth, Air", cts.Token));
            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(240f), "compatibility_triple", cts);

            TestResult r = task.Result;
            Debug.Log(
                $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
            Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
            InconclusiveIfNoResponse(r, "three ingredients group rule");
            Debug.Log("[CompatibilityTest] TEST 3 PASSED");
        }

        // 
        // Helpers
        // 

        private sealed class TestResult
        {
            public string Response { get; set; }
            public int ToolsCount { get; set; }
        }

        private static void InconclusiveIfNoResponse(TestResult result, string phase)
        {
            if (!string.IsNullOrWhiteSpace(result.Response))
            {
                return;
            }

            Assert.Inconclusive(
                $"[{phase}] LLM backend returned no response. The compatibility tool was exposed, " +
                "but the configured real backend rejected the request or returned an infrastructure error.");
        }

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastContent;
            public IReadOnlyList<ILlmTool> LastTools;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastTools = request.Tools;
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken);
                if (result != null && result.Ok)
                {
                    LastContent = result.Content;
                }

                return result;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }

        private async Task<TestResult> RunAgentTestAsync(ILlmClient llm, AgentConfig cfg, string msg,
            CancellationToken cancellationToken)
        {
            InMemoryStore store = new();
            AgentMemoryPolicy policy = new();
            cfg.ApplyToPolicy(policy);
            CapturingLlmClient cap = new(llm);
            AiOrchestrator orch = new(
                new SoloAuthorityHost(), cap,
                new NullSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new CustomPromptProvider(cfg.SystemPrompt),
                    new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                store, policy, new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                new LocalActorIdentityProvider("compatibility-tool-test"));

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = cfg.RoleId, Hint = msg }, cancellationToken);
            return new TestResult { Response = cap.LastContent, ToolsCount = cap.LastTools?.Count ?? 0 };
        }

        private sealed class CustomPromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _p;

            public CustomPromptProvider(string p)
            {
                _p = p;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = _p;
                return !string.IsNullOrEmpty(prompt);
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
#endif

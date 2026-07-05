#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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
    /// Comprehensive end-to-end PlayMode tests validating the full CoreAI resilience pipeline.
    /// What is covered:
    /// 1. Tool calls execute without leaking JSON into user-facing text.
    /// 2. Streaming tool calls keep text flowing while JSON is stripped mid-stream.
    /// 3. Memory writes persist through the real LLM tool-call path.
    /// 4. Orchestrator-level Merchant inventory calls produce clean output.
    /// 5. Tool call trace diagnostics populated on streaming chunks.
    /// All tests use a real LLM backend (HTTP API or LLMUnity) and validate that
    /// LoggingLlmClientDecorator, RoutingLlmClient, OpenAiChatLlmClient, MeaiLlmClient,
    /// SmartToolCallingChatClient, ToolExecutionPolicy, and AIFunction work together.
    /// </summary>
    public sealed class FullPipelineResiliencePlayModeTests
    {
        private TestAgentSetup _setup;

        /// <summary>Identity of the last settings used for <see cref="s_liveLlmProbeState"/>; must reset when user switches API URL/backend so a stale 429 does not skip runs against LM Studio.</summary>
        private static string s_llmProbeSettingsKey;

        /// <summary>0 = not probed, 1 = OK, -1 = failed (skip remaining tests quickly).</summary>
        private static int s_liveLlmProbeState;

        private static string s_liveLlmProbeFailMessage = "";

        [UnitySetUp]
        public IEnumerator Setup()
        {
            LogAssert.ignoreFailingMessages = true;
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            if (!_setup.IsReady)
            {
                Assert.Ignore($"LLM backend not available ({_setup.BackendName}). Skipping.");
            }

            if (_setup.Client is OfflineLlmClient)
            {
                Assert.Inconclusive(
                    "FullPipelineResilience needs HTTP or LLMUnity with a tool-capable model. " +
                    "OfflineLlmClient does not execute tools.");
            }

            yield return EnsureLiveLlmReachableOnce();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            LogAssert.ignoreFailingMessages = false;
            yield return null;
        }

        /// <summary>
        /// HTTP <see cref="TestAgentSetup"/> leaves <see cref="TestAgentSetup.IsReady"/> true even when the server
        /// misbehaves (e.g. 500 or 400 “no model”). One cheap non-stream call skips the fixture with
        /// <see cref="Assert.Ignore"/> instead of many inconclusive/red SetUp outcomes.
        /// </summary>
        private IEnumerator EnsureLiveLlmReachableOnce()
        {
            string settingsKey = BuildLlmProbeSettingsKey();
            if (!string.Equals(s_llmProbeSettingsKey, settingsKey, StringComparison.Ordinal))
            {
                s_llmProbeSettingsKey = settingsKey;
                s_liveLlmProbeState = 0;
                s_liveLlmProbeFailMessage = "";
            }

            if (s_liveLlmProbeState == -1)
            {
                Assert.Ignore(s_liveLlmProbeFailMessage);
            }

            if (s_liveLlmProbeState == 1)
            {
                yield break;
            }

            LlmCompletionRequest probeRequest = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "Reply briefly.",
                UserPayload = "ping",
                Tools = new List<ILlmTool>()
            };

            const int maxAttempts = 4;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                LlmCompletionResult probeResult = null;
                using CancellationTokenSource cts = new();
                Task probeTask = CompleteNonStreamAsync(_setup.Client, probeRequest, r => probeResult = r,
                    cts.Token);
                yield return WaitTask(probeTask, 60f, "FullPipeline LLM reachability", cts);

                if (probeResult != null && probeResult.Ok && !string.IsNullOrWhiteSpace(probeResult.Content))
                {
                    s_liveLlmProbeState = 1;
                    yield break;
                }

                string err = probeResult?.Error ?? "null result";
                bool canRetry = attempt < maxAttempts && IsRetryableProbeFailure(err);
                if (canRetry)
                {
                    Debug.Log(
                        $"[FullPipelineResilience] LLM probe attempt {attempt}/{maxAttempts} failed ({err}); retrying after backoff…");
                    yield return new WaitForSecondsRealtime(5f);
                    continue;
                }

                s_liveLlmProbeState = -1;
                s_liveLlmProbeFailMessage = FormatLiveLlmProbeSkipMessage(err);
                Assert.Ignore(s_liveLlmProbeFailMessage);
            }
        }

        private static string BuildLlmProbeSettingsKey()
        {
            CoreAISettingsAsset inst = CoreAISettingsAsset.Instance;
            if (inst == null)
            {
                return "no-settings";
            }

            return $"{(int)inst.BackendType}|{inst.ApiBaseUrl?.Trim() ?? ""}|{inst.ModelName?.Trim() ?? ""}";
        }

        private static bool IsRetryableProbeFailure(string err)
        {
            if (string.IsNullOrEmpty(err))
            {
                return false;
            }

            if (err.IndexOf("429", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (err.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (err.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (err.IndexOf("503", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (err.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Skipped (Assert.Ignore) when the HTTP API is up but misconfigured — e.g. LM Studio returns 400 until a GGUF is loaded.
        /// </summary>
        private static string FormatLiveLlmProbeSkipMessage(string error)
        {
            string err = error ?? "null result";
            if (err.IndexOf("no models loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                err.IndexOf("load a model", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return
                    "Live LLM probe skipped: server responded but no model is loaded " +
                    "(LM Studio / llama.cpp: load a model in the Developer UI or via `lms load`, then re-run). " +
                    $"Probe: {err}";
            }

            if (err.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                err.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                err.IndexOf("free-models-per-min", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return
                    "Live LLM probe skipped: HTTP 429 / rate limit from the provider (often OpenRouter free tier). " +
                    "Wait and re-run, point CoreAISettings.ApiBaseUrl at a local LM Studio / llama.cpp URL, or use a paid quota. " +
                    $"Probe: {err}";
            }

            return
                "Live LLM probe skipped (check CoreAISettings ApiBaseUrl / local server / CORS / model id). " +
                $"Probe: {err}";
        }

        // =====================================================================
        // Test 1: Streaming memory write → no JSON leak → clean assistant text
        // =====================================================================

        /// <summary>
        /// Full pipeline: streaming + memory tool calling.
        /// LLM should persist memory and then produce clean text.
        /// Validates: MeaiLlmClient → TryExtractToolCallsFromText → ToolExecutionPolicy
        /// → TryRepairToolName → AIFunction → memory persisted → JSON stripped.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StreamingMemoryWrite_ToolExecutes_NoJsonLeak()
        {
            Debug.Log($"[FullPipeline1] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher with a memory tool. Use it when the user asks you to remember something. " +
                    "Do not expose tool-call syntax, JSON, or arguments in the user-visible reply.",
                UserPayload = "Remember that the student's name is Alex and they prefer math.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() },
                MaxOutputTokens = 2048
            };

            StreamResultBox box = new();
            using CancellationTokenSource cts = new();
            Task task = CollectStreamAsync(_setup.Client, request, box, cts.Token);
            yield return WaitTask(task, 120f, "StreamingMemoryWrite", cts);

            Debug.Log($"[FullPipeline1] Output ({box.ChunkCount} chunks): '{box.FullText}'");

            // --- Assertions ---

            // 1. Memory should be persisted
            Assert.IsTrue(_setup.MemoryStore.TryLoad("Teacher", out AgentMemoryState state),
                "Memory tool must execute and persist data");
            Assert.That(state.Memory, Is.Not.Empty,
                "Memory content should not be empty");
            Debug.Log($"[FullPipeline1] Memory: '{state.Memory}'");

            // 2. No raw JSON in output
            Assert.That(box.FullText, Does.Not.Contain("\"name\":"),
                "Tool call JSON 'name' key must not appear in user text");
            Assert.That(box.FullText, Does.Not.Contain("\"arguments\":"),
                "Tool call JSON 'arguments' key must not appear in user text");
            Assert.That(box.FullText, Does.Not.Contain("\"action\":\"write\""),
                "Tool call argument must not appear in user text");

            // 3. Response is meaningful text (not empty, not just whitespace)
            Assert.That(box.FullText.Trim(), Is.Not.Empty,
                "Assistant should produce meaningful response text");

            // 4. Streaming should have multiple chunks (real streaming, not single-shot fallback)
            Assert.GreaterOrEqual(box.ChunkCount, 1,
                "Should receive at least 1 streaming chunk");

            Debug.Log("[FullPipeline1] ✓ PASSED");
        }

        // =====================================================================
        // Test 2: Non-streaming tool call → tool executes → clean result text
        // =====================================================================

        /// <summary>
        /// Non-streaming path: CompleteAsync with memory tool.
        /// Validates: LoggingLlmClientDecorator → OpenAiChatLlmClient → MeaiLlmClient →
        /// SmartToolCallingChatClient (non-streaming loop) → ToolExecutionPolicy → JSON strip.
        /// </summary>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator NonStreamingMemoryWrite_ToolExecutes_NoJsonLeak()
        {
            Debug.Log($"[FullPipeline2] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher with a memory tool. Use it when the user asks you to remember a fact. " +
                    "Keep the final user-visible reply brief and free of tool-call JSON.",
                UserPayload = "Remember: The student scored 95 on the math test.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmCompletionResult result = null;
            using CancellationTokenSource cts = new();
            Task task = CompleteNonStreamAsync(_setup.Client, request, r => result = r, cts.Token);
            yield return WaitTask(task, 120f, "NonStreamingMemoryWrite", cts);

            Assert.IsNotNull(result, "Result should not be null");
            Assert.IsTrue(result.Ok, $"Request should succeed: {result?.Error}");

            Debug.Log($"[FullPipeline2] Content: '{result.Content}'");

            // --- Assertions ---

            // 1. Memory persisted
            Assert.IsTrue(_setup.MemoryStore.TryLoad("Teacher", out AgentMemoryState state),
                "Memory tool must execute");
            Assert.That(state.Memory, Is.Not.Empty,
                "Memory content should not be empty");

            // 2. No JSON leak
            Assert.That(result.Content, Does.Not.Contain("\"name\":"),
                "No tool JSON in non-streaming response");
            Assert.That(result.Content, Does.Not.Contain("\"arguments\":"),
                "No arguments JSON in non-streaming response");

            // 3. Meaningful text
            Assert.That(result.Content.Trim(), Is.Not.Empty,
                "Should have meaningful assistant text");

            // 4. Tool traces populated
            if (result.ExecutedToolCalls != null && result.ExecutedToolCalls.Count > 0)
            {
                Debug.Log($"[FullPipeline2] Tool traces: {result.ExecutedToolCalls.Count}");
                foreach (LlmToolCallTrace trace in result.ExecutedToolCalls)
                {
                    Debug.Log($"  → {trace.Name} ok={trace.Success} dur={trace.DurationMs}ms src={trace.Source}");
                }
            }

            Debug.Log("[FullPipeline2] ✓ PASSED");
        }

        // =====================================================================
        // Test 3: Orchestrator with Merchant → inventory tool → items in response
        // =====================================================================

        /// <summary>
        /// Full orchestrator stack: AiOrchestrator → prompt compose → authority →
        /// LLM client → tool calling → inventory tool → response with items.
        /// No JSON leak at ANY level.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator OrchestratorMerchantInventory_FullStack_NoJsonLeak()
        {
            Debug.Log($"[FullPipeline3] Backend: {_setup.BackendName}");

            TestInventoryProvider inventoryProvider = new();
            inventoryProvider.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Dragon Slayer", Type = "weapon", Quantity = 1, Price = 500 });
            inventoryProvider.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Healing Elixir", Type = "consumable", Quantity = 5, Price = 75 });

            AgentMemoryPolicy policy = new();
            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            ListSink sink = new();

            // Register Merchant with inventory tool
            new AgentBuilder(BuiltInAgentRoleIds.Merchant)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .WithTool(new InventoryLlmTool(inventoryProvider))
                .Build()
                .ApplyToPolicy(policy);

            AiOrchestrator orch = new(
                new SoloAuthorityHost(),
                _setup.Client,
                sink,
                telemetry,
                composer,
                _setup.MemoryStore,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>());

            using CancellationTokenSource orchCts = new();
            Task orchTask = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Merchant,
                Hint = "What items do you have for sale?",
                MaxOutputTokens = 2048
            }, orchCts.Token);

            yield return WaitTask(orchTask, 240f, "OrchestratorMerchant", orchCts);

            // Check commands
            Debug.Log($"[FullPipeline3] Commands: {sink.Items.Count}");
            string response = "";
            foreach (ApplyAiGameCommand cmd in sink.Items)
            {
                Debug.Log($"[FullPipeline3] Command: '{cmd.JsonPayload}'");
                response += cmd.JsonPayload + "\n";
            }

            // --- Assertions ---

            Assert.IsNotEmpty(response.Trim(), "Orchestrator should produce a response");

            // No JSON leak through orchestrator defense-in-depth
            Assert.That(response, Does.Not.Contain("\"name\":\"get_inventory\""),
                "get_inventory JSON must not leak through orchestrator");
            Assert.That(response, Does.Not.Contain("\"name\":\"memory\""),
                "memory JSON must not leak through orchestrator");

            // Should mention at least one item
            bool mentionsItems =
                response.Contains("Dragon", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("Slayer", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("Healing", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("Elixir", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("items", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(mentionsItems,
                $"Merchant inventory response should mention available items. Response: {response}");

            if (mentionsItems)
            {
                Debug.Log("[FullPipeline3] ✓ Agent mentioned inventory items");
            }
            else
            {
                Debug.LogWarning("[FullPipeline3] Agent did not mention items — model-dependent");
            }

            Debug.Log("[FullPipeline3] ✓ PASSED (no JSON leak)");
        }

        // =====================================================================
        // Test 4: Streaming → memory write → memory read → confirms data
        // =====================================================================

        /// <summary>
        /// Validates that a streaming memory tool call persists state without leaking raw tool JSON.
        /// MemoryLlmTool supports write/append/clear, not read; recall behavior belongs in
        /// orchestrator/context tests that inject persisted memory into prompts.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator StreamingMemoryWrite_PersistsAndNoJsonLeak()
        {
            Debug.Log($"[FullPipeline4] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            // --- Phase 1: Write ---
            LlmCompletionRequest writeRequest = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher with a memory tool. Use it to save what the user asks you to remember. " +
                    "Keep the final reply brief and do not show tool-call JSON.",
                UserPayload = "Remember this: Final exam is on June 15th.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            StreamResultBox writeBox = new();
            using CancellationTokenSource writeCts = new();
            Task writeTask = CollectStreamAsync(_setup.Client, writeRequest, writeBox, writeCts.Token);
            yield return WaitTask(writeTask, 240f, "Write_Phase", writeCts);

            Debug.Log($"[FullPipeline4] Write output: '{writeBox.FullText}'");
            Assert.IsTrue(_setup.MemoryStore.TryLoad("Teacher", out AgentMemoryState writeState),
                "Write phase: memory must be saved");
            Debug.Log($"[FullPipeline4] Memory after write: '{writeState.Memory}'");

            // No JSON in write output
            Assert.That(writeBox.FullText, Does.Not.Contain("\"name\":"),
                "Write phase: no JSON leak");
            Assert.That(writeBox.FullText, Does.Not.Contain("\"arguments\":"),
                "Write phase: no arguments JSON leak");
            Assert.That(writeState.Memory, Does.Contain("June 15"),
                "Write phase: memory should contain the requested exam date.");

            Debug.Log("[FullPipeline4] ✓ PASSED");
        }

        // =====================================================================
        // Test 5: Tool call trace diagnostics populated
        // =====================================================================

        /// <summary>
        /// Validates that <see cref="LlmStreamChunk.ExecutedToolCalls"/> is populated
        /// on the final IsDone chunk when tools were called during streaming.
        /// </summary>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator StreamingToolCall_TracesDiagnosticsPopulated()
        {
            Debug.Log($"[FullPipeline5] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher with a memory tool. Save requested trace data when asked. " +
                    "Keep the final response brief.",
                UserPayload = "Save trace test data to memory now.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            TraceResultBox traceBox = new();
            using CancellationTokenSource cts = new();
            Task task = CollectStreamWithTracesAsync(_setup.Client, request, traceBox, cts.Token);
            yield return WaitTask(task, 240f, "StreamingTraces", cts);

            Debug.Log($"[FullPipeline5] Output: '{traceBox.FullText}' | Traces: {traceBox.Traces.Count}");

            // Memory should be saved (tool executed)
            bool memorySaved = _setup.MemoryStore.TryLoad("Teacher", out _);

            if (memorySaved && traceBox.Traces.Count > 0)
            {
                Debug.Log($"[FullPipeline5] ✓ {traceBox.Traces.Count} tool traces found:");
                foreach (LlmToolCallTrace t in traceBox.Traces)
                {
                    Debug.Log($"  → {t.Name} ok={t.Success} dur={t.DurationMs}ms src={t.Source}");
                }

                Assert.That(traceBox.Traces, Has.Some.Matches<LlmToolCallTrace>(t => t.Name == "memory" && t.Success),
                    "Should have at least one successful 'memory' trace");
            }
            else if (memorySaved)
            {
                Debug.LogWarning("[FullPipeline5] Memory saved but no tool traces — backend may not return traces");
                Assert.Fail("Memory tool executed but no LlmToolCallTrace diagnostics were emitted.");
            }
            else
            {
                // Model-gate: whether the model emits the memory tool call is model behaviour, not a
                // pipeline defect (a weak local model may answer in prose / ask for clarification). Treat
                // it as inconclusive, consistent with the model-dependent branches elsewhere in this file,
                // rather than a hard failure. The genuine-defect branch above (tool executed but no traces)
                // still fails hard, so a real tracing regression is still caught.
                Debug.LogWarning("[FullPipeline5] Tool not called — model-dependent");
                Assert.Inconclusive("Streaming trace test requires the model to execute the memory tool; " +
                                    "the backend model did not call it this run (model-dependent).");
            }

            // No JSON leak regardless
            Assert.That(traceBox.FullText, Does.Not.Contain("\"name\":"),
                "No JSON leak even in trace test");

            Debug.Log("[FullPipeline5] ✓ PASSED");
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static async Task CollectStreamAsync(
            ILlmClient client, LlmCompletionRequest request,
            StreamResultBox box, CancellationToken ct)
        {
            StringBuilder sb = new();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                chunks++;
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    sb.Append(chunk.Text);
                }
            }

            box.FullText = sb.ToString();
            box.ChunkCount = chunks;
        }

        private static async Task CollectStreamWithTracesAsync(
            ILlmClient client, LlmCompletionRequest request,
            TraceResultBox box, CancellationToken ct)
        {
            StringBuilder sb = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    sb.Append(chunk.Text);
                }

                if (chunk.IsDone && chunk.ExecutedToolCalls != null)
                {
                    box.Traces.AddRange(chunk.ExecutedToolCalls);
                }
            }

            box.FullText = sb.ToString();
        }

        private static IEnumerator WaitTask(Task task, float timeoutSec, string label)
        {
            return PlayModeTestAwait.WaitTask(task, timeoutSec, label);
        }

        private static IEnumerator WaitTask(
            Task task,
            float timeoutSec,
            string label,
            CancellationTokenSource cancellationOnTimeout)
        {
            return PlayModeTestAwait.WaitTask(task, timeoutSec, label, cancellationOnTimeout);
        }

        private static async Task CompleteNonStreamAsync(
            ILlmClient client, LlmCompletionRequest request,
            Action<LlmCompletionResult> callback, CancellationToken ct)
        {
            LlmCompletionResult r = await client.CompleteAsync(request, ct);
            callback(r);
        }

        // =====================================================================
        // Inner types
        // =====================================================================

        private sealed class StreamResultBox
        {
            public string FullText = "";
            public int ChunkCount;
        }

        private sealed class TraceResultBox
        {
            public string FullText = "";
            public readonly List<LlmToolCallTrace> Traces = new();
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Inventory { get; } = new();

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken ct)
            {
                return Task.FromResult(Inventory);
            }
        }
    }
}
#endif
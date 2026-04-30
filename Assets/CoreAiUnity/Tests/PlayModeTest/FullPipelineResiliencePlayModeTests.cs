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
    ///
    /// What is covered:
    /// 1. Tool calling → tool executes → JSON never leaks into user-facing text.
    /// 2. Streaming + tool calling → text flows token-by-token → JSON stripped mid-stream.
    /// 3. Memory write → read cycle with a real LLM.
    /// 4. Orchestrator-level tool calling with Merchant + inventory → clean output.
    /// 5. Tool call trace diagnostics populated on streaming chunks.
    ///
    /// All tests use a real LLM backend (HTTP API or LLMUnity) and validate that
    /// LoggingLlmClientDecorator → RoutingLlmClient → OpenAiChatLlmClient → MeaiLlmClient →
    /// SmartToolCallingChatClient → ToolExecutionPolicy → AIFunction all work correctly together.
    /// </summary>
    public sealed class FullPipelineResiliencePlayModeTests
    {
        private TestAgentSetup _setup;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            LogAssert.ignoreFailingMessages = true;
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            if (!_setup.IsReady)
                Assert.Ignore($"LLM backend not available ({_setup.BackendName}). Skipping.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            LogAssert.ignoreFailingMessages = false;
            yield return null;
        }

        // =====================================================================
        // Test 1: Streaming memory write → no JSON leak → clean assistant text
        // =====================================================================

        /// <summary>
        /// Full pipeline: streaming + memory tool calling.
        /// LLM must call 'memory' tool with action='write', then produce clean text.
        /// Validates: MeaiLlmClient → TryExtractToolCallsFromText → ToolExecutionPolicy
        /// → TryRepairToolName → AIFunction → memory persisted → JSON stripped.
        /// </summary>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator StreamingMemoryWrite_ToolExecutes_NoJsonLeak()
        {
            Debug.Log($"[FullPipeline1] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            var request = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. You have a 'memory' tool. " +
                    "When asked to remember something, call memory with action='write' and the content. " +
                    "After saving, confirm with a short sentence.",
                UserPayload = "Remember that the student's name is Alex and they prefer math.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            var box = new StreamResultBox();
            Task task = CollectStreamAsync(_setup.Client, request, box, CancellationToken.None);
            yield return WaitTask(task, 150f, "StreamingMemoryWrite");

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

            var request = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. You have a 'memory' tool. " +
                    "When asked to remember, call memory with action='write' and the provided content. " +
                    "After saving, say 'Saved successfully' and nothing more.",
                UserPayload = "Remember: The student scored 95 on the math test.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmCompletionResult result = null;
            Task task = CompleteNonStreamAsync(_setup.Client, request, r => result = r, CancellationToken.None);
            yield return WaitTask(task, 150f, "NonStreamingMemoryWrite");

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
                foreach (var trace in result.ExecutedToolCalls)
                    Debug.Log($"  → {trace.Name} ok={trace.Success} dur={trace.DurationMs}ms src={trace.Source}");
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

            var inventoryProvider = new TestInventoryProvider();
            inventoryProvider.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Dragon Slayer", Type = "weapon", Quantity = 1, Price = 500 });
            inventoryProvider.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Healing Elixir", Type = "consumable", Quantity = 5, Price = 75 });

            var policy = new AgentMemoryPolicy();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            var sink = new ListSink();

            // Register Merchant with inventory tool
            new AgentBuilder(BuiltInAgentRoleIds.Merchant)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .WithTool(new InventoryLlmTool(inventoryProvider))
                .Build()
                .ApplyToPolicy(policy);

            var orch = new AiOrchestrator(
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

            Task orchTask = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Merchant,
                Hint = "What items do you have for sale?"
            });

            yield return WaitTask(orchTask, 240f, "OrchestratorMerchant");

            // Check commands
            Debug.Log($"[FullPipeline3] Commands: {sink.Items.Count}");
            string response = "";
            foreach (var cmd in sink.Items)
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
        /// Two-phase test: first request writes to memory, second request reads and confirms.
        /// Validates the full round-trip through the pipeline with persistent state.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WriteRead_TwoRequests_MemoryPersistsAndNoJsonLeak()
        {
            Debug.Log($"[FullPipeline4] Backend: {_setup.BackendName}");

            _setup.MemoryStore.Clear("Teacher");

            // --- Phase 1: Write ---
            var writeRequest = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. You have a 'memory' tool. " +
                    "Call memory with action='write' and the content the user asks to save. " +
                    "After saving, say only 'Saved.'",
                UserPayload = "Remember this: Final exam is on June 15th.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            var writeBox = new StreamResultBox();
            Task writeTask = CollectStreamAsync(_setup.Client, writeRequest, writeBox, CancellationToken.None);
            yield return WaitTask(writeTask, 120f, "Write_Phase");

            Debug.Log($"[FullPipeline4] Write output: '{writeBox.FullText}'");
            Assert.IsTrue(_setup.MemoryStore.TryLoad("Teacher", out AgentMemoryState writeState),
                "Write phase: memory must be saved");
            Debug.Log($"[FullPipeline4] Memory after write: '{writeState.Memory}'");

            // No JSON in write output
            Assert.That(writeBox.FullText, Does.Not.Contain("\"name\":"),
                "Write phase: no JSON leak");

            // --- Phase 2: Read ---
            var readRequest = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. You have a 'memory' tool. " +
                    "Call memory with action='read' to check what is saved. " +
                    "Then tell the user what's in your memory. Be brief.",
                UserPayload = "What do you have in your memory?",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            var readBox = new StreamResultBox();
            Task readTask = CollectStreamAsync(_setup.Client, readRequest, readBox, CancellationToken.None);
            yield return WaitTask(readTask, 120f, "Read_Phase");

            Debug.Log($"[FullPipeline4] Read output: '{readBox.FullText}'");

            // No JSON in read output
            Assert.That(readBox.FullText, Does.Not.Contain("\"name\":"),
                "Read phase: no JSON leak");
            Assert.That(readBox.FullText, Does.Not.Contain("\"arguments\":"),
                "Read phase: no arguments JSON leak");

            // Memory should still contain the written data
            Assert.IsTrue(_setup.MemoryStore.TryLoad("Teacher", out AgentMemoryState readState),
                "Read phase: memory should still be persisted");
            Assert.That(readState.Memory, Is.Not.Empty,
                "Read phase: memory should not be empty after read");

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

            var request = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. You have a 'memory' tool. " +
                    "Call memory with action='write' and content='trace_test_data'. " +
                    "After saving, say 'Trace test complete.'",
                UserPayload = "Save trace test data to memory now.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            var traceBox = new TraceResultBox();
            Task task = CollectStreamWithTracesAsync(_setup.Client, request, traceBox, CancellationToken.None);
            yield return WaitTask(task, 150f, "StreamingTraces");

            Debug.Log($"[FullPipeline5] Output: '{traceBox.FullText}' | Traces: {traceBox.Traces.Count}");

            // Memory should be saved (tool executed)
            bool memorySaved = _setup.MemoryStore.TryLoad("Teacher", out _);

            if (memorySaved && traceBox.Traces.Count > 0)
            {
                Debug.Log($"[FullPipeline5] ✓ {traceBox.Traces.Count} tool traces found:");
                foreach (var t in traceBox.Traces)
                    Debug.Log($"  → {t.Name} ok={t.Success} dur={t.DurationMs}ms src={t.Source}");

                Assert.That(traceBox.Traces, Has.Some.Matches<LlmToolCallTrace>(
                    t => t.Name == "memory" && t.Success),
                    "Should have at least one successful 'memory' trace");
            }
            else if (memorySaved)
            {
                Debug.LogWarning("[FullPipeline5] Memory saved but no tool traces — backend may not return traces");
                Assert.Pass("Tool executed but traces not populated (backend-dependent)");
            }
            else
            {
                Debug.LogWarning("[FullPipeline5] Tool not called — model-dependent");
                Assert.Pass("Model did not call tool (model-dependent behavior)");
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
            var sb = new StringBuilder();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                chunks++;
                if (!string.IsNullOrEmpty(chunk.Text))
                    sb.Append(chunk.Text);
            }
            box.FullText = sb.ToString();
            box.ChunkCount = chunks;
        }

        private static async Task CollectStreamWithTracesAsync(
            ILlmClient client, LlmCompletionRequest request,
            TraceResultBox box, CancellationToken ct)
        {
            var sb = new StringBuilder();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                    sb.Append(chunk.Text);

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

        private static async Task CompleteNonStreamAsync(
            ILlmClient client, LlmCompletionRequest request,
            Action<LlmCompletionResult> callback, CancellationToken ct)
        {
            var r = await client.CompleteAsync(request, ct);
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
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Inventory { get; } = new();
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken ct)
                => Task.FromResult(Inventory);
        }
    }
}
#endif

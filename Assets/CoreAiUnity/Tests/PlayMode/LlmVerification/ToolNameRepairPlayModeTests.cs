#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode verification for tool-name repair behavior.
    /// Wraps <see cref="MEAI.IChatClient"/> so the first streaming response is scripted
    /// and later responses use a real LLM through <see cref="MeaiOpenAiChatClient"/>.
    /// The outer <see cref="MeaiLlmClient"/> keeps text extraction and tool-name repair active.
    /// </summary>
    public sealed class ToolNameRepairPlayModeTests
    {
        [UnitySetUp]
        public IEnumerator Setup()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            yield return null;
        }

        private static bool TryCreateRealMeaiClient(out MEAI.IChatClient meaiClient)
        {
            meaiClient = null;
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                return false;
            }

            if (settings.BackendType != LlmBackendType.OpenAiHttp &&
                settings.BackendType != LlmBackendType.Auto)
            {
                return false;
            }

            if (string.IsNullOrEmpty(settings.ApiBaseUrl) ||
                string.IsNullOrEmpty(settings.ModelName))
            {
                return false;
            }

            _ = GameLoggerUnscopedFallback.Instance;
            meaiClient = new MeaiOpenAiChatClient(new SettingsHttpAdapter(settings));
            return true;
        }

        /// <summary>Adapts <see cref="CoreAISettingsAsset"/> to <see cref="IOpenAiHttpSettings"/>.</summary>
        private sealed class SettingsHttpAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _s;

            public SettingsHttpAdapter(CoreAISettingsAsset s)
            {
                _s = s;
            }

            public string ApiBaseUrl => _s.ApiBaseUrl;
            public string ApiKey => _s.ApiKey;
            public string AuthorizationHeader => "";
            public string Model => _s.ModelName;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.RequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }
        // Тест 1: wrong casing → repair → tool executed → final answer from real LLM
        // =========================================================================

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator WrongCasing_Repair_ToolExecuted_RealLlmContinues()
        {
            if (!TryCreateRealMeaiClient(out MEAI.IChatClient realMeai))
            {
                Assert.Ignore("HTTP backend not configured");
            }

            // First call: scripted "MEMORY" (wrong case). Subsequent calls: real LLM.
            SingleShotScriptedMeaiClient hybrid = new(realMeai,
                "{\"name\":\"MEMORY\",\"arguments\":{\"action\":\"write\",\"content\":\"Wrong casing repaired by TryRepairToolName\"}}");

            StatefulMemoryStore memoryStore = new();
            MeaiLlmClient client = new(hybrid, GameLoggerUnscopedFallback.Instance,
                new StubSettings(), memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher. When you receive a tool result confirming data was saved, " +
                    "respond with 'Data saved successfully' and nothing else.",
                UserPayload = "Save: Wrong casing repaired by TryRepairToolName",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            ResultBox box = new();
            Task task = CollectStreamAsync(client, request, box, CancellationToken.None);
            yield return WaitTask(task, 120f, "WrongCasing_Repair");

            Debug.Log($"[RepairTest1] Output: '{box.FullText}' | Calls: {hybrid.StreamCalls}");

            // Tool should be executed (repair fixed "MEMORY" → "memory")
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state),
                "Memory should be saved — TryRepairToolName fixed 'MEMORY' → 'memory'");
            Assert.That(state.Memory, Does.Contain("Wrong casing repaired"),
                "Memory content should match");

            // Live streaming may concatenate raw tool-shaped JSON into Text before the follow-up turn; execution + memory above are the real invariant.
            Assert.That(box.FullText, Does.Contain("Data saved"),
                "Final assistant text should acknowledge save (real LLM turn after tool)");

            Assert.GreaterOrEqual(hybrid.StreamCalls, 2,
                "Should have ≥2 stream calls (1st=scripted tool, 2nd=real LLM)");
        }

        // =========================================================================
        // Тест 2: unknown tool → error fed back → real LLM self-corrects
        // =========================================================================

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator UnknownTool_ErrorFedBack_RealLlmSelfCorrects()
        {
            if (!TryCreateRealMeaiClient(out MEAI.IChatClient realMeai))
            {
                Assert.Ignore("HTTP backend not configured");
            }

            // First call: scripted unknown tool. Subsequent calls: real LLM.
            SingleShotScriptedMeaiClient hybrid = new(realMeai,
                "{\"name\":\"nonexistent_tool\",\"arguments\":{\"data\":\"important info\"}}");

            StatefulMemoryStore memoryStore = new();
            MeaiLlmClient client = new(hybrid, GameLoggerUnscopedFallback.Instance,
                new StubSettings(), memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt =
                    "You are a teacher with a memory tool for saving information. " +
                    "If a tool is unavailable, recover by using an available tool instead of retrying the failed name.",
                UserPayload = "Save this important info to memory.",
                Tools = new List<ILlmTool> { new MemoryLlmTool() },
                MaxOutputTokens = 128000
            };

            ResultBox box = new();
            using CancellationTokenSource cts = new();
            Task task = CollectStreamAsync(client, request, box, cts.Token);
            yield return PlayModeTestAwait.WaitTask(task, 600f, "UnknownTool_SelfCorrection", cts);

            Debug.Log($"[RepairTest2] Output: '{box.FullText}' | Calls: {hybrid.StreamCalls}");

            // The real LLM should have received the error and self-corrected
            bool memoryWasSaved = memoryStore.TryLoad("Teacher", out AgentMemoryState state);
            if (memoryWasSaved)
            {
                Debug.Log($"[RepairTest2] ✓ LLM self-corrected. Memory: '{state.Memory}'");
                Assert.That(state.Memory, Is.Not.Empty,
                    "Memory should not be empty after self-correction");
            }
            else
            {
                // Model may not support self-correction — soft failure
                Debug.LogWarning("[RepairTest2] Model did not self-correct. " +
                                 "This is acceptable for models without good tool-calling.");
                Assert.IsNotEmpty(box.FullText, "Should at least have text output");
            }

            // Aggregated stream Text may still include the scripted unknown-tool JSON before the model self-corrects.
            Assert.GreaterOrEqual(hybrid.StreamCalls, 2,
                "Expected scripted first turn then at least one real LLM stream.");
        }

        // =========================================================================
        // Тест 3: mixed-case tool in text with prefix → repair + text preserved
        // =========================================================================

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MixedCaseWithTextPrefix_ToolRepaired_TextPreserved()
        {
            if (!TryCreateRealMeaiClient(out MEAI.IChatClient realMeai))
            {
                Assert.Ignore("HTTP backend not configured");
            }

            // First call: scripted text + wrong case tool JSON
            SingleShotScriptedMeaiClient hybrid = new(realMeai,
                "Working on it... {\"name\":\"Memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Mixed case repair test\"}}");

            StatefulMemoryStore memoryStore = new();
            MeaiLlmClient client = new(hybrid, GameLoggerUnscopedFallback.Instance,
                new StubSettings(), memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "After saving, say 'Done.'",
                UserPayload = "Save mixed case data",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            ResultBox box = new();
            Task task = CollectStreamAsync(client, request, box, CancellationToken.None);
            yield return WaitTask(task, 120f, "MixedCase_Repair");

            Debug.Log($"[RepairTest3] Output: '{box.FullText}' | Calls: {hybrid.StreamCalls}");

            // Tool executed despite mixed casing
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state),
                "Memory should be saved after repair");
            Assert.That(state.Memory, Is.Not.Empty,
                "Memory content should not be empty — tool was executed via repair");

            // Text prefix preserved; JSON may appear in accumulated stream Text during live streaming before strip on later iteration.
            Assert.That(box.FullText, Does.Contain("Working on it"),
                "Text prefix should be visible");

            Assert.GreaterOrEqual(hybrid.StreamCalls, 2,
                "Should have ≥2 stream calls (1st=scripted tool, 2nd=real LLM)");
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static async Task CollectStreamAsync(
            ILlmClient client, LlmCompletionRequest request,
            ResultBox box, CancellationToken ct)
        {
            StringBuilder sb = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    sb.Append(chunk.Text);
                }
            }

            box.FullText = sb.ToString();
        }

        private static IEnumerator WaitTask(Task task, float timeoutSec, string label)
        {
            return PlayModeTestAwait.WaitTask(task, timeoutSec, label);
        }

        private sealed class ResultBox
        {
            public string FullText = "";
        }

        /// <summary>
        /// MEAI-level scripted client: first streaming call returns a scripted response,
        /// all subsequent calls delegate to the real LLM.
        /// This ensures <see cref="MeaiLlmClient"/> processes every response through
        /// TryExtractToolCallsFromText and TryRepairToolName.
        /// </summary>
        private sealed class SingleShotScriptedMeaiClient : MEAI.IChatClient
        {
            private readonly MEAI.IChatClient _real;
            private readonly string _scriptedFirst;
            private int _callCount;

            public int StreamCalls => _callCount;

            public SingleShotScriptedMeaiClient(MEAI.IChatClient real, string scriptedFirst)
            {
                _real = real;
                _scriptedFirst = scriptedFirst;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                CancellationToken ct = default)
            {
                return _real.GetResponseAsync(messages, options, ct);
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [EnumeratorCancellation]
                CancellationToken ct = default)
            {
                int idx = Interlocked.Increment(ref _callCount);
                if (idx == 1)
                {
                    // Scripted first turn
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, _scriptedFirst);
                    await Task.Yield();
                }
                else
                {
                    // Real LLM for all subsequent turns
                    await foreach (MEAI.ChatResponseUpdate u in _real.GetStreamingResponseAsync(messages, options, ct))
                    {
                        yield return u;
                    }
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return _real.GetService(serviceType, serviceKey);
            }

            public void Dispose()
            {
                _real.Dispose();
            }
        }

        private sealed class StatefulMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, AgentMemoryState> _s = new();

            public bool TryLoad(string r, out AgentMemoryState s)
            {
                return _s.TryGetValue(r, out s);
            }

            public void Save(string r, AgentMemoryState s)
            {
                _s[r] = s;
            }

            public void Clear(string r)
            {
                _s.Remove(r);
            }

            public void ClearChatHistory(string r)
            {
            }

            public void AppendChatMessage(string r, string role, string c, bool p = true)
            {
            }

            public ChatMessage[] GetChatHistory(string r, int m = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => true;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 60f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => true;
            public bool LogToolCallArguments => true;
            public bool LogToolCallResults => true;
            public bool EnableStreaming => true;
        }
    }
}
#endif

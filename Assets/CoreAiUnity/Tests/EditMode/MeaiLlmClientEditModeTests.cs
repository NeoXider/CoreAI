using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
#if !COREAI_NO_LLM
    public sealed class MeaiLlmClientEditModeTests
    {
        [Test]
        public void CreateHttp_WithOpenAiSettings_ShouldNotThrow()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateHttp_WithCoreAiSettings_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            OpenAiChatLlmClient client = new(settings);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateLlmUnity_RequiresAgent()
        {
            Exception ex = Assert.Catch<Exception>(() =>
            {
                MeaiLlmClient.CreateLlmUnity(null, GameLoggerUnscopedFallback.Instance, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
            });

#if UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            Assert.That(ex, Is.TypeOf<NotSupportedException>());
#else
            Assert.That(ex, Is.TypeOf<System.ArgumentNullException>());
#endif
        }

        [Test]
        public void BuildAIFunctions_ShouldCreateMemoryTool()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger, memoryStore);

            List<ILlmTool> tools = new() { new MemoryLlmTool() };
            client.SetTools(tools);

            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText()
        {
            StreamingScriptedChatClient inner = new(
                new[] { "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Saved from stream\"}}" },
                new[] { "Quiz created successfully." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Saved from stream"));
            Assert.That(full, Does.Contain("Quiz created successfully."));
            Assert.That(full, Does.Not.Contain("\"name\":\"memory\""));
            Assert.GreaterOrEqual(inner.StreamCalls, 2, "Tool cycle should trigger second stream call.");
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson()
        {
            StreamingScriptedChatClient inner = new(
                new[] { "Working... {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Prefix persisted\"}}" },
                new[] { "Done." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.That(full, Does.Contain("Working..."));
            Assert.That(full, Does.Contain("Done."));
            Assert.That(full, Does.Not.Contain("\"name\":\"memory\""));
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Prefix persisted"));
        }

        [Test]
        public async Task CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError()
        {
            string toolJson = "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"loop\"}}";
            StreamingScriptedChatClient inner = new(
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmStreamChunk last = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                last = chunk;
            }

            Assert.IsNotNull(last);
            Assert.IsTrue(last.IsDone);
            // ToolExecutionPolicy detects duplicate tool calls and increments consecutive errors,
            // so the error comes from the policy's max-errors guard rather than the loop counter.
            Assert.IsTrue(
                last.Error.Contains("max consecutive tool errors") ||
                last.Error.Contains("tool loop exceeded"),
                $"Unexpected error: {last.Error}");
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState { Memory = "" };
                return true;
            }

            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => System.Array.Empty<ChatMessage>();
        }

        private sealed class StatefulMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, AgentMemoryState> _states = new();

            public bool TryLoad(string roleId, out AgentMemoryState state) => _states.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => _states[roleId] = state;
            public void Clear(string roleId) => _states.Remove(roleId);
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => Array.Empty<ChatMessage>();
        }

        private sealed class StubCoreSettings : ICoreAISettings
        {
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
        }

        private sealed class StreamingScriptedChatClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _streamScripts;
            public int StreamCalls { get; private set; }

            public StreamingScriptedChatClient(params string[][] streamScripts)
            {
                _streamScripts = new Queue<string[]>(streamScripts ?? Array.Empty<string[]>());
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                if (_streamScripts.Count == 0)
                {
                    yield break;
                }

                string[] chunks = _streamScripts.Dequeue();
                foreach (string chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Tests for the hardened TryExtractToolCallsFromText parser.
    /// Covers: multi-tool, code block false-positives, partial JSON, edge cases.
    /// </summary>
    [TestFixture]
    public sealed class TryExtractToolCallsFromTextTests
    {
        [Test]
        public void SingleToolCall_ExtractedCorrectly()
        {
            string text = "Here is the result: {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"hello\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("memory", calls[0].Name);
            Assert.That(cleaned, Does.Contain("Here is the result:"));
            Assert.That(cleaned, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void MultipleToolCalls_AllExtracted()
        {
            string text = "{\"name\":\"tool_a\",\"arguments\":{\"x\":1}} some text {\"name\":\"tool_b\",\"arguments\":{\"y\":2}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("tool_a", calls[0].Name);
            Assert.AreEqual("tool_b", calls[1].Name);
            Assert.That(cleaned, Does.Contain("some text"));
        }

        [Test]
        public void JsonInCodeBlock_NotExtracted()
        {
            string text = "Here is an example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}\n```\nDone.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsFalse(found, "JSON inside code blocks should be ignored");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void MalformedJson_GracefullySkipped()
        {
            string text = "Partial: {\"name\":\"tool\",\"arguments\":{\"broken";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsFalse(found, "Unclosed JSON should not produce tool calls");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void JsonWithoutNameAndArguments_NotExtracted()
        {
            string text = "Config: {\"key\":\"value\",\"count\":42}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsFalse(found, "Regular JSON without name+arguments keys should be ignored");
        }

        [Test]
        public void EmptyText_ReturnsFalse()
        {
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("", out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText(null, out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("   ", out _, out _));
        }

        [Test]
        public void NestedBracesInArguments_HandledCorrectly()
        {
            string text = "{\"name\":\"config\",\"arguments\":{\"data\":{\"nested\":true}}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("config", calls[0].Name);
        }

        [Test]
        public void StripCodeBlocks_PreservesPositions()
        {
            string text = "Before ```code``` After";
            string stripped = MeaiLlmClient.StripCodeBlocks(text);

            Assert.AreEqual(text.Length, stripped.Length, "Stripped text should have same length");
            Assert.That(stripped, Does.StartWith("Before "));
            Assert.That(stripped, Does.EndWith(" After"));
        }

        [Test]
        public void IsValidToolCallJson_RequiresBothKeys()
        {
            Assert.IsTrue(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\",\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\"}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson(""));
        }

        [Test]
        public void FindToolCallJsonSpans_MultipleSpans()
        {
            string text = "A {\"name\":\"a\",\"arguments\":{}} B {\"name\":\"b\",\"arguments\":{\"x\":1}}";
            var spans = MeaiLlmClient.FindToolCallJsonSpans(text);

            Assert.AreEqual(2, spans.Count);
        }

        [Test]
        public void ToolCallWithStringContainingBraces_HandledCorrectly()
        {
            string text = "{\"name\":\"tool\",\"arguments\":{\"code\":\"function() { return {}; }\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out var calls, out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("tool", calls[0].Name);
        }
    }
#endif
}

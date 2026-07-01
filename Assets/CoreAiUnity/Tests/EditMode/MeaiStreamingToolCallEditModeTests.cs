#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class MeaiStreamingToolCallEditModeTests
    {
        [Test]
        public void ResolveStreamingMaxToolRoundtrips_UsesRequestOverrideAndPreservesZero()
        {
            StubSettings settings = new() { MaxToolCallRoundtripsValue = 7 };

            Assert.AreEqual(7, MeaiLlmClient.ResolveStreamingMaxToolRoundtrips(null, settings));
            Assert.AreEqual(3, MeaiLlmClient.ResolveStreamingMaxToolRoundtrips(3, settings));
            Assert.AreEqual(0, MeaiLlmClient.ResolveStreamingMaxToolRoundtrips(0, settings));
        }

        [Test]
        public void MalformedTextToolCall_IncompleteJson_BuildsParseErrorAndStripsTail()
        {
            string text = "Before {\"name\":\"memory\",\"arguments\":{\"action\":\"write\"";

            bool found = MeaiLlmClient.TryBuildMalformedTextToolCall(
                text,
                new List<ILlmTool> { new TestTool("memory") },
                new List<MEAI.AIFunction> { MakeAIFunction("memory") },
                out MEAI.FunctionCallContent call,
                out string cleaned,
                out string reason);

            Assert.IsTrue(found);
            Assert.AreEqual("memory", call.Name);
            Assert.AreEqual("Before", cleaned);
            Assert.AreEqual("incomplete-json-object", reason);
            Assert.IsTrue(call.Arguments.ContainsKey(ToolCallArgumentMarkers.ParseErrorKey));
            Assert.IsTrue(call.Arguments.ContainsKey(ToolCallArgumentMarkers.RawArgumentsKey));
        }

        [Test]
        public async Task CompleteStreamingAsync_MalformedTextToolJson_DoesNotLeakRawJson()
        {
            StreamingScripted inner = new(
                new[] { "Before {\"name\":\"memory\",\"arguments\":{\"action\":\"write\"" },
                new[] { "Retry complete." });
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { new TestTool("memory") }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            string visible = string.Concat(chunks.Select(c => c.Text));
            Assert.That(visible, Does.Contain("Before"));
            Assert.That(visible, Does.Contain("Retry complete."));
            Assert.That(visible, Does.Not.Contain("\"arguments\""));
            Assert.That(chunks.Last().ExecutedToolCalls.Any(t => t.Source == "parse-error"), Is.True);
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonInsideThink_LogsDiagnosticButKeepsThinkHidden()
        {
            const string hiddenTool =
                "<think>{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"x\"}}</think>";
            StreamingScripted inner = new(new[] { hiddenTool, "Done." });
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { new TestTool("memory") }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            string visible = string.Concat(chunks.Select(c => c.Text));
            Assert.AreEqual("Done.", visible);
            Assert.That(visible, Does.Not.Contain("<think>"));
            Assert.That(visible, Does.Not.Contain("\"name\""));
            Assert.That(logger.Warnings.Any(w => w.Contains("inside a <think> block")), Is.True);
        }

        [Test]
        public void ContainsCompleteThinkBlockToolCall_NormalThinkWithoutTool_ReturnsFalse()
        {
            Assert.IsFalse(MeaiLlmClient.ContainsCompleteThinkBlockToolCall("<think>private reasoning</think>Visible."));
            Assert.IsTrue(MeaiLlmClient.ContainsCompleteThinkBlockToolCall(
                "<think>{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}</think>"));
        }

        private static MEAI.AIFunction MakeAIFunction(string name)
        {
            Func<CancellationToken, Task<string>> func =
                _ => Task.FromResult("{\"Success\":true}");
            return MEAI.AIFunctionFactory.Create(func,
                new MEAI.AIFunctionFactoryOptions { Name = name, Description = "test tool" });
        }

        private sealed class TestTool : ILlmTool, IAIFunctionLlmTool
        {
            public TestTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "test tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MakeAIFunction(Name);
            }
        }

        private sealed class StreamingScripted : MEAI.IChatClient
        {
            private readonly Queue<string[]> _scripts;

            public StreamingScripted(params string[][] scripts)
            {
                _scripts = new Queue<string[]>(scripts);
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                if (_scripts.Count == 0)
                {
                    yield break;
                }

                foreach (string text in _scripts.Dequeue())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, text);
                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class RecordingLogger : IGameLogger
        {
            public readonly List<string> Warnings = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Warnings.Add(message);
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxToolCallRoundtripsValue { get; set; } = 20;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public int MaxToolCallRoundtrips => MaxToolCallRoundtripsValue;
            public bool AllowDuplicateToolCalls => true;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 1;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => true;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}
#endif

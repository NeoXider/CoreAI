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
        public async Task CompleteStreamingAsync_NativeToolCallMidStream_ExecutesBeforeStreamEnds()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed);
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { tool }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.IsTrue(inner.ObservedToolExecutedBeforeStreamEnd == true,
                "The tool must run the moment its call arrives, while later stream updates are still pending.");
            LlmStreamChunk last = chunks.Last();
            Assert.IsTrue(last.IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(last.Error), $"Unexpected error: {last.Error}");
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "world_tool" && t.Success));
            Assert.That(string.Concat(chunks.Select(c => c.Text)), Does.Contain("Done."));
        }

        [Test]
        public async Task CompleteStreamingAsync_StreamThrowsAfterExecutedToolCall_YieldsTerminalErrorChunkWithTraces()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                ThrowAfterToolCall = new InvalidOperationException("connection reset")
            };
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            // Draining without a try/catch is part of the assertion: the partially-applied
            // turn must surface as a terminal chunk, never as an escaping transport exception.
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { tool }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.IsTrue(tool.Executed, "The tool must have executed before the transport failure.");
            LlmStreamChunk last = chunks.Last();
            Assert.IsTrue(last.IsDone);
            Assert.That(last.Error, Does.Contain("connection reset"));
            Assert.That(last.Error, Does.Contain("1 executed tool call"));
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "world_tool" && t.Success),
                "The executed call's trace must ride the terminal chunk so the failure is graded, not retried blind.");
            Assert.That(logger.Warnings.Any(w => w.Contains("already executed mid-stream")), Is.True,
                "Turn finalization must be logged when the stream dies after executed calls.");
        }

        [Test]
        public async Task CompleteStreamingAsync_StreamThrowsBeforeAnyToolCall_ExceptionPropagates()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed) { ThrowImmediately = true };
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            // async Task + try/catch instead of Assert.ThrowsAsync: ThrowsAsync blocks the Unity
            // main thread while the awaited chain (Runtime code without ConfigureAwait(false))
            // posts continuations back to it - the classic EditMode sync-over-async deadlock.
            bool sawDone = false;
            InvalidOperationException caught = null;
            try
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                               {
                                   AgentRoleId = "Role",
                                   SystemPrompt = "sys",
                                   UserPayload = "go",
                                   Tools = new List<ILlmTool> { tool }
                               }, CancellationToken.None))
                {
                    sawDone |= chunk.IsDone;
                }
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "The pre-first-chunk failure must escape as its original exception.");
            Assert.IsFalse(tool.Executed);
            Assert.IsFalse(sawDone,
                "No terminal chunk may be emitted when the failure precedes any tool execution - " +
                "FallbackLlmClientDecorator relies on the exception escaping.");
        }

        [Test]
        public async Task CompleteStreamingAsync_CancelledAfterExecutedToolCall_PropagatesCancellationAfterFinalizingTurn()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                ThrowAfterToolCall = new OperationCanceledException()
            };
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), null);

            // async Task + try/catch instead of Assert.CatchAsync (which would block the Unity main
            // thread - EditMode sync-over-async deadlock); the catch accepts derived cancellation
            // types just like the production catch blocks do.
            OperationCanceledException caught = null;
            try
            {
                await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                               {
                                   AgentRoleId = "Role",
                                   SystemPrompt = "sys",
                                   UserPayload = "go",
                                   Tools = new List<ILlmTool> { tool }
                               }, CancellationToken.None))
                {
                }
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "Cancellation must propagate to the consumer after finalization.");
            Assert.IsTrue(tool.Executed, "Cancellation arrived after the tool already mutated state.");
            // The per-request ToolExecutionPolicy is method-local inside CompleteStreamingAsync, so
            // its ConsecutiveErrors/echo registry cannot be probed after the request dies; the
            // finalization warning is logged strictly AFTER policy.CompleteStreamedTurn returns and
            // is therefore the observable proof that the turn was recorded before cancellation.
            Assert.That(logger.Warnings.Any(w =>
                    w.Contains("already executed mid-stream") && w.Contains("1 tool call")), Is.True,
                "CompleteStreamedTurn must run (and be logged) before OperationCanceledException propagates.");
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

        /// <summary>Tool whose AIFunction flips a flag when it actually executes.</summary>
        private sealed class FlagTool : ILlmTool, IAIFunctionLlmTool
        {
            public FlagTool(string name)
            {
                Name = name;
            }

            public bool Executed { get; private set; }
            public string Name { get; }
            public string Description => "flag tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;

            public MEAI.AIFunction CreateAIFunction()
            {
                Func<CancellationToken, Task<string>> func = _ =>
                {
                    Executed = true;
                    return Task.FromResult("{\"Success\":true}");
                };
                return MEAI.AIFunctionFactory.Create(func,
                    new MEAI.AIFunctionFactoryOptions { Name = Name, Description = Description });
            }
        }

        /// <summary>
        /// Inner client for the NATIVE tool-call streaming path: first stream yields one
        /// FunctionCallContent update, then (when the consumer asks for the NEXT update -
        /// i.e., after MeaiLlmClient has already awaited ExecuteStreamedAsync) observes the
        /// tool's side effect and either throws a configured exception mid-stream or yields a
        /// trailing text update. The second stream (tool-result roundtrip) yields plain text.
        /// </summary>
        private sealed class NativeToolCallScripted : MEAI.IChatClient
        {
            private readonly Func<bool> _observeToolRan;

            public NativeToolCallScripted(Func<bool> observeToolRan)
            {
                _observeToolRan = observeToolRan;
            }

            public Exception ThrowAfterToolCall { get; set; }
            public bool ThrowImmediately { get; set; }
            public bool? ObservedToolExecutedBeforeStreamEnd { get; private set; }
            public int StreamCalls { get; private set; }

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
                StreamCalls++;
                if (StreamCalls == 1)
                {
                    if (ThrowImmediately)
                    {
                        throw new InvalidOperationException("boom before any tool call");
                    }

                    yield return new MEAI.ChatResponseUpdate(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent>
                        {
                            new MEAI.FunctionCallContent(
                                "call-1", "world_tool", new Dictionary<string, object>())
                        });
                    await Task.Yield();

                    // Runs when the consumer requests the NEXT update - by then MeaiLlmClient
                    // has already executed the call above via ExecuteStreamedAsync.
                    ObservedToolExecutedBeforeStreamEnd = _observeToolRan?.Invoke();
                    if (ThrowAfterToolCall != null)
                    {
                        throw ThrowAfterToolCall;
                    }

                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "tail");
                }
                else
                {
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "Done.");
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

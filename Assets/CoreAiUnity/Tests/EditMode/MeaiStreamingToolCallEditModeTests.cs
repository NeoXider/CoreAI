#if COREAI_LLM
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
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: it asserts through Assert.ThrowsAsync/CatchAsync, which BLOCK
        /// the calling thread until the awaited delegate finishes — being inside an async test does
        /// not change that. Under Unity's SynchronizationContext the delegate's continuation is
        /// posted back to that same blocked thread, and the editor deadlocks with no results file.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

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

        /// <summary>
        /// F13 probe: a teacher's unfinished JSON EXAMPLE (a scalar-valued key, not a tool-call shape) must
        /// never be misread as a truncated call and swallowed at turn end — it has to survive as visible
        /// assistant text.
        /// </summary>
        [Test]
        public void MalformedTextToolCall_TeacherJsonLiteral_StaysVisibleText()
        {
            string text = "Example JSON: {\"key\": value";

            bool found = MeaiLlmClient.TryBuildMalformedTextToolCall(
                text,
                new List<ILlmTool> { new TestTool("memory") },
                new List<MEAI.AIFunction> { MakeAIFunction("memory") },
                out _,
                out _,
                out _);

            Assert.IsFalse(found,
                "A scalar-valued key with no name/arguments shape must not be treated as a truncated tool call.");
        }

        /// <summary>
        /// F13 probe: some providers wrap the call one level deeper
        /// (<c>{"function":{"name":...,"arguments":...}}</c>), and truncation can cut the stream off before
        /// the inner "name" key is visible at all — the outer key's value already opening a container
        /// (<c>{"function": {</c>) must still be held instead of leaking that prefix as raw JSON.
        /// </summary>
        [Test]
        public void MalformedTextToolCall_NestedFunctionWrapperPrefix_IsHeldBeforeNameAppears()
        {
            string text = "Before {\"function\": {";

            bool found = MeaiLlmClient.TryBuildMalformedTextToolCall(
                text,
                new List<ILlmTool> { new TestTool("memory") },
                new List<MEAI.AIFunction> { MakeAIFunction("memory") },
                out MEAI.FunctionCallContent call,
                out string cleaned,
                out string reason);

            Assert.IsTrue(found,
                "A nested-object-valued key must be held: it can still grow into a wrapped tool call.");
            Assert.IsNotNull(call);
            Assert.AreEqual("Before", cleaned);
            Assert.AreEqual("incomplete-json-object", reason);
        }

        [Test]
        public async Task CompleteStreamingAsync_MalformedTextToolJson_DoesNotLeakRawJson()
        {
            StreamingScripted inner = new(
                new[] { "Before {\"name\":\"memory\",\"arguments\":{\"action\":\"write\"" },
                new[] { "Retry complete." });
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

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

        /// <summary>
        /// Вторая итерация tool-цикла — это ВТОРАЯ реплика модели, и признак границы обязан стоять
        /// ровно на её первом видимом чанке. Без признака потребитель склеивал реплики встык, и на
        /// проде ученик читал «Проверь себя:**Ход завершён — ждём ответ ученика на карточке.**».
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_SecondIteration_FlagsFirstVisibleChunkOnly()
        {
            StreamingScripted inner = new(
                new[] { "Before {\"name\":\"memory\",\"arguments\":{\"action\":\"write\"" },
                new[] { "Retry complete." });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

            List<LlmStreamChunk> visible = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { new TestTool("memory") }
                           }, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    visible.Add(chunk);
                }
            }

            Assert.That(visible.Count, Is.GreaterThanOrEqualTo(2));
            Assert.IsFalse(visible[0].StartsNewMessage,
                "Первая реплика хода не является «новой» — разделять нечего.");
            Assert.AreEqual(1, visible.Count(c => c.StartsNewMessage),
                "Признак ставится РОВНО на одном чанке итерации, иначе потребитель размножит разделители.");

            LlmStreamChunk boundary = visible.First(c => c.StartsNewMessage);
            Assert.That(boundary.Text, Does.StartWith("Retry"),
                "Граница обязана попасть на первый видимый чанк ВТОРОЙ итерации.");
        }

        /// <summary>
        /// Один ход без инструментов — одна реплика: границ в потоке нет, и потребитель ведёт себя
        /// ровно как до появления признака.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_SingleIteration_NeverFlagsNewMessage()
        {
            StreamingScripted inner = new(new[] { "Привет, ", "как дела?" });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go"
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual("Привет, как дела?", string.Concat(chunks.Select(c => c.Text)));
            Assert.IsFalse(chunks.Any(c => c.StartsNewMessage));
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonInsideThink_LogsDiagnosticButKeepsThinkHidden()
        {
            const string hiddenTool =
                "<think>{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"x\"}}</think>";
            StreamingScripted inner = new(new[] { hiddenTool, "Done." });
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

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
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

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

        private const string ToolCallShapedExample = "{\"name\":\"world_tool\",\"arguments\":{}}";

        /// <summary>
        /// The defect this guards: a teacher explaining JSON writes an object shaped exactly like a tool
        /// call — the everyday job of a programming tutor, not an anomaly — and the engine used to read the
        /// model's PROSE for calls no matter which channel the endpoint offered. The example got executed
        /// instead of explained. Where a native tool channel exists, calls arrive on it, and text is text.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_NativeChannel_JsonExampleInProse_IsShownNotExecuted()
        {
            FlagTool tool = new("world_tool");
            StreamingScripted inner = new(new[]
            {
                "Вызов инструмента выглядит так: ", ToolCallShapedExample, " — просто пример."
            });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

            System.Text.StringBuilder visible = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "объясни",
                               Tools = new List<ILlmTool> { tool }
                           }, CancellationToken.None))
            {
                visible.Append(chunk.Text ?? "");
            }

            Assert.IsFalse(tool.Executed,
                "An example of a call written in prose must never run on an endpoint with a native channel.");
            Assert.AreEqual(
                "Вызов инструмента выглядит так: " + ToolCallShapedExample + " — просто пример.",
                visible.ToString(),
                "The learner must read the example exactly as the teacher wrote it.");
        }

        /// <summary>
        /// The mirror of the case above, and the regression that nearly shipped beside it: with no native
        /// channel a text-shaped call is the ONLY way the model can call a tool. A local llama.cpp /
        /// LLMUnity server speaks the same OpenAI HTTP dialect as a remote endpoint, so if its client were
        /// created as "native" the gate would disable every one of its tools SILENTLY — no error, the call
        /// simply never happens. That is why the channel is declared where the endpoint is built
        /// (`supportsNativeToolCalling: false` for LLMUnity) and why this case is asserted.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_FallbackChannel_JsonInProse_StillCallsTheTool()
        {
            FlagTool tool = new("world_tool");
            StreamingScripted inner = new(
                new[] { "Вызываю: ", ToolCallShapedExample },
                new[] { "Готово." });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "сделай",
                               Tools = new List<ILlmTool> { tool }
                           }, CancellationToken.None))
            {
            }

            Assert.IsTrue(tool.Executed,
                "Without a native channel a text-shaped call is a real call and must still execute.");
        }

        /// <summary>
        /// The escape hatch for an endpoint that advertises a native channel and then answers with JSON in
        /// the text. It is opt-in per request precisely because the safe default cannot be the one that
        /// executes examples.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_NativeChannel_WithExplicitOptIn_ReadsProseAgain()
        {
            FlagTool tool = new("world_tool");
            StreamingScripted inner = new(
                new[] { "Вызываю: ", ToolCallShapedExample },
                new[] { "Готово." });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "сделай",
                               Tools = new List<ILlmTool> { tool },
                               AllowTextShapedToolCallsOnNativeEndpoint = true
                           }, CancellationToken.None))
            {
            }

            Assert.IsTrue(tool.Executed,
                "The opt-in exists for endpoints that lie about their tool channel; it must actually work.");
        }

        /// <summary>
        /// The same defect on the non-streaming path, which runs its own agentic loop
        /// (<c>SmartToolCallingChatClient</c>) with its own copy of the text extraction.
        /// </summary>
        [Test]
        public async Task CompleteAsync_NativeChannel_JsonExampleInProse_IsShownNotExecuted()
        {
            FlagTool tool = new("world_tool");
            SingleAnswerChatClient inner = new(
                "Вызов выглядит так: " + ToolCallShapedExample + " — это пример.");
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "объясни",
                Tools = new List<ILlmTool> { tool }
            }, CancellationToken.None);

            Assert.IsFalse(tool.Executed, "The non-streaming loop must judge by channel too.");
            StringAssert.Contains(ToolCallShapedExample, result.Content,
                "The example must survive into the answer instead of being taken for a call.");
        }

        /// <summary>Answers one fixed assistant text and never emits a tool call of its own.</summary>
        private sealed class SingleAnswerChatClient : MEAI.IChatClient
        {
            private readonly string _text;

            public SingleAnswerChatClient(string text)
            {
                _text = text;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(
                    new MEAI.ChatMessage(MEAI.ChatRole.Assistant, _text)));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, _text);
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// On an endpoint with a native tool channel the text must flow as it is generated. The hold that
        /// waits for a possible text-shaped tool call starts at the first still-open <c>{</c> — and a
        /// teacher of Python types <c>{</c> constantly (a dict, a set, an f-string), so on the main path it
        /// froze prose mid-sentence and then delivered it in a lump. Nothing is held here, and the client
        /// does not announce buffering it is not doing.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_NativeToolCalling_StreamsProseWithBracesAsItArrives()
        {
            StreamingScripted inner = new(new[] { "Пиши так: ", "{\"ключ\": ", "значение" });
            MeaiLlmClient client = new(inner,
                new RecordingLogger(),
                new StubSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);

            List<LlmStreamChunk> visible = new();
            bool announcedBuffering = false;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { new TestTool("memory") }
                           }, CancellationToken.None))
            {
                announcedBuffering |= chunk.BufferedStreamingNoToolBinding;
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    visible.Add(chunk);
                }
            }

            Assert.IsFalse(announcedBuffering,
                "Buffering must not be announced on a path that does not buffer.");
            Assert.GreaterOrEqual(visible.Count, 3,
                "Each provider delta must reach the reader on its own, including the one opening a brace.");
            Assert.AreEqual("Пиши так: {\"ключ\": значение", string.Concat(visible.Select(c => c.Text)));
        }

        /// <summary>
        /// The same script on the fallback path: with no native tool channel a text-shaped call is how the
        /// model calls tools at all, so half a JSON object must not reach the reader. The hold stays here —
        /// and only here.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_WithoutNativeToolCalling_HoldsTheUnfinishedBrace()
        {
            StreamingScripted inner = new(new[] { "Пиши так: ", "{\"ключ\": ", "значение" });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

            List<LlmStreamChunk> visible = new();
            bool announcedBuffering = false;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { new TestTool("memory") }
                           }, CancellationToken.None))
            {
                announcedBuffering |= chunk.BufferedStreamingNoToolBinding;
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    visible.Add(chunk);
                }
            }

            Assert.IsTrue(announcedBuffering, "The fallback path still tells the consumer it holds text.");
            Assert.AreEqual("Пиши так: ", visible[0].Text,
                "Prose before the brace still streams; the hold starts at the brace, not before it.");
            Assert.AreEqual("Пиши так: {\"ключ\": значение", string.Concat(visible.Select(c => c.Text)),
                "Held text is released, never lost.");
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
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

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
            Assert.That(logger.Warnings.Any(w => w.Contains("started mid-stream")), Is.True,
                "Turn finalization must be logged when the stream dies after executed calls.");
        }

        [Test]
        public async Task CompleteStreamingAsync_NextRoundtripFailsAfterToolOnlyTurn_DoesNotReplayTool()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                SuppressFirstTail = true,
                ThrowOnSecondStreamBeforeContent = true
            };
            MeaiLlmClient meai = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);
            RetryingStreamingLlmClientDecorator client = new(meai, 1);

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

            Assert.AreEqual(1, tool.ExecutionCount);
            Assert.AreEqual(0, client.RetryCount);
            Assert.That(chunks.Last().Error, Does.Contain("second roundtrip failed"));
            Assert.IsTrue(chunks.Last().ExecutedToolCalls.Any(t => t.Name == "world_tool"));
        }

        [Test]
        public async Task CompleteStreamingAsync_RoundtripCapSummary_SumsUsageOnTerminalChunk()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                SuppressFirstTail = true,
                EmitUsage = true
            };
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { tool },
                               MaxToolCallRoundtrips = 1
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            LlmStreamChunk terminal = chunks.Last();
            Assert.AreEqual(9, terminal.PromptTokens);
            Assert.AreEqual(14, terminal.CompletionTokens);
            Assert.AreEqual(23, terminal.TotalTokens);
        }

        [Test]
        public async Task CompleteStreamingAsync_StreamThrowsBeforeAnyToolCall_ExceptionPropagates()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed) { ThrowImmediately = true };
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

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
        public async Task
            CompleteStreamingAsync_CancelledAfterExecutedToolCall_PropagatesCancellationAfterFinalizingTurn()
        {
            FlagTool tool = new("world_tool");
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                ThrowAfterToolCall = new OperationCanceledException()
            };
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

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
            // finalization warning is logged strictly AFTER policy.CompleteStreamedTurnAsync
            // completes and is therefore the observable proof that the turn was recorded before
            // cancellation.
            Assert.That(logger.Warnings.Any(w =>
                    w.Contains("started mid-stream") && w.Contains("1 tool call")), Is.True,
                "CompleteStreamedTurnAsync must run (and be logged) before OperationCanceledException propagates.");
        }

        [Test]
        public async Task CompleteStreamingAsync_TwoNativeToolCallsWithParallelLimit_OverlapAndResultsInCallOrder()
        {
            OverlappingToolPair pair = new();
            TwoNativeToolCallsScripted inner = new();
            RecordingLogger logger = new();
            MeaiLlmClient client = new(inner, logger, new StubSettings { MaxParallelToolCalls = 4 }, supportsNativeToolCalling: true, memoryStore: null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { pair.ToolAlpha, pair.ToolBeta }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            LlmStreamChunk last = chunks.Last();
            Assert.IsTrue(last.IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(last.Error), $"Unexpected error: {last.Error}");
            Assert.AreEqual(2, pair.MaxObservedConcurrency,
                "With MaxParallelToolCalls >= 2 both streamed calls must run concurrently: each tool " +
                "holds its slot until the other has started, so a sequential regression caps this at 1.");
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "tool_alpha" && t.Success));
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "tool_beta" && t.Success));

            // Wire protocol: the results ride ONE tool-role message, collated in CALL (arrival)
            // order regardless of completion order, each result echoing its call's id.
            Assert.IsNotNull(inner.SecondCallMessages,
                "The tool-result roundtrip (second stream call) must have happened.");
            MEAI.ChatMessage toolMessage = inner.SecondCallMessages
                .LastOrDefault(m => m.Role == MEAI.ChatRole.Tool);
            Assert.IsNotNull(toolMessage, "The second roundtrip must carry the tool-role results message.");
            List<MEAI.FunctionResultContent> results =
                toolMessage.Contents.OfType<MEAI.FunctionResultContent>().ToList();
            Assert.AreEqual(2, results.Count, "Both tool results must ride the single tool-role message.");
            Assert.AreEqual("call-alpha", results[0].CallId,
                "Result order must match call order, not completion order.");
            Assert.AreEqual("call-beta", results[1].CallId,
                "Result order must match call order, not completion order.");
        }

        [Test]
        public void ContainsCompleteThinkBlockToolCall_NormalThinkWithoutTool_ReturnsFalse()
        {
            Assert.IsFalse(
                MeaiLlmClient.ContainsCompleteThinkBlockToolCall("<think>private reasoning</think>Visible."));
            Assert.IsTrue(MeaiLlmClient.ContainsCompleteThinkBlockToolCall(
                "<think>{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}</think>"));
        }

        /// <summary>
        /// Native path: a tool declaring <c>EndsTurn</c> that SUCCEEDED closes the streamed turn — the
        /// stream must not be reopened for a roundtrip in which the model writes its reaction to an answer
        /// the student has not given yet. The prose said before the call stays visible, and the terminal
        /// chunk carries the traces and usage fields exactly like every other clean end of the loop.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_TurnEndingToolSucceeded_ClosesTurnWithoutAnotherRoundtrip()
        {
            FlagTool tool = new("world_tool") { EndsTurn = true };
            NativeToolCallScripted inner = new(() => tool.Executed)
            {
                SuppressFirstTail = true,
                EmitUsage = true,
                FirstVisibleText = "Проверь себя:"
            };
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);

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

            Assert.AreEqual(1, inner.StreamCalls,
                "The tool result must NOT be sent back: the second roundtrip may never start.");
            string visible = string.Concat(chunks.Select(c => c.Text));
            StringAssert.Contains("Проверь себя", visible,
                "Prose said before the turn-ending call must reach the student.");
            StringAssert.DoesNotContain("Done.", visible,
                "Nothing from the roundtrip that must not happen may appear.");

            LlmStreamChunk last = chunks.Last();
            Assert.IsTrue(last.IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(last.Error), $"Unexpected error: {last.Error}");
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "world_tool" && t.Success),
                "The executed call's trace must ride the terminal chunk (the tool-only completion line " +
                "upstream is synthesized from it).");
            Assert.AreEqual(2, last.PromptTokens,
                "The terminal chunk must carry usage like every other end of the loop, or the turn reads " +
                "as zero-token to the accounting.");
            Assert.AreEqual(3, last.CompletionTokens);
            Assert.AreEqual(5, last.TotalTokens);
        }

        /// <summary>
        /// Text-extraction path (local models emit tool calls as JSON in the assistant text): the same
        /// turn-ending rule holds there, otherwise the defect simply moves to the other branch.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_TurnEndingToolFromText_ClosesTurnAndKeepsProse()
        {
            StreamingScripted inner = new(
                new[] { "Проверь себя: ", "{\"name\":\"quiz_tool\",\"arguments\":{\"question\":\"2+2\"}}" },
                new[] { "Верно!" });
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool>
                               {
                                   new TurnEndingTextTool("quiz_tool",
                                       "{\"success\":true,\"status\":\"card_shown_waiting_for_student\"}")
                               }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            string visible = string.Concat(chunks.Select(c => c.Text));
            StringAssert.Contains("Проверь себя", visible);
            StringAssert.DoesNotContain("Верно", visible,
                "The turn ended on the card; the model gets no roundtrip to grade an unanswered question.");
            Assert.IsTrue(chunks.Last().ExecutedToolCalls.Any(t => t.Name == "quiz_tool" && t.Success));
        }

        /// <summary>
        /// A FAILED turn-ending tool must keep the ordinary loop: the error result is the only thing the
        /// model can recover from, and a student left in front of a card that was never shown is worse
        /// than one extra roundtrip.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_TurnEndingToolFailed_KeepsTheRecoveryRoundtrip()
        {
            StreamingScripted inner = new(
                new[] { "Проверь себя: ", "{\"name\":\"quiz_tool\",\"arguments\":{\"question\":\"2+2\"}}" },
                new[] { "Карточка не открылась, разберём вслух." });
            // Запасной канал объявлен явно: этот скрипт отдаёт вызов ПРОЗОЙ (см. сестринский
            // CompleteStreamingAsync_TurnEndingToolFromText_ClosesTurnAndKeepsProse), а на нативном канале
            // проза не разбирается вовсе — тест проверял бы только то, что сырой JSON утекает в текст.
            MeaiLlmClient client = new(inner, new RecordingLogger(), new StubSettings(), supportsNativeToolCalling: false, memoryStore: null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool>
                               {
                                   new TurnEndingTextTool("quiz_tool",
                                       "{\"Success\":false,\"Error\":\"no card prefab\"}")
                               }
                           }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            StringAssert.Contains("разберём вслух", string.Concat(chunks.Select(c => c.Text)),
                "A failed turn-ending tool must still get its recovery roundtrip.");
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

        /// <summary>
        /// Tool that hands control to a human: its AIFunction returns the configured result payload and
        /// the tool declares that a successful call ENDS the model's turn.
        /// </summary>
        private sealed class TurnEndingTextTool : ILlmTool, IAIFunctionLlmTool
        {
            private readonly string _resultJson;

            public TurnEndingTextTool(string name, string resultJson)
            {
                Name = name;
                _resultJson = resultJson;
            }

            public string Name { get; }
            public string Description => "shows a card and waits for the student";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;
            public bool EndsTurn => true;

            public MEAI.AIFunction CreateAIFunction()
            {
                Func<CancellationToken, Task<string>> func = _ => Task.FromResult(_resultJson);
                return MEAI.AIFunctionFactory.Create(func,
                    new MEAI.AIFunctionFactoryOptions { Name = Name, Description = Description });
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
            public int ExecutionCount { get; private set; }
            public string Name { get; }
            public string Description => "flag tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;

            /// <summary>Set per test: most cases keep the default (a tool that does not end the turn).</summary>
            public bool EndsTurn { get; set; }

            public MEAI.AIFunction CreateAIFunction()
            {
                Func<CancellationToken, Task<string>> func = _ =>
                {
                    Executed = true;
                    ExecutionCount++;
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
            public bool ThrowOnSecondStreamBeforeContent { get; set; }
            public bool SuppressFirstTail { get; set; }
            public bool EmitUsage { get; set; }
            public string FirstVisibleText { get; set; }
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

                    List<MEAI.AIContent> contents = new()
                    {
                        new MEAI.FunctionCallContent(
                            "call-1", "world_tool", new Dictionary<string, object>())
                    };
                    if (!string.IsNullOrEmpty(FirstVisibleText))
                    {
                        contents.Add(new MEAI.TextContent(FirstVisibleText));
                    }

                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, contents);
                    await Task.Yield();

                    // Runs when the consumer requests the NEXT update - by then MeaiLlmClient
                    // has already executed the call above via ExecuteStreamedAsync.
                    ObservedToolExecutedBeforeStreamEnd = _observeToolRan?.Invoke();
                    if (ThrowAfterToolCall != null)
                    {
                        throw ThrowAfterToolCall;
                    }

                    if (EmitUsage)
                    {
                        yield return UsageUpdate(2, 3, 5);
                    }

                    if (!SuppressFirstTail)
                    {
                        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "tail");
                    }
                }
                else
                {
                    if (ThrowOnSecondStreamBeforeContent)
                    {
                        throw new InvalidOperationException("second roundtrip failed");
                    }

                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "Done.");
                    if (EmitUsage)
                    {
                        yield return UsageUpdate(7, 11, 18);
                    }

                    await Task.Yield();
                }
            }

            private static MEAI.ChatResponseUpdate UsageUpdate(int input, int output, int total)
            {
                return new MEAI.ChatResponseUpdate(
                    MEAI.ChatRole.Assistant,
                    new List<MEAI.AIContent>
                    {
                        new MEAI.UsageContent(new MEAI.UsageDetails
                        {
                            InputTokenCount = input,
                            OutputTokenCount = output,
                            TotalTokenCount = total
                        })
                    });
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Two tools that prove observable overlap: each call increments a shared concurrency
        /// counter, then holds its slot (Task.Delay-based) until BOTH calls have started. Under
        /// parallel scheduling the max observed concurrency reaches 2; under a sequential
        /// regression the first call only stops waiting via the bounded timeout and the counter
        /// never exceeds 1.
        /// </summary>
        private sealed class OverlappingToolPair
        {
            private readonly TaskCompletionSource<bool> _bothStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _startedCount;
            private int _running;
            private int _maxObservedConcurrency;

            public OverlappingToolPair()
            {
                // Asymmetric tails: the FIRST call (alpha) finishes LAST, so the call-order
                // assertion on the tool-role message genuinely distinguishes call order from
                // completion order.
                ToolAlpha = new OverlappingTool("tool_alpha", this, 150);
                ToolBeta = new OverlappingTool("tool_beta", this, 1);
            }

            public OverlappingTool ToolAlpha { get; }
            public OverlappingTool ToolBeta { get; }
            public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

            private async Task<string> RunAsync(int extraHoldMs, CancellationToken cancellationToken)
            {
                int running = Interlocked.Increment(ref _running);
                InterlockedMax(ref _maxObservedConcurrency, running);
                if (Interlocked.Increment(ref _startedCount) == 2)
                {
                    _bothStarted.TrySetResult(true);
                }

                // Deterministic overlap window: hold this slot until the other call starts too.
                // Bounded so a sequential regression fails the concurrency assertion (~2s) instead
                // of deadlocking the test run.
                await Task.WhenAny(_bothStarted.Task, Task.Delay(2000, cancellationToken));
                await Task.Delay(extraHoldMs, cancellationToken);
                Interlocked.Decrement(ref _running);
                return "{\"Success\":true}";
            }

            private static void InterlockedMax(ref int location, int value)
            {
                int current = Volatile.Read(ref location);
                while (value > current)
                {
                    int previous = Interlocked.CompareExchange(ref location, value, current);
                    if (previous == current)
                    {
                        break;
                    }

                    current = previous;
                }
            }

            public sealed class OverlappingTool : ILlmTool, IAIFunctionLlmTool
            {
                private readonly OverlappingToolPair _pair;
                private readonly int _extraHoldMs;

                public OverlappingTool(string name, OverlappingToolPair pair, int extraHoldMs)
                {
                    Name = name;
                    _pair = pair;
                    _extraHoldMs = extraHoldMs;
                }

                public string Name { get; }
                public string Description => "overlap probe tool";
                public string ParametersSchema => "{}";
                public bool AllowDuplicates => true;

                public MEAI.AIFunction CreateAIFunction()
                {
                    Func<CancellationToken, Task<string>> func = ct => _pair.RunAsync(_extraHoldMs, ct);
                    return MEAI.AIFunctionFactory.Create(func,
                        new MEAI.AIFunctionFactoryOptions { Name = Name, Description = Description });
                }
            }
        }

        /// <summary>
        /// Inner client for the parallel streamed path: the first stream yields TWO native
        /// FunctionCallContent updates (distinct tools, distinct call ids) and ends; the second
        /// stream (tool-result roundtrip) snapshots the request messages - so the test can
        /// inspect the tool-role protocol message - and yields plain text.
        /// </summary>
        private sealed class TwoNativeToolCallsScripted : MEAI.IChatClient
        {
            private int _streamCalls;

            public List<MEAI.ChatMessage> SecondCallMessages { get; private set; }

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
                _streamCalls++;
                if (_streamCalls == 1)
                {
                    yield return new MEAI.ChatResponseUpdate(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent>
                        {
                            new MEAI.FunctionCallContent(
                                "call-alpha", "tool_alpha", new Dictionary<string, object>())
                        });
                    await Task.Yield();
                    yield return new MEAI.ChatResponseUpdate(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent>
                        {
                            new MEAI.FunctionCallContent(
                                "call-beta", "tool_beta", new Dictionary<string, object>())
                        });
                    await Task.Yield();
                }
                else
                {
                    // Snapshot: the streaming loop keeps mutating the same message list.
                    SecondCallMessages = chatMessages.ToList();
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

            /// <summary>
            /// Mirrors the ToolExecutionPolicyEditModeTests stub: explicit override of the
            /// ICoreAISettings default-interface member (also 4) so tests can pin the mode.
            /// </summary>
            public int MaxParallelToolCalls { get; set; } = 4;
        }
    }
}
#endif

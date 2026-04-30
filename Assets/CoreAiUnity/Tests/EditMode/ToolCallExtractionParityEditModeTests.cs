#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode tests covering the unified text-based tool-call extraction across
    /// streaming and non-streaming paths. The fix: providers (Ollama, llama.cpp,
    /// LM Studio, some Qwen builds) that emit tool calls as JSON-in-text used to
    /// be invisible to <see cref="SmartToolCallingChatClient"/>; now both paths
    /// strip the JSON, execute the tool, and surface the extracted set via
    /// <see cref="LlmCompletionResult.ExecutedToolCalls"/> / <see cref="LlmStreamChunk.ExecutedToolCalls"/>.
    /// </summary>
    [TestFixture]
    public sealed class ToolCallExtractionParityEditModeTests
    {
        // ------------ Non-streaming: JSON-in-text fallback ------------

        [Test]
        public void NonStreaming_TextShapedToolCall_ExecutedAndStripped()
        {
            int iter = 0;
            int memInvocations = 0;

            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                return iter == 1
                    // First reply contains an embedded JSON tool-call (text mode).
                    ? MakeTextResponse(
                        "Working... {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"hello\"}}")
                    // After the tool result is fed back, the model finishes with text.
                    : MakeTextResponse("Saved.");
            });

            MEAI.AIFunction memTool = MakeAIFunction("memory", _ =>
            {
                memInvocations++;
                return Task.FromResult<object>("{\"Success\":true,\"Message\":\"ok\"}");
            });

            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var client = new SmartToolCallingChatClient(inner, new NullLogger(), settings,
                allowDuplicateToolCalls: true,
                tools: new List<ILlmTool> { new TestTool("memory") },
                roleId: "Teacher", maxConsecutiveErrors: 3);

            var options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { memTool } };
            MEAI.ChatResponse response = Task.Run(() =>
                client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            Assert.AreEqual(1, memInvocations, "Tool extracted from text must execute exactly once.");
            Assert.AreEqual(2, iter, "Loop should terminate after the model returns plain text.");
            Assert.AreEqual(1, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("memory", client.LastExecutedToolCalls[0].Name);
            Assert.IsTrue(client.LastExecutedToolCalls[0].Success);
            // Final assistant text must not contain the tool-call JSON.
            string finalText = response.Messages?.LastOrDefault()?.Text ?? "";
            Assert.That(finalText, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void NonStreaming_NoExecutionWhenToolNotBound_ButLastTracesEmpty()
        {
            // Simulate a model that emits text-mode JSON but the AIFunction is missing.
            // The non-streaming loop should give up after maxConsecutiveErrors because
            // each round resolves to "tool not found" (consistent with native fallback).
            int iter = 0;
            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                return MakeTextResponse(
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}");
            });

            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var client = new SmartToolCallingChatClient(inner, new NullLogger(), settings,
                allowDuplicateToolCalls: true,
                tools: new List<ILlmTool>(), roleId: "X", maxConsecutiveErrors: 3);

            // Tools list non-empty so the text-extraction path activates, but the AIFunction
            // for "memory" is *not* in the dictionary — extraction will succeed, but execution
            // will report "Tool 'memory' not found".
            var options = new MEAI.ChatOptions
            {
                Tools = new List<MEAI.AITool> { MakeAIFunction("other_tool", _ => Task.FromResult<object>("{\"Success\":true}")) }
            };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // Should record at least one missing-tool trace.
            Assert.That(client.LastExecutedToolCalls.Any(t => t.Source == "missing"), Is.True,
                "missing-tool trace should be recorded when AIFunction lookup fails");
        }

        // ------------ Streaming: gating no longer keyed on AIFunction count ------------

        [Test]
        public async Task Streaming_RequestedButNotBound_StripsJsonAndEmitsClean()
        {
            // Simulate the production bug: model emits text-mode JSON for a tool,
            // request.Tools contains MemoryLlmTool, but BuildAIFunctions dropped it
            // (memoryStore is null). Old behaviour: JSON leaked to the chat panel.
            // New behaviour: stream emits cleaned text, IsDone fires.
            var inner = new StreamingScripted(
                new[] { "Saved! {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}}" });

            var settings = new StubSettings();
            // memoryStore is null → BuildAIFunctions drops MemoryLlmTool → aiTools=0
            var client = new MeaiLlmClient(inner, new NullLogger(), settings, memoryStore: null);

            var request = new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = new List<ILlmTool> { new CoreAI.AgentMemory.MemoryLlmTool() }
            };

            var chunks = new List<LlmStreamChunk>();
            await foreach (var c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(c);
            }

            string visible = string.Concat(chunks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
            Assert.That(visible, Does.Not.Contain("\"name\":\"memory\""),
                "Tool-call JSON must be stripped from the visible stream even when the tool is not bound.");
            Assert.That(visible, Does.Contain("Saved!"), "Visible prefix must survive the strip.");

            var done = chunks.LastOrDefault(c => c.IsDone);
            Assert.IsNotNull(done, "Stream must yield an IsDone terminator.");
            Assert.That(done!.ExecutedToolCalls, Is.Not.Null);
            Assert.That(done.ExecutedToolCalls.Any(t => t.Source == "missing"), Is.True,
                "Final chunk should record a synthetic 'missing' trace for the unbound tool.");
        }

        [Test]
        public async Task Streaming_NoToolsRequested_PassesThroughTextWithoutChange()
        {
            // Sanity: when request.Tools is empty/null, extraction must NOT run.
            var inner = new StreamingScripted(
                new[] { "Plain reply with {braces} but no tool keys." });

            var client = new MeaiLlmClient(inner, new NullLogger(), new StubSettings(), null);
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "X",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = null
            };

            string acc = "";
            await foreach (var c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(c.Text)) acc += c.Text;
            }

            Assert.That(acc, Does.Contain("{braces}"));
        }

        // ------------ Multi-tool chain (tool → tool → text) ------------

        [Test]
        public void NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll()
        {
            // Iter 1: tool A (text-shape) → execute → continue.
            // Iter 2: tool B (text-shape, different name + args so duplicate guard does not block) → execute → continue.
            // Iter 3: plain "Done." text → loop exits.
            int iter = 0;
            int aCount = 0, bCount = 0;
            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                return iter switch
                {
                    1 => MakeTextResponse("Step 1: {\"name\":\"tool_a\",\"arguments\":{\"x\":1}}"),
                    2 => MakeTextResponse("Step 2: {\"name\":\"tool_b\",\"arguments\":{\"y\":2}}"),
                    _ => MakeTextResponse("Done.")
                };
            });

            MEAI.AIFunction toolA = MakeAIFunction("tool_a", _ => { aCount++; return Task.FromResult<object>("{\"Success\":true}"); });
            MEAI.AIFunction toolB = MakeAIFunction("tool_b", _ => { bCount++; return Task.FromResult<object>("{\"Success\":true}"); });

            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var client = new SmartToolCallingChatClient(inner, new NullLogger(), settings,
                allowDuplicateToolCalls: false,
                tools: new List<ILlmTool> { new TestTool("tool_a"), new TestTool("tool_b") },
                roleId: "Chain", maxConsecutiveErrors: 3);

            var options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { toolA, toolB } };
            MEAI.ChatResponse response = Task.Run(() =>
                client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            Assert.AreEqual(1, aCount, "tool_a executed once");
            Assert.AreEqual(1, bCount, "tool_b executed once");
            Assert.AreEqual(3, iter, "Loop must run exactly 3 LLM iterations (tool, tool, text)");

            // Final reply visible to user contains the closing text but no leaked JSON.
            string finalText = response.Messages?.LastOrDefault()?.Text ?? "";
            Assert.That(finalText, Does.Contain("Done."));
            Assert.That(finalText, Does.Not.Contain("\"name\":\"tool_a\""));
            Assert.That(finalText, Does.Not.Contain("\"name\":\"tool_b\""));

            // Both tool traces captured, in order, both successful.
            Assert.AreEqual(2, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("tool_a", client.LastExecutedToolCalls[0].Name);
            Assert.AreEqual("tool_b", client.LastExecutedToolCalls[1].Name);
            Assert.IsTrue(client.LastExecutedToolCalls[0].Success);
            Assert.IsTrue(client.LastExecutedToolCalls[1].Success);
        }

        // ------------ Parallel tool calls in one iteration ------------

        [Test]
        public void NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration()
        {
            int iter = 0;
            int aCount = 0, bCount = 0;
            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                if (iter == 1)
                {
                    var fcA = new MEAI.FunctionCallContent("call_a", "tool_a",
                        new Dictionary<string, object?> { { "x", 1 } });
                    var fcB = new MEAI.FunctionCallContent("call_b", "tool_b",
                        new Dictionary<string, object?> { { "y", 2 } });
                    return new MEAI.ChatResponse(new MEAI.ChatMessage(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent> { fcA, fcB }));
                }
                return MakeTextResponse("Both done.");
            });

            MEAI.AIFunction toolA = MakeAIFunction("tool_a", _ => { aCount++; return Task.FromResult<object>("{\"Success\":true}"); });
            MEAI.AIFunction toolB = MakeAIFunction("tool_b", _ => { bCount++; return Task.FromResult<object>("{\"Success\":true}"); });

            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var client = new SmartToolCallingChatClient(inner, new NullLogger(), settings,
                allowDuplicateToolCalls: false,
                tools: new List<ILlmTool> { new TestTool("tool_a"), new TestTool("tool_b") },
                roleId: "Parallel", maxConsecutiveErrors: 3);

            var options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { toolA, toolB } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            Assert.AreEqual(1, aCount, "tool_a executed once");
            Assert.AreEqual(1, bCount, "tool_b executed once");
            Assert.AreEqual(2, iter, "Two parallel calls fit in one LLM iteration");

            Assert.AreEqual(2, client.LastExecutedToolCalls.Count);
            Assert.IsTrue(client.LastExecutedToolCalls.All(t => t.Success && t.Source == "native"));
        }

        // ------------ Native FunctionCallContent + text prefix in same response ------------

        [Test]
        public void NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked()
        {
            // Provider returned both a TextContent ("Working...") AND a native FunctionCallContent.
            // Native takes priority — text-extraction must NOT also fire and produce a phantom
            // duplicate call.
            int iter = 0;
            int execCount = 0;
            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                if (iter == 1)
                {
                    var prefix = new MEAI.TextContent(
                        "Working... {\"name\":\"phantom\",\"arguments\":{}}");
                    var fc = new MEAI.FunctionCallContent("call_real", "real_tool",
                        new Dictionary<string, object?>());
                    return new MEAI.ChatResponse(new MEAI.ChatMessage(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent> { prefix, fc }));
                }
                return MakeTextResponse("Finished.");
            });

            MEAI.AIFunction realTool = MakeAIFunction("real_tool", _ =>
            {
                execCount++;
                return Task.FromResult<object>("{\"Success\":true}");
            });

            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var client = new SmartToolCallingChatClient(inner, new NullLogger(), settings,
                allowDuplicateToolCalls: false,
                tools: new List<ILlmTool> { new TestTool("real_tool") },
                roleId: "NativeWins", maxConsecutiveErrors: 3);

            var options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { realTool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            Assert.AreEqual(1, execCount, "Only the native real_tool should execute (no phantom from text).");
            Assert.AreEqual(1, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("real_tool", client.LastExecutedToolCalls[0].Name);
            Assert.AreEqual("native", client.LastExecutedToolCalls[0].Source);
            // No 'phantom' from the text-mode pseudo-JSON should appear in the trace list.
            Assert.IsFalse(client.LastExecutedToolCalls.Any(t => t.Name == "phantom"));
        }

        // ------------ Streaming: success after failure resets the counter ------------

        [Test]
        public async Task Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues()
        {
            // Iter 1: tool fails. Iter 2: same tool succeeds (different args). Iter 3: text.
            // With AllowDuplicateToolCalls=true and different args, both calls reach the AIFunction.
            // After success the counter resets, so the third (text) iteration finishes cleanly.
            var inner = new StreamingScripted(
                new[] { "{\"name\":\"flaky\",\"arguments\":{\"n\":1}}" },
                new[] { "{\"name\":\"flaky\",\"arguments\":{\"n\":2}}" },
                new[] { "All good." });

            int call = 0;
            // DelegateLlmTool wraps a System.Delegate; MeaiLlmClient.BuildAIFunctions feeds it
            // through AIFunctionFactory.Create so the model sees an `(n: int) -> string` schema.
            Func<int, string> flakyDelegate = (int n) =>
            {
                call++;
                return call == 1
                    ? "{\"Success\":false,\"Error\":\"transient\"}"
                    : "{\"Success\":true}";
            };

            var settings = new StubSettings();
            var client = new MeaiLlmClient(inner, new NullLogger(), settings, memoryStore: null);

            var request = new LlmCompletionRequest
            {
                AgentRoleId = "Flaky",
                SystemPrompt = "x",
                UserPayload = "x",
                TraceId = "stream-flaky",
                AllowDuplicateToolCalls = true,
                Tools = new List<ILlmTool> { new DelegateLlmTool("flaky", "test", flakyDelegate) }
            };

            string acc = "";
            LlmStreamChunk last = null;
            await foreach (var c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(c.Text)) acc += c.Text;
                if (c.IsDone) last = c;
            }

            Assert.AreEqual(2, call, "Tool invoked twice (fail then success).");
            Assert.IsNotNull(last);
            Assert.IsNull(last!.Error, $"Expected no terminal error after counter reset; got: {last.Error}");
            Assert.That(acc, Does.Contain("All good."));
            Assert.That(acc, Does.Not.Contain("\"name\":\"flaky\""));
            Assert.AreEqual(2, last.ExecutedToolCalls.Count);
            Assert.IsFalse(last.ExecutedToolCalls[0].Success);
            Assert.IsTrue(last.ExecutedToolCalls[1].Success);
        }

        // ------------ Per-call [ToolCall] log line ------------

        [Test]
        public void NonStreaming_PerCallLogLine_IsEmittedWhenLogToolCallsEnabled()
        {
            // Spy logger so we can grep the log stream for [ToolCall] lines.
            // CoreAISettingsAsset defaults LogToolCalls/LogToolCallArguments to true,
            // so a fresh ScriptableObject already opts into the per-call diagnostic line.
            var spy = new SpyLogger();
            var settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            int iter = 0;
            var inner = new ScriptedChatClient(_ =>
            {
                iter++;
                return iter == 1
                    ? MakeToolCallResponse("memory")
                    : MakeTextResponse("done");
            });

            MEAI.AIFunction memTool = MakeAIFunction("memory", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"DONE\"}"));

            var client = new SmartToolCallingChatClient(inner, spy, settings,
                allowDuplicateToolCalls: true,
                tools: new List<ILlmTool> { new TestTool("memory") },
                roleId: "Teacher", maxConsecutiveErrors: 3, traceId: "trace-xyz");

            var options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { memTool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            string toolCallLine = spy.AllLines.FirstOrDefault(l => l.Contains("[ToolCall]"));
            Assert.IsNotNull(toolCallLine, $"Expected a [ToolCall] log line. Got:\n{string.Join("\n", spy.AllLines)}");
            Assert.That(toolCallLine, Does.Contain("traceId=trace-xyz"));
            Assert.That(toolCallLine, Does.Contain("role=Teacher"));
            Assert.That(toolCallLine, Does.Contain("tool=memory"));
            Assert.That(toolCallLine, Does.Contain("status=OK"));
        }

        // ------------ Diagnostic summary line ------------

        [Test]
        public void FormatExecutedTools_RendersStableLine()
        {
            var traces = new[]
            {
                new LlmToolCallTrace("memory", true, 12.3, "native"),
                new LlmToolCallTrace("missing_tool", false, 0.0, "missing"),
                new LlmToolCallTrace("memory", false, 0.0, "duplicate"),
            };

            string line = LoggingLlmClientDecorator.FormatExecutedTools(traces);

            Assert.That(line, Does.StartWith(" | tools=["));
            Assert.That(line, Does.Contain("memory(ok,12ms)"));
            Assert.That(line, Does.Contain("missing_tool(fail,0ms,missing)"));
            Assert.That(line, Does.Contain("memory(fail,0ms,duplicate)"));
            Assert.That(line, Does.EndWith("]"));
        }

        [Test]
        public void FormatExecutedTools_EmptyList_ReturnsEmpty()
        {
            Assert.AreEqual("", LoggingLlmClientDecorator.FormatExecutedTools(Array.Empty<LlmToolCallTrace>()));
            Assert.AreEqual("", LoggingLlmClientDecorator.FormatExecutedTools(null));
        }

        // ------------ Portable text-extractor (engine-agnostic) ------------

        [Test]
        public void PortableExtractor_StripsJsonInsideAssistantText()
        {
            string input = "Hi! {\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}} Bye!";
            string clean = LlmToolCallTextExtractor.StripForDisplay(input);
            Assert.That(clean, Does.Contain("Hi!"));
            Assert.That(clean, Does.Contain("Bye!"));
            Assert.That(clean, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void PortableExtractor_LeavesPlainTextUntouched()
        {
            string input = "Just text with {braces} and a config like {\"key\":\"value\"}.";
            string clean = LlmToolCallTextExtractor.StripForDisplay(input);
            Assert.AreEqual(input, clean);
        }

        [Test]
        public void PortableExtractor_TryExtractFindsMultipleMatches()
        {
            string input =
                "{\"name\":\"a\",\"arguments\":{}} mid {\"name\":\"b\",\"arguments\":{\"x\":1}}";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok);
            Assert.AreEqual(2, matches.Count);
            Assert.AreEqual("a", matches[0].Name);
            Assert.AreEqual("b", matches[1].Name);
            Assert.That(cleaned, Does.Contain("mid"));
            Assert.That(cleaned, Does.Not.Contain("\"name\""));
        }

        // ------------ Helpers ------------

        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));
        }

        private static MEAI.ChatResponse MakeToolCallResponse(string toolName)
        {
            var fc = new MEAI.FunctionCallContent(
                callId: "call_" + Guid.NewGuid().ToString("N"),
                name: toolName,
                arguments: new Dictionary<string, object?> { { "action", "write" }, { "content", "hi" } });
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent> { fc }));
        }

        private sealed class SpyLogger : IGameLogger
        {
            public readonly List<string> AllLines = new();
            public void LogDebug(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogInfo(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogWarning(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogError(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
        }

        private static MEAI.AIFunction MakeAIFunction(string name,
            Func<IEnumerable<KeyValuePair<string, object>>, Task<object>> handler)
        {
            Func<CancellationToken, Task<string>> func = async ct =>
            {
                object r = await handler(null);
                return r?.ToString() ?? "";
            };
            return MEAI.AIFunctionFactory.Create(func,
                new MEAI.AIFunctionFactoryOptions { Name = name, Description = "test tool" });
        }

        private sealed class TestTool : ILlmTool
        {
            public TestTool(string name) { Name = name; }
            public string Name { get; }
            public string Description => "";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _fn;
            private int _i;

            public ScriptedChatClient(Func<int, MEAI.ChatResponse> fn) { _fn = fn; }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null, CancellationToken ct = default)
            {
                _i++;
                return Task.FromResult(_fn(_i));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null, CancellationToken ct = default)
                => throw new NotSupportedException();

            public object GetService(Type t, object key = null) => null;
            public void Dispose() { }
        }

        private sealed class StreamingScripted : MEAI.IChatClient
        {
            private readonly Queue<string[]> _scripts;
            public StreamingScripted(params string[][] scripts) { _scripts = new Queue<string[]>(scripts); }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null, CancellationToken ct = default)
                => Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                if (_scripts.Count == 0) yield break;
                foreach (string s in _scripts.Dequeue())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, s);
                    await Task.Yield();
                }
            }

            public object GetService(Type t, object key = null) => null;
            public void Dispose() { }
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
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

        private sealed class NullLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature f, string m, UnityEngine.Object c = null) { }
            public void LogInfo(GameLogFeature f, string m, UnityEngine.Object c = null) { }
            public void LogWarning(GameLogFeature f, string m, UnityEngine.Object c = null) { }
            public void LogError(GameLogFeature f, string m, UnityEngine.Object c = null) { }
        }
    }
}
#endif

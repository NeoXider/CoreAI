#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
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
        public async Task NonStreaming_TextShapedToolCall_ExecutedAndStripped()
        {
            int iter = 0;
            int memInvocations = 0;

            ScriptedChatClient inner = new(_ =>
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

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true,
                new List<ILlmTool> { new TestTool("memory") },
                "Teacher", 3, allowTextShapedToolCalls: true);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { memTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, memInvocations, "Tool extracted from text must execute exactly once.");
            Assert.AreEqual(2, iter, "Loop should terminate after the model returns plain text.");
            Assert.AreEqual(1, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("memory", client.LastExecutedToolCalls[0].Name);
            Assert.IsTrue(client.LastExecutedToolCalls[0].Success);
            // Final assistant text must not contain the tool-call JSON.
            string finalText = response.Messages?.LastOrDefault()?.Text ?? "";
            AssertNoToolCallJsonFor(finalText, "memory");
        }

        [Test]
        public async Task NonStreaming_NoExecutionWhenToolNotBound_ButLastTracesEmpty()
        {
            // Simulate a model that emits text-mode JSON but the AIFunction is missing.
            // The non-streaming loop should give up after maxConsecutiveErrors because
            // each round resolves to "tool not found" (consistent with native fallback).
            int iter = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return MakeTextResponse(
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}");
            });

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // "memory" is declared to the role (so the text-extractor's known-tool-name registry
            // recognizes the JSON as a real call, not a hallucinated/example name) but deliberately
            // has no AIFunction below — extraction will succeed, but execution will report
            // "Tool 'memory' not found".
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true,
                new List<ILlmTool> { new TestTool("memory") }, "X", 3, allowTextShapedToolCalls: true);

            // Tools list non-empty so the text-extraction path activates, but the AIFunction
            // for "memory" is *not* in the dictionary — extraction will succeed, but execution
            // will report "Tool 'memory' not found".
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool>
                    { MakeAIFunction("other_tool", _ => Task.FromResult<object>("{\"Success\":true}")) }
            };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

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
            StreamingScripted inner = new(
                new[] { "Saved! {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}}" });

            StubSettings settings = new();
            // memoryStore is null → BuildAIFunctions drops MemoryLlmTool → aiTools=0
            MeaiLlmClient client = new(inner, new NullGameLogger(), settings, supportsNativeToolCalling: false, memoryStore: null);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = new List<ILlmTool> { new AgentMemory.MemoryLlmTool() }
            };

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(c);
            }

            string visible = string.Concat(chunks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
            AssertNoToolCallJsonFor(visible, "memory",
                "Tool-call JSON must be stripped from the visible stream even when the tool is not bound.");
            Assert.That(visible, Does.Contain("Saved!"), "Visible prefix must survive the strip.");

            LlmStreamChunk done = chunks.LastOrDefault(c => c.IsDone);
            Assert.IsNotNull(done, "Stream must yield an IsDone terminator.");
            Assert.That(done!.ExecutedToolCalls, Is.Not.Null);
            Assert.That(done.ExecutedToolCalls.Any(t => t.Source == "missing"), Is.True,
                "Final chunk should record a synthetic 'missing' trace for the unbound tool.");
        }

        [Test]
        public async Task Streaming_RequestedButNotBound_ChunkedStream_EmitsPrefixBeforeJsonCompletes()
        {
            // Two prose deltas before JSON: one "Saved! " delta would emit one hybrid chunk then dedupe at strip.
            StreamingScripted inner = new(
                new[]
                {
                    "Saved", "! ", "{\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}}"
                });

            StubSettings settings = new();
            MeaiLlmClient client = new(inner, new NullGameLogger(), settings, supportsNativeToolCalling: false, memoryStore: null);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = new List<ILlmTool> { new AgentMemory.MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    textChunks.Add(c.Text);
                }
            }

            Assert.GreaterOrEqual(textChunks.Count, 2,
                "Multiple inner prose deltas should yield multiple streamed text chunks before JSON.");
            string visible = string.Concat(textChunks);
            Assert.AreEqual("Saved! ", visible);
            AssertNoToolCallJsonFor(visible, "memory");
        }

        [Test]
        public async Task Streaming_NoToolsRequested_PassesThroughTextWithoutChange()
        {
            // Sanity: when request.Tools is empty/null, extraction must NOT run.
            StreamingScripted inner = new(
                new[] { "Plain reply with {braces} but no tool keys." });

            MeaiLlmClient client = new(inner, new NullGameLogger(), new StubSettings(), supportsNativeToolCalling: true, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "X",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = null
            };

            string acc = "";
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    acc += c.Text;
                }
            }

            Assert.That(acc, Does.Contain("{braces}"));
        }

        // ------------ Multi-tool chain (tool → tool → text) ------------

        [Test]
        public async Task NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll()
        {
            // Iter 1: tool A (text-shape) → execute → continue.
            // Iter 2: tool B (text-shape, different name + args so duplicate guard does not block) → execute → continue.
            // Iter 3: plain "Done." text → loop exits.
            int iter = 0;
            int aCount = 0, bCount = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return iter switch
                {
                    1 => MakeTextResponse("Step 1: {\"name\":\"tool_a\",\"arguments\":{\"x\":1}}"),
                    2 => MakeTextResponse("Step 2: {\"name\":\"tool_b\",\"arguments\":{\"y\":2}}"),
                    _ => MakeTextResponse("Done.")
                };
            });

            MEAI.AIFunction toolA = MakeAIFunction("tool_a", _ =>
            {
                aCount++;
                return Task.FromResult<object>("{\"Success\":true}");
            });
            MEAI.AIFunction toolB = MakeAIFunction("tool_b", _ =>
            {
                bCount++;
                return Task.FromResult<object>("{\"Success\":true}");
            });

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                false,
                new List<ILlmTool> { new TestTool("tool_a"), new TestTool("tool_b") },
                "Chain", 3, allowTextShapedToolCalls: true);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { toolA, toolB } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, aCount, "tool_a executed once");
            Assert.AreEqual(1, bCount, "tool_b executed once");
            Assert.AreEqual(3, iter, "Loop must run exactly 3 LLM iterations (tool, tool, text)");

            // Final reply visible to user contains the closing text but no leaked JSON.
            string finalText = response.Messages?.LastOrDefault()?.Text ?? "";
            Assert.That(finalText, Does.Contain("Done."));
            AssertNoToolCallJsonFor(finalText, "tool_a");
            AssertNoToolCallJsonFor(finalText, "tool_b");

            // Both tool traces captured, in order, both successful.
            Assert.AreEqual(2, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("tool_a", client.LastExecutedToolCalls[0].Name);
            Assert.AreEqual("tool_b", client.LastExecutedToolCalls[1].Name);
            Assert.IsTrue(client.LastExecutedToolCalls[0].Success);
            Assert.IsTrue(client.LastExecutedToolCalls[1].Success);
        }

        // ------------ Parallel tool calls in one iteration ------------

        [Test]
        public async Task NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration()
        {
            int iter = 0;
            int aCount = 0, bCount = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                if (iter == 1)
                {
                    MEAI.FunctionCallContent fcA = new("call_a", "tool_a",
                        new Dictionary<string, object?> { { "x", 1 } });
                    MEAI.FunctionCallContent fcB = new("call_b", "tool_b",
                        new Dictionary<string, object?> { { "y", 2 } });
                    return new MEAI.ChatResponse(new MEAI.ChatMessage(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent> { fcA, fcB }));
                }

                return MakeTextResponse("Both done.");
            });

            MEAI.AIFunction toolA = MakeAIFunction("tool_a", _ =>
            {
                aCount++;
                return Task.FromResult<object>("{\"Success\":true}");
            });
            MEAI.AIFunction toolB = MakeAIFunction("tool_b", _ =>
            {
                bCount++;
                return Task.FromResult<object>("{\"Success\":true}");
            });

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                false,
                new List<ILlmTool> { new TestTool("tool_a"), new TestTool("tool_b") },
                "Parallel", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { toolA, toolB } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, aCount, "tool_a executed once");
            Assert.AreEqual(1, bCount, "tool_b executed once");
            Assert.AreEqual(2, iter, "Two parallel calls fit in one LLM iteration");

            Assert.AreEqual(2, client.LastExecutedToolCalls.Count);
            Assert.IsTrue(client.LastExecutedToolCalls.All(t => t.Success && t.Source == "native"));
        }

        // ------------ Native FunctionCallContent + text prefix in same response ------------

        [Test]
        public async Task NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked()
        {
            // Provider returned both a TextContent ("Working...") AND a native FunctionCallContent.
            // Native takes priority — text-extraction must NOT also fire and produce a phantom
            // duplicate call.
            int iter = 0;
            int execCount = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                if (iter == 1)
                {
                    MEAI.TextContent prefix = new(
                        "Working... {\"name\":\"phantom\",\"arguments\":{}}");
                    MEAI.FunctionCallContent fc = new("call_real", "real_tool",
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

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                false,
                new List<ILlmTool> { new TestTool("real_tool") },
                "NativeWins", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { realTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

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
            StreamingScripted inner = new(
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

            StubSettings settings = new();
            // The call is written as PROSE, and the bound tool still has to run: this is the fallback channel
            // (the endpoint has no native call channel). The channel is declared explicitly rather than left to the
            // constructor default: the default is "there is a native channel", and prose is not parsed on it.
            MeaiLlmClient client = new(inner,
                new NullGameLogger(),
                settings,
                supportsNativeToolCalling: false,
                memoryStore: null);

            LlmCompletionRequest request = new()
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
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    acc += c.Text;
                }

                if (c.IsDone)
                {
                    last = c;
                }
            }

            Assert.AreEqual(2, call, "Tool invoked twice (fail then success).");
            Assert.IsNotNull(last);
            Assert.IsNull(last!.Error, $"Expected no terminal error after counter reset; got: {last.Error}");
            Assert.That(acc, Does.Contain("All good."));
            // Live token streaming may append raw tool-call JSON to Text before tools run; semantics below still matter.
            Assert.AreEqual(2, last.ExecutedToolCalls.Count);
            Assert.IsFalse(last.ExecutedToolCalls[0].Success);
            Assert.IsTrue(last.ExecutedToolCalls[1].Success);
        }

        // ------------ Per-call [ToolCall] log line ------------

        [Test]
        public async Task NonStreaming_PerCallLogLine_IsEmittedWhenLogToolCallsEnabled()
        {
            // Spy logger so we can grep the log stream for [ToolCall] lines.
            // CoreAISettingsAsset defaults LogToolCalls/LogToolCallArguments to true,
            // so a fresh ScriptableObject already opts into the per-call diagnostic line.
            SpyLogger spy = new();
            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            int iter = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return iter == 1
                    ? MakeToolCallResponse("memory")
                    : MakeTextResponse("done");
            });

            MEAI.AIFunction memTool = MakeAIFunction("memory", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"DONE\"}"));

            SmartToolCallingChatClient client = new(inner, spy, settings,
                true,
                new List<ILlmTool> { new TestTool("memory") },
                "Teacher", 3, "trace-xyz");

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { memTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

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
            LlmToolCallTrace[] traces = new[]
            {
                new LlmToolCallTrace("memory", true, 12.3, "native"),
                new LlmToolCallTrace("missing_tool", false, 0.0, "missing"),
                new LlmToolCallTrace("memory", false, 0.0, "duplicate")
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
            AssertNoToolCallJsonFor(clean, "memory");
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
            AssertNoToolCallJson(cleaned);
        }

        // ------------ LLMUnity Qwen3.5: arguments_json key (instead of arguments) --------

        [Test]
        public void PortableExtractor_ArgumentsJsonKey_ExtractedAsToolCall()
        {
            // Qwen3.5 via LLMUnity emits "arguments_json" instead of "arguments".
            string input =
                "{\"name\": \"read_skill\", \"arguments_json\": \"{\\\"skill_name\\\": \\\"Enchanting\\\"}\"}";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "arguments_json key should be recognized as a tool call.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("read_skill", matches[0].Name);
            Assert.That(matches[0].ArgumentsJson, Does.Contain("skill_name"));
            Assert.That(matches[0].ArgumentsJson, Does.Contain("Enchanting"));
        }

        [Test]
        public void PortableExtractor_LooksLikeToolCallJson_AcceptsArgumentsJson()
        {
            string json = "{\"name\":\"read_skill\",\"arguments_json\":\"{}\"}";
            Assert.IsTrue(LlmToolCallTextExtractor.LooksLikeToolCallJson(json),
                "LooksLikeToolCallJson must accept 'arguments_json' as alternative key.");
        }

        [Test]
        public void PortableExtractor_LooksLikeToolCallJson_RejectsWithoutEitherKey()
        {
            // Neither "arguments" nor "arguments_json" — not a tool call.
            string json = "{\"name\":\"foo\",\"params\":{}}";
            Assert.IsFalse(LlmToolCallTextExtractor.LooksLikeToolCallJson(json));
        }

        // ------------ Hermes / Qwen-Agent XML tool-call template --------

        [Test]
        public void PortableExtractor_XmlFunctionSyntax_ExtractsNameAndParameters()
        {
            // Many local GGUF models fall back to this when native tool_calls is empty (seen with
            // qwythos-9b: <tool_call><function=NAME><parameter=KEY>VALUE</parameter></function></tool_call>).
            string input =
                "I'll craft it.\n<tool_call>\n<function=call_skill_tool>\n" +
                "<parameter=tool_name>\ncraft_item\n</parameter>\n" +
                "<parameter=arguments_json>\n{\"item\": \"Flame Sword\"}\n</parameter>\n" +
                "</function>\n</tool_call>";

            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Hermes/Qwen XML tool-call format must be extracted.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("call_skill_tool", matches[0].Name);
            Assert.That(matches[0].ArgumentsJson, Does.Contain("tool_name"));
            Assert.That(matches[0].ArgumentsJson, Does.Contain("craft_item"));
            Assert.That(matches[0].ArgumentsJson, Does.Contain("Flame Sword"));
            Assert.That(cleaned, Does.Not.Contain("<tool_call>"));
            Assert.That(cleaned, Does.Not.Contain("<function="));
            StringAssert.Contains("I'll craft it.", cleaned);
        }

        // ------------ LLMUnity Qwen3.5: function-call syntax --------
        //
        // The `ident(...)` shape carries no evidence of a call at all: read_skill("Alchemy") and print("Привет, мир!")
        // are the very same line of code. That is why it is recognised ONLY against the registry of declared tools
        // (the TryExtract overload taking knownToolNames) and only for a declared name.
        // The tests below pass the registry explicitly; without a registry the shape does not work, and separate guards cover that.

        [Test]
        public void PortableExtractor_PythonOneLinerAnswer_IsTextForTheChild_NotAToolCall(
            [Values("print(\"Привет, мир!\")", "input(\"Введи своё имя\")", "len(my_list)")]
            string pythonAnswer)
        {
            // Defect: a teacher answer consisting of a single line of Python became, in its entirety, a call to a
            // non-existent tool `print`. The model got "Unknown tool" and the child got an empty bubble.
            bool ok = LlmToolCallTextExtractor.TryExtract(pythonAnswer, Declared("spawn_quiz", "memory"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "A line of Python written for the child is not a tool call.");
            Assert.AreEqual(0, matches.Count);
            Assert.AreEqual(pythonAnswer, cleaned, "The child must see the whole line of code.");
            Assert.AreEqual(pythonAnswer,
                LlmToolCallTextExtractor.StripForDisplay(pythonAnswer, Declared("spawn_quiz", "memory")));
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_WithoutToolRegistry_NeverFires()
        {
            // Without a registry read_skill("x") and print("x") are indistinguishable, so the `ident(...)` shape
            // is not recognised at all and the text reaches the child untouched.
            string input = "read_skill(\"Alchemy\")";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "With no registry of names the ident(...) shape does not count as a call.");
            Assert.AreEqual(input, cleaned);
            Assert.AreEqual(input, LlmToolCallTextExtractor.StripForDisplay(input));
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_UndeclaredTool_StaysVisible()
        {
            // There is a registry, but this tool is not in it: there is nothing to execute and nothing may be hidden.
            string input = "lookup_item(\"Flame Sword\")";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("memory", "read_skill"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "A name outside the registry is not a call.");
            Assert.AreEqual(input, cleaned);
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_ReadSkillQuoted()
        {
            // Qwen3.5 via LLMUnity: read_skill("Alchemy")
            string input = "read_skill(\"Alchemy\")";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("read_skill"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Function-call syntax should be extracted.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("read_skill", matches[0].Name);
            Assert.That(matches[0].ArgumentsJson, Does.Contain("skill_name"));
            Assert.That(matches[0].ArgumentsJson, Does.Contain("Alchemy"));
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_ReadSkillUnquoted()
        {
            // Qwen3.5 via LLMUnity: read_skill(Crafting)
            string input = "read_skill(Crafting)";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("read_skill"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Unquoted function-call syntax should be extracted.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("read_skill", matches[0].Name);
            Assert.That(matches[0].ArgumentsJson, Does.Contain("Crafting"));
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_CallSkillTool()
        {
            // call_skill_tool("get_recipes", '{"item":"sword"}')
            string input = "call_skill_tool(\"get_recipes\", '{\"item\":\"sword\"}')";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("call_skill_tool"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Multi-arg function-call syntax should be extracted.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("call_skill_tool", matches[0].Name);
            Assert.That(matches[0].ArgumentsJson, Does.Contain("tool_name"));
            Assert.That(matches[0].ArgumentsJson, Does.Contain("get_recipes"));
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_KeywordArguments()
        {
            // Live shape from the game benchmark (codex spark): a whole message that is one
            // python-style call with key=value args. Before the keyword branch this collapsed
            // into {"input":"action='spawn'"} and failed required-argument validation.
            string input =
                "world_command(action='spawn', targetName='Goal', prefabKey=\"Cube\", x=0, y=1.5, z=2, solid=true)";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("world_command"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Keyword-argument function-call syntax should be extracted.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("world_command", matches[0].Name);
            Dictionary<string, object> args = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(
                matches[0].ArgumentsJson);
            Assert.AreEqual("spawn", args["action"], "Quoted string values keep their content.");
            Assert.AreEqual("Goal", args["targetName"]);
            Assert.AreEqual("Cube", args["prefabKey"], "Double-quoted values are unquoted too.");
            Assert.AreEqual(0L, args["x"], "Integers parse as numbers, not strings.");
            Assert.AreEqual(1.5d, args["y"], "Decimals parse as numbers (invariant culture).");
            Assert.AreEqual(true, args["solid"], "Booleans parse as booleans.");
            Assert.That(args, Does.Not.ContainKey("input"),
                "Keyword calls must not collapse into the generic positional 'input' argument.");
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_PositionalStillUsesInput()
        {
            // A single positional argument (no key=value) keeps the legacy generic behavior —
            // for a DECLARED tool. The same text with the name outside the registry is plain code
            // (see UndeclaredTool_StaysVisible): the former norm "any ident(...) is a command" blanked
            // every one-line Python answer a teacher gives.
            string input = "lookup_item(\"Flame Sword\")";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("lookup_item"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, matches.Count);
            StringAssert.Contains("input", matches[0].ArgumentsJson);
            StringAssert.Contains("Flame Sword", matches[0].ArgumentsJson);
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_DoesNotMatchProseWithParens()
        {
            // Prose with parentheses should NOT be extracted as a function call — even with a registry.
            string input = "The player crafted a sword (iron + oak) and it was successful.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("lookup_item"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Prose with parentheses should NOT trigger function-call extraction.");
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_ParensInsideQuotedArgumentPreserved()
        {
            // FINDING-2c: the old lazy (.*?) regex stopped at the first ')' and silently
            // truncated (then rejected) arguments containing parentheses.
            string input = "lookup_item(\"Flame (Fire) Sword\")";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("lookup_item"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Parentheses inside a quoted argument must not break extraction.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("lookup_item", matches[0].Name);
            StringAssert.Contains("Flame (Fire) Sword", matches[0].ArgumentsJson);
        }

        [Test]
        public void PortableExtractor_FunctionCallSyntax_ParensInsideKeywordValuePreserved()
        {
            string input = "world_command(action='say', text='hello (world)')";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("world_command"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("world_command", matches[0].Name);
            Dictionary<string, object> args = Newtonsoft.Json.JsonConvert
                .DeserializeObject<Dictionary<string, object>>(matches[0].ArgumentsJson);
            Assert.AreEqual("say", args["action"]);
            Assert.AreEqual("hello (world)", args["text"],
                "The value must keep everything up to the balanced closing paren.");
        }

        // ------------ FINDING-2a: quoted schema examples must not execute --------

        [Test]
        public void PortableExtractor_BacktickQuotedSchemaExample_NotExecuted()
        {
            string input =
                "Use `{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}` to clear your memory.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Inline-code (backtick) JSON is a citation, not a command.");
            Assert.AreEqual(0, matches.Count);
            Assert.AreEqual(input, LlmToolCallTextExtractor.StripForDisplay(input),
                "Cited JSON must stay visible in the display text.");
        }

        [Test]
        public void PortableExtractor_QuoteWrappedSchemaExample_NotExecuted()
        {
            string input =
                "The docs show '{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}' as the format.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "JSON wrapped in matching quotes is a citation, not a command.");
        }

        [Test]
        public void PortableExtractor_FencedCodeBlockExample_NotExecuted()
        {
            string input =
                "Example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}\n```\nDone.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "JSON inside fenced code blocks must be ignored.");
        }

        [Test]
        public void PortableExtractor_PlaceholderToolName_NotExecuted()
        {
            // A model quoting the schema with a placeholder name must not trigger execution.
            string input = "Reply with {\"name\":\"<tool_name>\",\"arguments\":{}} to call a tool.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Placeholder tool names are schema examples, not commands.");
            Assert.IsFalse(LlmToolCallTextExtractor.LooksLikeToolCallJson(
                "{\"name\":\"<tool_name>\",\"arguments\":{}}"));
        }

        [Test]
        public void PortableExtractor_CitedExamplePlusRealCall_OnlyRealCallExecuted()
        {
            string input =
                "Example: `{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}` and now " +
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"real\"}}";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, matches.Count, "Only the non-cited call should be extracted.");
            StringAssert.Contains("real", matches[0].ArgumentsJson);
            StringAssert.Contains("`", cleaned, "The cited example must stay in the cleaned text.");
        }

        // ------------ FINDING-2b: pseudo Action=write only when command-shaped --------

        [Test]
        public void PortableExtractor_PseudoWrite_WholeMessageOrProsePrefix_StillExtracted()
        {
            bool ok = LlmToolCallTextExtractor.TryExtract(
                "Okay. Action=write content=\"hello\"",
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok, "Command-shaped pseudo write ending the line must still map to memory.");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("memory", matches[0].Name);
            StringAssert.Contains("hello", matches[0].ArgumentsJson);
            StringAssert.Contains("Okay.", cleaned);
        }

        [Test]
        public void PortableExtractor_PseudoWriteQuotedInProse_NotExecuted()
        {
            // The model talks ABOUT the syntax mid-sentence — synthesizing a write here
            // overwrote real memories with quoted text.
            string input =
                "Earlier I ran Action=write content=\"exam on June 15\" and it worked fine.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Pseudo write followed by prose on the same line is a citation.");
        }

        [Test]
        public void PortableExtractor_PseudoWriteInsideCodeFence_NotExecuted()
        {
            string input = "Use this syntax:\n```\nAction=write content=\"example\"\n```";
            bool ok = LlmToolCallTextExtractor.TryExtract(input,
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Pseudo write inside a fenced code block is an example.");
        }

        [Test]
        public void PortableExtractor_PseudoWrite_MemoryNotDeclared_NotExecuted()
        {
            // A pseudo-write synthesises a call to `memory` SPECIFICALLY; if the role was never given it, there is nothing to synthesise.
            string input = "Okay. Action=write content=\"hello\"";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("spawn_quiz"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "Without a declared memory tool a pseudo-write is just text.");
            Assert.AreEqual(input, cleaned);
        }

        // ------------ A JSON example inside a lesson is not a call (name registry and fences) --------

        [Test]
        public void PortableExtractor_SpawnQuizJsonExampleInFencedBlock_ShownToTheChild_NotExecuted()
        {
            // The teacher explains the quiz format and shows the JSON as an example inside ```json ... ```. The tool
            // name is real and declared, so only the fence can tell an example apart from a call.
            string example =
                "{\"name\": \"spawn_quiz\", \"arguments\": {\"question\": \"Что выведет print(2 + 2)?\", " +
                "\"options\": [\"4\", \"22\"], \"answer\": 0}}";
            string input =
                "Квиз описывается вот таким объектом:\n```json\n" + example + "\n```\nПопробуй составить свой!";

            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("spawn_quiz"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "A JSON example inside a code fence is not executed.");
            Assert.AreEqual(0, matches.Count);
            Assert.AreEqual(input, cleaned, "The example stays in the lesson text in full.");
        }

        [Test]
        public void PortableExtractor_JsonExampleInFenceCutOffByTokenLimit_NotExecuted()
        {
            // The answer was cut off by the token limit inside a ```json example, so there is no closing fence.
            // An unclosed fence used to not count as a code block, and the example was executed as a call.
            string input =
                "Вот пример вызова:\n```json\n" +
                "{\"name\": \"spawn_quiz\", \"arguments\": {\"question\": \"Сколько будет 2 + 2?\"}}\n" +
                "И дальше я объясню, что значит каждое по";

            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("spawn_quiz"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "A fence that is opened but never closed is still a code block until the end of the text.");
            Assert.AreEqual(input, cleaned);
        }

        [Test]
        public void PortableExtractor_JsonWithUndeclaredToolName_StaysVisibleWhenRegistryKnown()
        {
            // An example with an invented name: it cannot be executed, and cutting it out would show the child
            // emptiness where a line of explanation should be.
            string input = "Формат такой: {\"name\": \"example_tool\", \"arguments\": {\"x\": 1}} — имя и аргументы.";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("spawn_quiz", "memory"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "A name outside the registry is not a call.");
            Assert.AreEqual(input, cleaned);
        }

        [Test]
        public void PortableExtractor_JsonForDeclaredTool_StillExtractedWithRegistry()
        {
            // The registry narrows things down, it does not break them: a real call to a declared tool is extracted as
            // before, including with a space after the colon, which is how models write it.
            string input = "Запоминаю. {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"любит котов\"}}";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("memory", "spawn_quiz"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("memory", matches[0].Name);
            StringAssert.Contains("любит котов", matches[0].ArgumentsJson);
            Assert.AreEqual("Запоминаю.", cleaned);
        }

        [Test]
        public void PortableExtractor_EmptyRegistry_NothingIsACall()
        {
            // The registry was passed and it is empty: there are no declared tools, so there can be no calls either.
            string input = "{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared(),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok);
            Assert.AreEqual(input, cleaned);
        }

        [Test]
        public void PortableExtractor_XmlExampleInsideFence_NotExecuted()
        {
            // A hole beyond the one described: the XML branch searched the raw text, bypassing code fences.
            string input =
                "Некоторые модели пишут вызов так:\n```xml\n<function=memory><parameter=action>clear</parameter></function>\n```\n";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("memory"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "An XML example inside a code fence is not executed.");
            Assert.AreEqual(input, cleaned);
        }

        [Test]
        public void PortableExtractor_XmlUndeclaredTool_NotExecuted()
        {
            string input = "<tool_call><function=lookup_item><parameter=input>sword</parameter></function></tool_call>";
            bool ok = LlmToolCallTextExtractor.TryExtract(input, Declared("memory"),
                out List<LlmToolCallTextExtractor.Match> matches, out string cleaned);

            Assert.IsFalse(ok, "An XML tool call outside the registry is not a call.");
        }

        [Test]
        public void StripCodeBlocks_UnclosedFence_BlanksToEndAndKeepsLength()
        {
            string input = "text ```json\n{\"name\":\"x\",\"arguments\":{}}";
            string stripped = LlmToolCallTextExtractor.StripCodeBlocks(input);

            Assert.AreEqual(input.Length, stripped.Length, "The length must be preserved: offsets are computed against it.");
            StringAssert.StartsWith("text ", stripped);
            Assert.That(stripped.Substring(5).Trim(), Is.Empty, "Everything after an open fence is whitespace.");
        }

        // ------------ Non-streaming: arguments_json key through SmartToolCallingChatClient --------

        [Test]
        public async Task NonStreaming_ArgumentsJsonKey_ExecutedAndStripped()
        {
            int iter = 0;
            int readSkillInvocations = 0;

            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return iter == 1
                    ? MakeTextResponse(
                        "{\"name\":\"read_skill\",\"arguments_json\":\"{\\\"skill_name\\\":\\\"Crafting\\\"}\"}")
                    : MakeTextResponse("Skill loaded.");
            });

            MEAI.AIFunction readSkillTool = MakeAIFunction("read_skill", _ =>
            {
                readSkillInvocations++;
                return Task.FromResult<object>("{\"instructions\":\"craft stuff\"}");
            });

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true,
                new List<ILlmTool> { new TestTool("read_skill") },
                "Test", 3, allowTextShapedToolCalls: true);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { readSkillTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, readSkillInvocations,
                "read_skill with arguments_json key must execute exactly once.");
            Assert.AreEqual(2, iter,
                "Loop should terminate after the model returns plain text.");
            Assert.AreEqual(1, client.LastExecutedToolCalls.Count);
            Assert.AreEqual("read_skill", client.LastExecutedToolCalls[0].Name);
            Assert.IsTrue(client.LastExecutedToolCalls[0].Success);
        }

        // ------------ Non-streaming: a line of Python reaches the child --------

        [Test]
        public async Task NonStreaming_PythonOneLinerAnswer_ReachesTheChildUnchanged()
        {
            // The defect in full: on the fallback channel the model answered with a single line of code, and the loop
            // turned it into a call to the tool `print`. The child must get exactly that line, and the tools must stay quiet.
            const string answer = "print(\"Привет, мир!\")";
            int iter = 0;
            int quizInvocations = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return MakeTextResponse(answer);
            });

            MEAI.AIFunction quizTool = MakeAIFunction("spawn_quiz", _ =>
            {
                quizInvocations++;
                return Task.FromResult<object>("{\"Success\":true}");
            });

            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true,
                new List<ILlmTool> { new TestTool("spawn_quiz") },
                "Teacher", 3, allowTextShapedToolCalls: true);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { quizTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, iter, "A line of code is an ordinary answer: there must be no second iteration.");
            Assert.AreEqual(0, quizInvocations);
            Assert.AreEqual(0, client.LastExecutedToolCalls.Count, "Not a single call, not even a \"missing\" one.");
            Assert.AreEqual(answer, response.Messages?.LastOrDefault()?.Text ?? "");
        }

        // ------------ Streaming: production shapes - token slicing, Cyrillic, escaping --------

        [Test]
        public async Task Streaming_PythonOneLinerAnswer_IsShownToTheChild_NotTurnedIntoToolCall()
        {
            // The same defect in the streaming loop of MeaiLlmClient on the fallback channel: prose is parsed into
            // calls there, and that is exactly where a line of code became a call. The channel is declared explicitly
            // (`supportsNativeToolCalling: false`): on the native channel prose is not parsed at all and the test would
            // pass while guarding nothing. One tool is declared and bound, and the answer is a line of Python by tokens.
            StreamingScripted inner = new(new[] { "print(", "\"Привет, ", "мир!\")" });
            MeaiLlmClient client = new(inner,
                new NullGameLogger(),
                new StubSettings(),
                supportsNativeToolCalling: false,
                memoryStore: null);

            int quizInvocations = 0;
            Func<string, string> quizDelegate = (string question) =>
            {
                quizInvocations++;
                return "{\"Success\":true}";
            };

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = new List<ILlmTool> { new DelegateLlmTool("spawn_quiz", "test", quizDelegate) }
            };

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(c);
            }

            string visible = string.Concat(chunks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
            Assert.AreEqual("print(\"Привет, мир!\")", visible, "The child sees the whole line of code.");
            Assert.AreEqual(0, quizInvocations);
            LlmStreamChunk done = chunks.LastOrDefault(c => c.IsDone);
            Assert.IsNotNull(done, "Stream must yield an IsDone terminator.");
            Assert.IsNull(done!.Error, $"An answer with no call must not end with an error: {done.Error}");
            Assert.That(done.ExecutedToolCalls ?? new List<LlmToolCallTrace>(), Is.Empty,
                "No call at all, neither a real one nor a \"missing\" one, may be synthesised out of a line of code.");
        }

        [Test]
        public async Task Streaming_ToolCallJsonSplitAcrossTokenBoundaries_ExtractedAndStripped()
        {
            // The production case: the model emits the call JSON in dozens of deltas, and the keys and the name break
            // mid-word. Until now every test fed the JSON in a single delta. The fallback channel is declared
            // explicitly; the tool is declared but not bound (memoryStore == null), so extraction must still fire and
            // the JSON must vanish from the feed.
            StreamingScripted inner = new(
                new[]
                {
                    "Сейчас запомню. ",
                    "{\"name\":\"mem", "ory\",\"argu", "ments\":{\"action\":\"wri", "te\",\"content\":\"hel", "lo\"}}"
                });
            MeaiLlmClient client = new(inner,
                new NullGameLogger(),
                new StubSettings(),
                supportsNativeToolCalling: false,
                memoryStore: null);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                Tools = new List<ILlmTool> { new AgentMemory.MemoryLlmTool() }
            };

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(c);
            }

            string visible = string.Concat(chunks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
            Assert.That(visible, Does.Contain("Сейчас запомню."));
            AssertNoToolCallJsonFor(visible, "memory", "Call JSON sliced up by tokens must not leak into the feed.");
            Assert.That(visible, Does.Not.Contain("\"arguments\""));

            LlmStreamChunk done = chunks.LastOrDefault(c => c.IsDone);
            Assert.IsNotNull(done);
            Assert.That(done!.ExecutedToolCalls, Is.Not.Null);
            Assert.That(done.ExecutedToolCalls.Any(t => t.Name == "memory" && t.Source == "missing"), Is.True,
                "The call was reassembled from the pieces and reached the policy (the tool is not bound, so the trace is \"missing\").");
        }

        [Test]
        public async Task Streaming_CyrillicAndEscapedQuotesSplitAtChunkBoundary_ArgumentsArriveIntact()
        {
            // A production spawn_quiz: a Russian question with escaped quotes inside it, sliced so that one delta ends
            // with a lone `\` (the escape breaks on a chunk boundary) and the Cyrillic breaks mid-word.
            // The tool is bound: the argument has to arrive in it without losses.
            StreamingScripted inner = new(
                new[]
                {
                    "Проверим! ",
                    "{\"name\":\"spawn_",
                    "quiz\",\"arguments\":{\"ques",
                    "tion\":\"Что выве",
                    "дет print(\\",
                    "\"Привет\\\")?\"}}"
                },
                new[] { "Готово!" });

            string capturedQuestion = null;
            Func<string, string> quizDelegate = (string question) =>
            {
                capturedQuestion = question;
                return "{\"Success\":true}";
            };

            // The fallback channel is declared explicitly: a call arrives as prose only where there is no native channel.
            MeaiLlmClient client = new(inner,
                new NullGameLogger(),
                new StubSettings(),
                supportsNativeToolCalling: false,
                memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "x",
                UserPayload = "x",
                TraceId = "stream-quiz",
                Tools = new List<ILlmTool> { new DelegateLlmTool("spawn_quiz", "test", quizDelegate) }
            };

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(c);
            }

            Assert.AreEqual("Что выведет print(\"Привет\")?", capturedQuestion,
                "Cyrillic and escaped quotes sliced at a chunk boundary must arrive intact.");

            string visible = string.Concat(chunks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
            Assert.That(visible, Does.Contain("Проверим!"));
            Assert.That(visible, Does.Contain("Готово!"));
            AssertNoToolCallJsonFor(visible, "spawn_quiz");
            Assert.That(visible, Does.Not.Contain("\"arguments\""));

            LlmStreamChunk done = chunks.LastOrDefault(c => c.IsDone);
            Assert.IsNotNull(done);
            Assert.IsNull(done!.Error, $"Expected no terminal error; got: {done.Error}");
            Assert.That(done.ExecutedToolCalls, Is.Not.Null);
            Assert.That(done.ExecutedToolCalls.Any(t => t.Name == "spawn_quiz" && t.Success), Is.True);
        }

        // ------------ Helpers ------------

        /// <summary>The registry of declared tools for the <c>TryExtract</c> overload that takes names.</summary>
        private static IReadOnlyCollection<string> Declared(params string[] names)
        {
            return names;
        }

        /// <summary>
        /// WHY: models write JSON both as <c>"name":"x"</c> and as <c>"name": "x"</c>; a check against the exact
        /// substring let a leak with a space after the colon slip through.
        /// </summary>
        private static void AssertNoToolCallJsonFor(string text, string toolName, string message = null)
        {
            string pattern = "\"name\"\\s*:\\s*\"" + Regex.Escape(toolName) + "\"";
            Assert.That(text ?? "", Does.Not.Match(pattern), message ?? $"Tool-call JSON for '{toolName}' leaked.");
        }

        private static void AssertNoToolCallJson(string text, string message = null)
        {
            Assert.That(text ?? "", Does.Not.Match("\"name\"\\s*:"), message ?? "Tool-call JSON leaked.");
        }

        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));
        }

        private static MEAI.ChatResponse MakeToolCallResponse(string toolName)
        {
            MEAI.FunctionCallContent fc = new(
                "call_" + Guid.NewGuid().ToString("N"),
                toolName,
                new Dictionary<string, object?> { { "action", "write" }, { "content", "hi" } });
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { fc }));
        }

        private sealed class SpyLogger : ILog
        {
            public readonly List<string> AllLines = new();

            public void Debug(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Info(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Warn(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Error(string message, string tag = null)
            {
                AllLines.Add(message);
            }
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
            public TestTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _fn;
            private int _i;

            public ScriptedChatClient(Func<int, MEAI.ChatResponse> fn)
            {
                _fn = fn;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat,
                MEAI.ChatOptions options = null, CancellationToken ct = default)
            {
                _i++;
                return Task.FromResult(_fn(_i));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null, CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type t, object key = null)
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

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat,
                MEAI.ChatOptions options = null, CancellationToken ct = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                if (_scripts.Count == 0)
                {
                    yield break;
                }

                foreach (string s in _scripts.Dequeue())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, s);
                    await Task.Yield();
                }
            }

            public object GetService(Type t, object key = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
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

        private sealed class NullGameLogger : CoreAI.Infrastructure.Logging.IGameLogger
        {
            public void LogDebug(CoreAI.Infrastructure.Logging.GameLogFeature f, string m, UnityEngine.Object c = null)
            {
            }

            public void LogInfo(CoreAI.Infrastructure.Logging.GameLogFeature f, string m, UnityEngine.Object c = null)
            {
            }

            public void LogWarning(CoreAI.Infrastructure.Logging.GameLogFeature f, string m,
                UnityEngine.Object c = null)
            {
            }

            public void LogError(CoreAI.Infrastructure.Logging.GameLogFeature f, string m, UnityEngine.Object c = null)
            {
            }
        }
    }
}
#endif

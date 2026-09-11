using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Retry/fallback suppression must count only traces whose tool body actually ran.
    /// An error result carrying only rejected/never-invoked traces (unknown tool, duplicate,
    /// parse error, missing binding) executed nothing, so a retryable provider failure in the
    /// same turn must stay retry- and fallback-eligible. Any actually-invoked trace (including
    /// invoked-but-failed, which may have mutated state) must keep suppressing both.
    /// </summary>
    public sealed class RetryFallbackToolTraceSuppressionEditModeTests
    {
        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 3;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 4096;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.7f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls => 1;
            public int MaxToolResultChars => 8000;
            public ILlmAsyncMarshaler ToolInvocationMarshaler => PassThroughLlmAsyncMarshaler.Instance;
        }

        private static LlmToolCallTrace RejectedUnknownToolTrace()
        {
            return new LlmToolCallTrace("no_such_tool", false, 0d, "unknown-tool",
                "Error: Unknown tool 'no_such_tool'.");
        }

        private static LlmToolCallTrace RejectedDuplicateTrace()
        {
            // An echo is a successful no-op (ok:true, duplicate:true), but the body did NOT run: for a retry
            // that still counts as "nothing was invoked", and the trace source decides that, not its Success.
            return new LlmToolCallTrace("spawn", true, 0d, "duplicate",
                "{\"ok\":true,\"duplicate\":true,\"message\":\"Duplicate tool call 'spawn' with identical arguments: not executed again.\"}");
        }

        private static LlmToolCallTrace InvokedNativeTrace()
        {
            return new LlmToolCallTrace("world_tool", false, 3d, "native", "threw: boom");
        }

        private sealed class RetryableFailureThenOkMock : ILlmClient
        {
            private readonly IReadOnlyList<LlmToolCallTrace> _firstFailureTraces;
            public int CompleteCallCount;

            public RetryableFailureThenOkMock(IReadOnlyList<LlmToolCallTrace> firstFailureTraces)
            {
                _firstFailureTraces = firstFailureTraces;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                if (CompleteCallCount == 1)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "HTTP 429",
                        ErrorCode = LlmErrorCode.RateLimited,
                        HttpStatus = 429,
                        RetryAfterSeconds = 1,
                        ExecutedToolCalls = _firstFailureTraces
                    });
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "recovered" });
            }
        }

        private sealed class FixedResultLlm : ILlmClient
        {
            private readonly LlmCompletionResult _result;
            public int CompleteCallCount;

            public FixedResultLlm(LlmCompletionResult result)
            {
                _result = result;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                return Task.FromResult(_result);
            }
        }

#if COREAI_LLM
        [Test]
        public async Task DelegateLlmTool_MutateThenThrow_ReturnsErrorAndRecordsNativeTrace()
        {
            int sideEffects = 0;
            Func<string> body = () =>
            {
                sideEffects++;
                throw new JsonReaderException("cannot convert mutated payload");
            };
            DelegateLlmTool tool = new("grant_item", "Grant an item.", body);
            ToolExecutionPolicy policy = new(
                NullLog.Instance,
                new StubSettings(),
                new ILlmTool[] { tool },
                false,
                "Tester");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            MEAI.FunctionCallContent call = new(
                "call_grant_item",
                tool.Name,
                new Dictionary<string, object>());

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(call, options, CancellationToken.None);

            Assert.AreEqual(1, sideEffects);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("Error: cannot convert mutated payload", result.Result.Result.ToString());
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("native", policy.ExecutedTraces[0].Source);
        }

        /// <summary>
        /// An unconvertible argument is rejected BEFORE the body, so nothing was mutated and the trace says
        /// never-invoked: a retryable provider failure in the same turn stays retry- and fallback-eligible.
        /// <para>
        /// WHY this test previously expected the opposite (<c>native</c>, "invoked") and that was wrong: the
        /// verdict used to be guessed by looking for the delegate's method in the exception stack, which
        /// IL2CPP/WebGL strips - so an exception FROM THE BODY was read as a binding failure and the retry
        /// decorators replayed a turn that had already changed the world. Deleting that guess was right; but
        /// the distinction did not stay deleted. <c>TryBindArgumentsStructurally</c> now runs MEAI's own
        /// coercion standalone BEFORE the invocation boundary, so the rejection is proof, not inference, and
        /// the conservative reading has nothing left to protect against here.
        /// </para>
        /// </summary>
        [Test]
        public async Task DelegateLlmTool_ArgumentCoercionFailure_IsRejectedBeforeInvocation()
        {
            int sideEffects = 0;
            Func<int, string> body = count =>
            {
                sideEffects += count;
                return "ok";
            };
            DelegateLlmTool tool = new("grant_items", "Grant items.", body);
            ToolExecutionPolicy policy = new(
                NullLog.Instance,
                new StubSettings(),
                new ILlmTool[] { tool },
                false,
                "Tester");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            MEAI.FunctionCallContent call = new(
                "call_grant_items",
                tool.Name,
                new Dictionary<string, object> { ["count"] = "not-an-integer" });

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(call, options, CancellationToken.None);

            Assert.AreEqual(0, sideEffects, "The argument must still be rejected before the body runs");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("arg-conversion", policy.ExecutedTraces[0].Source);
            Assert.IsFalse(
                LoggingLlmClientDecorator.TraceIndicatesInvocation(policy.ExecutedTraces[0]),
                "Only a call that crossed the invocation boundary may suppress retry/fallback replay");
        }

        private enum StubColor
        {
            Red,
            Green,
            Blue
        }

        /// <summary>
        /// Shapes the structural arg preflight (<c>TryBindArgumentsStructurally</c>) must accept because
        /// MEAI's own binder accepts them. Each case names the tool, the argument MEAI would bind
        /// successfully, and an assertion on the value the delegate actually received.
        /// </summary>
        private static IEnumerable<TestCaseData> ArgConversionParityCases()
        {
            yield return new TestCaseData(new Func<Task>(async () =>
            {
                string received = null;
                Action<string> body = s => received = s;
                DelegateLlmTool tool = new("echo_string", "Echo a string.", body);
                await AssertAcceptedAsync(tool, "s", "hello");
                Assert.AreEqual("hello", received);
            })).SetName("ArgConversionParity_ValueAlreadyOfParameterType");

            yield return new TestCaseData(new Func<Task>(async () =>
            {
                int received = -1;
                Action<int> body = i => received = i;
                DelegateLlmTool tool = new("echo_int", "Echo an int.", body);
                await AssertAcceptedAsync(tool, "i", 42L);
                Assert.AreEqual(42, received);
            })).SetName("ArgConversionParity_LongToInt");

            yield return new TestCaseData(new Func<Task>(async () =>
            {
                float received = -1f;
                Action<float> body = f => received = f;
                DelegateLlmTool tool = new("echo_float", "Echo a float.", body);
                await AssertAcceptedAsync(tool, "f", 3.5d);
                Assert.AreEqual(3.5f, received);
            })).SetName("ArgConversionParity_DoubleToFloat");

            yield return new TestCaseData(new Func<Task>(async () =>
            {
                string received = null;
                Action<string> body = s => received = s;
                DelegateLlmTool tool = new("echo_json_string", "Echo a string.", body);
                System.Text.Json.JsonElement element =
                    System.Text.Json.JsonDocument.Parse("\"hi\"").RootElement;
                await AssertAcceptedAsync(tool, "s", element);
                Assert.AreEqual("hi", received);
            })).SetName("ArgConversionParity_JsonStringElementToString");

            yield return new TestCaseData(new Func<Task>(async () =>
            {
                StubColor received = StubColor.Red;
                Action<StubColor> body = c => received = c;
                DelegateLlmTool tool = new("echo_enum", "Echo an enum.", body);
                await AssertAcceptedAsync(tool, "c", "Green");
                Assert.AreEqual(StubColor.Green, received);
            })).SetName("ArgConversionParity_EnumFromName");

            yield return new TestCaseData(new Func<Task>(async () =>
            {
                object received = null;
                Action<object> body = o => received = o;
                DelegateLlmTool tool = new("echo_object", "Echo an object.", body);
                System.Text.Json.JsonElement element =
                    System.Text.Json.JsonDocument.Parse("{\"x\":1}").RootElement;
                await AssertAcceptedAsync(tool, "o", element);
                Assert.IsNotNull(received);
            })).SetName("ArgConversionParity_JsonElementToObject");

            // The production shape of spawn_quiz: the normalizer hands an object argument as a compact
            // JSON string, and MEAI binds that string's CONTENT into a typed parameter. The preflight
            // must accept exactly what the binder accepts - otherwise the call failed before the tool
            // body ran.
            yield return new TestCaseData(new Func<Task>(async () =>
            {
                StubPayload received = null;
                Action<StubPayload> body = p => received = p;
                DelegateLlmTool tool = new("echo_payload", "Echo a payload.", body);
                await AssertAcceptedAsync(tool, "p", "{\"name\":\"kit\",\"count\":2}");
                Assert.IsNotNull(received);
                Assert.AreEqual("kit", received.Name);
                Assert.AreEqual(2, received.Count);
            })).SetName("ArgConversionParity_JsonStringToComplexParameter");
        }

        private sealed class StubPayload
        {
            public string Name { get; set; }

            public int Count { get; set; }
        }

        /// <summary>
        /// Fails the calling test if <c>TryBindArgumentsStructurally</c> rejects a shape MEAI itself
        /// would have bound (surfaced as an "arg-conversion" trace instead of an executed "native" one).
        /// </summary>
        private static async Task AssertAcceptedAsync(DelegateLlmTool tool, string argName, object rawValue)
        {
            ToolExecutionPolicy policy = new(
                NullLog.Instance,
                new StubSettings(),
                new ILlmTool[] { tool },
                false,
                "Tester");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            MEAI.FunctionCallContent call = new(
                "call_" + tool.Name,
                tool.Name,
                new Dictionary<string, object> { [argName] = rawValue });

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(call, options, CancellationToken.None);

            Assert.IsTrue(result.Succeeded,
                $"MEAI would accept this value for tool '{tool.Name}'; the structural preflight must not " +
                $"reject it. Got: {result.Result.Result}");
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("native", policy.ExecutedTraces[0].Source);
        }

        [TestCaseSource(nameof(ArgConversionParityCases))]
        public async Task ArgConversionParity(Func<Task> scenario)
        {
            await scenario();
        }
#endif

        [Test]
        [Timeout(20_000)]
        public async Task LoggingDecorator_RetryableFailure_OnlyRejectedTraces_RetryProceeds()
        {
            RetryableFailureThenOkMock inner = new(new[]
            {
                RejectedUnknownToolTrace(),
                RejectedDuplicateTrace()
            });
            LoggingLlmClientDecorator dec = new(inner, NullLog.Instance, 0f, 1);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                TraceId = "rejected-traces-retry",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "A 429 after only rejected tool calls must be retried");
            Assert.AreEqual("recovered", result.Content);
            Assert.AreEqual(2, inner.CompleteCallCount);
        }

        [Test]
        [Timeout(20_000)]
        public async Task LoggingDecorator_RetryableFailure_ExecutedTrace_RetrySuppressed()
        {
            RetryableFailureThenOkMock inner = new(new[] { InvokedNativeTrace() });
            LoggingLlmClientDecorator dec = new(inner, NullLog.Instance, 0f, 1);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                TraceId = "executed-trace-no-retry",
                UserPayload = "x"
            });

            Assert.IsFalse(result.Ok, "A turn whose tool body ran must never be blindly retried");
            Assert.AreEqual(1, inner.CompleteCallCount);
            Assert.AreEqual(1, result.ExecutedToolCalls.Count);
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_RetryableFailure_OnlyRejectedTraces_FallsBack()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "HTTP 429",
                ErrorCode = LlmErrorCode.RateLimited,
                ExecutedToolCalls = new[] { RejectedUnknownToolTrace() }
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "Nothing executed, so the secondary provider must be tried");
            Assert.AreEqual("secondary", result.Content);
            Assert.AreEqual(1, primary.CompleteCallCount);
            Assert.AreEqual(1, secondary.CompleteCallCount);
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_RetryableFailure_ExecutedTrace_DoesNotFallBack()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "HTTP 503 after mutation",
                ErrorCode = LlmErrorCode.BackendUnavailable,
                ExecutedToolCalls = new[] { InvokedNativeTrace() }
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsFalse(result.Ok, "A turn that invoked a tool must not be replayed on the secondary");
            Assert.AreEqual(0, secondary.CompleteCallCount);
        }

        /// <summary>
        /// Defect: the streaming fallback checked whether the code was retryable before it checked for
        /// ExecutedToolCalls on the failing chunk. A "tool ran, then 503" chunk moved the turn to the
        /// secondary, and the tool was executed a second time.
        /// </summary>
        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_Streaming_RetryableErrorChunkAfterToolExecution_DoesNotFallBack()
        {
            LlmStreamChunk failedAfterTool = new()
            {
                IsDone = true,
                Error = "HTTP 503 after the tool already ran",
                ErrorCode = LlmErrorCode.BackendUnavailable,
                ExecutedToolCalls = new[] { InvokedNativeTrace() }
            };
            ScriptedStreamingLlm primary = new(failedAfterTool);
            ScriptedStreamingLlm secondary = new(
                new LlmStreamChunk { Text = "secondary" },
                new LlmStreamChunk { IsDone = true });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in dec.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Tester",
                               UserPayload = "x"
                           }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(0, secondary.StreamCallCount,
                "A turn whose tool body ran must not be replayed on the secondary backend");
            Assert.AreEqual(0, dec.FallbackCount);
            Assert.AreEqual(1, chunks.Count);
            Assert.AreSame(failedAfterTool, chunks[0], "The post-tool failure must reach the caller unchanged");
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_TimeoutFailureResult_IsFallbackEligible()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "LLM request timed out after 30s without a response.",
                ErrorCode = LlmErrorCode.Timeout
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "A transport timeout on the primary must reach the secondary");
            Assert.AreEqual(1, secondary.CompleteCallCount);
        }


        /// <summary>
        /// Every trace source production actually emits must be classified ON PURPOSE by
        /// <see cref="LoggingLlmClientDecorator.TraceIndicatesInvocation"/>, i.e. named in the
        /// classifier test below.
        /// <para>
        /// WHY this exists rather than trust review: the same defect has now landed three times, each
        /// time through the same door. The classifier's <c>default</c> branch fails safe ("assume the
        /// tool ran"), which is right for an unknown source but silent for a KNOWN one - a source that
        /// simply nobody added to the list behaves as if a tool executed, and the only visible symptom
        /// is a lesson turn lost to a 429 that could have been retried. "arg-conversion" was removed as
        /// dead and had to come back; "tools-disabled" was never added at all. This test makes the
        /// omission loud at the moment it happens instead of at the moment a learner loses a turn.
        /// </para>
        /// </summary>
        [Test]
        public void EveryEmittedTraceSource_IsClassifiedOnPurpose()
        {
            string policy = Path.Combine(
                Application.dataPath,
                "CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs");
            Assert.IsTrue(File.Exists(policy), "ToolExecutionPolicy.cs moved: this guard needs its new path.");

            // Sources are recorded as a string argument to RecordSyntheticTrace(name, ok, ms, "<source>", ...).
            HashSet<string> emitted = Regex
                .Matches(File.ReadAllText(policy), @"RecordSyntheticTrace\([^;]*?""(?<source>[a-z][a-z-]*)""")
                .Select(match => match.Groups["source"].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.IsNotEmpty(emitted, "No trace sources found - the recording call was renamed and this guard went blind.");

            string classifierTest = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "CoreAiUnity/Tests/EditMode/RetryFallbackToolTraceSuppressionEditModeTests.cs"));
            // WHY the newline and the indent are part of the needle: the method name also appears in
            // this guard's summary, in its failure message, AND in this very constant - all of which
            // sit ABOVE the real declaration in the file. The first version searched for the bare
            // declaration text and matched the constant holding it, sliced an empty body, and
            // reported every source as unclassified. A guard that cannot find what it guards is
            // worse than none: it fails loudly for the wrong reason. Only a real declaration starts
            // a line at method indentation.
            const string declaration = "\n        public void TraceIndicatesInvocation_ClassifiesSourcesCorrectly()";
            int pinned = classifierTest.IndexOf(declaration, StringComparison.Ordinal);
            Assert.Greater(pinned, -1, "The classifier test was renamed: this guard cannot find what it checks.");
            int nextTest = classifierTest.IndexOf("[Test]", pinned, StringComparison.Ordinal);
            string pinnedBody = nextTest < 0
                ? classifierTest.Substring(pinned)
                : classifierTest.Substring(pinned, nextTest - pinned);

            List<string> unclassified = emitted
                .Where(source => !pinnedBody.Contains("\"" + source + "\"", StringComparison.Ordinal))
                .OrderBy(source => source, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(
                unclassified,
                "These trace sources are emitted by ToolExecutionPolicy but never pinned in " +
                "TraceIndicatesInvocation_ClassifiesSourcesCorrectly, so they silently fall to the " +
                "fail-safe default of \"a tool ran\" - which suppresses retry and failover for a turn " +
                "that may have executed nothing. Decide for each, and pin it with the reason: " +
                string.Join(", ", unclassified));
        }

        [Test]
        public void TraceIndicatesInvocation_ClassifiesSourcesCorrectly()
        {
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "duplicate")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "parse-error")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "unknown-tool")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "missing")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "unbound-native")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "schema-validation")));
            // WHY this one is pinned explicitly: it was once removed from the classifier as a dead branch,
            // because the source had stopped being emitted. The structural argument preflight emits it
            // again, and without this assertion the branch reads as dead a second time - while deleting it
            // now silently costs a retry/failover for a turn that provably executed nothing.
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "arg-conversion")));
            // WHY: ToolMode = None means the policy refused the call before reaching the function,
            // so nothing ran and the surrounding request stays retry/failover eligible.
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "tools-disabled")));

            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", true, 5d, "native")));
            // WHY "blocked" is TRUE while every other refusal is false: it is recorded when an
            // EARLIER invocation in the same turn did not observe its deadline, so that body may
            // still be running. Replaying the turn could execute it twice. A lost turn is cheaper
            // than a mutation applied twice - this is a decision, not a gap in the list.
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "blocked")));
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "native")));
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "timeout")));
            // WHY: unknown/new sources must fail safe as "invoked" so double-execution
            // protection holds even if a new trace source is added later.
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "some-future-source")));
        }

        [Test]
        public void HasInvokedToolCalls_EmptyOrRejectedOnly_ReturnsFalse()
        {
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(null));
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(Array.Empty<LlmToolCallTrace>()));
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(new[]
            {
                RejectedUnknownToolTrace(),
                RejectedDuplicateTrace()
            }));
            Assert.IsTrue(LoggingLlmClientDecorator.HasInvokedToolCalls(new[]
            {
                RejectedDuplicateTrace(),
                InvokedNativeTrace()
            }));
        }

        private sealed class ScriptedStreamingLlm : ILlmClient
        {
            private readonly LlmStreamChunk[] _chunks;
            public int StreamCallCount;

            public ScriptedStreamingLlm(params LlmStreamChunk[] chunks)
            {
                _chunks = chunks;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = false, Error = "streaming only" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCallCount++;
                foreach (LlmStreamChunk chunk in _chunks)
                {
                    await Task.Yield();
                    yield return chunk;
                }
            }
        }

        private sealed class FixedResultClient : ILlmClient
        {
            public LlmCompletionResult Result;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }
        }

        [Test]
        public async Task DefaultStreamAdapter_TerminalChunk_KeepsCumulativePromptTokensAndCarriesLastRoundtrip()
        {
            ILlmClient client = new FixedResultClient
            {
                Result = new LlmCompletionResult
                {
                    Ok = true,
                    Content = "hi",
                    PromptTokens = 100,
                    LastRoundtripPromptTokens = 25,
                    CompletionTokens = 40,
                    TotalTokens = 140
                }
            };

            LlmStreamChunk terminal = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                if (chunk.IsDone)
                {
                    terminal = chunk;
                }
            }

            Assert.IsNotNull(terminal);
            Assert.AreEqual(100, terminal.PromptTokens, "PromptTokens must stay the cumulative turn sum");
            Assert.AreEqual(25, terminal.LastRoundtripPromptTokens,
                "calibration field must carry the last roundtrip's prompt size");
            Assert.AreEqual(terminal.PromptTokens + terminal.CompletionTokens, terminal.TotalTokens);
        }
    }
}

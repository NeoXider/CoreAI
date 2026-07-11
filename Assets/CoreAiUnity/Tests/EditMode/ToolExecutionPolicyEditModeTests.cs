#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class ToolExecutionPolicyEditModeTests
    {
        // ==================== Helpers ====================

        private sealed class StubLogger : ILog
        {
            public readonly List<string> Logs = new();

            public void Debug(string message, string tag = null)
            {
                Logs.Add($"[DBG] {message}");
            }

            public void Info(string message, string tag = null)
            {
                Logs.Add($"[INFO] {message}");
            }

            public void Warn(string message, string tag = null)
            {
                Logs.Add($"[WARN] {message}");
            }

            public void Error(string message, string tag = null)
            {
                Logs.Add($"[ERR] {message}");
            }
        }

        private sealed class StubSettings : ICoreAISettings
        {
            private ILlmAsyncMarshaler _toolMarshalerOverride;

            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30;
            public int MaxLlmRequestRetries => 3;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 4096;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.7f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls { get; set; }
            public bool LogToolCallArguments { get; set; }
            public bool LogToolCallResults { get; set; }
            public bool LogMeaiToolCallingSteps => true;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls { get; set; } = 4;
            public int MaxToolResultChars { get; set; } = 8000;

            public ILlmAsyncMarshaler ToolInvocationMarshaler =>
                _toolMarshalerOverride ?? PassThroughLlmAsyncMarshaler.Instance;

            public StubSettings WithToolMarshaler(ILlmAsyncMarshaler marshaler)
            {
                _toolMarshalerOverride = marshaler;
                return this;
            }
        }

        private sealed class CountingMarshaler : ILlmAsyncMarshaler
        {
            public int InvokeCount;

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                InvokeCount++;
                return factory();
            }
        }

        private sealed class StubTool : ILlmTool
        {
            public string Name { get; set; } = "test_tool";
            public string Description => "Test tool";
            public string ParametersSchema { get; set; } = "{}";
            public bool AllowDuplicates { get; set; } = false;
        }

        private static MEAI.FunctionCallContent MakeToolCall(string name, Dictionary<string, object?> args = null)
        {
            return new MEAI.FunctionCallContent(
                $"call_{name}_{Guid.NewGuid():N}",
                name,
                args ?? new Dictionary<string, object?> { { "key", "value" } });
        }

        private static MEAI.ChatOptions MakeChatOptions(params (string name, string result)[] tools)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            foreach ((string name, string result) in tools)
            {
                opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => result),
                    new MEAI.AIFunctionFactoryOptions { Name = name, Description = $"Tool {name}" }));
            }

            return opts;
        }

        // ==================== Duplicate Detection ====================

        [Test]
        public void CheckDuplicate_FirstCall_ReturnsNull()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                false, "test", 3);

            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("greet") };
            List<MEAI.FunctionResultContent> result = policy.CheckDuplicate(calls);
            Assert.IsNull(result, "First call should not be blocked");
        }

        [Test]
        public void CheckDuplicate_SameSignatureTwice_BlocksSecond()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "who", "world" } };
            List<MEAI.FunctionCallContent> calls1 = new() { MakeToolCall("greet", args) };
            List<MEAI.FunctionCallContent> calls2 = new() { MakeToolCall("greet", args) };

            Assert.IsNull(policy.CheckDuplicate(calls1));
            List<MEAI.FunctionResultContent> blocked = policy.CheckDuplicate(calls2);
            Assert.IsNotNull(blocked, "Second identical call should be blocked");
            Assert.AreEqual(1, blocked.Count);
        }

        [Test]
        public void CheckDuplicate_SameArgsDifferentKeyOrder_BlocksSecond()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                false, "test", 3);

            // Same argument values, different key insertion order (e.g. streamed vs text-extracted
            // reconstructions). The duplicate guard canonicalizes keys, so the second call is a duplicate.
            Dictionary<string, object?> argsA = new() { { "a", "1" }, { "b", "2" } };
            Dictionary<string, object?> argsB = new() { { "b", "2" }, { "a", "1" } };
            List<MEAI.FunctionCallContent> calls1 = new() { MakeToolCall("greet", argsA) };
            List<MEAI.FunctionCallContent> calls2 = new() { MakeToolCall("greet", argsB) };

            Assert.IsNull(policy.CheckDuplicate(calls1));
            Assert.IsNotNull(policy.CheckDuplicate(calls2),
                "Reordered-key arguments are semantically identical and must be detected as a duplicate");
        }

        [Test]
        public void CheckDuplicate_SameSignatureTwice_RecordsDuplicateTrace()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "who", "world" } };
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("greet", args) };

            Assert.IsNull(policy.CheckDuplicate(calls));
            Assert.AreEqual(0, policy.ExecutedTraces.Count);

            List<MEAI.FunctionResultContent> blocked = policy.CheckDuplicate(calls);
            Assert.IsNotNull(blocked, "Second identical call should be blocked");
            Assert.AreEqual(1, blocked.Count);
            Assert.AreEqual(1, policy.ExecutedTraces.Count, "Duplicate must create synthetic trace");

            LlmToolCallTrace trace = policy.ExecutedTraces[0];
            Assert.AreEqual("greet", trace.Name);
            Assert.IsFalse(trace.Success);
            Assert.AreEqual("duplicate", trace.Source);
        }

        [Test]
        public void CheckDuplicate_DifferentArgs_Allowed()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                false, "test", 3);

            List<MEAI.FunctionCallContent> calls1 = new()
                { MakeToolCall("greet", new Dictionary<string, object?> { { "who", "A" } }) };
            List<MEAI.FunctionCallContent> calls2 = new()
                { MakeToolCall("greet", new Dictionary<string, object?> { { "who", "B" } }) };

            Assert.IsNull(policy.CheckDuplicate(calls1));
            Assert.IsNull(policy.CheckDuplicate(calls2), "Different args should be allowed");
        }

        [Test]
        public void CheckDuplicate_AllowDuplicatesGlobal_NeverBlocks()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                true, "test", 3);

            Dictionary<string, object> args = new() { { "x", 1 } };
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("greet", args) };

            Assert.IsNull(policy.CheckDuplicate(calls));
            Assert.IsNull(policy.CheckDuplicate(calls), "Global AllowDuplicateToolCalls=true should never block");
        }

        [Test]
        public void CheckDuplicate_PerToolAllowDuplicates_Respected()
        {
            StubTool tool = new() { Name = "repeat_action", AllowDuplicates = true };
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { tool },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "action", "jump" } };
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("repeat_action", args) };

            Assert.IsNull(policy.CheckDuplicate(calls));
            Assert.IsNull(policy.CheckDuplicate(calls), "Per-tool AllowDuplicates should be respected");
        }

        // ==================== Error Counter ====================

        [Test]
        public void RecordSuccess_ResetsCounter()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            policy.RecordFailure();
            policy.RecordFailure();
            Assert.AreEqual(2, policy.ConsecutiveErrors);

            policy.RecordSuccess();
            Assert.AreEqual(0, policy.ConsecutiveErrors);
        }

        [Test]
        public void RecordFailure_IncrementsCounter()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            policy.RecordFailure();
            Assert.AreEqual(1, policy.ConsecutiveErrors);
            policy.RecordFailure();
            Assert.AreEqual(2, policy.ConsecutiveErrors);
        }

        [Test]
        public void IsMaxErrorsReached_AtThreshold_ReturnsTrue()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 2);

            Assert.IsFalse(policy.IsMaxErrorsReached);
            policy.RecordFailure();
            Assert.IsFalse(policy.IsMaxErrorsReached);
            policy.RecordFailure();
            Assert.IsTrue(policy.IsMaxErrorsReached);
        }

        [Test]
        public void Reset_ClearsEverything()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "t" } },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "a", 1 } };
            policy.CheckDuplicate(new List<MEAI.FunctionCallContent> { MakeToolCall("t", args) });
            policy.RecordFailure();
            policy.RecordFailure();

            policy.Reset();

            Assert.AreEqual(0, policy.ConsecutiveErrors);
            // Same signature should be allowed again after reset
            Assert.IsNull(policy.CheckDuplicate(new List<MEAI.FunctionCallContent> { MakeToolCall("t", args) }));
        }

        // ==================== ExecuteSingleAsync ====================

        [Test]
        public async Task ExecuteSingle_ToolFound_ReturnsResult()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("hello", "world"));
            MEAI.FunctionCallContent fc = MakeToolCall("hello");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(result.Result);
            Assert.AreEqual("world", result.Result.Result.ToString());
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("world", policy.ExecutedTraces[0].Detail);
        }

        [Test]
        public async Task ExecuteSingle_NullToolResult_ReturnsExplicitPayloadWithCallId()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => null),
                new MEAI.AIFunctionFactoryOptions { Name = "empty_tool", Description = "Empty tool" }));
            MEAI.FunctionCallContent fc = MakeToolCall("empty_tool");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(fc.CallId, result.Result.CallId);
            string text = result.Result.Result.ToString();
            StringAssert.Contains("\"Success\":true", text);
            StringAssert.Contains("Tool completed without an explicit result payload", text);
        }

        [Test]
        public async Task ExecuteSingle_AsyncTool_WaitsForCompletion()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);
            bool completed = false;
            Func<CancellationToken, Task<string>> func = async ct =>
            {
                await Task.Delay(75, ct);
                completed = true;
                return "async-ok";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(func,
                new MEAI.AIFunctionFactoryOptions { Name = "async_tool", Description = "Async tool" }));
            MEAI.FunctionCallContent fc = MakeToolCall("async_tool");

            Stopwatch sw = Stopwatch.StartNew();
            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            sw.Stop();

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(completed);
            Assert.GreaterOrEqual(sw.ElapsedMilliseconds, 50);
            Assert.AreEqual("async-ok", result.Result.Result.ToString());
        }

        [Test]
        public async Task ExecuteSingle_ToolNotFound_ReturnsFailed()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions();
            MEAI.FunctionCallContent fc = MakeToolCall("nonexistent");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Result.Result.ToString().Contains("not found"));
        }

        [Test]
        public async Task ExecuteSingle_UnknownTool_RecordsUnknownToolTrace()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("memory", "ok"));
            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("unknown_tool"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count, "Unknown tool should create exactly one trace");

            LlmToolCallTrace trace = policy.ExecutedTraces[0];
            Assert.AreEqual("unknown_tool", trace.Name);
            Assert.IsFalse(trace.Success);
            Assert.AreEqual("unknown-tool", trace.Source);
        }

        [Test]
        public async Task ExecuteSingle_KnownToolButMissingBinding_RecordsMissingTrace()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions();
            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("memory"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count, "Missing binding should create exactly one trace");

            LlmToolCallTrace trace = policy.ExecutedTraces[0];
            Assert.AreEqual("memory", trace.Name);
            Assert.IsFalse(trace.Success);
            Assert.AreEqual("missing", trace.Source);
        }

        [Test]
        public async Task ExecuteSingle_MissingRequiredArgument_ReturnsSchemaRepairError()
        {
            const string schema =
                "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"One of: list, load\"}},\"required\":[\"action\"]}";

            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>
                {
                    new StubTool
                    {
                        Name = "manage_mods",
                        ParametersSchema = schema
                    }
                },
                false, "test", 3);

            Func<string, string> func = action => $"action={action}";
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(func,
                new MEAI.AIFunctionFactoryOptions { Name = "manage_mods", Description = "Manage mods" }));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                MakeToolCall("manage_mods", new Dictionary<string, object?>()),
                opts,
                CancellationToken.None);

            string text = result.Result.Result.ToString();
            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("missing required argument(s): action", text);
            StringAssert.Contains("Retry the same tool call with JSON arguments matching this schema", text);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("schema-validation", policy.ExecutedTraces[0].Source);
        }

        [Test]
        public async Task ExecuteSingle_TypeConversionError_AppendsSchemaRetryHint()
        {
            // F7: an argument/JSON conversion failure must carry the same compact schema + retry
            // suffix as the missing-required-argument path, so the model can fix its arguments.
            const string schema =
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}},\"required\":[\"count\"]}";
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "typed_tool", ParametersSchema = schema } },
                false, "test", 3);

            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                (Func<string>)(() => throw new FormatException("Input string was not in a correct format.")),
                new MEAI.AIFunctionFactoryOptions { Name = "typed_tool", Description = "Typed tool" }));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                MakeToolCall("typed_tool", new Dictionary<string, object?> { { "count", "not-a-number" } }),
                opts,
                CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            string text = result.Result.Result.ToString();
            StringAssert.Contains("matching this schema", text);
            StringAssert.Contains("\"count\"", text, "The compact schema itself must be embedded");
        }

        [Test]
        public async Task ExecuteSingle_TypeConversionError_NoSchemaRegistered_NoHintAppended()
        {
            // Conversion failure for a tool with no meaningful schema: nothing useful to append.
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "typed_tool", ParametersSchema = "{}" } },
                false, "test", 3);

            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                (Func<string>)(() => throw new InvalidCastException("cannot convert value")),
                new MEAI.AIFunctionFactoryOptions { Name = "typed_tool", Description = "Typed tool" }));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                MakeToolCall("typed_tool"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            StringAssert.DoesNotContain("matching this schema", result.Result.Result.ToString());
        }

        [Test]
        public async Task ExecuteSingle_NonConversionError_DoesNotAppendSchemaHint()
        {
            // A genuine tool-body failure must keep its plain error - the schema hint is only for
            // argument-shape problems the model can actually fix by re-emitting arguments.
            const string schema =
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}},\"required\":[\"count\"]}";
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "typed_tool", ParametersSchema = schema } },
                false, "test", 3);

            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                (Func<string>)(() => throw new InvalidOperationException("world is not loaded")),
                new MEAI.AIFunctionFactoryOptions { Name = "typed_tool", Description = "Typed tool" }));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                MakeToolCall("typed_tool", new Dictionary<string, object?> { { "count", 1 } }),
                opts,
                CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            StringAssert.DoesNotContain("matching this schema", result.Result.Result.ToString());
        }

        [Test]
        public void LooksLikeArgumentConversionError_ClassifiesExceptionChain()
        {
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                new FormatException("bad number")));
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                new InvalidCastException("cast failed")));
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                new ArgumentException("wrong arg")));
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                new Newtonsoft.Json.JsonReaderException("unexpected token")));
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                    new Exception("Unable to CONVERT value to Int32")),
                "Message-based detection must be case-insensitive");
            Assert.IsTrue(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                    new InvalidOperationException("wrapper", new FormatException("inner"))),
                "Inner exceptions must be inspected (MEAI wraps the real conversion failure)");
            Assert.IsFalse(ToolExecutionPolicy.LooksLikeArgumentConversionError(
                new InvalidOperationException("world is not loaded")));
        }

        [Test]
        public async Task ExecuteSingle_UsesToolInvocationMarshaler_WhenProvided()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings().WithToolMarshaler(countingMarshaler);

            ToolExecutionPolicy policy =
                new(new StubLogger(), settings, new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("hello", "world"));
            MEAI.FunctionCallContent fc = MakeToolCall("hello");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(1, countingMarshaler.InvokeCount);
        }

        // ==================== Parse-error guard ====================

        [Test]
        public async Task ExecuteSingle_ParseErrorMarker_ShortCircuitsWithoutInvoking()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings().WithToolMarshaler(countingMarshaler);

            ToolExecutionPolicy policy =
                new(new StubLogger(), settings, new List<ILlmTool>(), false, "test", 3);

            // Tool has no required params, so required-arg validation would otherwise pass.
            MEAI.ChatOptions opts = MakeChatOptions(("noargs", "should-not-run"));
            MEAI.FunctionCallContent fc = MakeToolCall("noargs", new Dictionary<string, object?>
            {
                { ToolCallArgumentMarkers.RawArgumentsKey, "{\"x\": 1" },
                { ToolCallArgumentMarkers.ParseErrorKey, true }
            });

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded, "Parse-error marker must not execute the tool");
            Assert.AreEqual(0, countingMarshaler.InvokeCount, "Real tool must never be invoked");
            string text = result.Result.Result.ToString();
            StringAssert.Contains("truncated or malformed", text);
            StringAssert.Contains("Retry the same tool call", text);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("parse-error", policy.ExecutedTraces[0].Source);
        }

        [Test]
        public async Task ExecuteSingle_ValidArgsWithoutMarker_StillExecutes()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings().WithToolMarshaler(countingMarshaler);

            ToolExecutionPolicy policy =
                new(new StubLogger(), settings, new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("noargs", "ran-ok"));
            MEAI.FunctionCallContent fc = MakeToolCall("noargs", new Dictionary<string, object?>
            {
                { "x", 1 }
            });

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded, "Normal args should execute the tool");
            Assert.AreEqual(1, countingMarshaler.InvokeCount);
            Assert.AreEqual("ran-ok", result.Result.Result.ToString());
        }

        // ==================== ExecuteBatchAsync ====================

        [Test]
        public async Task ExecuteBatch_AllSucceed_ResetsErrorCounter()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), true, "test", 3);

            policy.RecordFailure(); // Pre-existing failure
            Assert.AreEqual(1, policy.ConsecutiveErrors);

            MEAI.ChatOptions opts = MakeChatOptions(("tool_a", "ok"));
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("tool_a") };

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual("ok", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
            Assert.AreEqual(0, policy.ConsecutiveErrors, "Success should reset error counter");
        }

        [Test]
        public async Task ExecuteBatch_DuplicateBlocked_ReturnsFailed()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("dup", args) };

            // First call succeeds
            ToolExecutionPolicy.BatchToolCallResult batch1 =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            Assert.IsFalse(batch1.AnyFailed);

            // Second identical call is blocked by duplicate detection
            ToolExecutionPolicy.BatchToolCallResult batch2 = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) },
                opts, CancellationToken.None);
            Assert.IsTrue(batch2.AnyFailed, "Duplicate should be blocked");
        }

        [Test]
        public async Task ExecuteBatch_AllDuplicatesInBatch_CountsAsConsecutiveFailure()
        {
            // Regression: a batch where every call is a duplicate (no executable call) must still
            // increment the consecutive-error counter, same as any other failed iteration. Otherwise a
            // model stuck repeating the same call forever never trips the max-consecutive-errors guard.
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) }, opts, CancellationToken.None);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "First (non-duplicate) call must not count as a failure");

            Assert.IsFalse(policy.IsMaxErrorsReached);
            await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) }, opts, CancellationToken.None);
            Assert.AreEqual(1, policy.ConsecutiveErrors, "All-duplicate batch must increment the error counter");

            await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) }, opts, CancellationToken.None);
            Assert.AreEqual(2, policy.ConsecutiveErrors);
            Assert.IsFalse(policy.IsMaxErrorsReached);

            await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) }, opts, CancellationToken.None);
            Assert.AreEqual(3, policy.ConsecutiveErrors);
            Assert.IsTrue(policy.IsMaxErrorsReached,
                "Repeated all-duplicate batches must eventually trip the max-consecutive-errors guard");
        }

        [Test]
        public async Task ExecuteBatch_IntraBatchIdenticalCalls_AllExecute()
        {
            // "Spawn tree x3" in ONE turn is a legitimate request: intra-batch repeats must all
            // execute (Claude/Cursor parity). Only the CROSS-turn whole-batch echo is suppressed.
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings
            {
                MaxParallelToolCalls = 1
            }.WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.FunctionCallContent first = MakeToolCall("dup", args);
            MEAI.FunctionCallContent second = MakeToolCall("dup", args);
            MEAI.FunctionCallContent third = MakeToolCall("dup", args);
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.BatchToolCallResult batch = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { first, second, third },
                opts,
                CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed, "Identical calls within ONE batch are all legitimate");
            Assert.AreEqual(3, countingMarshaler.InvokeCount, "Every identical intra-batch call must execute");
            Assert.AreEqual(first.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual(second.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
            Assert.AreEqual(third.CallId, ((MEAI.FunctionResultContent)batch.Results[2]).CallId);
            foreach (MEAI.AIContent content in batch.Results)
            {
                Assert.AreEqual("ok", ((MEAI.FunctionResultContent)content).Result.ToString());
            }
        }

        [Test]
        public async Task ExecuteBatch_IntraBatchIdenticalCalls_CrossTurnEchoStillBlocked()
        {
            // The cross-turn whole-batch echo guard must survive the intra-batch relaxation: a model
            // re-sending the exact same (repeated) batch NEXT turn is still suppressed as an echo.
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings
            {
                MaxParallelToolCalls = 1
            }.WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.BatchToolCallResult firstTurn = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args), MakeToolCall("dup", args) },
                opts, CancellationToken.None);
            Assert.IsFalse(firstTurn.AnyFailed);
            Assert.AreEqual(2, countingMarshaler.InvokeCount);

            ToolExecutionPolicy.BatchToolCallResult echoTurn = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args), MakeToolCall("dup", args) },
                opts, CancellationToken.None);
            Assert.IsTrue(echoTurn.AnyFailed, "Identical batch re-sent in a later turn is an echo");
            Assert.IsTrue(echoTurn.AllFailed, "Every slot of a cross-turn echo batch is suppressed");
            Assert.AreEqual(2, countingMarshaler.InvokeCount, "The echo turn must not invoke the tool again");
            StringAssert.Contains("Duplicate tool call",
                ((MEAI.FunctionResultContent)echoTurn.Results[0]).Result.ToString());
        }

        // ==================== Streamed turn (execute-as-you-stream) ====================

        [Test]
        public async Task StreamedTurn_ExecutesCallsOneByOne_TurnResultMatchesBatchShape()
        {
            // MaxParallelToolCalls pinned to 1: this test exercises the sequential streamed path
            // (the pre-parallel semantics), where every call executes inline as it arrives.
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool> { new StubTool { Name = "tool_a" } },
                false, "test", 3);
            policy.RecordFailure(); // pre-existing failure must be reset by a clean turn

            MEAI.ChatOptions opts = MakeChatOptions(("tool_a", "ok"));
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();

            MEAI.FunctionCallContent first = MakeToolCall("tool_a", new Dictionary<string, object?> { { "x", 1 } });
            MEAI.FunctionCallContent second = MakeToolCall("tool_a", new Dictionary<string, object?> { { "x", 2 } });
            ToolExecutionPolicy.ToolCallResult? r1 =
                await policy.ExecuteStreamedAsync(turn, first, opts, CancellationToken.None);
            Assert.IsTrue(r1.HasValue, "Sequential mode must return the executed result inline.");
            Assert.IsTrue(r1.Value.Succeeded, "First call must execute immediately.");
            Assert.AreEqual(1, policy.ConsecutiveErrors,
                "Per-call streamed execution must NOT touch the consecutive-error counter mid-turn.");

            await policy.ExecuteStreamedAsync(turn, second, opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult batch = policy.CompleteStreamedTurn(turn);

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, batch.Results.Count);
            Assert.AreEqual(first.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual(second.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "A clean streamed turn records ONE success, like a batch.");
        }

        [Test]
        public async Task StreamedTurn_IntraTurnIdenticalCalls_AllExecute()
        {
            // Batch parity with ExecuteBatch_IntraBatchIdenticalCalls_AllExecute: "spawn tree x3"
            // streamed call-by-call must execute every repeat; only cross-turn echoes are suppressed.
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings { MaxParallelToolCalls = 1 }
                .WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();

            ToolExecutionPolicy.ToolCallResult? r1 =
                await policy.ExecuteStreamedAsync(turn, MakeToolCall("dup", args), opts, CancellationToken.None);
            ToolExecutionPolicy.ToolCallResult? r2 =
                await policy.ExecuteStreamedAsync(turn, MakeToolCall("dup", args), opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult batch = policy.CompleteStreamedTurn(turn);

            Assert.IsTrue(r1.HasValue && r1.Value.Succeeded);
            Assert.IsTrue(r2.HasValue && r2.Value.Succeeded,
                "An exact repeat WITHIN the same turn is a legitimate request and must execute.");
            Assert.AreEqual(2, countingMarshaler.InvokeCount, "Both identical intra-turn calls must run.");
            Assert.IsFalse(batch.AnyFailed);
            Assert.IsFalse(policy.ExecutedTraces.Any(t => t.Source == "duplicate"),
                "No duplicate trace may be recorded for intra-turn repeats.");
        }

        [Test]
        public async Task StreamedTurn_AllFailed_RecordsOneConsecutiveError()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool>(), true, "test", 3);
            MEAI.ChatOptions opts = MakeChatOptions(("known", "ok"));

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(turn, MakeToolCall("unknown_a"), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(turn, MakeToolCall("unknown_b"), opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult batch = policy.CompleteStreamedTurn(turn);

            Assert.IsTrue(batch.AnyFailed);
            Assert.IsTrue(batch.AllFailed);
            Assert.AreEqual(1, policy.ConsecutiveErrors,
                "A fully failed streamed turn records exactly ONE failure, like a failed batch.");
        }

        [Test]
        public async Task StreamedTurn_EchoOfWholeTurn_LaterIdenticalBatchIsBlocked()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);
            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(turn, MakeToolCall("dup", args), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(turn);

            // The exact same batch sent again through the CLASSIC path must be caught as an echo —
            // CompleteStreamedTurn registered the turn's combined signature.
            ToolExecutionPolicy.BatchToolCallResult echo = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) }, opts, CancellationToken.None);
            Assert.IsTrue(echo.AnyFailed, "A later identical batch must be blocked as an echo of the streamed turn.");
        }

        [Test]
        public async Task StreamedTurn_CrossTurnEchoOfSingleCall_SuppressedBeforeExecuting()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings { MaxParallelToolCalls = 1 }
                .WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);
            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", args), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(first);

            // The model re-issues the identical single call in the NEXT streamed turn (an echo).
            // Batch parity: a one-call batch registers the call's own signature, so the classic
            // path would suppress this — the streamed path must too, WITHOUT invoking the tool.
            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? echo = await policy.ExecuteStreamedAsync(
                second, MakeToolCall("dup", args), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(second);

            Assert.IsTrue(echo.HasValue, "Suppressed duplicates return their result inline in every mode.");
            Assert.IsFalse(echo.Value.Succeeded, "Cross-turn echo of an identical single call must be suppressed.");
            StringAssert.Contains("Duplicate tool call", echo.Value.Result.Result.ToString());
            Assert.AreEqual(1, countingMarshaler.InvokeCount, "The echo must not invoke the tool again.");
        }

        [Test]
        public async Task StreamedTurn_MultiCallEchoTurn_SecondCompleteRecordsFailure()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings { MaxParallelToolCalls = 1 }
                .WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);
            Dictionary<string, object?> argsA = new() { { "x", 1 } };
            Dictionary<string, object?> argsB = new() { { "x", 2 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsA), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsB), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(first);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "A clean first multi-call turn records a success.");

            // The model echoes the exact same TWO-call turn. The per-call cross-turn guard cannot
            // catch this (only the COMBINED signature was registered, and it does not equal either
            // per-call signature), so the calls re-execute — but CompleteStreamedTurn must detect
            // the whole-turn echo and record ONE failure, like the all-duplicate batch branch.
            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(second, MakeToolCall("dup", argsA), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(second, MakeToolCall("dup", argsB), opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult echo = policy.CompleteStreamedTurn(second);

            Assert.AreEqual(1, policy.ConsecutiveErrors,
                "A whole-turn echo must record a failure, not a success, or the model can loop forever.");
            Assert.IsTrue(echo.AnyFailed, "The echo turn must report AnyFailed, like the batch echo branch.");
            Assert.IsTrue(echo.AllFailed, "The echo turn must report AllFailed, like the batch echo branch.");
            // Guard restoration only: the echoed calls still executed (their results already streamed
            // back on the wire); the fix is the error accounting, not per-call suppression.
            Assert.AreEqual(4, countingMarshaler.InvokeCount,
                "Multi-call echo calls still execute; only the turn-level accounting flags the echo.");
        }

        [Test]
        public async Task StreamedTurn_RepeatedMultiCallEchoTurns_TripMaxConsecutiveErrors()
        {
            const int maxConsecutiveErrors = 3;
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", maxConsecutiveErrors);
            Dictionary<string, object?> argsA = new() { { "x", 1 } };
            Dictionary<string, object?> argsB = new() { { "x", 2 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            // First (non-echo) cycle executes cleanly.
            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsA), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsB), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(first);
            Assert.AreEqual(0, policy.ConsecutiveErrors);

            // A model stuck echoing the same multi-call batch: each echoed turn must increment the
            // consecutive-error counter (its calls succeed, so without the whole-turn echo branch
            // RecordSuccess would reset the counter every cycle and the guard would never trip).
            for (int i = 1; i <= maxConsecutiveErrors; i++)
            {
                ToolExecutionPolicy.StreamedTurn echo = policy.BeginStreamedTurn();
                await policy.ExecuteStreamedAsync(echo, MakeToolCall("dup", argsA), opts, CancellationToken.None);
                await policy.ExecuteStreamedAsync(echo, MakeToolCall("dup", argsB), opts, CancellationToken.None);
                policy.CompleteStreamedTurn(echo);

                Assert.AreEqual(i, policy.ConsecutiveErrors, $"Echo cycle {i} must increment the error counter.");
                Assert.AreEqual(i == maxConsecutiveErrors, policy.IsMaxErrorsReached,
                    "The guard must trip exactly when the echo cycles reach maxConsecutiveErrors.");
            }
        }

        [Test]
        public async Task StreamedTurn_NonEchoSecondTurn_StillRecordsSuccess()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(
                first, MakeToolCall("dup", new Dictionary<string, object?> { { "x", 1 } }), opts,
                CancellationToken.None);
            await policy.ExecuteStreamedAsync(
                first, MakeToolCall("dup", new Dictionary<string, object?> { { "x", 2 } }), opts,
                CancellationToken.None);
            policy.CompleteStreamedTurn(first);
            Assert.AreEqual(0, policy.ConsecutiveErrors);

            // One argument differs, so the combined turn signature is new: this is progress, not an
            // echo, and the turn must record a success exactly as before the whole-turn echo guard.
            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(
                second, MakeToolCall("dup", new Dictionary<string, object?> { { "x", 1 } }), opts,
                CancellationToken.None);
            await policy.ExecuteStreamedAsync(
                second, MakeToolCall("dup", new Dictionary<string, object?> { { "x", 3 } }), opts,
                CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult batch = policy.CompleteStreamedTurn(second);

            Assert.IsFalse(batch.AnyFailed, "A non-echo turn with one changed argument must not be flagged.");
            Assert.AreEqual(0, policy.ConsecutiveErrors,
                "A successful non-echo turn must keep the consecutive-error counter at zero.");
        }

        [Test]
        public async Task StreamedTurn_AllowDuplicatesTool_RepeatsExecute()
        {
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings { MaxParallelToolCalls = 1 }
                .WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "repeat_action", AllowDuplicates = true } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("repeat_action", "ok"));
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();

            ToolExecutionPolicy.ToolCallResult? r1 = await policy.ExecuteStreamedAsync(
                turn, MakeToolCall("repeat_action", args), opts, CancellationToken.None);
            ToolExecutionPolicy.ToolCallResult? r2 = await policy.ExecuteStreamedAsync(
                turn, MakeToolCall("repeat_action", args), opts, CancellationToken.None);
            policy.CompleteStreamedTurn(turn);

            Assert.IsTrue(r1.HasValue && r1.Value.Succeeded);
            Assert.IsTrue(r2.HasValue && r2.Value.Succeeded,
                "AllowDuplicates tools may repeat exactly, streamed or batched.");
            Assert.AreEqual(2, countingMarshaler.InvokeCount);
        }

        [Test]
        public async Task ExecuteBatch_CrossTurnEcho_UsesRepairedCanonicalName()
        {
            // Casing variants of the same canonical tool name must collide in the CROSS-turn echo
            // guard: a turn re-sending "memory" after last turn's "MEMORY" (same args) is an echo.
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings
            {
                MaxParallelToolCalls = 1
            }.WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "slot", "same" } };
            MEAI.FunctionCallContent first = MakeToolCall("MEMORY", args);
            MEAI.FunctionCallContent second = MakeToolCall("memory", args);
            MEAI.ChatOptions opts = MakeChatOptions(("memory", "saved"));

            ToolExecutionPolicy.BatchToolCallResult firstTurn = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { first }, opts, CancellationToken.None);
            Assert.IsFalse(firstTurn.AnyFailed);
            Assert.AreEqual(1, countingMarshaler.InvokeCount);
            Assert.AreEqual("saved", ((MEAI.FunctionResultContent)firstTurn.Results[0]).Result.ToString());

            ToolExecutionPolicy.BatchToolCallResult echoTurn = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { second }, opts, CancellationToken.None);

            Assert.AreEqual(1, countingMarshaler.InvokeCount,
                "Casing variants of the same canonical tool name should collide in the echo guard");
            Assert.IsTrue(echoTurn.AnyFailed);
            Assert.AreEqual(second.CallId, ((MEAI.FunctionResultContent)echoTurn.Results[0]).CallId);
            StringAssert.Contains("Duplicate tool call",
                ((MEAI.FunctionResultContent)echoTurn.Results[0]).Result.ToString());
        }

        [Test]
        public async Task ExecuteBatch_RepeatedMixedBatch_StillExecutesAllowDuplicatesTool()
        {
            int repeatCount = 0;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => "fixed-ok"),
                new MEAI.AIFunctionFactoryOptions { Name = "fixed", Description = "Fixed tool" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => $"repeat-{++repeatCount}"),
                new MEAI.AIFunctionFactoryOptions { Name = "repeat", Description = "Repeat tool" }));

            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool>
                {
                    new StubTool { Name = "fixed" },
                    new StubTool { Name = "repeat", AllowDuplicates = true }
                },
                false, "test", 3);

            Dictionary<string, object?> fixedArgs = new() { { "x", 1 } };
            Dictionary<string, object?> repeatArgs = new() { { "x", 1 } };
            ToolExecutionPolicy.BatchToolCallResult firstBatch = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent>
                {
                    MakeToolCall("fixed", fixedArgs),
                    MakeToolCall("repeat", repeatArgs)
                },
                opts,
                CancellationToken.None);
            Assert.IsFalse(firstBatch.AnyFailed);

            MEAI.FunctionCallContent fixedAgain = MakeToolCall("fixed", fixedArgs);
            MEAI.FunctionCallContent repeatAgain = MakeToolCall("repeat", repeatArgs);
            ToolExecutionPolicy.BatchToolCallResult secondBatch = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { fixedAgain, repeatAgain },
                opts,
                CancellationToken.None);

            Assert.IsTrue(secondBatch.AnyFailed);
            Assert.IsFalse(secondBatch.AllFailed, "AllowDuplicates call must still run in a repeated mixed batch");
            Assert.AreEqual(fixedAgain.CallId, ((MEAI.FunctionResultContent)secondBatch.Results[0]).CallId);
            StringAssert.Contains("Duplicate tool call",
                ((MEAI.FunctionResultContent)secondBatch.Results[0]).Result.ToString());
            Assert.AreEqual(repeatAgain.CallId, ((MEAI.FunctionResultContent)secondBatch.Results[1]).CallId);
            Assert.AreEqual("repeat-2", ((MEAI.FunctionResultContent)secondBatch.Results[1]).Result.ToString());
        }

        [Test]
        public async Task ExecuteBatch_ToolFailure_DebugLog_RecordsFailStatusAndResultDetail()
        {
            StubLogger logger = new();
            StubSettings settings = new() { LogToolCalls = true, LogToolCallResults = true };
            ToolExecutionPolicy policy = new(logger, settings,
                new List<ILlmTool> { new StubTool { Name = "manage_mods" } },
                false, "Programmer", 3);

            // Tool returns a structured failure with a real reason.
            MEAI.ChatOptions opts = MakeChatOptions(("manage_mods",
                "{\"success\":false,\"message\":\"attempt to index a function value\"}"));
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("manage_mods") };

            await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);

            Assert.IsTrue(
                logger.Logs.Any(l =>
                    l.Contains("[ToolCall]") && l.Contains("tool=manage_mods") &&
                    l.Contains("status=FAIL") && l.Contains("attempt to index a function value")),
                "Tool-call debug log must record the tool name, FAIL status, and the result detail.\n" +
                string.Join("\n", logger.Logs));
        }

        [Test]
        public async Task ExecuteBatch_ToolSuccess_DebugLog_RecordsOkStatus()
        {
            StubLogger logger = new();
            StubSettings settings = new() { LogToolCalls = true };
            ToolExecutionPolicy policy = new(logger, settings,
                new List<ILlmTool> { new StubTool { Name = "manage_mods" } },
                false, "Programmer", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("manage_mods", "{\"success\":true}"));
            List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("manage_mods") };

            await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);

            Assert.IsTrue(
                logger.Logs.Any(l =>
                    l.Contains("[ToolCall]") && l.Contains("tool=manage_mods") && l.Contains("status=OK")),
                "Tool-call debug log must record an OK status for a successful tool call.\n" +
                string.Join("\n", logger.Logs));
        }

        // ==================== BuildMaxErrorsResponse ====================

        [Test]
        public void BuildMaxErrorsResponse_ContainsErrorText()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatResponse response = policy.BuildMaxErrorsResponse();
            Assert.IsNotNull(response);
            Assert.IsTrue(response.Text.Contains("error"), "Should contain error description");
        }

        // ==================== TryRepairToolName ====================

        [Test]
        public void TryRepairToolName_ExactMatch_ReturnsSameFc()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.FunctionCallContent fc = MakeToolCall("memory");
            MEAI.FunctionCallContent repaired = policy.TryRepairToolName(fc);

            Assert.IsNotNull(repaired);
            Assert.AreEqual("memory", repaired.Name, "Exact match should be returned as-is");
            Assert.AreSame(fc, repaired, "Should return the same instance for exact match");
        }

        [Test]
        public void TryRepairToolName_WrongCase_ReturnsRepaired()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.FunctionCallContent fc = MakeToolCall("MEMORY");
            MEAI.FunctionCallContent repaired = policy.TryRepairToolName(fc);

            Assert.IsNotNull(repaired, "Should repair wrong casing");
            Assert.AreEqual("memory", repaired.Name, "Name should be corrected to registered casing");
            Assert.AreNotSame(fc, repaired, "Should return new instance with repaired name");
        }

        [Test]
        public void TryRepairToolName_MixedCase_ReturnsRepaired()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "spawn_quiz" } },
                false, "test", 3);

            MEAI.FunctionCallContent repaired = policy.TryRepairToolName(MakeToolCall("Spawn_Quiz"));
            Assert.IsNotNull(repaired);
            Assert.AreEqual("spawn_quiz", repaired.Name);
        }

        [Test]
        public void TryRepairToolName_UnknownTool_ReturnsNull()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.FunctionCallContent result = policy.TryRepairToolName(MakeToolCall("completely_unknown_tool_xyz"));
            Assert.IsNull(result, "Unknown tool should return null");
        }

        [Test]
        public void TryRepairToolName_AmbiguousCaseInsensitiveMatch_ReturnsNull()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>
                {
                    new StubTool { Name = "Tool" },
                    new StubTool { Name = "tool" }
                },
                false, "test", 3);

            MEAI.FunctionCallContent result = policy.TryRepairToolName(MakeToolCall("TOOL"));
            Assert.IsNull(result, "Ambiguous case-insensitive matches must fail closed");
        }

        [Test]
        public void TryRepairToolName_NullFc_ReturnsNull()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            Assert.IsNull(policy.TryRepairToolName(null));
        }

        [Test]
        public async Task ExecuteSingle_WrongCaseName_IsRepaired()
        {
            // Model called "MEMORY" but tool is registered as "memory"
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("memory", "Memory saved"));
            MEAI.FunctionCallContent fc = MakeToolCall("MEMORY"); // wrong casing from model

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsTrue(result.Succeeded, "Tool should succeed after name repair");
        }

        [Test]
        public async Task ExecuteSingle_TrulyUnknownTool_ReturnsFailed()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            MEAI.ChatOptions opts = MakeChatOptions(("memory", "ok"));
            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("totally_unknown"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Result.Result.ToString().Contains("Unknown tool") ||
                          result.Result.Result.ToString().Contains("not found"),
                "Error message should mention the unknown tool");
        }

        // ==================== ComputeBackoff (LoggingLlmClientDecorator) ====================

        [Test]
        public void ComputeBackoff_ZeroAttempt_WithinJitterWindow()
        {
            // ComputeBackoff is full-jitter: uniform [0, base]; base for attempt=0 is 2s.
            int val = LoggingLlmClientDecorator.ComputeBackoffDelay(0, new Random(1234));
            Assert.GreaterOrEqual(val, 0, "attempt=0: jittered delay must be >= 0");
            Assert.LessOrEqual(val, 2, "attempt=0: jittered delay must be <= base 2s");
        }

        [Test]
        public void ComputeBackoff_ExponentialCurve_CappedAt30()
        {
            // Deterministic base curve: attempt 0 → 2, 1 → 4, 2 → 8, 3 → 16, then capped at 30.
            int[] expected = { 2, 4, 8, 16, 30, 30, 30 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], LoggingLlmClientDecorator.ComputeBackoffBase(i),
                    $"attempt={i} base should give {expected[i]}s");

                int jittered = LoggingLlmClientDecorator.ComputeBackoffDelay(i, new Random(42 + i));
                Assert.GreaterOrEqual(jittered, 0, $"attempt={i}: jittered delay must be >= 0");
                Assert.LessOrEqual(jittered, expected[i], $"attempt={i}: jittered delay must be <= base");
            }
        }
        // ==================== v1.5.4: IsToolResultSuccess (BUG-5) ====================

        [Test]
        public void IsToolResultSuccess_EmptyString_ReturnsTrue()
        {
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess(""));
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess(null));
        }

        [Test]
        public void IsToolResultSuccess_JsonSuccessFalse_ReturnsFalse()
        {
            Assert.IsFalse(ToolExecutionPolicy.IsToolResultSuccess("{\"Success\":false,\"Error\":\"not found\"}"));
        }

        [Test]
        public void IsToolResultSuccess_JsonSuccessTrue_ReturnsTrue()
        {
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess("{\"Success\":true,\"Message\":\"done\"}"));
        }

        [Test]
        public void IsToolResultSuccess_LowercaseProperty_ReturnsFalse()
        {
            Assert.IsFalse(ToolExecutionPolicy.IsToolResultSuccess("{\"success\":false}"));
        }

        [Test]
        public void IsToolResultSuccess_NestedEscapedJson_NoFalsePositive()
        {
            // The nested JSON contains "success":false in an escaped string, but the TOP-LEVEL
            // Success property is true — should not be a false positive.
            string json = "{\"Success\":true,\"Data\":\"{\\\"success\\\":false}\"}";
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess(json),
                "Nested escaped JSON should not trigger false positive");
        }

        [Test]
        public void IsToolResultSuccess_NonJsonWithSuccessFalse_FallsBackToStringHeuristic()
        {
            // Non-JSON text that contains the heuristic string
            Assert.IsFalse(ToolExecutionPolicy.IsToolResultSuccess(
                "Operation result: \"Success\":false, please retry."));
        }

        [Test]
        public void IsToolResultSuccess_PlainText_ReturnsTrue()
        {
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess("Memory saved for role: teacher"));
        }

        [Test]
        public void IsToolResultSuccess_NoSuccessProperty_ReturnsTrue()
        {
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess("{\"result\":\"ok\",\"count\":42}"));
        }

        [TestCase("{\"error\":\"not found\"}")]
        [TestCase("{\"Error\":\"not found\"}")]
        [TestCase("{\"ok\":false}")]
        [TestCase("{\"Succeeded\":false}")]
        [TestCase("{\"SuCcEsS\":false}")]
        [TestCase("Failed to load scene")]
        [TestCase("Error: missing target")]
        [TestCase("Exception: boom")]
        public void IsToolResultSuccess_CommonFailureShapes_ReturnFalse(string resultText)
        {
            Assert.IsFalse(ToolExecutionPolicy.IsToolResultSuccess(resultText));
        }

        [TestCase("{\"Success\":true,\"Error\":null,\"Message\":\"Content appended\"}")]
        [TestCase("{\"Success\":true,\"error\":\"\"}")]
        [TestCase("{\"Success\":true,\"error\":false}")]
        public void IsToolResultSuccess_NullOrEmptyErrorProperty_ReturnsTrue(string resultText)
        {
            // Regression: many result contracts (e.g. MemoryResult) always serialize an "Error" property,
            // null/empty on success. Presence of the key alone must not be treated as a failure signal.
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess(resultText));
        }

        [Test]
        public void IsToolResultSuccess_NormalContentMentioningSuccess_ReturnsTrue()
        {
            Assert.IsTrue(ToolExecutionPolicy.IsToolResultSuccess(
                "The report discusses success criteria and failure analysis without reporting a tool error."));
        }

        [Test]
        public async Task ExecuteSingle_ClassifiesUntruncatedResultBeforeReturningTruncatedPayload()
        {
            StubSettings settings = new() { MaxToolResultChars = 32 };
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool>(), false, "test", 3);

            string longFailure = "{\"message\":\"" + new string('x', 80) + "\",\"success\":false}";
            MEAI.ChatOptions opts = MakeChatOptions(("long_failure", longFailure));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("long_failure"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded,
                "Success classification must inspect the full result before truncating the returned payload");
            StringAssert.Contains("[truncated:", result.Result.Result.ToString());
        }

        // ==================== Parallel batch execution (R1) ====================

        private static MEAI.ChatOptions MakeAsyncTools(params (string name, int delayMs, string result)[] tools)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            foreach ((string name, int delayMs, string result) in tools)
            {
                int d = delayMs;
                string r = result;
                Func<CancellationToken, Task<string>> fn = async ct =>
                {
                    await Task.Delay(d, ct);
                    return r;
                };
                opts.Tools.Add(MEAI.AIFunctionFactory.Create(fn,
                    new MEAI.AIFunctionFactoryOptions { Name = name, Description = $"Tool {name}" }));
            }

            return opts;
        }

        [Test]
        public async Task ExecuteBatch_PreservesCallOrder_DespiteOutOfOrderCompletion()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            // "slow" finishes after "fast" but must still come first in Results (original call order).
            MEAI.ChatOptions opts = MakeAsyncTools(("slow", 120, "SLOW"), ("fast", 5, "FAST"));
            MEAI.FunctionCallContent callSlow = MakeToolCall("slow", new Dictionary<string, object> { { "n", 1 } });
            MEAI.FunctionCallContent callFast = MakeToolCall("fast", new Dictionary<string, object> { { "n", 2 } });

            ToolExecutionPolicy.BatchToolCallResult batch = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { callSlow, callFast }, opts, CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, batch.Results.Count);
            Assert.AreEqual(callSlow.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual("SLOW", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
            Assert.AreEqual(callFast.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
        }

        [Test]
        public async Task ExecuteBatch_IndependentTools_RunConcurrently()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool>(), false, "test", 3);

            // Four independent 150ms tools with MaxParallelToolCalls=4: sequentially this is ~600ms, but
            // concurrently it is ~150ms. Using four tools (rather than two) widens the gap between the
            // concurrent and sequential times so editor thread-pool scheduling jitter cannot flip the
            // verdict — the assertion only needs to separate "clearly concurrent" from "sequential".
            MEAI.ChatOptions opts = MakeAsyncTools(
                ("a", 150, "A"), ("b", 150, "B"), ("c", 150, "C"), ("d", 150, "D"));
            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("a", new Dictionary<string, object> { { "n", 1 } }),
                MakeToolCall("b", new Dictionary<string, object> { { "n", 2 } }),
                MakeToolCall("c", new Dictionary<string, object> { { "n", 3 } }),
                MakeToolCall("d", new Dictionary<string, object> { { "n", 4 } })
            };

            Stopwatch sw = Stopwatch.StartNew();
            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            sw.Stop();

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(4, batch.Results.Count);
            Assert.Less(sw.ElapsedMilliseconds, 450,
                "Four 150ms independent tools run in parallel (~150ms) must finish far under the ~600ms " +
                "sequential sum; the generous 450ms bound tolerates scheduler jitter while still failing " +
                "if execution were sequential.");
        }

        [Test]
        public async Task ExecuteBatch_MaxParallelOne_RunsSequentially()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeAsyncTools(("a", 120, "A"), ("b", 120, "B"));
            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("a", new Dictionary<string, object> { { "n", 1 } }),
                MakeToolCall("b", new Dictionary<string, object> { { "n", 2 } })
            };

            Stopwatch sw = Stopwatch.StartNew();
            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            sw.Stop();

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, batch.Results.Count);
            Assert.GreaterOrEqual(sw.ElapsedMilliseconds, 200,
                "MaxParallelToolCalls=1 must run tools sequentially (~240ms for two 120ms tools).");
        }

        [Test]
        public async Task ExecuteBatch_OneFails_OthersSucceed_OrderPreserved()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => "OK1"),
                new MEAI.AIFunctionFactoryOptions { Name = "ok1", Description = "ok" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                (Func<CancellationToken, Task<string>>)(ct => throw new InvalidOperationException("boom")),
                new MEAI.AIFunctionFactoryOptions { Name = "bad", Description = "bad" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => "OK3"),
                new MEAI.AIFunctionFactoryOptions { Name = "ok3", Description = "ok" }));

            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("ok1", new Dictionary<string, object> { { "n", 1 } }),
                MakeToolCall("bad", new Dictionary<string, object> { { "n", 2 } }),
                MakeToolCall("ok3", new Dictionary<string, object> { { "n", 3 } })
            };

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);

            Assert.IsTrue(batch.AnyFailed);
            Assert.IsFalse(batch.AllFailed);
            Assert.AreEqual(3, batch.Results.Count);
            Assert.AreEqual(calls[0].CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual(calls[2].CallId, ((MEAI.FunctionResultContent)batch.Results[2]).CallId);
        }

        [Test]
        public async Task ExecuteBatch_PartialFailureRetry_RunsOnlyPreviouslyFailedSlot()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool>(), false, "test", 3);
            int stableInvocations = 0;
            int flakyInvocations = 0;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() =>
            {
                stableInvocations++;
                return "stable";
            }), new MEAI.AIFunctionFactoryOptions { Name = "stable", Description = "stable" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() =>
            {
                flakyInvocations++;
                return flakyInvocations == 1 ? "Error: transient" : "recovered";
            }), new MEAI.AIFunctionFactoryOptions { Name = "flaky", Description = "flaky" }));
            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("stable", new Dictionary<string, object> { { "n", 1 } }),
                MakeToolCall("flaky", new Dictionary<string, object> { { "n", 2 } })
            };

            ToolExecutionPolicy.BatchToolCallResult first =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult retry =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);

            Assert.IsTrue(first.AnyFailed);
            Assert.IsTrue(retry.AnyFailed,
                "The successful slot is represented as a suppressed duplicate in the retry result.");
            Assert.IsFalse(retry.AllFailed, "The previously failed slot must be allowed to recover.");
            Assert.AreEqual(1, stableInvocations, "A successful side effect must not repeat on retry.");
            Assert.AreEqual(2, flakyInvocations, "The failed slot must remain retryable with identical args.");
        }

        [Test]
        public async Task ExecuteBatch_MutatingTools_AreSerialized_NeverOverlap()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool> { new StubTool { Name = "memory" } }, false, "test", 3);

            int active = 0;
            bool overlapped = false;
            object gate = new();
            Func<CancellationToken, Task<string>> mem = async ct =>
            {
                lock (gate)
                {
                    active++;
                    if (active > 1)
                    {
                        overlapped = true;
                    }
                }

                await Task.Delay(40, ct);
                lock (gate)
                {
                    active--;
                }

                return "ok";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(mem,
                new MEAI.AIFunctionFactoryOptions { Name = "memory", Description = "mem" }));

            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("memory", new Dictionary<string, object> { { "n", 1 } }),
                MakeToolCall("memory", new Dictionary<string, object> { { "n", 2 } })
            };

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed);
            Assert.IsFalse(overlapped,
                "Two state-mutating 'memory' calls must be serialized, never run concurrently.");
        }

        // ==================== Parallel streamed execution ====================

        [Test]
        public async Task StreamedTurn_ParallelMode_CallsOverlap_ResultsStayInArrivalOrder()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool>(), false, "test", 3);

            // Hard overlap proof via rendezvous: "first" arrives first but refuses to complete until
            // "second" has STARTED (impossible without concurrency - sequentially the wait times out
            // and returns an error result, failing the test), then keeps running so "second" finishes
            // first. Completion order is therefore the REVERSE of arrival order.
            TaskCompletionSource<bool> secondStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> completionOrder = new();
            object completionLock = new();

            Func<CancellationToken, Task<string>> firstBody = async ct =>
            {
                Task winner = await Task.WhenAny(secondStarted.Task, Task.Delay(3000, ct));
                if (winner != secondStarted.Task)
                {
                    return "Error: calls never overlapped - 'second' did not start while 'first' ran.";
                }

                await Task.Delay(60, ct);
                lock (completionLock)
                {
                    completionOrder.Add("first");
                }

                return "FIRST";
            };
            Func<CancellationToken, Task<string>> secondBody = async ct =>
            {
                secondStarted.TrySetResult(true);
                await Task.Delay(5, ct);
                lock (completionLock)
                {
                    completionOrder.Add("second");
                }

                return "SECOND";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(firstBody,
                new MEAI.AIFunctionFactoryOptions { Name = "first", Description = "first" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(secondBody,
                new MEAI.AIFunctionFactoryOptions { Name = "second", Description = "second" }));

            MEAI.FunctionCallContent firstCall = MakeToolCall("first", new Dictionary<string, object> { { "n", 1 } });
            MEAI.FunctionCallContent secondCall = MakeToolCall("second", new Dictionary<string, object> { { "n", 2 } });

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? scheduledFirst =
                await policy.ExecuteStreamedAsync(turn, firstCall, opts, CancellationToken.None);
            ToolExecutionPolicy.ToolCallResult? scheduledSecond =
                await policy.ExecuteStreamedAsync(turn, secondCall, opts, CancellationToken.None);

            Assert.IsFalse(scheduledFirst.HasValue,
                "Parallel mode schedules the call and defers the result to turn completion.");
            Assert.IsFalse(scheduledSecond.HasValue,
                "Parallel mode schedules the call and defers the result to turn completion.");

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed,
                "Both calls must succeed - a failed 'first' means they never actually overlapped.");
            Assert.AreEqual(2, batch.Results.Count);
            Assert.AreEqual(firstCall.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId,
                "Results must collate in ARRIVAL order, not completion order.");
            Assert.AreEqual("FIRST", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
            Assert.AreEqual(secondCall.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
            Assert.AreEqual("SECOND", ((MEAI.FunctionResultContent)batch.Results[1]).Result.ToString());
            Assert.AreEqual("second", completionOrder[0],
                "The later-arrived call must have completed first (reversed completion order).");
            Assert.AreEqual(0, policy.ConsecutiveErrors, "A clean parallel turn records one success.");
        }

        [Test]
        public async Task StreamedTurn_ParallelMode_SerializedMutatingTools_NeverOverlap()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool>
                {
                    new StubTool { Name = "memory" },
                    new StubTool { Name = "manage_mods" },
                    new StubTool { Name = "manage_skills", AllowDuplicates = true },
                    new StubTool { Name = "world_command" },
                    new StubTool { Name = "component_command" },
                    new StubTool { Name = "execute_lua" },
                    new StubTool { Name = "call_skill_tool" }
                },
                false, "test", 3);

            int active = 0;
            bool overlapped = false;
            object gate = new();
            Func<CancellationToken, Task<string>> body = async ct =>
            {
                lock (gate)
                {
                    active++;
                    if (active > 1)
                    {
                        overlapped = true;
                    }
                }

                await Task.Delay(40, ct);
                lock (gate)
                {
                    active--;
                }

                return "ok";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            string[] mutatingTools =
            {
                "memory", "manage_mods", "manage_skills", "world_command", "component_command",
                "execute_lua", "call_skill_tool"
            };
            foreach (string name in mutatingTools)
            {
                opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                    new MEAI.AIFunctionFactoryOptions { Name = name, Description = name }));
            }

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            for (int i = 0; i < mutatingTools.Length; i++)
            {
                await policy.ExecuteStreamedAsync(
                    turn,
                    MakeToolCall(mutatingTools[i], new Dictionary<string, object> { { "n", i } }),
                    opts,
                    CancellationToken.None);
            }

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(mutatingTools.Length, batch.Results.Count);
            Assert.IsFalse(overlapped,
                "State-mutating built-ins in one streamed turn must be serialized, never concurrent.");
        }

        [Test]
        public async Task StreamedTurn_MultiCallMutatingEcho_IsRejectedBeforeSideEffectsRepeat()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool> { new StubTool { Name = "world_command" } },
                false, "test", 3);

            int invoked = 0;
            Func<CancellationToken, Task<string>> body = ct =>
            {
                Interlocked.Increment(ref invoked);
                return Task.FromResult("ok");
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = "world_command", Description = "mutates world" }));
            Dictionary<string, object?> argsA = new() { { "name", "A" } };
            Dictionary<string, object?> argsB = new() { { "name", "B" } };

            async Task<ToolExecutionPolicy.BatchToolCallResult> RunTurnAsync()
            {
                ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
                await policy.ExecuteStreamedAsync(
                    turn, MakeToolCall("world_command", argsA), opts, CancellationToken.None);
                await policy.ExecuteStreamedAsync(
                    turn, MakeToolCall("world_command", argsB), opts, CancellationToken.None);
                return await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);
            }

            ToolExecutionPolicy.BatchToolCallResult first = await RunTurnAsync();
            ToolExecutionPolicy.BatchToolCallResult echo = await RunTurnAsync();

            Assert.IsFalse(first.AnyFailed);
            Assert.IsTrue(echo.AnyFailed);
            Assert.IsTrue(echo.AllFailed);
            Assert.AreEqual(2, Volatile.Read(ref invoked),
                "An echoed streamed mutation turn must be rejected before it applies side effects twice.");
        }

        [Test]
        public async Task StreamedTurn_PartialMutationRetry_RunsOnlyPreviouslyFailedSideEffect()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool>
                {
                    new StubTool { Name = "world_command" },
                    new StubTool { Name = "execute_lua" }
                }, false, "test", 3);
            int worldInvocations = 0;
            int luaInvocations = 0;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() =>
            {
                worldInvocations++;
                return "world changed";
            }), new MEAI.AIFunctionFactoryOptions { Name = "world_command", Description = "world" }));
            opts.Tools.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() =>
            {
                luaInvocations++;
                return luaInvocations == 1 ? "Error: transient Lua failure" : "lua recovered";
            }), new MEAI.AIFunctionFactoryOptions { Name = "execute_lua", Description = "lua" }));
            Dictionary<string, object?> worldArgs = new() { { "name", "A" } };
            Dictionary<string, object?> luaArgs = new() { { "code", "return 1" } };

            async Task<ToolExecutionPolicy.BatchToolCallResult> RunTurnAsync()
            {
                ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
                await policy.ExecuteStreamedAsync(
                    turn, MakeToolCall("world_command", worldArgs), opts, CancellationToken.None);
                await policy.ExecuteStreamedAsync(
                    turn, MakeToolCall("execute_lua", luaArgs), opts, CancellationToken.None);
                return await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);
            }

            ToolExecutionPolicy.BatchToolCallResult first = await RunTurnAsync();
            ToolExecutionPolicy.BatchToolCallResult retry = await RunTurnAsync();

            Assert.IsTrue(first.AnyFailed);
            Assert.IsTrue(retry.AnyFailed,
                "The previously successful mutation is represented as a suppressed duplicate.");
            Assert.IsFalse(retry.AllFailed, "The failed mutation must remain retryable.");
            Assert.AreEqual(1, worldInvocations, "The successful world mutation must not repeat.");
            Assert.AreEqual(2, luaInvocations, "The failed Lua mutation must execute again and recover.");
        }

        [Test]
        public async Task StreamedTurn_ParallelMode_IntraTurnIdenticalCalls_AllExecute()
        {
            // Parallel-mode counterpart of StreamedTurn_IntraTurnIdenticalCalls_AllExecute:
            // identical calls in ONE turn are legitimate and both schedule/execute.
            CountingMarshaler countingMarshaler = new();
            StubSettings settings = new StubSettings { MaxParallelToolCalls = 4 }
                .WithToolMarshaler(countingMarshaler);
            ToolExecutionPolicy policy = new(new StubLogger(), settings,
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            Dictionary<string, object?> args = new() { { "x", 1 } };
            MEAI.ChatOptions opts = MakeChatOptions(("dup", "ok"));
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();

            MEAI.FunctionCallContent original = MakeToolCall("dup", args);
            MEAI.FunctionCallContent repeat = MakeToolCall("dup", args);
            ToolExecutionPolicy.ToolCallResult? scheduledFirst =
                await policy.ExecuteStreamedAsync(turn, original, opts, CancellationToken.None);
            ToolExecutionPolicy.ToolCallResult? scheduledSecond =
                await policy.ExecuteStreamedAsync(turn, repeat, opts, CancellationToken.None);

            Assert.IsFalse(scheduledFirst.HasValue, "The executable call is scheduled (deferred result).");
            Assert.IsFalse(scheduledSecond.HasValue,
                "The intra-turn repeat is just as executable and must also be scheduled.");

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Assert.AreEqual(2, countingMarshaler.InvokeCount, "Both identical intra-turn calls must run.");
            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, batch.Results.Count);
            Assert.AreEqual(original.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual("ok", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
            Assert.AreEqual(repeat.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
            Assert.AreEqual("ok", ((MEAI.FunctionResultContent)batch.Results[1]).Result.ToString());
        }

        [Test]
        public async Task StreamedTurn_ParallelMode_WholeTurnEcho_RecordsExactlyOneFailure()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                false, "test", 3);

            int invoked = 0;
            Func<CancellationToken, Task<string>> body = async ct =>
            {
                Interlocked.Increment(ref invoked);
                await Task.Delay(10, ct);
                return "ok";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = "dup", Description = "dup" }));

            Dictionary<string, object?> argsA = new() { { "x", 1 } };
            Dictionary<string, object?> argsB = new() { { "x", 2 } };

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsA), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(first, MakeToolCall("dup", argsB), opts, CancellationToken.None);
            await policy.CompleteStreamedTurnAsync(first, CancellationToken.None);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "A clean first parallel turn records a success.");

            // The model echoes the exact same TWO-call turn: only the COMBINED signature was
            // registered, so the calls re-execute, but completion must flag the whole-turn echo
            // with exactly ONE RecordFailure - identical to the sequential streamed semantics.
            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(second, MakeToolCall("dup", argsA), opts, CancellationToken.None);
            await policy.ExecuteStreamedAsync(second, MakeToolCall("dup", argsB), opts, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult echo =
                await policy.CompleteStreamedTurnAsync(second, CancellationToken.None);

            Assert.AreEqual(1, policy.ConsecutiveErrors,
                "A whole-turn echo must record exactly ONE failure under parallel execution too.");
            Assert.IsTrue(echo.AnyFailed, "The echo turn must report AnyFailed.");
            Assert.IsTrue(echo.AllFailed, "The echo turn must report AllFailed.");
            Assert.AreEqual(4, Volatile.Read(ref invoked),
                "Multi-call echo calls still execute; only the turn-level accounting flags the echo.");
        }

        [Test]
        public async Task StreamedTurn_MaxParallelOne_ExecutesInlineAndReturnsResults()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 1 },
                new List<ILlmTool>(), false, "test", 3);

            MEAI.ChatOptions opts = MakeAsyncTools(("slow_a", 60, "A"), ("slow_b", 5, "B"));
            MEAI.FunctionCallContent callA = MakeToolCall("slow_a", new Dictionary<string, object> { { "n", 1 } });
            MEAI.FunctionCallContent callB = MakeToolCall("slow_b", new Dictionary<string, object> { { "n", 2 } });

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            Stopwatch sw = Stopwatch.StartNew();
            ToolExecutionPolicy.ToolCallResult? r1 =
                await policy.ExecuteStreamedAsync(turn, callA, opts, CancellationToken.None);
            sw.Stop();

            Assert.IsTrue(r1.HasValue, "MaxParallelToolCalls=1 must execute inline and return the result.");
            Assert.IsTrue(r1.Value.Succeeded);
            Assert.AreEqual("A", r1.Value.Result.Result.ToString());
            Assert.GreaterOrEqual(sw.ElapsedMilliseconds, 40,
                "Inline execution: awaiting ExecuteStreamedAsync must include the tool's own runtime.");

            ToolExecutionPolicy.ToolCallResult? r2 =
                await policy.ExecuteStreamedAsync(turn, callB, opts, CancellationToken.None);
            Assert.IsTrue(r2.HasValue && r2.Value.Succeeded);

            // Nothing was scheduled, so the SYNCHRONOUS completion stays valid (pre-parallel API).
            ToolExecutionPolicy.BatchToolCallResult batch = policy.CompleteStreamedTurn(turn);

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, batch.Results.Count);
            Assert.AreEqual(callA.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            Assert.AreEqual(callB.CallId, ((MEAI.FunctionResultContent)batch.Results[1]).CallId);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "A clean sequential turn records one success.");
        }

        [Test]
        public async Task CompleteStreamedTurnAsync_CancelledWhileCallRuns_UnfinishedSlotBecomesFailure()
        {
            ToolExecutionPolicy policy = new(new StubLogger(), new StubSettings { MaxParallelToolCalls = 4 },
                new List<ILlmTool> { new StubTool { Name = "hang" } },
                false, "test", 3);

            // The tool IGNORES its cancellation token (worst case): finalization must still return
            // promptly once the outer token fires, without throwing, collating the unfinished slot
            // as an explicit failure.
            Func<CancellationToken, Task<string>> hangBody = async _ =>
            {
                await Task.Delay(2000, CancellationToken.None);
                return "late";
            };
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(hangBody,
                new MEAI.AIFunctionFactoryOptions { Name = "hang", Description = "hang" }));

            CancellationTokenSource cts = new();
            MEAI.FunctionCallContent call = MakeToolCall("hang", new Dictionary<string, object> { { "n", 1 } });

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? scheduled =
                await policy.ExecuteStreamedAsync(turn, call, opts, cts.Token);
            Assert.IsFalse(scheduled.HasValue, "Parallel mode defers the result to turn completion.");

            cts.Cancel();

            // async Task + try/catch instead of Assert.DoesNotThrowAsync (which would block the
            // Unity main thread - the EditMode sync-over-async deadlock).
            ToolExecutionPolicy.BatchToolCallResult batch = default;
            Exception caught = null;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                batch = await policy.CompleteStreamedTurnAsync(turn, cts.Token);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            sw.Stop();
            Assert.IsNull(caught,
                "Finalization must NEVER throw - it is also the mid-stream-abort accounting path.");
            Assert.Less(sw.ElapsedMilliseconds, 1500,
                "A cancelled token must bound the drain; a token-ignoring tool cannot hang finalization.");
            Assert.AreEqual(1, batch.Results.Count);
            Assert.AreEqual(call.CallId, ((MEAI.FunctionResultContent)batch.Results[0]).CallId);
            StringAssert.Contains("did not complete",
                ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
            Assert.IsTrue(batch.AnyFailed);
            Assert.IsTrue(batch.AllFailed);
            Assert.AreEqual(1, policy.ConsecutiveErrors,
                "The aborted turn records exactly ONE failure against the consecutive-error counter.");
        }
    }
}
#endif

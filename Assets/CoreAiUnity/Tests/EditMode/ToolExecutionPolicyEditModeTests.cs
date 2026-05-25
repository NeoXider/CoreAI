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
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => true;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;

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
            public string ParametersSchema => "{}";
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
        public void ComputeBackoff_ZeroAttempt_Returns2s()
        {
            // Access via reflection since it's private static
            MethodInfo method = typeof(LoggingLlmClientDecorator)
                .GetMethod("ComputeBackoff",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ComputeBackoff should exist");

            int val = (int)method.Invoke(null, new object[] { 0 });
            Assert.AreEqual(2, val, "attempt=0: 2 * 2^0 = 2s");
        }

        [Test]
        public void ComputeBackoff_ExponentialCurve_CappedAt30()
        {
            MethodInfo method = typeof(LoggingLlmClientDecorator)
                .GetMethod("ComputeBackoff",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            // attempt 0 → 2*2^0=2, attempt 1 → 4, attempt 2 → 8, attempt 3 → 16, attempt 4 → 30 (capped)
            int[] expected = { 2, 4, 8, 16, 30, 30, 30 };
            for (int i = 0; i < expected.Length; i++)
            {
                int val = (int)method.Invoke(null, new object[] { i });
                Assert.AreEqual(expected[i], val, $"attempt={i} should give {expected[i]}s");
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
    }
}
#endif

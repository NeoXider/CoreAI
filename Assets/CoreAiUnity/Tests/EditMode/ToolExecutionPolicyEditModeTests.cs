#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    [TestFixture]
    public sealed class ToolExecutionPolicyEditModeTests
    {
        // ==================== Helpers ====================

        private sealed class StubLogger : IGameLogger
        {
            public readonly List<string> Logs = new();
            public void LogDebug(GameLogFeature feature, string msg, UnityEngine.Object context = null) => Logs.Add($"[DBG] {msg}");
            public void LogInfo(GameLogFeature feature, string msg, UnityEngine.Object context = null) => Logs.Add($"[INFO] {msg}");
            public void LogWarning(GameLogFeature feature, string msg, UnityEngine.Object context = null) => Logs.Add($"[WARN] {msg}");
            public void LogError(GameLogFeature feature, string msg, UnityEngine.Object context = null) => Logs.Add($"[ERR] {msg}");
        }

        private sealed class StubSettings : ICoreAISettings
        {
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
            foreach (var (name, result) in tools)
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
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                allowDuplicateToolCalls: false, "test", 3);

            var calls = new List<MEAI.FunctionCallContent> { MakeToolCall("greet") };
            var result = policy.CheckDuplicate(calls);
            Assert.IsNull(result, "First call should not be blocked");
        }

        [Test]
        public void CheckDuplicate_SameSignatureTwice_BlocksSecond()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                allowDuplicateToolCalls: false, "test", 3);

            var args = new Dictionary<string, object?> { { "who", "world" } };
            var calls1 = new List<MEAI.FunctionCallContent> { MakeToolCall("greet", args) };
            var calls2 = new List<MEAI.FunctionCallContent> { MakeToolCall("greet", args) };

            Assert.IsNull(policy.CheckDuplicate(calls1));
            var blocked = policy.CheckDuplicate(calls2);
            Assert.IsNotNull(blocked, "Second identical call should be blocked");
            Assert.AreEqual(1, blocked.Count);
        }

        [Test]
        public void CheckDuplicate_DifferentArgs_Allowed()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                allowDuplicateToolCalls: false, "test", 3);

            var calls1 = new List<MEAI.FunctionCallContent>
                { MakeToolCall("greet", new Dictionary<string, object?> { { "who", "A" } }) };
            var calls2 = new List<MEAI.FunctionCallContent>
                { MakeToolCall("greet", new Dictionary<string, object?> { { "who", "B" } }) };

            Assert.IsNull(policy.CheckDuplicate(calls1));
            Assert.IsNull(policy.CheckDuplicate(calls2), "Different args should be allowed");
        }

        [Test]
        public void CheckDuplicate_AllowDuplicatesGlobal_NeverBlocks()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "greet" } },
                allowDuplicateToolCalls: true, "test", 3);

            var args = new Dictionary<string, object?> { { "x", 1 } };
            var calls = new List<MEAI.FunctionCallContent> { MakeToolCall("greet", args) };

            Assert.IsNull(policy.CheckDuplicate(calls));
            Assert.IsNull(policy.CheckDuplicate(calls), "Global AllowDuplicateToolCalls=true should never block");
        }

        [Test]
        public void CheckDuplicate_PerToolAllowDuplicates_Respected()
        {
            var tool = new StubTool { Name = "repeat_action", AllowDuplicates = true };
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { tool },
                allowDuplicateToolCalls: false, "test", 3);

            var args = new Dictionary<string, object?> { { "action", "jump" } };
            var calls = new List<MEAI.FunctionCallContent> { MakeToolCall("repeat_action", args) };

            Assert.IsNull(policy.CheckDuplicate(calls));
            Assert.IsNull(policy.CheckDuplicate(calls), "Per-tool AllowDuplicates should be respected");
        }

        // ==================== Error Counter ====================

        [Test]
        public void RecordSuccess_ResetsCounter()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
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
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            policy.RecordFailure();
            Assert.AreEqual(1, policy.ConsecutiveErrors);
            policy.RecordFailure();
            Assert.AreEqual(2, policy.ConsecutiveErrors);
        }

        [Test]
        public void IsMaxErrorsReached_AtThreshold_ReturnsTrue()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
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
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "t" } },
                allowDuplicateToolCalls: false, "test", 3);

            var args = new Dictionary<string, object?> { { "a", 1 } };
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
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            var opts = MakeChatOptions(("hello", "world"));
            var fc = MakeToolCall("hello");

            var result = await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(result.Result);
        }

        [Test]
        public async Task ExecuteSingle_AsyncTool_WaitsForCompletion()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
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
            var result = await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            sw.Stop();

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(completed);
            Assert.GreaterOrEqual(sw.ElapsedMilliseconds, 50);
            Assert.AreEqual("async-ok", result.Result.Result.ToString());
        }

        [Test]
        public async Task ExecuteSingle_ToolNotFound_ReturnsFailed()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            var opts = MakeChatOptions();
            var fc = MakeToolCall("nonexistent");

            var result = await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Result.Result.ToString().Contains("not found"));
        }

        // ==================== ExecuteBatchAsync ====================

        [Test]
        public async Task ExecuteBatch_AllSucceed_ResetsErrorCounter()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), true, "test", 3);

            policy.RecordFailure(); // Pre-existing failure
            Assert.AreEqual(1, policy.ConsecutiveErrors);

            var opts = MakeChatOptions(("tool_a", "ok"));
            var calls = new List<MEAI.FunctionCallContent> { MakeToolCall("tool_a") };

            var batch = await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(0, policy.ConsecutiveErrors, "Success should reset error counter");
        }

        [Test]
        public async Task ExecuteBatch_DuplicateBlocked_ReturnsFailed()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "dup" } },
                allowDuplicateToolCalls: false, "test", 3);

            var args = new Dictionary<string, object?> { { "x", 1 } };
            var opts = MakeChatOptions(("dup", "ok"));
            var calls = new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) };

            // First call succeeds
            var batch1 = await policy.ExecuteBatchAsync(calls, opts, CancellationToken.None);
            Assert.IsFalse(batch1.AnyFailed);

            // Second identical call is blocked by duplicate detection
            var batch2 = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent> { MakeToolCall("dup", args) },
                opts, CancellationToken.None);
            Assert.IsTrue(batch2.AnyFailed, "Duplicate should be blocked");
        }

        // ==================== BuildMaxErrorsResponse ====================

        [Test]
        public void BuildMaxErrorsResponse_ContainsErrorText()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool>(), false, "test", 3);

            var response = policy.BuildMaxErrorsResponse();
            Assert.IsNotNull(response);
            Assert.IsTrue(response.Text.Contains("error"), "Should contain error description");
        }

        // ==================== TryRepairToolName ====================

        [Test]
        public void TryRepairToolName_ExactMatch_ReturnsSameFc()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            var fc = MakeToolCall("memory");
            var repaired = policy.TryRepairToolName(fc);

            Assert.IsNotNull(repaired);
            Assert.AreEqual("memory", repaired.Name, "Exact match should be returned as-is");
            Assert.AreSame(fc, repaired, "Should return the same instance for exact match");
        }

        [Test]
        public void TryRepairToolName_WrongCase_ReturnsRepaired()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            var fc = MakeToolCall("MEMORY");
            var repaired = policy.TryRepairToolName(fc);

            Assert.IsNotNull(repaired, "Should repair wrong casing");
            Assert.AreEqual("memory", repaired.Name, "Name should be corrected to registered casing");
            Assert.AreNotSame(fc, repaired, "Should return new instance with repaired name");
        }

        [Test]
        public void TryRepairToolName_MixedCase_ReturnsRepaired()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "spawn_quiz" } },
                false, "test", 3);

            var repaired = policy.TryRepairToolName(MakeToolCall("Spawn_Quiz"));
            Assert.IsNotNull(repaired);
            Assert.AreEqual("spawn_quiz", repaired.Name);
        }

        [Test]
        public void TryRepairToolName_UnknownTool_ReturnsNull()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            var result = policy.TryRepairToolName(MakeToolCall("completely_unknown_tool_xyz"));
            Assert.IsNull(result, "Unknown tool should return null");
        }

        [Test]
        public void TryRepairToolName_NullFc_ReturnsNull()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            Assert.IsNull(policy.TryRepairToolName(null));
        }

        [Test]
        public async Task ExecuteSingle_WrongCaseName_IsRepaired()
        {
            // Model called "MEMORY" but tool is registered as "memory"
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            var opts = MakeChatOptions(("memory", "Memory saved"));
            var fc = MakeToolCall("MEMORY"); // wrong casing from model

            var result = await policy.ExecuteSingleAsync(fc, opts, CancellationToken.None);
            Assert.IsTrue(result.Succeeded, "Tool should succeed after name repair");
        }

        [Test]
        public async Task ExecuteSingle_TrulyUnknownTool_ReturnsFailed()
        {
            var policy = new ToolExecutionPolicy(new StubLogger(), new StubSettings(),
                new List<ILlmTool> { new StubTool { Name = "memory" } },
                false, "test", 3);

            var opts = MakeChatOptions(("memory", "ok"));
            var result = await policy.ExecuteSingleAsync(MakeToolCall("totally_unknown"), opts, CancellationToken.None);

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
            var method = typeof(LoggingLlmClientDecorator)
                .GetMethod("ComputeBackoff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "ComputeBackoff should exist");

            int val = (int)method.Invoke(null, new object[] { 0 });
            Assert.AreEqual(2, val, "attempt=0: 2 * 2^0 = 2s");
        }

        [Test]
        public void ComputeBackoff_ExponentialCurve_CappedAt30()
        {
            var method = typeof(LoggingLlmClientDecorator)
                .GetMethod("ComputeBackoff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);

            // attempt 0 → 2*2^0=2, attempt 1 → 4, attempt 2 → 8, attempt 3 → 16, attempt 4 → 30 (capped)
            int[] expected = { 2, 4, 8, 16, 30, 30, 30 };
            for (int i = 0; i < expected.Length; i++)
            {
                int val = (int)method.Invoke(null, new object[] { i });
                Assert.AreEqual(expected[i], val, $"attempt={i} should give {expected[i]}s");
            }
        }
    }
}
#endif


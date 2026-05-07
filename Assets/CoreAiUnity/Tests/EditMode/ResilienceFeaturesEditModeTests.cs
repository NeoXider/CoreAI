#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Tests for production resilience features: tool result truncation, per-tool timeout,
    /// max tool-call roundtrips. These validate <see cref="ToolExecutionPolicy"/> and
    /// <see cref="SmartToolCallingChatClient"/> safety mechanisms.
    /// </summary>
    [TestFixture]
    public sealed class ResilienceFeaturesEditModeTests
    {
        // ==================== Helpers ====================

        private sealed class ResilienceSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 120;
            public int MaxLlmRequestRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => true;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => true;
            public bool LogMeaiToolCallingSteps => true;
            public bool AllowDuplicateToolCalls => true;
            public bool EnableStreaming => false;

            // Resilience settings with test-friendly overrides
            public int MaxToolResultCharsOverride { get; set; } = 8000;
            public int MaxToolResultChars => MaxToolResultCharsOverride;

            public int DefaultToolTimeoutMsOverride { get; set; } = 30000;
            public int DefaultToolTimeoutMs => DefaultToolTimeoutMsOverride;

            public int MaxResponseCharsOverride { get; set; }
            public int MaxResponseChars => MaxResponseCharsOverride;

            public int MaxToolCallRoundtripsOverride { get; set; } = 10;
            public int MaxToolCallRoundtrips => MaxToolCallRoundtripsOverride;
        }

        private static MEAI.FunctionCallContent MakeToolCall(string name)
        {
            return new MEAI.FunctionCallContent($"call_{name}_{Guid.NewGuid():N}", name);
        }

        private static MEAI.ChatOptions MakeChatOptions(params (string name, Delegate func)[] tools)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            foreach (var (name, func) in tools)
            {
                opts.Tools.Add(MEAI.AIFunctionFactory.Create(func,
                    new MEAI.AIFunctionFactoryOptions { Name = name, Description = $"Tool {name}" }));
            }
            return opts;
        }

        // ==================== Tool Result Truncation ====================

        [Test]
        public async Task ToolResultTruncation_LargeResult_IsSoftTruncated()
        {
            var settings = new ResilienceSettings { MaxToolResultCharsOverride = 100 };
            string bigResult = new string('A', 500);
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => bigResult;
            var opts = MakeChatOptions(("big_tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("big_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            string text = result.Result.Result?.ToString() ?? "";
            Assert.That(text, Does.Contain("truncated"), "Should have truncation notice");
            Assert.That(text, Does.Contain("500"), "Should mention original length");
            Assert.That(text.Length, Is.LessThan(300), "Should be much smaller than original");
        }

        [Test]
        public async Task ToolResultTruncation_SmallResult_Untouched()
        {
            var settings = new ResilienceSettings { MaxToolResultCharsOverride = 8000 };
            string smallResult = "OK, crafted Iron Sword.";
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => smallResult;
            var opts = MakeChatOptions(("small_tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("small_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(smallResult, result.Result.Result?.ToString(),
                "Small result should not be truncated");
        }

        [Test]
        public async Task ToolResultTruncation_DisabledWhenZero()
        {
            var settings = new ResilienceSettings { MaxToolResultCharsOverride = 0 };
            string bigResult = new string('X', 50_000);
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => bigResult;
            var opts = MakeChatOptions(("huge_tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("huge_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(bigResult, result.Result.Result?.ToString(),
                "Zero maxToolResultChars should disable truncation");
        }

        // ==================== Per-Tool Timeout ====================

        [Test]
        public async Task ToolTimeout_SlowTool_ReturnsTimeoutError()
        {
            var settings = new ResilienceSettings { DefaultToolTimeoutMsOverride = 200 }; // 200ms
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<CancellationToken, Task<string>> func = async ct =>
            {
                await Task.Delay(10_000, ct); // 10 seconds — should be cancelled
                return "done";
            };
            var opts = MakeChatOptions(("slow_tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("slow_tool"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded, "Slow tool should fail");
            string text = result.Result.Result?.ToString() ?? "";
            Assert.That(text, Does.Contain("timed out"), "Should mention timeout");
        }

        [Test]
        public async Task ToolTimeout_FastTool_Succeeds()
        {
            var settings = new ResilienceSettings { DefaultToolTimeoutMsOverride = 5000 }; // 5s
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => "fast-result";
            var opts = MakeChatOptions(("fast_tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("fast_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("fast-result", result.Result.Result?.ToString());
        }

        [Test]
        public async Task ToolTimeout_DisabledWhenZero_NoTimeout()
        {
            var settings = new ResilienceSettings { DefaultToolTimeoutMsOverride = 0 };
            var policy = new ToolExecutionPolicy(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => "no-timeout-ok";
            var opts = MakeChatOptions(("tool", func));

            var result = await policy.ExecuteSingleAsync(MakeToolCall("tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
        }

        // ==================== CoreAISettings Static Proxy ====================

        [Test]
        public void CoreAISettings_Defaults_MatchInterfaceDefaults()
        {
            // Store original and reset
            CoreAISettings.ResetOverrides();
            var original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                Assert.AreEqual(8000, CoreAISettings.MaxToolResultChars,
                    "Default MaxToolResultChars should be 8000");
                Assert.AreEqual(30000, CoreAISettings.DefaultToolTimeoutMs,
                    "Default DefaultToolTimeoutMs should be 30000");
                Assert.AreEqual(0, CoreAISettings.MaxResponseChars,
                    "Default MaxResponseChars should be 0 (disabled)");
                Assert.AreEqual(10, CoreAISettings.MaxToolCallRoundtrips,
                    "Default MaxToolCallRoundtrips should be 10");
            }
            finally
            {
                CoreAISettings.Instance = original;
            }
        }

        [Test]
        public void CoreAISettings_OverridesWork()
        {
            CoreAISettings.ResetOverrides();
            var original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                CoreAISettings.MaxToolResultChars = 500;
                Assert.AreEqual(500, CoreAISettings.MaxToolResultChars);

                CoreAISettings.DefaultToolTimeoutMs = 1000;
                Assert.AreEqual(1000, CoreAISettings.DefaultToolTimeoutMs);

                CoreAISettings.MaxResponseChars = 2000;
                Assert.AreEqual(2000, CoreAISettings.MaxResponseChars);

                CoreAISettings.MaxToolCallRoundtrips = 5;
                Assert.AreEqual(5, CoreAISettings.MaxToolCallRoundtrips);

                CoreAISettings.ResetOverrides();
                Assert.AreEqual(8000, CoreAISettings.MaxToolResultChars, "Should reset to default");
            }
            finally
            {
                CoreAISettings.Instance = original;
                CoreAISettings.ResetOverrides();
            }
        }
    }
}
#endif

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LoggingLlmClientDecoratorEditModeTests
    {
        private sealed class SpyLogger : IGameLogger
        {
            public readonly List<string> Lines = new List<string>();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Lines.Add($"D:{feature}:{message}");

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Lines.Add($"I:{feature}:{message}");

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Lines.Add($"W:{feature}:{message}");

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Lines.Add($"E:{feature}:{message}");
        }

        private sealed class AllOnSettings : IGameLogSettings
        {
            public bool ShouldLog(GameLogFeature feature, GameLogLevel level) => true;
        }

        private sealed class MockLlm : ILlmClient
        {
            private readonly int _delayMs;
            private readonly LlmCompletionResult _result;

            public MockLlm(int delayMs, LlmCompletionResult result)
            {
                _delayMs = delayMs;
                _result = result;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (_delayMs > 0)
                    await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return _result;
            }
        }

        [Test]
        public async Task Success_LogsTraceIdAndRole()
        {
            var spy = new SpyLogger();
            var inner = new MockLlm(0, new LlmCompletionResult { Ok = true, Content = "ok" });
            var dec = new LoggingLlmClientDecorator(inner, spy, 0f);
            var req = new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.Creator,
                TraceId = "abc123",
                SystemPrompt = "sys",
                UserPayload = "user"
            };
            var r = await dec.CompleteAsync(req);
            Assert.IsTrue(r.Ok);
            var joined = string.Join("\n", spy.Lines);
            StringAssert.Contains("abc123", joined);
            StringAssert.Contains(BuiltInAgentRoleIds.Creator, joined);
            StringAssert.Contains("LLM ▶", joined);
            StringAssert.Contains("LLM ◀", joined);
        }

        [Test]
        public async Task Timeout_LogsWarningAndReturnsError()
        {
            var spy = new SpyLogger();
            var inner = new MockLlm(5000, new LlmCompletionResult { Ok = true, Content = "late" });
            var dec = new LoggingLlmClientDecorator(inner, spy, 0.05f);
            using var cts = new CancellationTokenSource();
            var req = new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.Programmer,
                TraceId = "t-out",
                UserPayload = "x"
            };
            var r = await dec.CompleteAsync(req, cts.Token);
            Assert.IsFalse(r.Ok);
            Assert.IsTrue(r.Error?.Contains("timeout") == true || r.Error?.Contains("Timeout") == true);
            var joined = string.Join("\n", spy.Lines);
            StringAssert.Contains("t-out", joined);
            StringAssert.Contains("LLM ⏱", joined);
        }
    }
}

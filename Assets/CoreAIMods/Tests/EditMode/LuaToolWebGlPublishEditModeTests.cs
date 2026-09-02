using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The MEAI surface of <see cref="LuaTool"/> must deliver an asynchronously completed body to MEAI
    /// on the completing call stack. MEAI awaits the tool task with <c>ConfigureAwait(false)</c> inside
    /// its binary; under a host <see cref="SynchronizationContext"/> (UnitySynchronizationContext in the
    /// WebGL player) that continuation is otherwise queued to a thread pool the browser does not have,
    /// and the model turn never resumes. A derived context stands in for Unity's here; the editor's
    /// thread pool is what makes the defect observable as a hop to another thread instead of a hang.
    /// </summary>
    public sealed class LuaToolWebGlPublishEditModeTests
    {
        private SynchronizationContext _previous;

        [SetUp]
        public void SetUp()
        {
            _previous = SynchronizationContext.Current;
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }

        [Test]
        public void CreateAIFunction_AsyncBodyUnderAHostContext_CompletesTheMeaiInvocationInline()
        {
            TaskCompletionSource<LuaTool.LuaResult> body = new();
            LuaTool tool = new(new PendingExecutor(body.Task), new FakeSettings(), new NullLog());
            AIFunction function = tool.CreateAIFunction();
            HostSynchronizationContext host = new();
            SynchronizationContext.SetSynchronizationContext(host);

            Task<object> invocation = function
                .InvokeAsync(new AIFunctionArguments { ["code"] = "return 1" })
                .AsTask();
            Assert.IsFalse(invocation.IsCompleted, "the executor is still pending, so the invocation must be too");
            int observedThread = -1;
            Task observer = invocation.ContinueWith(
                _ => observedThread = Environment.CurrentManagedThreadId,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            int completingThread = Environment.CurrentManagedThreadId;

            body.SetResult(new LuaTool.LuaResult { Success = true, Output = "1" });

            Assert.IsTrue(invocation.IsCompleted,
                "MEAI's continuation must run inline on the completing call stack; the WebGL player has no thread pool to run it later");
            Assert.IsTrue(observer.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(completingThread, observedThread,
                "the MEAI result hopped to another thread: that hop is a thread-pool continuation, which never runs in the WebGL player");
            StringAssert.Contains("Success", invocation.Result.ToString());
            Assert.AreSame(host, SynchronizationContext.Current, "the host context must be restored after publishing");
        }

        private sealed class PendingExecutor : LuaTool.ILuaExecutor
        {
            private readonly Task<LuaTool.LuaResult> _pending;

            public PendingExecutor(Task<LuaTool.LuaResult> pending)
            {
                _pending = pending;
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken) => _pending;
        }

        private sealed class HostSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) => d(state);

            public override void Send(SendOrPostCallback d, object state) => d(state);
        }

        private sealed class NullLog : ILog
        {
            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
            }
        }

        private sealed class FakeSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}

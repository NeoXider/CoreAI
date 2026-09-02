#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A streamed tool that completes ONLY on an external signal arriving AFTER
    /// <c>CompleteStreamedTurnAsync</c> already started draining — the shape of every interactive
    /// tool that waits for a human (quiz card, drag-and-drop, confirmation prompt).
    /// <para>
    /// This shape had no coverage at all: every existing streamed-turn test either finishes its tool
    /// immediately or ends it with <c>Task.Delay</c>. That gap is why a drain built on thread-pool
    /// continuations and a timer deadline shipped and hung a WebGL player indefinitely, with the chat
    /// animating a typing indicator that never resolved.
    /// </para>
    /// <para>
    /// Edit Mode has a real thread pool, so these tests CANNOT reproduce the WebGL hang itself; they
    /// pin the logic (the loop does issue the follow-up request once the tool is released). The
    /// platform half is guarded separately by <c>WebGlUnsafeAsyncPrimitivesEditModeTests</c>.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class StreamedTurnDeferredToolEditModeTests
    {
        // ============ policy-level ============

        [Test]
        public async Task CompleteStreamedTurnAsync_ToolReleasedByExternalSignalDuringDrain_Returns()
        {
            PolicyStubSettings settings = new() { MaxParallelToolCalls = 4, DefaultToolTimeoutMsValue = 150000 };
            ToolExecutionPolicy policy = new(new PolicyStubLogger(), settings,
                new List<ILlmTool> { new PolicyStubTool { Name = "spawn_quiz" } }, false, "test", 3);

            TaskCompletionSource<string> release = new();
            Func<CancellationToken, Task<string>> body = _ => release.Task;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = "spawn_quiz", Description = "blocks" }));

            MEAI.FunctionCallContent call =
                new("call-quiz", "spawn_quiz", new Dictionary<string, object> { { "q", 1 } });

            using CancellationTokenSource requestCts = new();
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? scheduled =
                await policy.ExecuteStreamedAsync(turn, call, opts, requestCts.Token);
            Assert.IsFalse(scheduled.HasValue, "Parallel mode must schedule the call, not run it inline.");

            // Start the drain FIRST, release only afterwards (production order: the SSE stream ends
            // while the student is still looking at the card).
            Task<ToolExecutionPolicy.BatchToolCallResult> completion =
                policy.CompleteStreamedTurnAsync(turn, requestCts.Token);
            Assert.IsFalse(completion.IsCompleted, "The drain must still be waiting for the blocked tool.");

            // Kept and awaited below: a fire-and-forget Task.Run swallows its own failure and can then
            // resurface as an UnobservedTaskException inside an unrelated fixture.
            Task releaser = Task.Run(async () =>
            {
                await Task.Delay(150);
                release.TrySetResult("{\"Success\":true,\"answer\":\"correct\"}");
            });

            Task finished = await Task.WhenAny(completion, Task.Delay(10000));
            Assert.AreSame(completion, finished,
                "CompleteStreamedTurnAsync never returned after the tool was released externally.");

            await releaser;
            ToolExecutionPolicy.BatchToolCallResult batch = await completion;
            Assert.AreEqual(1, batch.Results.Count);
            Assert.IsFalse(batch.AnyFailed, "The released tool must collate as a success, not a drain failure.");
            StringAssert.Contains("correct", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
        }

        [Test]
        public async Task CompleteStreamedTurnAsync_ToolWithLongerOwnTimeout_DrainWaitsPastTheGlobalOne()
        {
            // The drain is ONE deadline over every in-flight call, so it has to follow the same per-tool
            // override the call itself got. Global budget 200 ms (+1 s drain margin) versus a card the
            // student finishes at ~1.5 s: with the drain still reading the global setting the slot would
            // collate as "did not complete" and the model would be told the quiz failed while the student
            // was answering it.
            PolicyStubSettings settings = new() { MaxParallelToolCalls = 4, DefaultToolTimeoutMsValue = 200 };
            ToolExecutionPolicy policy = new(new PolicyStubLogger(), settings,
                new List<ILlmTool>
                {
                    new PolicyStubTool { Name = "spawn_quiz", ToolTimeoutMsOverride = 30000 }
                },
                false, "test", 3);

            TaskCompletionSource<string> release = new();
            Func<CancellationToken, Task<string>> body = _ => release.Task;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = "spawn_quiz", Description = "blocks" }));

            MEAI.FunctionCallContent call =
                new("call-quiz", "spawn_quiz", new Dictionary<string, object> { { "q", 1 } });

            using CancellationTokenSource requestCts = new();
            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(turn, call, opts, requestCts.Token);

            Task<ToolExecutionPolicy.BatchToolCallResult> completion =
                policy.CompleteStreamedTurnAsync(turn, requestCts.Token);

            Task releaser = Task.Run(async () =>
            {
                await Task.Delay(1500);
                release.TrySetResult("{\"Success\":true,\"answer\":\"correct\"}");
            });

            Task finished = await Task.WhenAny(completion, Task.Delay(20000));
            Assert.AreSame(completion, finished, "CompleteStreamedTurnAsync never returned.");

            await releaser;
            ToolExecutionPolicy.BatchToolCallResult batch = await completion;
            Assert.IsFalse(batch.AnyFailed,
                "The drain abandoned a tool that declared a longer budget than the global default.");
            StringAssert.Contains("correct", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
        }

        [Test]
        public async Task CompleteStreamedTurnAsync_ToolWithTimeoutDisabled_UncancellableDrainStillWaits()
        {
            // Mid-stream-abort shape: finalization passes CancellationToken.None on purpose. With the
            // deadline switched off per tool this is the branch that waits for natural completion, and it
            // is the one place where "no timeout" really does mean "nothing above will end this call".
            PolicyStubSettings settings = new() { MaxParallelToolCalls = 4, DefaultToolTimeoutMsValue = 200 };
            ToolExecutionPolicy policy = new(new PolicyStubLogger(), settings,
                new List<ILlmTool>
                {
                    new PolicyStubTool { Name = "spawn_drag_and_drop", ToolTimeoutMsOverride = 0 }
                },
                false, "test", 3);

            TaskCompletionSource<string> release = new();
            Func<CancellationToken, Task<string>> body = _ => release.Task;
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = "spawn_drag_and_drop", Description = "blocks" }));

            MEAI.FunctionCallContent call =
                new("call-dnd", "spawn_drag_and_drop", new Dictionary<string, object> { { "z", 1 } });

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(turn, call, opts, CancellationToken.None);

            Task<ToolExecutionPolicy.BatchToolCallResult> completion =
                policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Task releaser = Task.Run(async () =>
            {
                await Task.Delay(600);
                release.TrySetResult("{\"Success\":true,\"placed\":\"all\"}");
            });

            Task finished = await Task.WhenAny(completion, Task.Delay(20000));
            Assert.AreSame(completion, finished, "CompleteStreamedTurnAsync never returned.");

            await releaser;
            ToolExecutionPolicy.BatchToolCallResult batch = await completion;
            Assert.IsFalse(batch.AnyFailed,
                "A tool whose deadline is disabled must be drained to its natural completion.");
            StringAssert.Contains("placed", ((MEAI.FunctionResultContent)batch.Results[0]).Result.ToString());
        }

        // ============ client-loop level: does a SECOND request happen? ============

        [Test]
        public async Task StreamingLoop_BlockingToolReleasedExternally_IssuesSecondModelRequest()
        {
            BlockingTool tool = new("spawn_quiz");
            CountingNativeToolClient inner = new();
            ClientStubSettings settings = new();
            MeaiLlmClient client = new(inner, new SilentLogger(), settings, null);

            // Kept and awaited below, for the same reason as in the policy-level test above.
            Task releaser = Task.Run(async () =>
            {
                await tool.StartedTask;
                await Task.Delay(150);
                tool.Release("{\"Success\":true,\"answer\":\"correct\"}");
            });

            List<LlmStreamChunk> chunks = new();
            Task consume = ConsumeAsync(client, tool, chunks);
            Task finished = await Task.WhenAny(consume, Task.Delay(15000));
            Assert.AreSame(consume, finished,
                "The streaming tool loop never finished after the blocking tool was released.");
            await consume;
            await releaser;

            Assert.AreEqual(2, inner.StreamCalls,
                "After a blocking tool is released, the loop MUST issue a second model request.");
            Assert.That(string.Concat(chunks.Select(c => c.Text)), Does.Contain("Done."));
        }

        private static async Task ConsumeAsync(
            MeaiLlmClient client, ILlmTool tool, List<LlmStreamChunk> sink)
        {
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "sys",
                               UserPayload = "go",
                               Tools = new List<ILlmTool> { tool }
                           }, CancellationToken.None))
            {
                sink.Add(chunk);
            }
        }

        // ============ stubs ============

        private sealed class BlockingTool : ILlmTool, IAIFunctionLlmTool
        {
            private readonly TaskCompletionSource<string> _release = new();
            private readonly TaskCompletionSource<bool> _started = new();

            public BlockingTool(string name)
            {
                Name = name;
            }

            public Task StartedTask => _started.Task;
            public string Name { get; }
            public string Description => "blocking tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;

            public void Release(string result)
            {
                _release.TrySetResult(result);
            }

            public MEAI.AIFunction CreateAIFunction()
            {
                Func<CancellationToken, Task<string>> func = _ =>
                {
                    _started.TrySetResult(true);
                    return _release.Task;
                };
                return MEAI.AIFunctionFactory.Create(func,
                    new MEAI.AIFunctionFactoryOptions { Name = Name, Description = Description });
            }
        }

        private sealed class CountingNativeToolClient : MEAI.IChatClient
        {
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
                    yield return new MEAI.ChatResponseUpdate(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent>
                        {
                            new MEAI.FunctionCallContent(
                                "call-quiz", "spawn_quiz", new Dictionary<string, object>())
                        });
                    await Task.Yield();
                }
                else
                {
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

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class ClientStubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public int MaxToolCallRoundtrips => 20;
            public bool AllowDuplicateToolCalls => true;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 1;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls => 4;
            public int DefaultToolTimeoutMs => 150000;
        }

        private sealed class PolicyStubLogger : ILog
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

        private sealed class PolicyStubTool : ILlmTool
        {
            public string Name { get; set; } = "test_tool";
            public string Description => "Test tool";
            public string ParametersSchema { get; set; } = "{}";
            public bool AllowDuplicates { get; set; } = true;

            /// <summary>Per-tool budget; <c>null</c> leaves the tool on the global setting.</summary>
            public int? ToolTimeoutMsOverride { get; set; }
        }

        private sealed class PolicyStubSettings : ICoreAISettings
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
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls { get; set; } = 4;
            public int MaxToolResultChars => 8000;
            public int DefaultToolTimeoutMsValue { get; set; } = 150000;
            public int DefaultToolTimeoutMs => DefaultToolTimeoutMsValue;
        }
    }
}
#endif

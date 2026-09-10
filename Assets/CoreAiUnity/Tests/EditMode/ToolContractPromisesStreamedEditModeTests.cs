#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The same contract promises as <see cref="ToolContractPromisesEditModeTests"/>, but on the
    /// EXECUTE-AS-YOU-STREAM path — the one a streaming endpoint actually takes, and the one where a
    /// broken promise reaches a player first.
    /// <para>
    /// The batch path was guarded for all of this; the streamed path was guarded only for the built-in
    /// mutating NAMES, so a host tool that declares <see cref="ILlmTool.IsMutating"/> had no coverage at
    /// all where it matters most. Every test here is written from the defect's point of view: what the
    /// tool author was promised, and what a player would see if the promise silently stopped holding.
    /// </para>
    /// </summary>
    public sealed class ToolContractPromisesStreamedEditModeTests
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
            public int MaxParallelToolCalls { get; set; } = 4;
            public int MaxToolResultChars => 8000;
            public ILlmAsyncMarshaler ToolInvocationMarshaler => PassThroughLlmAsyncMarshaler.Instance;
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "stub tool description";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates { get; set; }
            public bool IsMutating { get; set; }
        }

        private static ToolExecutionPolicy MakePolicy(StubSettings settings, params ILlmTool[] tools)
        {
            return new ToolExecutionPolicy(NullLog.Instance, settings, tools, false, "Tester");
        }

        private static MEAI.ChatOptions OptionsFor(string name, Delegate body)
        {
            return new MEAI.ChatOptions
            {
                Tools = new List<MEAI.AITool>
                {
                    MEAI.AIFunctionFactory.Create(body,
                        new MEAI.AIFunctionFactoryOptions { Name = name, Description = name })
                }
            };
        }

        private static MEAI.FunctionCallContent Call(string name, int n)
        {
            return new MEAI.FunctionCallContent($"call_{name}_{n}", name,
                new Dictionary<string, object> { ["n"] = n });
        }

        private static MEAI.FunctionCallContent SameCall(string name, string callId)
        {
            return new MEAI.FunctionCallContent(callId, name,
                new Dictionary<string, object> { ["n"] = 1 });
        }

        private static bool IsDuplicateNoOp(MEAI.FunctionResultContent result)
        {
            string text = result?.Result?.ToString() ?? "";
            return text.Contains("\"duplicate\":true") && text.Contains("\"ok\":true");
        }

        // ==================== Defect 4: mutation ordering is a TOOL property ====================

        /// <summary>
        /// A host tool that declares <see cref="ILlmTool.IsMutating"/> must be serialized on the streamed
        /// path exactly like a built-in mutating name. The list of built-in names is private to the
        /// package, so before the flag existed a host's own save/spawn tool ran up to
        /// <c>MaxParallelToolCalls</c> at a time and lost writes — and the streamed path is the one a
        /// streaming endpoint takes.
        /// </summary>
        [Test]
        public async Task StreamedTurn_DeclaredMutatingTool_IsSerialized_WithoutBeingABuiltIn()
        {
            int active = 0;
            bool overlapped = false;
            object gate = new();
            Func<int, CancellationToken, Task<string>> body = async (n, ct) =>
            {
                lock (gate)
                {
                    active++;
                    overlapped |= active > 1;
                }

                await Task.Delay(40, ct);
                lock (gate)
                {
                    active--;
                }

                return "ok";
            };

            StubSettings settings = new() { MaxParallelToolCalls = 4 };
            ToolExecutionPolicy policy = MakePolicy(settings,
                new StubTool("save_progress") { IsMutating = true });
            MEAI.ChatOptions options = OptionsFor("save_progress", body);

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            for (int n = 1; n <= 3; n++)
            {
                await policy.ExecuteStreamedAsync(turn, Call("save_progress", n), options,
                    CancellationToken.None);
            }

            ToolExecutionPolicy.BatchToolCallResult result =
                await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Assert.IsFalse(result.AnyFailed);
            Assert.AreEqual(3, result.Results.Count);
            Assert.IsFalse(overlapped,
                "A tool that declares IsMutating must never overlap another mutating call, streamed or batched.");
        }

        /// <summary>
        /// The other half of the same promise: the flag is opt-in, so an undeclared (read-only) tool keeps
        /// its parallelism. A guard that only proves serialization would still pass if the policy
        /// serialized everything and quietly took the throughput away.
        /// </summary>
        [Test]
        public async Task StreamedTurn_UndeclaredTool_StillRunsInParallel()
        {
            int active = 0;
            int maxActive = 0;
            object gate = new();
            TaskCompletionSource<bool> bothActive = new();
            Func<int, CancellationToken, Task<string>> body = async (n, ct) =>
            {
                lock (gate)
                {
                    active++;
                    maxActive = Math.Max(maxActive, active);
                    if (active >= 2)
                    {
                        bothActive.TrySetResult(true);
                    }
                }

                // Resolves immediately once both calls overlap; the delay is only the failure budget.
                await Task.WhenAny(bothActive.Task, Task.Delay(2000, ct));
                lock (gate)
                {
                    active--;
                }

                return "ok";
            };

            StubSettings settings = new() { MaxParallelToolCalls = 4 };
            ToolExecutionPolicy policy = MakePolicy(settings, new StubTool("lookup"));
            MEAI.ChatOptions options = OptionsFor("lookup", body);

            ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(turn, Call("lookup", 1), options, CancellationToken.None);
            await policy.ExecuteStreamedAsync(turn, Call("lookup", 2), options, CancellationToken.None);
            await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

            Assert.AreEqual(2, maxActive,
                "Undeclared tools are read-only by contract and must overlap under MaxParallelToolCalls > 1.");
        }

        // ==================== Defect 3: AllowDuplicates is the author's decision ====================

        /// <summary>
        /// <see cref="ILlmTool.AllowDuplicates"/> must hold for a built-in MUTATING name too. The check
        /// used to read "flag AND the name is not one of the serialized mutating ones", so "run that
        /// snippet again" on <c>execute_lua</c> came back as a skipped duplicate although the author had
        /// explicitly declared repeats meaningful.
        /// <para>
        /// The repeat has to cross a TURN boundary to test anything: repeats inside one turn execute
        /// regardless of the flag, which is why an intra-turn test passes even with the flag ignored.
        /// </para>
        /// </summary>
        [Test]
        public async Task StreamedTurn_AllowDuplicatesMutatingBuiltIn_CrossTurnRepeatStillExecutes()
        {
            int invocations = 0;
            // The parameter is named 'n' because MEAI binds arguments BY NAME: renaming it to '_' makes
            // every call fail to bind, which would look exactly like a suppressed duplicate here.
            Func<int, string> body = n =>
            {
                invocations++;
                return "ran";
            };

            StubSettings settings = new() { MaxParallelToolCalls = 1 };
            ToolExecutionPolicy policy = MakePolicy(settings,
                new StubTool("execute_lua") { AllowDuplicates = true });
            MEAI.ChatOptions options = OptionsFor("execute_lua", body);

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, SameCall("execute_lua", "c1"), options,
                CancellationToken.None);
            await policy.CompleteStreamedTurnAsync(first, CancellationToken.None);

            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? repeat = await policy.ExecuteStreamedAsync(
                second, SameCall("execute_lua", "c2"), options, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult turn =
                await policy.CompleteStreamedTurnAsync(second, CancellationToken.None);

            Assert.AreEqual(2, invocations,
                "AllowDuplicates must be honoured for a mutating built-in name too.");
            Assert.IsTrue(repeat.HasValue && repeat.Value.Succeeded);
            Assert.IsFalse(turn.AllDuplicates);
            Assert.AreEqual("ran", ((MEAI.FunctionResultContent)turn.Results[0]).Result.ToString(),
                "The author asked for a real re-run, so the model must get the real result, not a no-op.");
        }

        // ==================== Defect 2: the echo key is per CALL, not per turn ====================

        /// <summary>
        /// Turn 1 = <c>[A]</c>, turn 2 = <c>[A, B]</c>. With a batch-wide signature the second turn hashed
        /// differently and <c>A</c> ran a SECOND time — the exact double mutation the docs promise an
        /// author never has to defend against with their own idempotency key. Per-call keys suppress
        /// <c>A</c> and still run <c>B</c>.
        /// </summary>
        [Test]
        public async Task StreamedTurn_MixedTurn_SuppressesOnlyTheEchoedCall()
        {
            List<int> executed = new();
            Func<int, string> body = n =>
            {
                executed.Add(n);
                return "ok";
            };

            StubSettings settings = new() { MaxParallelToolCalls = 1 };
            ToolExecutionPolicy policy = MakePolicy(settings, new StubTool("spawn"));
            MEAI.ChatOptions options = OptionsFor("spawn", body);

            ToolExecutionPolicy.StreamedTurn first = policy.BeginStreamedTurn();
            await policy.ExecuteStreamedAsync(first, Call("spawn", 1), options, CancellationToken.None);
            await policy.CompleteStreamedTurnAsync(first, CancellationToken.None);

            ToolExecutionPolicy.StreamedTurn second = policy.BeginStreamedTurn();
            ToolExecutionPolicy.ToolCallResult? echo = await policy.ExecuteStreamedAsync(
                second, Call("spawn", 1), options, CancellationToken.None);
            await policy.ExecuteStreamedAsync(second, Call("spawn", 2), options, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult turn =
                await policy.CompleteStreamedTurnAsync(second, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2 }, executed,
                "The echoed call must not run again, and the new call must not be blocked by it.");
            Assert.IsTrue(echo.HasValue && echo.Value.Succeeded, "An echo is a no-op, not a failure.");
            Assert.IsTrue(IsDuplicateNoOp(echo.Value.Result));
            Assert.IsFalse(turn.AllDuplicates, "One call really executed, so the turn is progress.");
            Assert.IsFalse(turn.AnyFailed);
            Assert.AreEqual(0, policy.ConsecutiveErrors,
                "A mixed echo/new turn is progress and must leave the abort counter at zero.");
        }

        // ==================== Defect 1: an echo-only turn is not a failed iteration ====================

        /// <summary>
        /// "Show me that again" with identical arguments, three streamed turns in a row. The suppressed
        /// slots used to be recorded as failures, so the third one hit <c>maxConsecutiveErrors</c> and the
        /// user got an abort message instead of an answer.
        /// </summary>
        [Test]
        public async Task StreamedTurn_RepeatedEchoOnlyTurns_NeverReachTheAbortThreshold()
        {
            int invocations = 0;
            Func<int, string> body = n =>
            {
                invocations++;
                return "shown";
            };

            StubSettings settings = new() { MaxParallelToolCalls = 1 };
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new ILlmTool[] { new StubTool("show_card") }, false, "Tester", 3);
            MEAI.ChatOptions options = OptionsFor("show_card", body);

            for (int attempt = 0; attempt < 4; attempt++)
            {
                ToolExecutionPolicy.StreamedTurn turn = policy.BeginStreamedTurn();
                await policy.ExecuteStreamedAsync(turn, SameCall("show_card", $"c{attempt}"), options,
                    CancellationToken.None);
                ToolExecutionPolicy.BatchToolCallResult result =
                    await policy.CompleteStreamedTurnAsync(turn, CancellationToken.None);

                Assert.IsFalse(result.AnyFailed, $"Turn {attempt} must not report a failure.");
                Assert.IsFalse(policy.IsMaxErrorsReached,
                    $"Turn {attempt} must not push the agent toward the max-errors abort.");
            }

            Assert.AreEqual(1, invocations, "Only the first turn may execute the tool.");
            Assert.AreEqual(0, policy.ConsecutiveErrors);
            Assert.IsTrue(policy.ExecutedTraces.Where(t => t.Source == "duplicate").All(t => t.Success),
                "A suppressed echo is traced as a SUCCESS: it is a no-op, not a failed call.");
        }
    }
}
#endif

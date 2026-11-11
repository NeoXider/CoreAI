using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// A scripted <see cref="IAiTaskResultService"/>: records every sub-agent request and the token it ran with,
    /// answers at once, holds runs until a test completes them, or runs a test-supplied body.
    /// </summary>
    internal sealed class ScriptedSubAgentRunner : IAiTaskResultService
    {
        private readonly object _lock = new();
        private int _active;

        public List<AiTaskRequest> Requests { get; } = new();

        public List<CancellationToken> Tokens { get; } = new();

        public List<TaskCompletionSource<LlmCompletionResult>> Held { get; } = new();

        public bool HoldRuns { get; set; }

        public Func<AiTaskRequest, CancellationToken, Task<LlmCompletionResult>> OnRun { get; set; }

        public int MaxActive { get; private set; }

        public int Active
        {
            get
            {
                lock (_lock)
                {
                    return _active;
                }
            }
        }

        public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Requests.Add(task);
                Tokens.Add(cancellationToken);
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
            }

            Task<LlmCompletionResult> work;
            if (OnRun != null)
            {
                work = OnRun(task, cancellationToken);
            }
            else if (HoldRuns)
            {
                TaskCompletionSource<LlmCompletionResult> held = new(TaskCreationOptions.None);
                cancellationToken.Register(() => held.TrySetCanceled(cancellationToken));
                lock (_lock)
                {
                    Held.Add(held);
                }

                work = held.Task;
            }
            else
            {
                work = Task.FromResult(SubAgentTestSupport.Answer("answer to: " + task.Hint));
            }

            return TrackAsync(work);
        }

        private async Task<LlmCompletionResult> TrackAsync(Task<LlmCompletionResult> work)
        {
            try
            {
                return await work;
            }
            finally
            {
                lock (_lock)
                {
                    _active--;
                }
            }
        }
    }

    /// <summary>A host delay a test elapses by hand, recording every requested delay and its token.</summary>
    internal sealed class ManualDelayMarshaler : ILlmAsyncMarshaler
    {
        public List<(int Milliseconds, TaskCompletionSource<bool> Source, CancellationToken Token)> Delays { get; } =
            new();

        public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
        {
            return factory();
        }

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> source = new(TaskCreationOptions.None);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            Delays.Add((milliseconds, source, cancellationToken));
            return source.Task;
        }

        public void ElapseAll()
        {
            foreach ((int Milliseconds, TaskCompletionSource<bool> Source, CancellationToken Token) delay in
                     Delays.ToArray())
            {
                delay.Source.TrySetResult(true);
            }
        }
    }

    /// <summary>Shared builders for the sub-agent fixtures.</summary>
    internal static class SubAgentTestSupport
    {
        public const string LeadTrace = "lead-trace";
        public const string LeadScope = "lead-scope";
        public const string ActorId = "player-1";

        public static LlmCompletionResult Answer(string content, int? totalTokens = null)
        {
            return new LlmCompletionResult { Ok = true, Content = content, TotalTokens = totalTokens };
        }

        public static SubAgentDefinition Builder(
            int maxResultChars = SubAgentDefinition.DefaultMaxResultChars,
            TimeSpan? deadline = null)
        {
            return new SubAgentDefinition(
                "builder",
                "Builder",
                "Builds structures in the world.",
                new[] { "world_command" },
                routingProfileId: "fast",
                maxOutputTokens: 512,
                maxToolCallRoundtrips: 6,
                maxResultChars: maxResultChars,
                deadline: deadline,
                instructions: "Report what you built.");
        }

        public static SubAgentDefinition Scripter()
        {
            return new SubAgentDefinition("scripter", "Programmer", "Writes game logic in Lua.",
                new[] { "execute_lua", "get_mod_logs" });
        }

        public static ActorContext Actor()
        {
            return new LocalActorIdentityProvider(ActorId).GetActorContext(BuiltInAgentRoleIds.Creator);
        }

        public static SubAgentParentContext Parent(string trace = LeadTrace, string scope = LeadScope)
        {
            return new SubAgentParentContext(BuiltInAgentRoleIds.Creator, trace, scope, Actor(), 3);
        }

        public static OrchestratorSession Session(
            IAiTaskResultService runner,
            OrchestratorModeOptions options = null,
            ILlmAsyncMarshaler marshaler = null,
            params SubAgentDefinition[] definitions)
        {
            SubAgentDefinition[] resolved = definitions == null || definitions.Length == 0
                ? new[] { Builder(), Scripter() }
                : definitions;
            return new OrchestratorSession("conversation-1", options ?? new OrchestratorModeOptions(),
                new SubAgentRegistry(resolved), runner, marshaler);
        }

        public static Task<SubAgentDelegationResult> Delegate(
            OrchestratorSession session,
            string task,
            string subAgent = "builder",
            string context = "",
            string taskId = "",
            CancellationToken cancellationToken = default)
        {
            return session.Delegator.DelegateAsync(new SubAgentDelegationRequest
            {
                SubAgentId = subAgent,
                Task = task,
                Context = context,
                TaskId = taskId
            }, cancellationToken);
        }

        /// <summary>
        /// Awaits <paramref name="action"/> and returns the exception it must throw. Used instead of
        /// <c>Assert.ThrowsAsync</c>, which blocks the calling thread and deadlocks under the editor's
        /// synchronization context.
        /// </summary>
        public static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException expected)
            {
                return expected;
            }

            NUnit.Framework.Assert.Fail(
                "Expected " + typeof(TException).Name + ", but the operation completed without throwing.");
            return null;
        }

        /// <summary>Yields until <paramref name="condition"/> holds; the bound turns a missed wake-up into a failure.</summary>
        public static async Task<bool> EventuallyAsync(Func<bool> condition, int maxYields = 500)
        {
            for (int i = 0; i < maxYields; i++)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Yield();
            }

            return condition();
        }

        /// <summary>The text of an AIFunction result, whether the binder returned a string or a JSON string element.</summary>
        public static string AsText(object raw)
        {
            if (raw is JsonElement element)
            {
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }

            return raw?.ToString() ?? "";
        }
    }
}

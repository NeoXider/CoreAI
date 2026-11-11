using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.ChatOverlay;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>A manual clock for the NPC block.</summary>
    internal sealed class ManualNpcClock : INpcClock
    {
        public double Now;

        public double NowSeconds => Now;
    }

    /// <summary>What the scripted orchestrator does with one turn.</summary>
    internal sealed class ScriptedNpcTurn
    {
        public string[] Chunks = { "Hello there." };

        public LlmErrorCode FailWith = LlmErrorCode.None;

        public string FailText = "";

        public Exception Throw;

        /// <summary>When set, the turn waits for it (or for cancellation) before producing anything.</summary>
        public TaskCompletionSource<bool> Gate;

        public static ScriptedNpcTurn Reply(string text)
        {
            return new ScriptedNpcTurn { Chunks = new[] { text } };
        }

        public static ScriptedNpcTurn Fail(LlmErrorCode code, string providerText)
        {
            return new ScriptedNpcTurn { Chunks = Array.Empty<string>(), FailWith = code, FailText = providerText };
        }

        public static ScriptedNpcTurn Held()
        {
            return new ScriptedNpcTurn { Chunks = new[] { "Held reply." }, Gate = new TaskCompletionSource<bool>() };
        }
    }

    /// <summary>
    /// A scripted orchestration service: records every request and answers by <see cref="Script"/>. Without a gate
    /// it completes synchronously, so a director call sees its own result before it returns.
    /// </summary>
    internal sealed class ScriptedNpcOrchestrator : IAiOrchestrationService, IAiTaskResultService
    {
        public readonly List<AiTaskRequest> Requests = new();

        public readonly List<CancellationToken> Tokens = new();

        public Func<AiTaskRequest, ScriptedNpcTurn> Script = _ => ScriptedNpcTurn.Reply("Hello there.");

        public int StreamingCalls;

        public int TypedCalls;

        public bool SupportsTaskResults => true;

        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("NPC turns must use the typed or streaming path.");
        }

        public async Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            TypedCalls++;
            ScriptedNpcTurn turn = Record(task, cancellationToken);
            if (turn.Throw != null)
            {
                throw turn.Throw;
            }

            if (turn.Gate != null)
            {
                await WaitGate(turn.Gate, cancellationToken);
            }

            if (turn.FailWith != LlmErrorCode.None)
            {
                return new LlmCompletionResult { Ok = false, Error = turn.FailText, ErrorCode = turn.FailWith };
            }

            return new LlmCompletionResult { Ok = true, Content = string.Join("", turn.Chunks), Model = "scripted" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            ScriptedNpcTurn turn = Record(task, cancellationToken);
            if (turn.Throw != null)
            {
                throw turn.Throw;
            }

            if (turn.Gate != null)
            {
                await WaitGate(turn.Gate, cancellationToken);
            }

            foreach (string chunk in turn.Chunks)
            {
                yield return new LlmStreamChunk { Text = chunk, Model = "scripted" };
            }

            if (turn.FailWith != LlmErrorCode.None)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = turn.FailText, ErrorCode = turn.FailWith };
                yield break;
            }

            yield return new LlmStreamChunk { IsDone = true, Model = "scripted" };
        }

        public void CancelTasks(string cancellationScope)
        {
        }

        private ScriptedNpcTurn Record(AiTaskRequest task, CancellationToken token)
        {
            Requests.Add(task);
            Tokens.Add(token);
            return Script(task) ?? ScriptedNpcTurn.Reply("Hello there.");
        }

        private static async Task WaitGate(TaskCompletionSource<bool> gate, CancellationToken token)
        {
            TaskCompletionSource<bool> cancelled = new();
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                Task finished = await Task.WhenAny(gate.Task, cancelled.Task);
                if (!ReferenceEquals(finished, gate.Task))
                {
                    throw new OperationCanceledException(token);
                }
            }
        }
    }

    /// <summary>Records every sink call as a short line.</summary>
    internal sealed class RecordingNpcSink : INpcSpeechSink
    {
        public readonly List<string> Calls = new();

        public readonly List<string> Completed = new();

        public void SpeechStarted(NpcSpeech speech)
        {
            Calls.Add("start:" + speech.NpcId + ":" + speech.TurnId);
        }

        public void SpeechGrew(NpcSpeech speech, string textSoFar)
        {
            Calls.Add("grew:" + speech.NpcId + ":" + textSoFar);
        }

        public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
        {
            Calls.Add("done:" + speech.NpcId + ":" + (isFallback ? "fallback:" : "") + text);
            Completed.Add(speech.NpcId + ":" + text);
        }

        public void SpeechCancelled(NpcSpeech speech)
        {
            Calls.Add("cancel:" + speech.NpcId + ":" + speech.TurnId);
        }
    }

    /// <summary>Captures what the chat overlay was asked to post.</summary>
    internal sealed class RecordingChatMessages : IChatOverlayMessages
    {
        public readonly List<string> Lines = new();

        public void PostSystemMessage(string text)
        {
            Lines.Add("|" + text);
        }

        public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
        {
            Lines.Add(speaker + "|" + text + "|" + kind);
        }
    }

    /// <summary>Builders shared by the NPC fixtures.</summary>
    internal static class NpcTestWorld
    {
        public static NpcAgentDefinition Npc(string id, string name = null, NpcPosition? position = null,
            params string[] fallbackLines)
        {
            NpcAgentDefinition definition = new()
            {
                Persona = new NpcPersona
                {
                    Id = id,
                    DisplayName = name ?? id,
                    PromptFragment = "A villager named " + (name ?? id) + ".",
                    VoiceRules = "Short, warm sentences.",
                    FallbackLines = fallbackLines ?? Array.Empty<string>()
                },
                Budget = new NpcBudget { CooldownSeconds = 0d }
            };
            if (position.HasValue)
            {
                NpcPosition fixedPosition = position.Value;
                definition.PositionSource = () => fixedPosition;
            }

            return definition;
        }

        public static NpcDirector Director(ScriptedNpcOrchestrator orchestrator, INpcSpeechSink sink,
            ManualNpcClock clock, NpcDirectorOptions options = null)
        {
            return new NpcDirector(orchestrator, sink, options ?? new NpcDirectorOptions { UseStreaming = true },
                clock);
        }
    }
}

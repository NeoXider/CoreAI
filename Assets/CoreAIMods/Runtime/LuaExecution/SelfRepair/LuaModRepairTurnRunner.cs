using System;
using System.Threading;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using Cysharp.Threading.Tasks;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>One repair turn the loop asks a runner to start.</summary>
    public sealed class LuaModRepairTurnRequest
    {
        /// <summary>The mod being repaired.</summary>
        public string ModId { get; set; } = "";

        /// <summary>1-based attempt number.</summary>
        public int Attempt { get; set; }

        /// <summary>Attempts the incident has.</summary>
        public int MaxAttempts { get; set; }

        /// <summary>The role that maintains mods.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.Programmer;

        /// <summary>The repair request for the model.</summary>
        public string Text { get; set; } = "";

        /// <summary>The line the player sees when the turn starts.</summary>
        public string Announcement { get; set; } = "";

        /// <summary>Correlation id of the turn.</summary>
        public string TraceId { get; set; } = "";
    }

    /// <summary>A running repair turn.</summary>
    public interface ILuaModRepairTurnHandle
    {
        /// <summary>True until the turn ended.</summary>
        bool IsRunning { get; }

        /// <summary>Stops the turn; its end is then reported as cancelled (or nothing, if it already ended).</summary>
        void Cancel();
    }

    /// <summary>
    /// Runs the model turns of the repair loop. The default runs them in the in-world chat as host turns
    /// (<see cref="ChatOverlayLuaModRepairTurnRunner"/>); without a chat the orchestrator runs them directly
    /// (<see cref="OrchestratorLuaModRepairTurnRunner"/>); a test runs a scripted model.
    /// </summary>
    public interface ILuaModRepairTurnRunner
    {
        /// <summary>False when no model answers (offline backend, no AI service): the loop never starts a turn.</summary>
        bool IsModelAvailable { get; }

        /// <summary>True while another turn holds the runner (the player's own chat turn); the repair waits.</summary>
        bool IsBusy { get; }

        /// <summary>
        /// Starts a turn and reports its end exactly once through <paramref name="ended"/>, on any thread, with the
        /// failure sentence when it did not complete; null when it cannot start now.
        /// </summary>
        ILuaModRepairTurnHandle TryStart(LuaModRepairTurnRequest request, Action<LuaModRepairTurnEnd, string> ended);
    }

    /// <summary>Maps chat and pipeline failures onto the loop's turn ends.</summary>
    public static class LuaModRepairTurnEnds
    {
        /// <summary>True for failures no retry of the same request fixes now: the model is effectively not there.</summary>
        public static bool MeansUnavailable(LlmErrorCode code)
        {
            return code == LlmErrorCode.RoutingError || code == LlmErrorCode.BackendUnavailable ||
                   code == LlmErrorCode.AuthExpired || code == LlmErrorCode.PaymentRequired ||
                   code == LlmErrorCode.QuotaExceeded;
        }

        /// <summary>The loop's end for a host turn of the chat.</summary>
        public static LuaModRepairTurnEnd From(ChatOverlayHostTurnResult result)
        {
            switch (result.End)
            {
                case ChatOverlayHostTurnEnd.Completed:
                    return LuaModRepairTurnEnd.Completed;
                case ChatOverlayHostTurnEnd.Cancelled:
                    return LuaModRepairTurnEnd.Cancelled;
                case ChatOverlayHostTurnEnd.TimedOut:
                    return LuaModRepairTurnEnd.TimedOut;
                default:
                    return MeansUnavailable(result.ErrorCode) ? LuaModRepairTurnEnd.Unavailable : LuaModRepairTurnEnd.Failed;
            }
        }
    }

    /// <summary>
    /// Runs repair turns as host turns of the in-world chat (<see cref="ChatOverlayController.TryStartHostTurn"/>):
    /// the status line shows what the agent does, <c>/stop</c> stops it, the player's own line waits behind it with the
    /// busy notice, and the model's answer stays out of the feed (the loop posts one line per outcome itself). The turn
    /// goes through the chat's sender, so it reaches the same role, history and tools as a typed line.
    /// </summary>
    public sealed class ChatOverlayLuaModRepairTurnRunner : ILuaModRepairTurnRunner
    {
        private readonly ChatOverlayController _chat;
        private readonly Func<bool> _modelAvailable;

        /// <param name="chat">The in-world chat.</param>
        /// <param name="modelAvailable">False while no model is configured (the Offline backend); null means available.</param>
        public ChatOverlayLuaModRepairTurnRunner(ChatOverlayController chat, Func<bool> modelAvailable = null)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _modelAvailable = modelAvailable;
        }

        /// <inheritdoc />
        public bool IsModelAvailable => _modelAvailable == null || _modelAvailable();

        /// <inheritdoc />
        public bool IsBusy => _chat.IsTurnActive;

        /// <inheritdoc />
        public ILuaModRepairTurnHandle TryStart(LuaModRepairTurnRequest request, Action<LuaModRepairTurnEnd, string> ended)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string traceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId;
            ChatOverlayHostTurnRequest hostTurn = new()
            {
                Text = request.Text,
                RoleId = request.RoleId,
                TraceId = traceId,
                Announcement = request.Announcement,
                ShowAnswer = false,
                Ended = result => ended?.Invoke(LuaModRepairTurnEnds.From(result), result.Message)
            };

            return _chat.TryStartHostTurn(hostTurn) ? new Handle(_chat, traceId) : null;
        }

        private sealed class Handle : ILuaModRepairTurnHandle
        {
            private readonly ChatOverlayController _chat;
            private readonly string _traceId;

            public Handle(ChatOverlayController chat, string traceId)
            {
                _chat = chat;
                _traceId = traceId;
            }

            public bool IsRunning => string.Equals(_chat.ActiveTraceId, _traceId, StringComparison.Ordinal);

            public void Cancel()
            {
                if (IsRunning)
                {
                    _chat.StopActiveTurn();
                }
            }
        }
    }

    /// <summary>
    /// Runs repair turns straight through the orchestrator, for a host without the in-world chat: the same role, actor
    /// and tools as a chat turn, streamed, with no stop control beyond the loop's own timeout.
    /// </summary>
    public sealed class OrchestratorLuaModRepairTurnRunner : ILuaModRepairTurnRunner
    {
        /// <summary>Source tag of the repair turns.</summary>
        public const string SourceTag = "lua_mod_runtime_repair";

        private readonly IAiOrchestrationService _orchestrator;
        private readonly Func<string, ActorContext> _actorForRole;
        private readonly Func<bool> _modelAvailable;
        private int _running;

        /// <param name="orchestrator">The orchestration service.</param>
        /// <param name="actorForRole">The trusted actor of a role, the one chat turns run as.</param>
        /// <param name="modelAvailable">False while no model is configured; null means available.</param>
        public OrchestratorLuaModRepairTurnRunner(IAiOrchestrationService orchestrator,
            Func<string, ActorContext> actorForRole, Func<bool> modelAvailable = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _actorForRole = actorForRole ?? throw new ArgumentNullException(nameof(actorForRole));
            _modelAvailable = modelAvailable;
        }

        /// <inheritdoc />
        public bool IsModelAvailable => _modelAvailable == null || _modelAvailable();

        /// <inheritdoc />
        public bool IsBusy => Volatile.Read(ref _running) != 0;

        /// <inheritdoc />
        public ILuaModRepairTurnHandle TryStart(LuaModRepairTurnRequest request, Action<LuaModRepairTurnEnd, string> ended)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return null;
            }

            ActorContext actor = _actorForRole(request.RoleId);
            AiTaskRequest task = new()
            {
                RoleId = request.RoleId,
                Hint = request.Text,
                SourceTag = SourceTag,
                ActorContext = actor,
                CancellationScope = actor.SessionId,
                TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId
            };
            Handle handle = new();
            RunAsync(task, handle, ended).Forget();
            return handle;
        }

        private async UniTaskVoid RunAsync(AiTaskRequest task, Handle handle, Action<LuaModRepairTurnEnd, string> ended)
        {
            LuaModRepairTurnEnd end = LuaModRepairTurnEnd.Completed;
            string message = "";
            try
            {
                await foreach (LlmStreamChunk chunk in _orchestrator.RunStreamingAsync(task, handle.Token))
                {
                    if (chunk != null && (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None))
                    {
                        LlmErrorCode code = chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode;
                        end = code == LlmErrorCode.Cancelled
                            ? LuaModRepairTurnEnd.Cancelled
                            : LuaModRepairTurnEnds.MeansUnavailable(code) ? LuaModRepairTurnEnd.Unavailable : LuaModRepairTurnEnd.Failed;
                        message = LlmErrorPresentation.ForErrorCode(code);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                end = LuaModRepairTurnEnd.Cancelled;
            }
            catch (Exception ex)
            {
                end = LuaModRepairTurnEnd.Failed;
                message = LlmErrorPresentation.ToUserMessage(ex);
            }
            finally
            {
                handle.Finish();
                Volatile.Write(ref _running, 0);
            }

            ended?.Invoke(end, message);
        }

        private sealed class Handle : ILuaModRepairTurnHandle
        {
            private readonly CancellationTokenSource _source = new();
            private int _running = 1;

            public CancellationToken Token => _source.Token;

            public bool IsRunning => Volatile.Read(ref _running) != 0;

            public void Cancel()
            {
                if (IsRunning)
                {
                    _source.Cancel();
                }
            }

            public void Finish()
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }
}

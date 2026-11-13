using System;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Default <see cref="IChatOverlayTurnSender"/>: sends through <see cref="CoreAiChatService"/>, the service the
    /// chat panel uses, so both surfaces share the role's history and the orchestrator. Streams when the role and
    /// settings allow it, otherwise waits for the whole answer. Every observer call is made on the Unity main
    /// thread; nothing blocks, so it runs in a WebGL player.
    /// </summary>
    public sealed class CoreAiChatServiceTurnSender : IChatOverlayTurnSender
    {
        /// <summary>Source tag of the overlay's requests; the same as the chat panel's, so the pipeline treats them alike.</summary>
        public const string SourceTag = "Chat";

        private readonly CoreAiChatService _service;
        private readonly IActorIdentityProvider _identity;
        private readonly IGameLogger _logger;
        private readonly bool _enableCameraTool;

        /// <summary>
        /// Creates the sender. A null <paramref name="service"/> makes it unavailable (the chat says the AI is not
        /// connected); a null <paramref name="identity"/> uses the default local host identity.
        /// </summary>
        public CoreAiChatServiceTurnSender(CoreAiChatService service, IActorIdentityProvider identity = null,
            IGameLogger logger = null, bool enableCameraTool = true)
        {
            _service = service;
            _identity = identity ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider;
            _logger = logger;
            _enableCameraTool = enableCameraTool;
        }

        /// <inheritdoc />
        public bool IsAvailable => _service != null;

        /// <inheritdoc />
        public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            Turn turn = new();
            if (_service == null)
            {
                observer.OnFailed(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                    LlmErrorPresentation.ForErrorCode(LlmErrorCode.RoutingError), LlmErrorCode.RoutingError));
                turn.Dispose();
                return turn;
            }

            RunAsync(request, observer, turn).Forget();
            return turn;
        }

        /// <inheritdoc />
        public void StopAgent(string roleId)
        {
            if (_service != null && !string.IsNullOrWhiteSpace(roleId))
            {
                _service.StopAgent(roleId);
            }
        }

        /// <summary>The request the overlay sends for <paramref name="request"/>, built like the chat panel's.</summary>
        internal AiTaskRequest BuildTaskRequest(ChatOverlayTurnRequest request)
        {
            string roleId = string.IsNullOrWhiteSpace(request.RoleId)
                ? CoreAiChatOptions.DefaultRoleId
                : request.RoleId.Trim();
            ActorContext actor = _identity.GetActorContext(roleId);
            return new AiTaskRequest
            {
                RoleId = roleId,
                RoutingProfileId = request.RoutingProfileId ?? "",
                Hint = request.Text ?? "",
                SourceTag = SourceTag,
                ActorContext = actor,
                CancellationScope = actor.SessionId,
                TraceId = request.TraceId ?? "",
                HistoryMode = request.HistoryMode,
                Inbox = request.Inbox
            };
        }

        private async UniTaskVoid RunAsync(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer, Turn turn)
        {
            CancellationToken token = turn.Token;
            try
            {
                AiTaskRequest task = BuildTaskRequest(request);
                if (_enableCameraTool)
                {
                    _service.TryEnsureCameraToolForRole(task.RoleId, true);
                }

                if (_service.IsStreamingEnabled(task.RoleId, request.StreamingOverride))
                {
                    await foreach (LlmStreamChunk chunk in _service.SendMessageStreamingAsync(task, token))
                    {
                        await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                        observer.OnChunk(chunk);
                    }
                }
                else if (_service.SupportsTaskResults)
                {
                    LlmCompletionResult result = await _service.SendMessageResultAsync(task, token);
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                    if (result == null || !result.Ok)
                    {
                        LlmErrorCode code = result == null ? LlmErrorCode.EmptyResponse : result.ErrorCode;
                        observer.OnFailed(ChatOverlayTurnFailure.FromErrorCode(
                            LlmCancellation.ClassifyCode(code, token), result?.RetryAfterSeconds));
                        return;
                    }

                    observer.OnChunk(new LlmStreamChunk { Text = result.Content ?? "", IsDone = true });
                }
                else
                {
                    string text = await _service.SendMessageAsync(task, token);
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                    observer.OnChunk(new LlmStreamChunk { Text = text ?? "", IsDone = true });
                }

                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                observer.OnCompleted();
            }
            catch (OperationCanceledException ex)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                LlmErrorCode code = LlmCancellation.Classify(ex, token);
                observer.OnFailed(code == LlmErrorCode.Timeout
                    ? new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Timeout,
                        LlmErrorPresentation.ForErrorCode(LlmErrorCode.Timeout), LlmErrorCode.Timeout, ex)
                    : new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Cancelled,
                        LlmErrorPresentation.ForErrorCode(LlmErrorCode.Cancelled), LlmErrorCode.Cancelled, ex));
            }
            catch (Exception ex)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                (_logger ?? GameLoggerUnscopedFallback.Instance).LogError(GameLogFeature.Llm,
                    $"[CoreAiChatOverlay] Turn failed: {LlmErrorPresentation.ToDiagnosticText(ex)} | {ex}");
                LlmErrorCode code = ex is LlmClientException typed ? typed.ErrorCode : LlmErrorCode.ProviderError;
                observer.OnFailed(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                    LlmErrorPresentation.ToUserMessage(ex), code, ex));
            }
            finally
            {
                turn.Dispose();
            }
        }

        /// <summary>One request's cancellation; cancelling after the request ended does nothing.</summary>
        private sealed class Turn : IChatOverlayTurn, IDisposable
        {
            private CancellationTokenSource _source = new();

            public Turn()
            {
                Token = _source.Token;
            }

            public CancellationToken Token { get; }

            public void Cancel()
            {
                _source?.Cancel();
            }

            public void Dispose()
            {
                CancellationTokenSource source = _source;
                _source = null;
                source?.Dispose();
            }
        }
    }
}

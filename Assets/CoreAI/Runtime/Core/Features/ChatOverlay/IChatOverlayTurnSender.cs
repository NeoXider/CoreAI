using System;
using CoreAI.Ai;

namespace CoreAI.ChatOverlay
{
    /// <summary>One message the in-world chat sends to the model.</summary>
    public sealed class ChatOverlayTurnRequest
    {
        /// <summary>The player's text.</summary>
        public string Text { get; set; } = "";

        /// <summary>Agent role that answers.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Correlation id the tool-call events of this turn carry; the chat uses it to follow its own turn.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>False forces a buffered answer; null follows the role and global streaming settings.</summary>
        public bool? StreamingOverride { get; set; }

        /// <summary>Routing profile for this turn only; empty uses the role's assignment.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Whether the turn reads and extends the role's chat history (<see cref="AiTaskRequest.HistoryMode"/>).</summary>
        public AiTaskHistoryMode HistoryMode { get; set; }
    }

    /// <summary>
    /// Receives one turn's progress. The sender calls it on the thread that owns the chat (the Unity main thread)
    /// and calls exactly one of <see cref="OnCompleted"/> or <see cref="OnFailed"/> at the end.
    /// </summary>
    public interface IChatOverlayTurnObserver
    {
        /// <summary>A streamed piece of the answer, a progress hint, or a terminal chunk that may carry a failure.</summary>
        void OnChunk(LlmStreamChunk chunk);

        /// <summary>The answer is complete.</summary>
        void OnCompleted();

        /// <summary>The turn ended without a complete answer.</summary>
        void OnFailed(ChatOverlayTurnFailure failure);
    }

    /// <summary>A running turn the chat can stop.</summary>
    public interface IChatOverlayTurn
    {
        /// <summary>Cancels the request; the observer then gets a cancelled failure (or nothing, if it already ended).</summary>
        void Cancel();
    }

    /// <summary>
    /// Sends the chat's messages to an agent. The default sends through <c>CoreAiChatService</c>, the same
    /// service the chat panel uses; a game can replace it to route the chat anywhere.
    /// </summary>
    public interface IChatOverlayTurnSender
    {
        /// <summary>False when no AI service is connected; the chat then answers locally.</summary>
        bool IsAvailable { get; }

        /// <summary>Starts a turn and reports it to <paramref name="observer"/>; never throws for a model failure.</summary>
        IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer);

        /// <summary>Asks the agent pipeline to stop the role's work beyond this chat's own request.</summary>
        void StopAgent(string roleId);
    }

    /// <summary>How a turn ended without an answer.</summary>
    public enum ChatOverlayTurnFailureKind
    {
        /// <summary>A provider, transport or pipeline failure.</summary>
        Error = 0,

        /// <summary>The turn made no progress within its deadline.</summary>
        Timeout = 1,

        /// <summary>Somebody stopped the turn.</summary>
        Cancelled = 2
    }

    /// <summary>Why a turn ended without an answer, with a sentence for the player.</summary>
    public readonly struct ChatOverlayTurnFailure
    {
        private readonly string _message;

        /// <summary>Creates a failure.</summary>
        public ChatOverlayTurnFailure(ChatOverlayTurnFailureKind kind, string message,
            LlmErrorCode errorCode = LlmErrorCode.None, Exception exception = null)
        {
            Kind = kind;
            _message = message ?? "";
            ErrorCode = errorCode;
            Exception = exception;
        }

        /// <summary>How the turn ended.</summary>
        public ChatOverlayTurnFailureKind Kind { get; }

        /// <summary>One readable sentence for the feed; never null.</summary>
        public string Message => _message ?? "";

        /// <summary>The typed failure category when known.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>The exception behind the failure, for logs; may be null.</summary>
        public Exception Exception { get; }

        /// <summary>
        /// Maps a failure reported as data (a failed result, a terminal chunk) to a failure with the player-facing
        /// sentence of <see cref="LlmErrorPresentation"/>.
        /// </summary>
        public static ChatOverlayTurnFailure FromErrorCode(LlmErrorCode errorCode, int? retryAfterSeconds = null)
        {
            switch (errorCode)
            {
                case LlmErrorCode.Cancelled:
                    return new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Cancelled,
                        LlmErrorPresentation.ForErrorCode(errorCode), errorCode);
                case LlmErrorCode.Timeout:
                    return new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Timeout,
                        LlmErrorPresentation.ForErrorCode(errorCode), errorCode);
                default:
                    LlmErrorCode code = errorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : errorCode;
                    return new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                        LlmErrorPresentation.ForErrorCode(code, retryAfterSeconds), code);
            }
        }
    }
}

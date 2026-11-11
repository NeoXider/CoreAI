using System;
using CoreAI.Ai;

namespace CoreAI.ChatOverlay
{
    /// <summary>How a host turn ended.</summary>
    public enum ChatOverlayHostTurnEnd
    {
        /// <summary>The model finished its answer.</summary>
        Completed = 0,

        /// <summary>A provider, transport or pipeline failure.</summary>
        Failed = 1,

        /// <summary>The player stopped it (<c>/stop</c>) or the host cancelled it.</summary>
        Cancelled = 2,

        /// <summary>The turn made no progress within its deadline.</summary>
        TimedOut = 3
    }

    /// <summary>The end of one host turn, with the failure sentence when it did not complete.</summary>
    public readonly struct ChatOverlayHostTurnResult
    {
        /// <summary>Creates a result.</summary>
        public ChatOverlayHostTurnResult(ChatOverlayHostTurnEnd end, string message = "",
            LlmErrorCode errorCode = LlmErrorCode.None)
        {
            End = end;
            Message = message ?? "";
            ErrorCode = errorCode;
        }

        /// <summary>How the turn ended.</summary>
        public ChatOverlayHostTurnEnd End { get; }

        /// <summary>The readable failure sentence; empty on completion.</summary>
        public string Message { get; }

        /// <summary>The typed failure category when known.</summary>
        public LlmErrorCode ErrorCode { get; }
    }

    /// <summary>
    /// A turn the host starts in the in-world chat instead of the player: a repair of a failing mod, a scripted
    /// follow-up. It takes the chat's one turn slot, so the status line follows it, <c>/stop</c> stops it like a typed
    /// turn and a line the player types meanwhile gets the busy notice. <see cref="Announcement"/> is shown as a system
    /// line where the player's line would be; the model's answer is shown only with <see cref="ShowAnswer"/>.
    /// </summary>
    public sealed class ChatOverlayHostTurnRequest
    {
        /// <summary>What the model is asked; it is not shown in the feed.</summary>
        public string Text { get; set; } = "";

        /// <summary>The role that answers; empty uses the chat's active role.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Correlation id of the turn's tool events; empty creates one.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>A system line shown when the turn starts; empty shows none.</summary>
        public string Announcement { get; set; } = "";

        /// <summary>True streams the model's answer into the feed like a typed turn; false keeps the feed quiet.</summary>
        public bool ShowAnswer { get; set; }

        /// <summary>Called once on the chat's thread when the turn ended, however it ended.</summary>
        public Action<ChatOverlayHostTurnResult> Ended { get; set; }
    }
}

using System;
using CoreAI.Messaging;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Entry point for code that is not the player to write into the in-world chat feed: NPC speech, command
    /// replies that arrive later, game announcements. Safe to call from any thread; a call from another thread
    /// is shown on the next chat tick.
    /// </summary>
    public interface IChatOverlayMessages
    {
        /// <summary>Adds a system line without a speaker.</summary>
        void PostSystemMessage(string text);

        /// <summary>Adds a line said by <paramref name="speaker"/> (empty for none) with the look of <paramref name="kind"/>.</summary>
        void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System);
    }

    /// <summary>
    /// Whether the in-world chat input owns the keyboard. A game reads it (or listens to
    /// <see cref="InputOpenChanged"/>) to keep its controls from reacting to the typing.
    /// <see cref="IsInputOpen"/> turns true the moment the input opens and turns false on the chat tick after
    /// it closed, so the key that closed it (Enter or Escape) never reaches the game in the same frame.
    /// </summary>
    public interface IChatOverlayInputState
    {
        /// <summary>True while the input is open, and for the rest of the frame in which it closed.</summary>
        bool IsInputOpen { get; }

        /// <summary>Raised with the new value whenever <see cref="IsInputOpen"/> changes.</summary>
        event Action<bool> InputOpenChanged;
    }

    /// <summary>Control over the chat's running agent turn.</summary>
    public interface IChatTurnControl
    {
        /// <summary>True while an answer is being produced.</summary>
        bool IsTurnActive { get; }

        /// <summary>Stops the running turn; false when nothing was running.</summary>
        bool StopActiveTurn();
    }

    /// <summary>Time source of the chat, in seconds; a test passes a manual clock.</summary>
    public interface IChatOverlayClock
    {
        /// <summary>Monotonic seconds since an arbitrary start, unaffected by game time scale.</summary>
        double NowSeconds { get; }
    }

    /// <summary>
    /// Tool-call lifecycle events of every agent, the same events the chat panel and the chat service observe.
    /// They may be raised on any thread; the chat filters them to its own turn by trace id.
    /// </summary>
    public interface IAgentToolEventSource
    {
        /// <summary>Raised before a model-requested tool runs.</summary>
        event Action<LlmToolCallStarted> ToolCallStarted;

        /// <summary>Raised after a tool finished successfully.</summary>
        event Action<LlmToolCallCompleted> ToolCallCompleted;

        /// <summary>Raised after a tool failed or returned an unsuccessful result.</summary>
        event Action<LlmToolCallFailed> ToolCallFailed;
    }

    /// <summary>
    /// The replaceable presentation of the in-world chat. The controller calls it on the thread that owns it;
    /// the view raises <see cref="Submitted"/> and <see cref="CloseRequested"/> from the player's input.
    /// </summary>
    public interface IChatOverlayView
    {
        /// <summary>Raised with the input text when the player presses Enter.</summary>
        event Action<string> Submitted;

        /// <summary>Raised when the player asks to close the input (Escape).</summary>
        event Action CloseRequested;

        /// <summary>Shows or hides the one-line input; <paramref name="prefill"/> is placed in it with the caret at the end.</summary>
        void SetInputOpen(bool open, string prefill);

        /// <summary>Adds a feed line at the bottom.</summary>
        void AddLine(ChatFeedLine line);

        /// <summary>Re-renders a line whose text changed (a streamed answer growing in place).</summary>
        void UpdateLine(ChatFeedLine line);

        /// <summary>Removes a line (the feed dropped its oldest).</summary>
        void RemoveLine(ChatFeedLine line);

        /// <summary>Fades a line out (<paramref name="visible"/> false) or back in.</summary>
        void SetLineVisible(ChatFeedLine line, bool visible);

        /// <summary>Shows the agent status line; an empty <paramref name="text"/> hides it.</summary>
        void SetStatus(AgentStatusKind kind, string text);
    }
}

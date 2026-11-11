using System;

namespace CoreAI.ChatOverlay
{
    /// <summary>One plain line the player typed, before it reaches the player's own agent.</summary>
    public readonly struct ChatOverlayPlayerLine
    {
        /// <summary>Creates the line; null strings become empty.</summary>
        public ChatOverlayPlayerLine(string text, string speakerName, double atSeconds)
        {
            Text = text ?? "";
            SpeakerName = speakerName ?? "";
            AtSeconds = atSeconds;
        }

        /// <summary>What the player typed, trimmed and length-capped as the agent would get it.</summary>
        public string Text { get; }

        /// <summary>The speaker name the feed shows for the player (<see cref="ChatOverlayOptions.PlayerSpeakerName"/>).</summary>
        public string SpeakerName { get; }

        /// <summary>The chat clock when the line was submitted.</summary>
        public double AtSeconds { get; }
    }

    /// <summary>
    /// A chance to take a plain line the player typed before the overlay sends it to the player's own agent: an NPC
    /// the line addresses, a game's own chat channel. Runs on the chat's thread, after the line is shown in the feed
    /// and before the agent is asked; slash commands never reach it.
    /// </summary>
    public interface IChatOverlayPreSendHook
    {
        /// <summary>True when the hook took the line; the overlay then sends nothing to the agent.</summary>
        bool TryTake(ChatOverlayPlayerLine line);
    }

    /// <summary>Where pre-send hooks are added. The overlay's <see cref="ChatOverlayController"/> implements it.</summary>
    public interface IChatOverlayPreSendHooks
    {
        /// <summary>
        /// Adds <paramref name="hook"/>. Hooks are asked in the order they were added and the first that takes a
        /// line ends the round; a hook that throws is skipped for that line. Dispose the handle to remove it.
        /// </summary>
        IDisposable AddPreSendHook(IChatOverlayPreSendHook hook);
    }
}

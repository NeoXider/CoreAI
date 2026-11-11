using System;
using CoreAI.ChatOverlay;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>
    /// Remembers the player's latest plain chat line, so an AI-written mod can be repaired with what the player asked
    /// for in mind. A pre-send hook that never takes a line: it only reads it. While no chat turn runs, the line is
    /// stale and <see cref="CurrentRequest"/> is empty.
    /// </summary>
    public sealed class LuaModPlayerRequestRecorder : IChatOverlayPreSendHook
    {
        /// <summary>Characters of a line kept.</summary>
        public const int MaximumCharacters = 600;

        private readonly IChatTurnControl _turns;
        private readonly object _gate = new();
        private string _latest = "";

        /// <param name="turns">The chat's turn state; null keeps the latest line regardless of a running turn.</param>
        public LuaModPlayerRequestRecorder(IChatTurnControl turns = null)
        {
            _turns = turns;
        }

        /// <summary>The player's line behind the running chat turn; empty when none runs.</summary>
        public string CurrentRequest
        {
            get
            {
                if (_turns != null && !_turns.IsTurnActive)
                {
                    return "";
                }

                lock (_gate)
                {
                    return _latest;
                }
            }
        }

        /// <inheritdoc />
        public bool TryTake(ChatOverlayPlayerLine line)
        {
            string text = (line.Text ?? "").Trim();
            if (text.Length > MaximumCharacters)
            {
                text = text.Substring(0, MaximumCharacters);
            }

            lock (_gate)
            {
                _latest = text;
            }

            return false;
        }

        /// <summary>Records a line directly (a host that has no pre-send hooks).</summary>
        public void Record(string text)
        {
            TryTake(new ChatOverlayPlayerLine(text, "", 0d));
        }
    }
}

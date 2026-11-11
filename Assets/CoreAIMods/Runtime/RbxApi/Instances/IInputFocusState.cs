using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Engine-free answer to "is device input meant for the game right now". While it is captured, a
    /// host surface (the chat input, the Hub, any game UI) owns the keyboard and pointer, and
    /// <see cref="RbxUserInputService"/> fires every input signal with <c>gameProcessedEvent = true</c>,
    /// which is Roblox's meaning of "the engine consumed this input".
    /// </summary>
    public interface IInputFocusState
    {
        /// <summary>True while at least one host surface holds the input.</summary>
        bool IsGameInputCaptured { get; }

        /// <summary>
        /// True while the pointer is over host UI: mouse buttons, movement and the wheel are then the
        /// UI's, while keys stay the game's (hovering a panel must not stop WASD).
        /// </summary>
        bool IsPointerOverUi => false;
    }

    /// <summary>
    /// The default <see cref="IInputFocusState"/>: a set of named holders. Each surface claims and
    /// releases under its own id, so two surfaces never release each other's claim.
    /// </summary>
    public sealed class InputFocusState : IInputFocusState
    {
        private readonly HashSet<string> _holders = new(StringComparer.Ordinal);

        /// <summary>Raised with the new value whenever <see cref="IsGameInputCaptured"/> flips.</summary>
        public event Action<bool> CaptureChanged;

        /// <inheritdoc />
        public bool IsGameInputCaptured => _holders.Count > 0;

        /// <summary>The ids that hold the input now, in no particular order.</summary>
        public IReadOnlyCollection<string> Holders => _holders;

        /// <summary>Claims (<paramref name="captured"/> true) or releases the input for one holder.</summary>
        public void SetCaptured(string holderId, bool captured)
        {
            if (string.IsNullOrWhiteSpace(holderId))
            {
                throw new ArgumentException("A focus holder needs a non-empty id.", nameof(holderId));
            }

            bool wasCaptured = IsGameInputCaptured;
            bool changed = captured ? _holders.Add(holderId) : _holders.Remove(holderId);
            if (changed && wasCaptured != IsGameInputCaptured)
            {
                CaptureChanged?.Invoke(IsGameInputCaptured);
            }
        }

        /// <summary>True while <paramref name="holderId"/> holds the input.</summary>
        public bool IsHeldBy(string holderId)
        {
            return holderId != null && _holders.Contains(holderId);
        }

        /// <inheritdoc />
        public bool IsPointerOverUi => _pointerHolders.Count > 0;

        /// <summary>Claims (true) or releases the pointer for one holder; keys are not affected.</summary>
        public void SetPointerOverUi(string holderId, bool over)
        {
            if (string.IsNullOrWhiteSpace(holderId))
            {
                throw new ArgumentException("A focus holder needs a non-empty id.", nameof(holderId));
            }

            if (over)
            {
                _pointerHolders.Add(holderId);
            }
            else
            {
                _pointerHolders.Remove(holderId);
            }
        }

        private readonly HashSet<string> _pointerHolders = new(StringComparer.Ordinal);
    }
}

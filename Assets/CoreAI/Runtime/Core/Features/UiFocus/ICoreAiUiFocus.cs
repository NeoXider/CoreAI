using System;
using System.Collections.Generic;

namespace CoreAI.UiFocus
{
    /// <summary>
    /// Engine-free answer to "does CoreAI UI own the input right now": every CoreAI surface (the chat
    /// panel, the Hub, an in-world chat overlay, a runtime UI host) reports under its own id whether one
    /// of its text fields has keyboard focus and whether the pointer is over it; a screen that takes the
    /// keyboard while it is open (a Hub opened over a play world) also reports that hold. A game world reads
    /// the union, so typing in any surface never reaches gameplay. Registered once per CoreAI scope.
    /// </summary>
    public interface ICoreAiUiFocus
    {
        /// <summary>True while any surface has a focused text field.</summary>
        bool HasTextFocus { get; }

        /// <summary>True while the pointer is over any surface.</summary>
        bool IsPointerOverUi { get; }

        /// <summary>
        /// True while the keyboard belongs to CoreAI UI: a text field has focus (<see cref="HasTextFocus"/>) or an
        /// open screen holds the keyboard (<see cref="ReportKeyboardHold"/>). What stands down for the UI reads this:
        /// the world's input, the chat overlay's open keys, the mod console key. An implementation that predates the
        /// hold answers <see cref="HasTextFocus"/>.
        /// </summary>
        bool HoldsKeyboard => HasTextFocus;

        /// <summary>
        /// Raised after <see cref="HasTextFocus"/>, <see cref="IsPointerOverUi"/> or <see cref="HoldsKeyboard"/> changes.
        /// </summary>
        event Action Changed;

        /// <summary>Records one surface's state; a surface calls it whenever either value changes.</summary>
        void Report(string surfaceId, bool textFocused, bool pointerOver);

        /// <summary>
        /// Records whether one surface holds the keyboard while it is open, apart from its text fields (a settings
        /// screen opened over the game). An implementation that predates the hold ignores it.
        /// </summary>
        void ReportKeyboardHold(string surfaceId, bool holds)
        {
        }

        /// <summary>Forgets a surface (disabled or destroyed), releasing whatever it held.</summary>
        void Clear(string surfaceId);
    }

    /// <summary>The default <see cref="ICoreAiUiFocus"/>: the union over every reporting surface.</summary>
    public sealed class CoreAiUiFocus : ICoreAiUiFocus
    {
        private readonly Dictionary<string, (bool TextFocused, bool PointerOver)> _surfaces =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _holds = new(StringComparer.Ordinal);

        private bool _hasTextFocus;
        private bool _isPointerOverUi;
        private bool _holdsKeyboard;

        /// <inheritdoc />
        public event Action Changed;

        /// <inheritdoc />
        public bool HasTextFocus => _hasTextFocus;

        /// <inheritdoc />
        public bool IsPointerOverUi => _isPointerOverUi;

        /// <inheritdoc />
        public bool HoldsKeyboard => _holdsKeyboard;

        /// <inheritdoc />
        public void Report(string surfaceId, bool textFocused, bool pointerOver)
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                throw new ArgumentException("A UI focus surface needs a non-empty id.", nameof(surfaceId));
            }

            if (!textFocused && !pointerOver)
            {
                _surfaces.Remove(surfaceId);
            }
            else
            {
                _surfaces[surfaceId] = (textFocused, pointerOver);
            }

            Recompute();
        }

        /// <inheritdoc />
        public void ReportKeyboardHold(string surfaceId, bool holds)
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                throw new ArgumentException("A UI focus surface needs a non-empty id.", nameof(surfaceId));
            }

            bool changed = holds ? _holds.Add(surfaceId) : _holds.Remove(surfaceId);
            if (changed)
            {
                Recompute();
            }
        }

        /// <inheritdoc />
        public void Clear(string surfaceId)
        {
            if (surfaceId == null)
            {
                return;
            }

            bool removed = _surfaces.Remove(surfaceId);
            removed |= _holds.Remove(surfaceId);
            if (removed)
            {
                Recompute();
            }
        }

        private void Recompute()
        {
            bool text = false;
            bool pointer = false;
            foreach ((bool TextFocused, bool PointerOver) state in _surfaces.Values)
            {
                text |= state.TextFocused;
                pointer |= state.PointerOver;
            }

            bool holds = text || _holds.Count > 0;
            if (text == _hasTextFocus && pointer == _isPointerOverUi && holds == _holdsKeyboard)
            {
                return;
            }

            _hasTextFocus = text;
            _isPointerOverUi = pointer;
            _holdsKeyboard = holds;
            Changed?.Invoke();
        }
    }
}

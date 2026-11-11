using System;
using System.Collections.Generic;

namespace CoreAI.UiFocus
{
    /// <summary>
    /// Engine-free answer to "does CoreAI UI own the input right now": every CoreAI surface (the chat
    /// panel, the Hub, an in-world chat overlay, a runtime UI host) reports under its own id whether one
    /// of its text fields has keyboard focus and whether the pointer is over it. A game world reads the
    /// union, so typing in any surface never reaches gameplay. Registered once per CoreAI scope.
    /// </summary>
    public interface ICoreAiUiFocus
    {
        /// <summary>True while any surface has a focused text field.</summary>
        bool HasTextFocus { get; }

        /// <summary>True while the pointer is over any surface.</summary>
        bool IsPointerOverUi { get; }

        /// <summary>Raised after <see cref="HasTextFocus"/> or <see cref="IsPointerOverUi"/> changes.</summary>
        event Action Changed;

        /// <summary>Records one surface's state; a surface calls it whenever either value changes.</summary>
        void Report(string surfaceId, bool textFocused, bool pointerOver);

        /// <summary>Forgets a surface (disabled or destroyed), releasing whatever it held.</summary>
        void Clear(string surfaceId);
    }

    /// <summary>The default <see cref="ICoreAiUiFocus"/>: the union over every reporting surface.</summary>
    public sealed class CoreAiUiFocus : ICoreAiUiFocus
    {
        private readonly Dictionary<string, (bool TextFocused, bool PointerOver)> _surfaces =
            new(StringComparer.Ordinal);

        private bool _hasTextFocus;
        private bool _isPointerOverUi;

        /// <inheritdoc />
        public event Action Changed;

        /// <inheritdoc />
        public bool HasTextFocus => _hasTextFocus;

        /// <inheritdoc />
        public bool IsPointerOverUi => _isPointerOverUi;

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
        public void Clear(string surfaceId)
        {
            if (surfaceId != null && _surfaces.Remove(surfaceId))
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

            if (text == _hasTextFocus && pointer == _isPointerOverUi)
            {
                return;
            }

            _hasTextFocus = text;
            _isPointerOverUi = pointer;
            Changed?.Invoke();
        }
    }
}

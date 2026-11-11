using System;
using CoreAI.ChatOverlay;
using CoreAI.UiFocus;

namespace CoreAI.Composition
{
    /// <summary>
    /// The default <see cref="IChatOverlayOpenGate"/> of a CoreAI scope: the overlay's open keys do nothing while any
    /// CoreAI surface holds the keyboard (a focused text field of the chat panel, a Hub page or a runtime UI screen,
    /// or a Hub open over the game), so a T or an Enter pressed there never opens the chat.
    /// </summary>
    public sealed class UiFocusChatOverlayOpenGate : IChatOverlayOpenGate
    {
        private readonly ICoreAiUiFocus _focus;

        /// <summary>Creates the gate over <paramref name="focus"/>.</summary>
        public UiFocusChatOverlayOpenGate(ICoreAiUiFocus focus)
        {
            _focus = focus ?? throw new ArgumentNullException(nameof(focus));
        }

        /// <inheritdoc />
        public bool IsOpenAllowed()
        {
            return !_focus.HoldsKeyboard;
        }
    }
}

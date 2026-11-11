using System;
using CoreAI.ChatOverlay;
using CoreAI.UiFocus;

namespace CoreAI.Composition
{
    /// <summary>
    /// Reports the in-world chat overlay's input into <see cref="ICoreAiUiFocus"/>: a text focus while
    /// <see cref="IChatOverlayInputState.IsInputOpen"/> is true, so the world marks every key with
    /// <c>gameProcessedEvent = true</c> and typing never moves the character. Inert when either end is missing.
    /// Disposing stops reporting and releases the surface.
    /// </summary>
    public sealed class ChatOverlayUiFocusReporter : IDisposable
    {
        private readonly IChatOverlayInputState _input;
        private readonly ICoreAiUiFocus _focus;
        private readonly string _surfaceId;
        private bool _disposed;

        /// <summary>Starts reporting at once under <paramref name="surfaceId"/>.</summary>
        public ChatOverlayUiFocusReporter(IChatOverlayInputState input, ICoreAiUiFocus focus, string surfaceId)
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                throw new ArgumentException("The overlay needs a UI focus surface id.", nameof(surfaceId));
            }

            _input = input;
            _focus = focus;
            _surfaceId = surfaceId;
            if (!IsActive)
            {
                return;
            }

            _input.InputOpenChanged += OnInputOpenChanged;
            Report(_input.IsInputOpen);
        }

        /// <summary>True while both ends exist and the reporter has not been disposed.</summary>
        public bool IsActive => !_disposed && _input != null && _focus != null;

        /// <inheritdoc />
        public void Dispose()
        {
            if (!IsActive)
            {
                _disposed = true;
                return;
            }

            _disposed = true;
            _input.InputOpenChanged -= OnInputOpenChanged;
            _focus.Clear(_surfaceId);
        }

        private void OnInputOpenChanged(bool open)
        {
            if (!_disposed)
            {
                Report(open);
            }
        }

        private void Report(bool open)
        {
            _focus.Report(_surfaceId, open, false);
        }
    }
}

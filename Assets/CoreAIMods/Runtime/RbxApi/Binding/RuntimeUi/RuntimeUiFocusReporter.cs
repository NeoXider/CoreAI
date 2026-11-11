using System;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.UiFocus;

namespace CoreAI.Mods.Rbx.Binding.RuntimeUi
{
    /// <summary>
    /// Reports the runtime UI host's focus and pointer state into the shared CoreAI UI focus under one
    /// surface id. <c>CoreAiUiFocusBridge</c> mirrors that union into the world's input focus, so while
    /// a runtime-UI text field has focus or the pointer is over a screen, <c>UserInputService</c> marks
    /// input with <c>gameProcessedEvent = true</c> and typing never moves the character. Dispose to stop
    /// and release the claim.
    /// </summary>
    public sealed class RuntimeUiFocusReporter : IDisposable
    {
        /// <summary>The surface id this reporter claims in <see cref="ICoreAiUiFocus"/>.</summary>
        public const string SurfaceId = "coreai.runtime-ui";

        private readonly IRbxUiBackend _backend;
        private readonly ICoreAiUiFocus _focus;
        private readonly Action<bool> _onChanged;
        private bool _disposed;

        /// <summary>Starts reporting at once; either argument may be null, which makes the reporter inert.</summary>
        public RuntimeUiFocusReporter(IRbxUiBackend backend, ICoreAiUiFocus focus)
        {
            _backend = backend;
            _focus = focus;
            _onChanged = _ => Report();
            if (!IsActive)
            {
                return;
            }

            _backend.TextInputFocusChanged += _onChanged;
            _backend.PointerOverUiChanged += _onChanged;
            Report();
        }

        /// <summary>True when both ends were supplied and the reporter is running.</summary>
        public bool IsActive => !_disposed && _backend != null && _focus != null;

        /// <inheritdoc />
        public void Dispose()
        {
            if (!IsActive)
            {
                _disposed = true;
                return;
            }

            _disposed = true;
            _backend.TextInputFocusChanged -= _onChanged;
            _backend.PointerOverUiChanged -= _onChanged;
            _focus.Clear(SurfaceId);
        }

        private void Report()
        {
            if (_disposed)
            {
                return;
            }

            _focus.Report(SurfaceId, _backend.IsTextInputFocused, _backend.IsPointerOverUi);
        }
    }
}

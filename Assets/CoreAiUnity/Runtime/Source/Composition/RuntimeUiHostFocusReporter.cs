using System;
using CoreAI.RuntimeUi;
using CoreAI.UiFocus;

namespace CoreAI.Composition
{
    /// <summary>
    /// Reports the runtime UI host into <see cref="ICoreAiUiFocus"/>: a text focus while a field of a runtime screen
    /// has the keyboard, a pointer claim while the pointer is over a pickable element. Registered by the CoreAI scope,
    /// so a game that uses runtime UI without the mods package gets the same focus handling; the mods package's Lua
    /// surface then stands down and the surface is reported once. Inert without a host or a focus service.
    /// </summary>
    public sealed class RuntimeUiHostFocusReporter : IDisposable
    {
        private readonly IRuntimeUiHost _host;
        private readonly ICoreAiUiFocus _focus;
        private readonly string _surfaceId;
        private readonly Action<bool> _onChanged;
        private bool _disposed;

        /// <summary>Starts reporting at once under <paramref name="surfaceId"/>.</summary>
        public RuntimeUiHostFocusReporter(IRuntimeUiHost host, ICoreAiUiFocus focus, string surfaceId)
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                throw new ArgumentException("The runtime UI needs a UI focus surface id.", nameof(surfaceId));
            }

            _host = host;
            _focus = focus;
            _surfaceId = surfaceId;
            _onChanged = _ => Report();
            if (!IsActive)
            {
                return;
            }

            _host.TextInputFocusChanged += _onChanged;
            _host.PointerOverUiChanged += _onChanged;
            Report();
        }

        /// <summary>True while both ends exist and the reporter has not been disposed.</summary>
        public bool IsActive => !_disposed && _host != null && _focus != null;

        /// <inheritdoc />
        public void Dispose()
        {
            if (!IsActive)
            {
                _disposed = true;
                return;
            }

            _disposed = true;
            _host.TextInputFocusChanged -= _onChanged;
            _host.PointerOverUiChanged -= _onChanged;
            _focus.Clear(_surfaceId);
        }

        private void Report()
        {
            if (!_disposed)
            {
                _focus.Report(_surfaceId, _host.IsTextInputFocused, _host.IsPointerOverUi);
            }
        }
    }
}

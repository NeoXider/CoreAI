using System;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.UiFocus;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Mirrors the CoreAI UI focus (chat panel, Hub, any later surface) into a world's
    /// <see cref="InputFocusState"/> under one holder id, so UserInputService marks input the UI took
    /// with <c>gameProcessedEvent = true</c>. Other holders of the same state (a host game's own menus)
    /// are left alone. Dispose to stop mirroring and release the claim.
    /// </summary>
    public sealed class CoreAiUiFocusBridge : IDisposable
    {
        /// <summary>The holder id this bridge claims the world's input under.</summary>
        public const string HolderId = "coreai.ui";

        private readonly ICoreAiUiFocus _uiFocus;
        private readonly InputFocusState _worldFocus;
        private bool _disposed;

        /// <summary>Starts mirroring at once; either argument may be null, which makes the bridge inert.</summary>
        public CoreAiUiFocusBridge(ICoreAiUiFocus uiFocus, InputFocusState worldFocus)
        {
            _uiFocus = uiFocus;
            _worldFocus = worldFocus;
            if (_uiFocus == null || _worldFocus == null)
            {
                return;
            }

            _uiFocus.Changed += Mirror;
            Mirror();
        }

        /// <summary>True when both ends were supplied and the bridge is mirroring.</summary>
        public bool IsActive => !_disposed && _uiFocus != null && _worldFocus != null;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_uiFocus == null || _worldFocus == null)
            {
                return;
            }

            _uiFocus.Changed -= Mirror;
            _worldFocus.SetCaptured(HolderId, false);
            _worldFocus.SetPointerOverUi(HolderId, false);
        }

        private void Mirror()
        {
            if (_disposed)
            {
                return;
            }

            _worldFocus.SetCaptured(HolderId, _uiFocus.HasTextFocus);
            _worldFocus.SetPointerOverUi(HolderId, _uiFocus.IsPointerOverUi);
        }
    }
}

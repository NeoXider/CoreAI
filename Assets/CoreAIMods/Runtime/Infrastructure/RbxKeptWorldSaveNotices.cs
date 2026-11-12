using System;
using CoreAI.ChatOverlay;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Says in the in-world chat when a kept world stops being saved, once per reason, and once when it is saved again:
    /// the chat half of <see cref="RbxWorldRuntimeSessionController.KeptWorldSaveStateChanged"/> (the HUD half is the
    /// Workspace attributes). Dispose to stop.
    /// </summary>
    public sealed class RbxKeptWorldSaveNotices : IDisposable
    {
        private readonly RbxWorldRuntimeSessionController _session;
        private readonly IChatOverlayMessages _messages;
        private string _postedFailure = "";
        private bool _disposed;

        /// <summary>Starts listening to <paramref name="session"/> and posting into <paramref name="messages"/>.</summary>
        public RbxKeptWorldSaveNotices(RbxWorldRuntimeSessionController session, IChatOverlayMessages messages)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _session.KeptWorldSaveStateChanged += OnSaveStateChanged;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.KeptWorldSaveStateChanged -= OnSaveStateChanged;
        }

        private void OnSaveStateChanged(RbxKeptWorldSaveState state, string note)
        {
            if (state == RbxKeptWorldSaveState.Failed)
            {
                string reason = note ?? "";
                if (reason.Length == 0 || string.Equals(reason, _postedFailure, StringComparison.Ordinal))
                {
                    return;
                }

                _postedFailure = reason;
                _messages.PostMessage("", "The world is not saved: " + reason, ChatFeedLineKind.Error);
                return;
            }

            if (state == RbxKeptWorldSaveState.Saved && _postedFailure.Length > 0)
            {
                _postedFailure = "";
                _messages.PostMessage("", "The world is saved again.", ChatFeedLineKind.System);
            }
        }
    }
}

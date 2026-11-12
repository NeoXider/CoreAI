using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Who draws the in-world chat: CoreAI's default overlay, or another surface (a script's chat, a game's own UI)
    /// that claimed it. While at least one claim is held the overlay steps aside: it shows nothing, reads no keys and
    /// gives back its built-in command names, and its feed, host turns and hooks keep working for the claimant.
    /// Releasing the last claim brings the overlay back. Call it on the chat's thread.
    /// </summary>
    public interface IChatOverlaySurfaceSwitch
    {
        /// <summary>True while nobody claimed the chat, so the default overlay draws it.</summary>
        bool IsDefaultSurfaceShown { get; }

        /// <summary>The owner of the most recent claim still held; empty when none.</summary>
        string Claimant { get; }

        /// <summary>Raised right after <see cref="IsDefaultSurfaceShown"/> changed.</summary>
        event Action Changed;

        /// <summary>Claims the chat for <paramref name="owner"/>; dispose the handle to release this claim.</summary>
        IDisposable Claim(string owner);
    }

    /// <summary>Default <see cref="IChatOverlaySurfaceSwitch"/>: a list of claims.</summary>
    public sealed class ChatOverlaySurfaceSwitch : IChatOverlaySurfaceSwitch
    {
        private readonly List<ClaimHandle> _claims = new();

        /// <inheritdoc />
        public event Action Changed;

        /// <inheritdoc />
        public bool IsDefaultSurfaceShown => _claims.Count == 0;

        /// <inheritdoc />
        public string Claimant => _claims.Count == 0 ? "" : _claims[_claims.Count - 1].Owner;

        /// <inheritdoc />
        public IDisposable Claim(string owner)
        {
            ClaimHandle handle = new(this, string.IsNullOrWhiteSpace(owner) ? "unnamed" : owner.Trim());
            _claims.Add(handle);
            if (_claims.Count == 1)
            {
                Changed?.Invoke();
            }

            return handle;
        }

        private void Release(ClaimHandle handle)
        {
            if (_claims.Remove(handle) && _claims.Count == 0)
            {
                Changed?.Invoke();
            }
        }

        private sealed class ClaimHandle : IDisposable
        {
            private ChatOverlaySurfaceSwitch _owner;

            public ClaimHandle(ChatOverlaySurfaceSwitch owner, string claimant)
            {
                _owner = owner;
                Owner = claimant;
            }

            public string Owner { get; }

            public void Dispose()
            {
                ChatOverlaySurfaceSwitch owner = _owner;
                _owner = null;
                owner?.Release(this);
            }
        }
    }
}

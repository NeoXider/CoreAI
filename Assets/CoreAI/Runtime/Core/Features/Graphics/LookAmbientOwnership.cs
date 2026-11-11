using System;
using System.Collections.Generic;

namespace CoreAI.Look
{
    /// <summary>
    /// Who owns the scene's ambient light. The default look writes the ambient mode, colours and intensity
    /// only while nobody claims them; a system that drives the ambient itself (a world whose scripts run a
    /// day cycle) claims it for as long as it drives, and the look takes the ambient back when the last
    /// claim is released. Engine-free and main-thread only.
    /// </summary>
    /// <remarks>
    /// WHY a process-wide instance next to the constructor seam: the claimant usually lives in an assembly
    /// the look may not reference and is often built outside any container (a world host component), so
    /// both sides meet at <see cref="Shared"/>; a test or a host that wants isolation passes its own
    /// instance to the applier.
    /// </remarks>
    public sealed class LookAmbientOwnership
    {
        private readonly List<string> _owners = new();

        /// <summary>The instance the default look appliers and the default claimants share.</summary>
        public static LookAmbientOwnership Shared { get; } = new();

        /// <summary>True while at least one claim is held.</summary>
        public bool IsClaimed => _owners.Count > 0;

        /// <summary>The names of the current claimants, oldest first.</summary>
        public IReadOnlyList<string> Owners => _owners;

        /// <summary>Raised when the ambient becomes claimed and when the last claim is released.</summary>
        public event Action Changed;

        /// <summary>
        /// Claims the ambient for <paramref name="owner"/> until the returned handle is disposed. Claims
        /// nest; disposing a handle twice releases it once.
        /// </summary>
        public IDisposable Claim(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                throw new ArgumentException("A claim needs the owner's name.", nameof(owner));
            }

            _owners.Add(owner);
            if (_owners.Count == 1)
            {
                Changed?.Invoke();
            }

            return new ClaimHandle(this, owner);
        }

        private void Release(string owner)
        {
            if (_owners.Remove(owner) && _owners.Count == 0)
            {
                Changed?.Invoke();
            }
        }

        private sealed class ClaimHandle : IDisposable
        {
            private LookAmbientOwnership _ownership;
            private readonly string _owner;

            public ClaimHandle(LookAmbientOwnership ownership, string owner)
            {
                _ownership = ownership;
                _owner = owner;
            }

            public void Dispose()
            {
                LookAmbientOwnership ownership = _ownership;
                _ownership = null;
                ownership?.Release(_owner);
            }
        }
    }
}

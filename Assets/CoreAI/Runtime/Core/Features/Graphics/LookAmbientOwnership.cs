using System;
using System.Collections.Generic;

namespace CoreAI.Look
{
    /// <summary>
    /// Who owns the scene's ambient light. The default look writes the ambient mode, colours and intensity
    /// only while nobody claims them; a system that drives the ambient itself (a world whose scripts run a
    /// day cycle) claims it for as long as it drives, and the look takes the ambient back when the last
    /// claim is released. The fog is claimed the same way, separately (<see cref="ClaimFog"/>): a world whose
    /// scripts set their own fog owns it while they do. Engine-free and main-thread only.
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
        private readonly List<string> _fogOwners = new();

        /// <summary>The instance the default look appliers and the default claimants share.</summary>
        public static LookAmbientOwnership Shared { get; } = new();

        /// <summary>True while at least one claim is held.</summary>
        public bool IsClaimed => _owners.Count > 0;

        /// <summary>The names of the current claimants, oldest first.</summary>
        public IReadOnlyList<string> Owners => _owners;

        /// <summary>Raised when the ambient becomes claimed and when the last claim is released.</summary>
        public event Action Changed;

        /// <summary>True while at least one fog claim is held: the look leaves the fog to its owner.</summary>
        public bool IsFogClaimed => _fogOwners.Count > 0;

        /// <summary>The names of the current fog claimants, oldest first.</summary>
        public IReadOnlyList<string> FogOwners => _fogOwners;

        /// <summary>Raised when the fog becomes claimed and when its last claim is released.</summary>
        public event Action FogChanged;

        /// <summary>
        /// Multiplier on the look's sky reflections (its sky reflection probe and the environment reflection
        /// intensity), at least 0; 1 leaves the look as authored. A world's <c>Lighting.EnvironmentSpecularScale</c>
        /// drives it while that world claims the ambient.
        /// </summary>
        public float SkyReflectionScale { get; private set; } = 1f;

        /// <summary>Raised when <see cref="SkyReflectionScale"/> changes.</summary>
        public event Action SkyReflectionScaleChanged;

        /// <summary>Sets <see cref="SkyReflectionScale"/>; negative or non-finite values become 0.</summary>
        public void SetSkyReflectionScale(float scale)
        {
            float next = float.IsNaN(scale) || scale < 0f ? 0f : float.IsInfinity(scale) ? 0f : scale;
            if (next.Equals(SkyReflectionScale))
            {
                return;
            }

            SkyReflectionScale = next;
            SkyReflectionScaleChanged?.Invoke();
        }

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

        /// <summary>
        /// Claims the fog for <paramref name="owner"/> until the returned handle is disposed: the look stops writing
        /// the fog's switch, mode, colour and distances, and writes its own again when the last fog claim ends. Claims
        /// nest; disposing a handle twice releases it once.
        /// </summary>
        public IDisposable ClaimFog(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                throw new ArgumentException("A claim needs the owner's name.", nameof(owner));
            }

            _fogOwners.Add(owner);
            if (_fogOwners.Count == 1)
            {
                FogChanged?.Invoke();
            }

            return new ClaimHandle(this, owner, true);
        }

        private void Release(string owner, bool fog)
        {
            List<string> owners = fog ? _fogOwners : _owners;
            if (!owners.Remove(owner) || owners.Count != 0)
            {
                return;
            }

            if (fog)
            {
                FogChanged?.Invoke();
            }
            else
            {
                Changed?.Invoke();
            }
        }

        private sealed class ClaimHandle : IDisposable
        {
            private LookAmbientOwnership _ownership;
            private readonly string _owner;
            private readonly bool _fog;

            public ClaimHandle(LookAmbientOwnership ownership, string owner, bool fog = false)
            {
                _ownership = ownership;
                _owner = owner;
                _fog = fog;
            }

            public void Dispose()
            {
                LookAmbientOwnership ownership = _ownership;
                _ownership = null;
                ownership?.Release(_owner, _fog);
            }
        }
    }
}

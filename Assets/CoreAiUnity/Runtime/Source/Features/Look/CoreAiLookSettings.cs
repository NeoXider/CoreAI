using System;
using UnityEngine;

namespace CoreAI.Look
{
    /// <summary>
    /// Scope configuration of the default look, edited on <c>CoreAILifetimeScope</c> or in code before the
    /// scope builds. A host game that owns its rendering sets <see cref="Mode"/> to
    /// <see cref="LookMode.Off"/>; one that wants its own applier or store sets the overrides.
    /// </summary>
    [Serializable]
    public sealed class CoreAiLookSettings
    {
        [Tooltip("Apply: the default look everywhere (a scene Volume still wins). FillMissing: only add what " +
                 "the scene did not author. Off: the game owns rendering and nothing is touched.")]
        [SerializeField]
        private LookMode mode = LookMode.Apply;

        [Tooltip("Optional look profile asset. Empty uses the code defaults.")]
        [SerializeField]
        private CoreAiLookProfile profile;

        [NonSerialized]
        private CoreAiLookProfile _defaultProfile;

        /// <summary>How the look is applied.</summary>
        public LookMode Mode
        {
            get => mode;
            set => mode = value;
        }

        /// <summary>The assigned profile asset, or null for the code defaults.</summary>
        public CoreAiLookProfile Profile
        {
            get => profile;
            set => profile = value;
        }

        /// <summary>
        /// Replaces the default applier when set before the scope builds. A container registration of
        /// <see cref="ICoreAiLookApplier"/> made before <c>RegisterCoreAiLook</c> wins over both.
        /// </summary>
        public ICoreAiLookApplier ApplierOverride { get; set; }

        /// <summary>Replaces the default file store when set before the scope builds.</summary>
        public IGraphicsOptionsStore StoreOverride { get; set; }

        /// <summary>The assigned profile, or a transient profile with the code defaults.</summary>
        public CoreAiLookProfile ResolveProfile()
        {
            if (profile != null)
            {
                return profile;
            }

            if (_defaultProfile == null)
            {
                _defaultProfile = CoreAiLookProfile.CreateDefault();
            }

            return _defaultProfile;
        }
    }
}

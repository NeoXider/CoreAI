using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// The play world's controls slot (P1): the local player joins every published world, so its character spawns
    /// and the bundled control script (<c>coreai_player_controls</c>: WASD, jump, double-tapped jump to fly) and the
    /// default third-person camera have someone to drive; a world a load or an undo replaces gets the player again.
    /// Turning <see cref="UseDefaultCamera"/> off hands the camera to the game.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiPlayerControlsModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        [Tooltip("Join the local player into every world the mods scope publishes, so its character spawns.")]
        [SerializeField]
        private bool connectLocalPlayer = true;

        [Tooltip("The default third-person camera around the local character. Off: the game owns the camera. "
                 + "Applied to a mods scope the bootstrap has not built yet.")]
        [SerializeField]
        private bool useDefaultCamera = true;

        private readonly CoreAiPublishedWorldWatcher _watcher = new();
        private CoreAiPlayWorldComposition _composition;

        /// <summary>Whether the local player joins every published world.</summary>
        public bool ConnectLocalPlayer
        {
            get => connectLocalPlayer;
            set => connectLocalPlayer = value;
        }

        /// <summary>Whether the default camera drives the scene camera.</summary>
        public bool UseDefaultCamera
        {
            get => useDefaultCamera;
            set => useDefaultCamera = value;
        }

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            CoreAiModsLifetimeScope mods = composition?.ModsScope;
            if (mods != null && mods.Container == null && !useDefaultCamera)
            {
                mods.SetPlayerCameraSettings(new CoreAiPlayerCameraSettings { useDefaultCamera = false });
            }
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
            _composition = composition;
            Apply();
        }

        private void Update()
        {
            Apply();
        }

        /// <summary>Joins the local player into the published world if it is one not seen yet; internal for tests.</summary>
        internal bool Apply()
        {
            if (!connectLocalPlayer || _composition == null)
            {
                return false;
            }

            CoreAiModsLifetimeScope mods = _composition.ModsScope;
            if (!_watcher.TryTakeNewWorld(mods, out LuaCsRbxApiBindings world))
            {
                return false;
            }

            // WHY contained: the watcher has already moved past this world, so a failure is reported once and the
            // next world is tried again; nothing throws out of Update every frame.
            try
            {
                return CoreAiLocalPlayer.Connect(world, ResolveActor(mods)) != null;
            }
            catch (System.Exception ex)
            {
                (mods.Container?.ResolveOrDefault<Logging.ILog>() ?? Logging.Log.Instance).Warn(
                    "[CoreAI] The local player could not join the world, so there is no character to control: "
                    + ex.Message, Logging.LogTag.World);
                return false;
            }
        }

        /// <summary>The actor of this screen: the one the chat and its AI act as.</summary>
        internal static ActorContext ResolveActor(CoreAiModsLifetimeScope mods)
        {
            IActorIdentityProvider identity = mods?.Container?.ResolveOrDefault<IActorIdentityProvider>()
                                              ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider;
            return identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }
    }
}

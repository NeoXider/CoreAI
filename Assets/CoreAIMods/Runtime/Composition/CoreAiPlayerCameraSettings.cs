using System;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Composition
{
    /// <summary>
    /// The player camera preset of a mods composition: whether the default third-person camera
    /// (<see cref="RbxDefaultCameraModule"/>: orbit around the local character, wheel zoom down to first person,
    /// pull-in on obstructions) drives <c>CameraType.Custom</c>, and its tuning. Applied to every world the
    /// composition builds, the initial one and each one a load stages. A game that owns its camera turns
    /// <see cref="useDefaultCamera"/> off; a script can still take the camera at runtime with
    /// <c>workspace.CurrentCamera.CameraType = Enum.CameraType.Scriptable</c>.
    /// </summary>
    [Serializable]
    public sealed class CoreAiPlayerCameraSettings
    {
        [Tooltip("Third-person camera around the local character, with wheel zoom to first person. Off: no camera "
                 + "module runs, so the game or its scripts own the camera.")]
        public bool useDefaultCamera = true;

        /// <summary>Tuning of the default module (sensitivity, zoom, pitch); null uses the PlayerModule values.</summary>
        [NonSerialized]
        public RbxCameraModuleOptions ModuleOptions;

        /// <summary>
        /// Replaces the default module with a game's own while <see cref="useDefaultCamera"/> is on; it is asked for
        /// one module per world. Null builds a <see cref="RbxDefaultCameraModule"/>.
        /// </summary>
        [NonSerialized]
        public Func<IRbxCameraModule> ModuleFactory;

        /// <summary>The module one world gets: null when the default camera is off.</summary>
        public IRbxCameraModule CreateCameraModule()
        {
            if (!useDefaultCamera)
            {
                return null;
            }

            return ModuleFactory?.Invoke() ?? new RbxDefaultCameraModule(ModuleOptions);
        }
    }
}

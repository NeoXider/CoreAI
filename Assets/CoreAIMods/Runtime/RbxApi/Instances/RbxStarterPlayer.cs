using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Mirror <c>Enum.DevComputerMovementMode</c>, values as in the mirror.</summary>
    public enum RbxDevComputerMovementMode
    {
        /// <summary>The player's choice (the default control script treats it as keyboard and mouse).</summary>
        UserChoice = 0,

        /// <summary>Keyboard and mouse.</summary>
        KeyboardMouse = 1,

        /// <summary>Click to move (not implemented by the default control script; it then does nothing).</summary>
        ClickToMove = 2,

        /// <summary>The default controls are off; the game's own scripts move the character.</summary>
        Scriptable = 3
    }

    /// <summary>Mirror <c>Enum.CameraMode</c>, values as in the mirror.</summary>
    public enum RbxCameraMode
    {
        /// <summary>Third person with zoom down to first person.</summary>
        Classic = 0,

        /// <summary>First person only.</summary>
        LockFirstPerson = 1
    }

    /// <summary>
    /// Mirror <c>StarterPlayer</c>: the world's player-control settings. The default control script reads
    /// <see cref="DevComputerMovementMode"/> live, so a change applies on the next frame; the camera
    /// settings are copied into each <c>Player</c> when it joins, as in Roblox, and the default camera reads
    /// the local player's copy (this object's values while no player is local). A host turns the default
    /// controls off from C# with <c>DevComputerMovementMode = Scriptable</c>, exactly as a script does.
    /// </summary>
    public sealed class RbxStarterPlayer : RbxInstance
    {
        /// <summary>Mirror default of <see cref="CameraMaxZoomDistance"/>, studs.</summary>
        public const double DefaultCameraMaxZoomDistance = 400d;

        /// <summary>Mirror default of <see cref="CameraMinZoomDistance"/>, studs.</summary>
        public const double DefaultCameraMinZoomDistance = 0.5d;

        private RbxDevComputerMovementMode _movementMode = RbxDevComputerMovementMode.UserChoice;
        private RbxCameraMode _cameraMode = RbxCameraMode.Classic;
        private double _maxZoom = DefaultCameraMaxZoomDistance;
        private double _minZoom = DefaultCameraMinZoomDistance;

        internal RbxStarterPlayer(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "StarterPlayer";
        }

        /// <summary>Mirror <c>StarterPlayer.DevComputerMovementMode</c>, default <c>UserChoice</c>.</summary>
        public RbxDevComputerMovementMode DevComputerMovementMode
        {
            get => _movementMode;
            set
            {
                if (!Enum.IsDefined(typeof(RbxDevComputerMovementMode), value) || _movementMode == value)
                {
                    return;
                }

                _movementMode = value;
                NotifyPropertyChanged(nameof(DevComputerMovementMode));
            }
        }

        /// <summary>True while the default control script may move the character.</summary>
        public bool DefaultControlsEnabled => _movementMode != RbxDevComputerMovementMode.Scriptable
                                              && _movementMode != RbxDevComputerMovementMode.ClickToMove;

        /// <summary>Mirror <c>StarterPlayer.CameraMode</c>, default <c>Classic</c>.</summary>
        public RbxCameraMode CameraMode
        {
            get => _cameraMode;
            set
            {
                if (!Enum.IsDefined(typeof(RbxCameraMode), value) || _cameraMode == value)
                {
                    return;
                }

                _cameraMode = value;
                NotifyPropertyChanged(nameof(CameraMode));
            }
        }

        /// <summary>
        /// Mirror <c>StarterPlayer.CameraMaxZoomDistance</c>, default 400 studs. A value below the
        /// minimum is raised to it, as the mirror describes.
        /// </summary>
        public double CameraMaxZoomDistance
        {
            get => _maxZoom;
            set => SetZoom(ref _maxZoom, Math.Max(value, _minZoom), nameof(CameraMaxZoomDistance));
        }

        /// <summary>
        /// Mirror <c>StarterPlayer.CameraMinZoomDistance</c>, default 0.5 studs. A value above the
        /// maximum is lowered to it.
        /// </summary>
        public double CameraMinZoomDistance
        {
            get => _minZoom;
            set => SetZoom(ref _minZoom, Math.Max(0d, Math.Min(value, _maxZoom)), nameof(CameraMinZoomDistance));
        }

        private void SetZoom(ref double field, double value, string property)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || field.Equals(value))
            {
                return;
            }

            field = value;
            NotifyPropertyChanged(property);
        }
    }
}

using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The camera behaviour of <c>CameraType.Custom</c>, the counterpart of Roblox's PlayerModule
    /// CameraModule. The world calls <see cref="Update"/> once per rendered frame while the camera is
    /// Custom and has a subject, and applies the pose it returns; a host replaces the whole behaviour by
    /// supplying its own implementation, or switches it off by supplying none.
    /// </summary>
    public interface IRbxCameraModule
    {
        /// <summary>One frame of the default camera; the output says what to apply.</summary>
        RbxCameraModuleOutput Update(in RbxCameraModuleInput input);

        /// <summary>
        /// Forgets the look angles, so the next <see cref="Update"/> starts from the camera's current pose;
        /// called when the camera leaves Custom (a script took it with <c>Scriptable</c>) or the subject
        /// goes away.
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Finds what stands between the camera subject and the camera (the collision pull-in ray). The world
    /// implements it over its physics port with the subject's own character excluded.
    /// </summary>
    public interface IRbxCameraOcclusion
    {
        /// <summary>
        /// Distance in studs from <paramref name="focus"/> to the first surface on the segment to
        /// <paramref name="camera"/>; false when nothing is in the way.
        /// </summary>
        bool TryFindObstruction(RbxVector3 focus, RbxVector3 camera, out double distanceStuds);
    }

    /// <summary>Everything one <see cref="IRbxCameraModule.Update"/> may read, gathered by the world each frame.</summary>
    public struct RbxCameraModuleInput
    {
        /// <summary>Seconds since the previous frame.</summary>
        public double DeltaSeconds;

        /// <summary>The camera's current pose, Roblox space.</summary>
        public RbxCFrame CurrentCFrame;

        /// <summary>The point the camera orbits (the subject's head for a Humanoid), Roblox space.</summary>
        public RbxVector3 Focus;

        /// <summary>Pointer movement of this frame in pixels, x right and y down, sensitivity applied.</summary>
        public RbxVector2 PointerDelta;

        /// <summary>Wheel movement of this frame: positive forward (zoom in), 0 none.</summary>
        public float Wheel;

        /// <summary>The right thumbstick, each axis in [-1, 1], y up.</summary>
        public RbxVector2 LookStick;

        /// <summary>True while the rotate button (the right mouse button) is held over the game view.</summary>
        public bool RotateHeld;

        /// <summary>True while the host pointer is locked.</summary>
        public bool PointerLocked;

        /// <summary>True while a host surface (the chat, the Hub) owns the input.</summary>
        public bool InputCaptured;

        /// <summary>True while the pointer is over host UI, which keeps the wheel and the rotate button.</summary>
        public bool PointerOverUi;

        /// <summary>The local player's <c>CameraMode</c>.</summary>
        public RbxCameraMode CameraMode;

        /// <summary>The local player's <c>CameraMinZoomDistance</c>, studs.</summary>
        public double MinZoomDistance;

        /// <summary>The local player's <c>CameraMaxZoomDistance</c>, studs.</summary>
        public double MaxZoomDistance;

        /// <summary>The pull-in query; null skips it.</summary>
        public IRbxCameraOcclusion Occlusion;
    }

    /// <summary>What one <see cref="IRbxCameraModule.Update"/> asks the world to apply.</summary>
    public struct RbxCameraModuleOutput
    {
        /// <summary>True when <see cref="CFrame"/> should be applied to the camera.</summary>
        public bool HasCFrame;

        /// <summary>The camera pose to apply, Roblox space.</summary>
        public RbxCFrame CFrame;

        /// <summary>The pointer lock the camera wants: <c>LockCenter</c> in first person,
        /// <c>LockCurrentPosition</c> while rotating, <c>Default</c> otherwise.</summary>
        public RbxMouseLock MouseLock;

        /// <summary>True while the camera is in first person.</summary>
        public bool IsFirstPerson;

        /// <summary>The zoom the player asked for, studs.</summary>
        public double ZoomDistance;

        /// <summary>The distance actually kept after the pull-in, studs.</summary>
        public double Distance;
    }
}

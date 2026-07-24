using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Engine-free <see cref="IRbxCameraRig"/>: stores the camera pose in pure Roblox space
    /// and records the follow target with no Unity camera behind it. The headless/solo default
    /// and the test double, mirroring <see cref="InMemoryPartPropertySink"/> for the camera seam
    /// so scripts drive the camera through the same API whether or not a live camera is wired.
    /// </summary>
    public sealed class InMemoryCameraRig : IRbxCameraRig
    {
        private RbxCFrame _cframe = RbxCFrame.Identity;

        /// <summary>Followed instance recorded by the last successful Follow; null when idle.</summary>
        public InstanceId? FollowTarget { get; private set; }

        public RbxCFrame GetCFrame() => _cframe;

        public void SetCFrame(in RbxCFrame cframe) => _cframe = cframe;

        public bool Follow(InstanceId id)
        {
            FollowTarget = id;
            return true;
        }

        public void StopFollowing() => FollowTarget = null;
    }
}

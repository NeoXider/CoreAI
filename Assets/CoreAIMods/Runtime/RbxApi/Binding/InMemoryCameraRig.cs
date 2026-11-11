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
        /// <summary>Viewport a headless camera reports until a test sets another, pixels.</summary>
        public static readonly RbxVector2 DefaultViewportSize = new(1920f, 1080f);

        private RbxCFrame _cframe = RbxCFrame.Identity;

        /// <summary>Followed instance recorded by the last successful Follow; null when idle.</summary>
        public InstanceId? FollowTarget { get; private set; }

        /// <summary>Vertical field of view, degrees.</summary>
        public double FieldOfView { get; set; } = RbxCameraProjection.DefaultFieldOfView;

        /// <summary>The viewport the camera queries project into, pixels.</summary>
        public RbxVector2 ViewportSize { get; set; } = DefaultViewportSize;

        /// <summary>How many times <see cref="SetCFrame"/> ran.</summary>
        public int SetCount { get; private set; }

        public RbxCFrame GetCFrame()
        {
            return _cframe;
        }

        public void SetCFrame(in RbxCFrame cframe)
        {
            _cframe = cframe;
            SetCount++;
        }

        public bool Follow(InstanceId id)
        {
            FollowTarget = id;
            return true;
        }

        public void StopFollowing()
        {
            FollowTarget = null;
        }

        /// <inheritdoc />
        public double GetFieldOfView()
        {
            return FieldOfView;
        }

        /// <inheritdoc />
        public bool TrySetFieldOfView(double degrees)
        {
            FieldOfView = degrees;
            return true;
        }

        /// <inheritdoc />
        public RbxVector2 GetViewportSize()
        {
            return ViewportSize;
        }
    }
}

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Engine-free mirror of <c>Enum.MouseBehavior</c>, values as in the mirror.</summary>
    public enum RbxMouseLock
    {
        /// <summary>The cursor moves freely.</summary>
        Default = 0,

        /// <summary>The cursor is locked to the centre of the screen.</summary>
        LockCenter = 1,

        /// <summary>The cursor is locked where it is.</summary>
        LockCurrentPosition = 2
    }

    /// <summary>
    /// Host cursor seam behind <c>UserInputService.MouseBehavior</c>: the service hands every change to
    /// the port, and the port answers whether the pointer is actually locked now (a browser grants
    /// pointer lock only after a user gesture and drops it on Escape, so the request and the state can
    /// differ).
    /// </summary>
    public interface IRbxCursorPort
    {
        /// <summary>Applies the requested behaviour to the host cursor.</summary>
        void ApplyMouseBehavior(RbxMouseLock mode);

        /// <summary>True while the host pointer is locked.</summary>
        bool IsPointerLocked { get; }
    }

    /// <summary>Headless <see cref="IRbxCursorPort"/>: records the request and reports it as granted.</summary>
    public sealed class InMemoryCursorPort : IRbxCursorPort
    {
        /// <summary>The last behaviour applied.</summary>
        public RbxMouseLock Applied { get; private set; }

        /// <summary>How many times a behaviour was applied.</summary>
        public int ApplyCount { get; private set; }

        /// <summary>When false, a lock request is recorded but not granted (a browser without a gesture).</summary>
        public bool GrantsLock { get; set; } = true;

        /// <inheritdoc />
        public bool IsPointerLocked => GrantsLock && Applied != RbxMouseLock.Default;

        /// <inheritdoc />
        public void ApplyMouseBehavior(RbxMouseLock mode)
        {
            Applied = mode;
            ApplyCount++;
        }
    }
}

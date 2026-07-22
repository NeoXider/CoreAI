namespace CoreAI.RobloxApi.Instances
{
    /// <summary>
    /// Seam between the engine-free registry and whatever backs instances in the running world
    /// (GameObjects in the Unity adapter). Semantics per roadmap D5: an instance materializes
    /// when it first enters the world-root (workspace) subtree, is DEACTIVATED (not destroyed)
    /// when detached so re-parenting stays cheap, and releases its backing object on Destroy.
    /// The Unity implementation (InstanceGameObjectBinder) lands with the world-binding task;
    /// this slice ships only the in-memory fake.
    /// </summary>
    public interface IInstanceBackingBinder
    {
        /// <summary>The instance entered the world-root subtree — materialize its backing object.</summary>
        void OnEnteredWorld(InstanceRecord record);

        /// <summary>The instance left the world-root subtree — deactivate, do not destroy (D5).</summary>
        void OnLeftWorld(InstanceRecord record);

        /// <summary>The instance was destroyed — release any backing object (D6 step 6).</summary>
        void OnDestroyed(InstanceRecord record);
    }

    /// <summary>Null object for hosts that bind nothing (headless tests, storage-only trees).</summary>
    public sealed class NullInstanceBackingBinder : IInstanceBackingBinder
    {
        public static readonly NullInstanceBackingBinder Instance = new NullInstanceBackingBinder();

        public void OnEnteredWorld(InstanceRecord record)
        {
        }

        public void OnLeftWorld(InstanceRecord record)
        {
        }

        public void OnDestroyed(InstanceRecord record)
        {
        }
    }
}

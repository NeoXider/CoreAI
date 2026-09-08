namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Seam between the engine-free registry and whatever backs instances in the running world
    /// (GameObjects in the Unity adapter). Semantics per roadmap D5: an instance materializes
    /// when it first enters the scene root (DataModel) subtree — mirroring the whole Roblox
    /// explorer, storage services included — is DEACTIVATED (not destroyed) when detached from
    /// the tree so re-parenting stays cheap, and releases its backing object on Destroy. The
    /// Unity adapter renders storage-service subtrees inactive; the physical world is Workspace.
    /// </summary>
    public interface IInstanceBackingBinder
    {
        /// <summary>The instance entered the scene-root subtree — materialize its backing object.</summary>
        void OnEnteredWorld(InstanceRecord record);

        /// <summary>The instance left the scene-root subtree — deactivate, do not destroy (D5).</summary>
        void OnLeftWorld(InstanceRecord record);

        /// <summary>The instance was destroyed — release any backing object (D6 step 6).</summary>
        void OnDestroyed(InstanceRecord record);

        /// <summary>
        /// The instance moved to a new parent while staying inside the scene-root subtree —
        /// mirror the move in the backing hierarchy (transform re-parenting in Unity). Fired
        /// only for materialized instances; membership changes fire OnEnteredWorld/OnLeftWorld
        /// instead.
        /// </summary>
        void OnReparented(InstanceRecord record);

        /// <summary>Name changed on a materialized instance — sync the backing object's name.</summary>
        void OnNameChanged(InstanceRecord record);

        /// <summary>
        /// Copies whatever backing state belongs to <paramref name="sourceId"/> onto
        /// <paramref name="destinationId"/> (e.g. BasePart spatial/appearance state kept outside
        /// the registry). Called by <see cref="RbxInstance"/>'s clone path so a completed Clone()
        /// never depends on the caller separately walking that external state. No-op for hosts
        /// that keep no such state.
        /// WHY here and not a registry-level copy: the state this copies (BasePart Size/CFrame/
        /// Color/Anchored/...) lives in an IPartPropertySink implemented alongside the Unity
        /// backing binder, in an assembly that references Instances — not the reverse. Declaring
        /// the copy on the binder seam (already owned by the registry) lets the engine-free side
        /// trigger the copy without ever seeing the sink's type.
        /// </summary>
        void CopyBackingState(InstanceId sourceId, InstanceId destinationId);
    }

    /// <summary>Null object for hosts that bind nothing (headless tests, storage-only trees).</summary>
    public sealed class NullInstanceBackingBinder : IInstanceBackingBinder
    {
        public static readonly NullInstanceBackingBinder Instance = new();

        public void OnEnteredWorld(InstanceRecord record)
        {
        }

        public void OnLeftWorld(InstanceRecord record)
        {
        }

        public void OnDestroyed(InstanceRecord record)
        {
        }

        public void OnReparented(InstanceRecord record)
        {
        }

        public void OnNameChanged(InstanceRecord record)
        {
        }

        public void CopyBackingState(InstanceId sourceId, InstanceId destinationId)
        {
        }
    }
}

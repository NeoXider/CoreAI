using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// One-way property push for the MVP1 Part surface: the Lua bindings layer calls these
    /// when a script writes a Part property; the binder converts through RbxSpace and
    /// updates the backing GameObject. Signatures are engine-free (Roblox datatypes + ids
    /// only) so callers never touch UnityEngine types (D2 lint). <see cref="GetLivePositionStuds"/>
    /// is the one read that crosses back the other way, for the same reason.
    /// TODO: MVP8 — AssemblyLinearVelocity/AssemblyAngularVelocity reads for unanchored bodies (the
    /// pose already reads back through <see cref="GetPartPropertiesOrDefault"/>).
    /// </summary>
    public interface IPartPropertySink
    {
        void SetCFrame(InstanceId id, in RbxCFrame cframe);

        /// <summary>Sets the position keeping the orientation (Roblox Part.Position).</summary>
        void SetPosition(InstanceId id, RbxVector3 position);

        /// <summary>Sets Part.Size; every finite axis is clamped into
        /// <see cref="PartPropertyBounds"/>' Roblox range.</summary>
        void SetSize(InstanceId id, RbxVector3 size);

        void SetColor(InstanceId id, RbxColor3 color);

        void SetAnchored(InstanceId id, bool anchored);

        void SetTransparency(InstanceId id, float transparency);

        void SetCanCollide(InstanceId id, bool canCollide);

        /// <summary>Sets Part.Shape (Enum.PartType); the binder swaps the backing primitive.</summary>
        void SetShape(InstanceId id, RbxPartShape shape);

        /// <summary>Sets Part.Material (Enum.Material) through the render-side provider.</summary>
        void SetMaterial(InstanceId id, in RbxMaterialId material);

        /// <summary>Sets Part.MaterialVariant by name (null or empty restores plain Material).</summary>
        void SetMaterialVariant(InstanceId id, string variantName);

        /// <summary>Re-resolves the surface of every part wearing this MaterialVariant, after the
        /// variant's own properties changed.</summary>
        void RefreshMaterialVariant(string variantName);

        /// <summary>Full-state push (bulk restore / Instance.new initialization). The id is a live
        /// part from here on, even if an earlier instance with the same id was destroyed.</summary>
        void SetPartProperties(InstanceId id, in PartProperties properties);

        /// <summary>True when a state bundle has been stored for the id, including the retained
        /// last-known state of a recently destroyed part.</summary>
        bool TryGetPartProperties(InstanceId id, out PartProperties properties);

        /// <summary>
        /// Stored state, or Roblox Part defaults when none was pushed yet. A part the physics
        /// simulation moves answers with the pose its body actually has, and a recently destroyed
        /// part answers with its last-known state (see <see cref="OnPartDestroyed"/>).
        /// </summary>
        PartProperties GetPartPropertiesOrDefault(InstanceId id);

        /// <summary>
        /// Live position in studs, read from a materialized part's own backing object rather than
        /// the last value a script pushed through <see cref="SetPosition"/>/<see cref="SetCFrame"/>.
        /// </summary>
        /// <remarks>
        /// WHY: a character motor or the world's gravity moves the backing Rigidbody directly and
        /// never writes back into the stored <see cref="PartProperties"/>, so reading the stored
        /// value gave every distance query a position frozen at spawn (or at the last script
        /// write) no matter how far the part had actually moved. Falls back to the stored/default
        /// position for a part with no backing object yet — an unmaterialized part genuinely has
        /// no live transform to read. The default body keeps this behavior for the headless sink
        /// (<see cref="InMemoryPartPropertySink"/>), which never materializes anything.
        /// </remarks>
        RbxVector3 GetLivePositionStuds(InstanceId id)
        {
            return GetPartPropertiesOrDefault(id).Position;
        }

        /// <summary>
        /// The part was destroyed: its live state is released and a bounded last-known copy is kept
        /// (<see cref="InMemoryPartPropertySink.DestroyedPartRetention"/> parts, oldest forgotten
        /// first), so reads made by Destroying/AncestryChanged handlers — which run after the
        /// destruction completed — answer with the part's final values instead of defaults.
        /// Idempotent; a no-op for an id with no stored state.
        /// </summary>
        /// <remarks>
        /// WHY a default body: added after the interface shipped, so a host outside this repo with
        /// its own implementation keeps compiling without one.
        /// </remarks>
        void OnPartDestroyed(InstanceId id)
        {
        }
    }

    /// <summary>
    /// Roblox's documented bounds for BasePart values, applied at the sink boundary so a host-side
    /// writer (tween host, restore, world adapter, character seeding) gets the same answer a Lua
    /// assignment gets.
    /// </summary>
    public static class PartPropertyBounds
    {
        /// <summary>Smallest Part.Size axis ("as low as 0.001", BasePart.yaml).</summary>
        public const float MinimumSizeStuds = 0.001f;

        /// <summary>Largest Part.Size axis ("as high as 2048", BasePart.yaml).</summary>
        public const float MaximumSizeStuds = 2048f;

        /// <summary>
        /// Clamps every finite axis into [<see cref="MinimumSizeStuds"/>, <see cref="MaximumSizeStuds"/>];
        /// a zero or negative axis rises to the minimum instead of mirroring the mesh. A non-finite
        /// axis is returned unchanged, because whether it is refused or kept is the caller's rule.
        /// </summary>
        public static RbxVector3 ClampSize(RbxVector3 size)
        {
            return new RbxVector3(ClampSizeAxis(size.X), ClampSizeAxis(size.Y), ClampSizeAxis(size.Z));
        }

        /// <summary>True when no component is NaN or infinite.</summary>
        public static bool IsFinite(RbxVector3 vector)
        {
            return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
        }

        /// <summary>True when neither the position nor any rotation axis holds NaN or infinity.</summary>
        public static bool IsFinite(in RbxCFrame cframe)
        {
            return IsFinite(cframe.Position) && IsFinite(cframe.XVector)
                                             && IsFinite(cframe.YVector)
                                             && IsFinite(cframe.ZVector);
        }

        private static bool IsFinite(float component)
        {
            return !float.IsNaN(component) && !float.IsInfinity(component);
        }

        private static float ClampSizeAxis(float axis)
        {
            if (!IsFinite(axis))
            {
                return axis;
            }

            return axis < MinimumSizeStuds ? MinimumSizeStuds
                : axis > MaximumSizeStuds ? MaximumSizeStuds
                : axis;
        }
    }
}

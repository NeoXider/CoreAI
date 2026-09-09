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
    /// TODO: MVP8 — reverse sync for unanchored bodies (full CFrame/AssemblyLinearVelocity reads).
    /// </summary>
    public interface IPartPropertySink
    {
        void SetCFrame(InstanceId id, in RbxCFrame cframe);

        /// <summary>Sets the position keeping the orientation (Roblox Part.Position).</summary>
        void SetPosition(InstanceId id, RbxVector3 position);

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

        /// <summary>Full-state push (bulk restore / Instance.new initialization).</summary>
        void SetPartProperties(InstanceId id, in PartProperties properties);

        /// <summary>True when a state bundle has been stored for the id.</summary>
        bool TryGetPartProperties(InstanceId id, out PartProperties properties);

        /// <summary>Stored state, or Roblox Part defaults when none was pushed yet.</summary>
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
    }
}

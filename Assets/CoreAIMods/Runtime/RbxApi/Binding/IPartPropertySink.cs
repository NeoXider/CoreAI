using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// One-way property push for the MVP1 Part surface: the Lua bindings layer calls these
    /// when a script writes a Part property; the binder converts through RobloxSpace and
    /// updates the backing GameObject. Signatures are engine-free (Roblox datatypes + ids
    /// only) so callers never touch UnityEngine types (D2 lint). Reverse sync (Unity physics
    /// writing back into registry state) is out of scope until the physics rung.
    /// TODO: MVP8 — reverse sync for unanchored bodies (Position/AssemblyLinearVelocity reads).
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

        /// <summary>Full-state push (bulk restore / Instance.new initialization).</summary>
        void SetPartProperties(InstanceId id, in PartProperties properties);

        /// <summary>True when a state bundle has been stored for the id.</summary>
        bool TryGetPartProperties(InstanceId id, out PartProperties properties);

        /// <summary>Stored state, or Roblox Part defaults when none was pushed yet.</summary>
        PartProperties GetPartPropertiesOrDefault(InstanceId id);
    }
}

using CoreAI.Mods.Roblox.Datatypes;

namespace CoreAI.Mods.Roblox.Binding
{
    /// <summary>
    /// The MVP1 Part rendering surface (ROBLOX_API_ROADMAP.md §5.1.3) as one value bundle:
    /// CFrame/Size/Color/Anchored/Transparency/CanCollide. Engine-free by design so the Lua
    /// bindings layer can build and push it without touching UnityEngine types; the binder is
    /// the only consumer that converts it (through RobloxSpace, D2).
    /// </summary>
    public struct PartProperties
    {
        /// <summary>World-space CFrame in Roblox coordinates (right-handed, studs).</summary>
        public RbxCFrame CFrame;

        /// <summary>Extents in studs; the binder applies localScale = Size * MetersPerStud (D3).</summary>
        public RbxVector3 Size;

        public RbxColor3 Color;

        /// <summary>True = no Rigidbody on the backing object (static geometry).</summary>
        public bool Anchored;

        /// <summary>0 = opaque, 1 = invisible (renderer disabled at 1, Roblox parity).</summary>
        public float Transparency;

        /// <summary>Toggles the backing collider.</summary>
        public bool CanCollide;

        /// <summary>Position component of the CFrame; setting it keeps the orientation
        /// (Roblox Part.Position semantics).</summary>
        public RbxVector3 Position
        {
            get => CFrame.Position;
            set => CFrame = RbxCFrame.FromPosition(value) * CFrame.Rotation;
        }

        /// <summary>Roblox Part defaults: 4x1x2 studs, medium stone grey, opaque, collidable.</summary>
        public static PartProperties CreateDefault() => new PartProperties
        {
            CFrame = RbxCFrame.Identity,
            Size = new RbxVector3(4f, 1f, 2f),
            Color = RbxColor3.FromRGB(163f, 162f, 165f),
            Anchored = false,
            Transparency = 0f,
            CanCollide = true
        };
    }
}

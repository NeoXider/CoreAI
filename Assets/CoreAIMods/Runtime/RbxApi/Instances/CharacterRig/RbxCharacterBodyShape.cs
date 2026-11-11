using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// The upright capsule a character's physics body takes, from its root part's size: from the feet (the
    /// root's bottom face, which rests on the floor) to the top of the head, as wide as a block-game character.
    /// Engine-free; the Unity motor turns it into a collider on the root.
    /// </summary>
    /// <remarks>
    /// WHY a capsule over the whole body and not the root's 2x2x1 box: the box was the character's only
    /// collider, two studs tall under a five-stud body, so the head and torso went through anything above two
    /// studs, and its square bottom edge caught on the top edge of a block it was jumping onto. The capsule
    /// covers the body the player sees, and its round bottom slides over block edges. WHY from the root's size:
    /// <c>Model:ScaleTo</c> scales the root with the rest of the body, so the capsule follows any scale with no
    /// separate setting. DEV candidate: Roblox's R6 body collides with its Torso and Head and floats the root
    /// at HipHeight; CoreAI keeps the root on the floor (see <see cref="RbxR6Rig"/>) and collides with this
    /// capsule instead, so <c>HumanoidRootPart.Size</c> stays Roblox's 2x2x1.
    /// </remarks>
    public readonly struct RbxCharacterBodyShape
    {
        /// <summary>The capsule's radius as a share of the root's width (0.8 studs for Roblox's 2-stud root).</summary>
        public const float RadiusPerRootWidth = 0.4f;

        private RbxCharacterBodyShape(RbxVector3 rootSize, float height, float radius, float centreAboveRoot,
            float eyeAboveRoot)
        {
            RootSize = rootSize;
            Height = height;
            Radius = radius;
            CentreAboveRoot = centreAboveRoot;
            EyeAboveRoot = eyeAboveRoot;
        }

        /// <summary>The root part size the shape was made for, in studs.</summary>
        public RbxVector3 RootSize { get; }

        /// <summary>The capsule's full height, feet to the top of the head, in studs.</summary>
        public float Height { get; }

        /// <summary>The capsule's radius, in studs.</summary>
        public float Radius { get; }

        /// <summary>How far the capsule's centre sits above the root's centre, in studs.</summary>
        public float CentreAboveRoot { get; }

        /// <summary>How far the head's centre (the first-person eye) sits above the root's centre, in studs.</summary>
        public float EyeAboveRoot { get; }

        /// <summary>How far the capsule's bottom (the feet) sits below the root's centre, in studs.</summary>
        public float BottomBelowRoot => Height * 0.5f - CentreAboveRoot;

        /// <summary>
        /// <see cref="Height"/> in the root's own units (divided by its height): constant for any uniform
        /// scale, so a collider on the scaled root object keeps it.
        /// </summary>
        public float LocalHeight => RootSize.Y > 0f ? Height / RootSize.Y : 0f;

        /// <summary><see cref="CentreAboveRoot"/> in the root's own units (divided by its height).</summary>
        public float LocalCentreY => RootSize.Y > 0f ? CentreAboveRoot / RootSize.Y : 0f;

        /// <summary>
        /// <see cref="Radius"/> in the root's own units: an upright capsule's radius scales with the larger of
        /// the root's two horizontal sizes.
        /// </summary>
        public float LocalRadius
        {
            get
            {
                float across = Math.Max(Math.Abs(RootSize.X), Math.Abs(RootSize.Z));
                return across > 0f ? Radius / across : 0f;
            }
        }

        /// <summary>
        /// The body of an R6 character whose root part is <paramref name="rootSize"/> studs: the default 2x2x1
        /// root gives a capsule 5 studs tall and 1.6 studs wide, its feet on the root's bottom face and its top at
        /// the head's top; every length follows the root's height, the width follows its width.
        /// </summary>
        public static RbxCharacterBodyShape ForR6Root(RbxVector3 rootSize)
        {
            float rootHeight = Math.Abs(rootSize.Y);
            float scale = rootHeight / RbxR6Rig.PartSizes[(int)RbxR6Part.HumanoidRootPart].Y;
            float bottom = rootHeight * 0.5f;
            float eye = RbxR6Rig.HeadOffsetFromRoot.Y * scale;
            float top = eye + RbxR6Rig.PartSizes[(int)RbxR6Part.Head].Y * 0.5f * scale;
            float height = top + bottom;
            float radius = Math.Min(RadiusPerRootWidth * Math.Abs(rootSize.X), height * 0.5f);
            return new RbxCharacterBodyShape(rootSize, height, radius, (top - bottom) * 0.5f, eye);
        }
    }
}

using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// The engine seam <see cref="RbxCharacterRigs"/> poses characters through: it reads a part's pose and
    /// writes one, in studs, by instance id.
    /// </summary>
    /// <remarks>
    /// WHY a port and not the part-property sink itself: BasePart state lives in a sink this engine-free
    /// assembly cannot reference (it sits beside the Unity binder). The bindings layer adapts its sink to
    /// this port, the same way the character factory reaches the sink through a seeder.
    /// </remarks>
    public interface IRbxRigPosePort
    {
        /// <summary>The part's current pose (the live body pose of a simulated part) and its Anchored flag.</summary>
        bool TryGetPose(InstanceId part, out RbxCFrame cframe, out bool anchored);

        /// <summary>True when the part is anchored; false for a part with no state yet.</summary>
        bool IsAnchored(InstanceId part);

        /// <summary>
        /// Moves the part to <paramref name="cframe"/> the way physics would: no <c>Changed</c>, no revision,
        /// no replication. Called once per posed part per frame.
        /// </summary>
        void SetPose(InstanceId part, RbxCFrame cframe);

        /// <summary>
        /// Tells the engine a part is (or no longer is) held by a joint: while driven, an unanchored part has no
        /// simulation of its own, as a Motor6D's Part1 is carried by its assembly in Roblox.
        /// </summary>
        void SetDriven(InstanceId part, bool driven);
    }

    /// <summary>
    /// Pushes the starting properties of a part the character factory builds into whatever holds BasePart
    /// state. The factory calls it before the character enters the world, so the part materializes right.
    /// </summary>
    public interface IRbxCharacterPartSeeder
    {
        /// <summary>Stores <paramref name="seed"/> as <paramref name="part"/>'s properties.</summary>
        void Seed(RbxInstance part, in RbxCharacterPartSeed seed);
    }

    /// <summary>The BasePart properties a spawned character part starts with.</summary>
    public readonly struct RbxCharacterPartSeed
    {
        /// <summary>Creates a seed.</summary>
        public RbxCharacterPartSeed(RbxVector3 size, RbxCFrame cframe, RbxColor3 color, bool hasColor,
            float transparency, bool canCollide, bool anchored)
        {
            Size = size;
            CFrame = cframe;
            Color = color;
            HasColor = hasColor;
            Transparency = transparency;
            CanCollide = canCollide;
            Anchored = anchored;
        }

        /// <summary>Part.Size in studs.</summary>
        public RbxVector3 Size { get; }

        /// <summary>Part.CFrame in studs.</summary>
        public RbxCFrame CFrame { get; }

        /// <summary>Part.Color; ignored unless <see cref="HasColor"/>.</summary>
        public RbxColor3 Color { get; }

        /// <summary>True when the seed sets a colour; false keeps the Part default.</summary>
        public bool HasColor { get; }

        /// <summary>Part.Transparency.</summary>
        public float Transparency { get; }

        /// <summary>Part.CanCollide.</summary>
        public bool CanCollide { get; }

        /// <summary>Part.Anchored.</summary>
        public bool Anchored { get; }
    }
}

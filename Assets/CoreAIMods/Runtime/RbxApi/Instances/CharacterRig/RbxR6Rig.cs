using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>The seven parts of Roblox's R6 character, in the order <see cref="RbxR6Rig"/> indexes them.</summary>
    public enum RbxR6Part
    {
        /// <summary><c>HumanoidRootPart</c>: the body the motor moves; invisible.</summary>
        HumanoidRootPart = 0,

        /// <summary><c>Torso</c>.</summary>
        Torso = 1,

        /// <summary><c>Head</c>.</summary>
        Head = 2,

        /// <summary><c>Right Arm</c>.</summary>
        RightArm = 3,

        /// <summary><c>Left Arm</c>.</summary>
        LeftArm = 4,

        /// <summary><c>Right Leg</c>.</summary>
        RightLeg = 5,

        /// <summary><c>Left Leg</c>.</summary>
        LeftLeg = 6
    }

    /// <summary>
    /// The classic R6 character: part names and sizes, the default body colours, and the six joints with
    /// Roblox's own <c>Motor6D</c> C0/C1 values.
    /// </summary>
    /// <remarks>
    /// Joint <c>j</c> moves part <c>j + 1</c> (<see cref="JointOf"/>): the RootJoint moves the Torso from the
    /// HumanoidRootPart, and the Neck, both Shoulders and both Hips hang from the Torso. Each joint swings
    /// about its own Z axis, as a Motor6D's angle does.
    /// <para>
    /// DEV candidate (the root joint lift): in Roblox the Torso sits on the HumanoidRootPart and the Humanoid
    /// holds the root <c>LeftLeg.Size.Y + RootPart.Size.Y / 2 + HipHeight</c> (3 studs) above the floor. CoreAI's
    /// motor rests the 2x2x1 root on the floor instead (its centre 1 stud up) and every control test is tuned
    /// to that, so the RootJoint here lifts the Torso by one leg length (<see cref="TorsoLiftStuds"/>) and the
    /// feet meet the floor. Scripts that read <c>Torso.Position</c> see it 2 studs above the root, not on it.
    /// </para>
    /// </remarks>
    public static class RbxR6Rig
    {
        /// <summary>How many parts an R6 character has.</summary>
        public const int PartCount = 7;

        /// <summary>How many joints connect them.</summary>
        public const int JointCount = 6;

        /// <summary>How far the RootJoint lifts the Torso above the HumanoidRootPart, in studs (one leg length).</summary>
        public const float TorsoLiftStuds = 2f;

        /// <summary>The Roblox part names, indexed by <see cref="RbxR6Part"/>.</summary>
        public static readonly string[] PartNames =
        {
            "HumanoidRootPart", "Torso", "Head", "Right Arm", "Left Arm", "Right Leg", "Left Leg"
        };

        /// <summary>The Roblox part sizes in studs, indexed by <see cref="RbxR6Part"/>.</summary>
        public static readonly RbxVector3[] PartSizes =
        {
            new(2f, 2f, 1f), new(2f, 2f, 1f), new(2f, 1f, 1f),
            new(1f, 2f, 1f), new(1f, 2f, 1f), new(1f, 2f, 1f), new(1f, 2f, 1f)
        };

        /// <summary>Mirror <c>BodyColors.HeadColor3</c>, <c>LeftArmColor3</c> and <c>RightArmColor3</c> default (253, 234, 141).</summary>
        public static readonly RbxColor3 SkinColor = RbxColor3.FromRGB(253f, 234f, 141f);

        /// <summary>Mirror <c>BodyColors.TorsoColor3</c> default (40, 127, 71).</summary>
        public static readonly RbxColor3 TorsoColor = RbxColor3.FromRGB(40f, 127f, 71f);

        /// <summary>Mirror <c>BodyColors.LeftLegColor3</c> and <c>RightLegColor3</c> default (13, 105, 172).</summary>
        public static readonly RbxColor3 LegColor = RbxColor3.FromRGB(13f, 105f, 172f);

        /// <summary>
        /// The six joints, indexed by joint (RootJoint, Neck, Right Shoulder, Left Shoulder, Right Hip, Left Hip).
        /// </summary>
        public static readonly RbxRigJoint[] Joints =
        {
            new("RootJoint", RbxR6Part.HumanoidRootPart, RbxR6Part.Torso,
                new RbxCFrame(0f, TorsoLiftStuds, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f),
                new RbxCFrame(0f, 0f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f)),
            new("Neck", RbxR6Part.Torso, RbxR6Part.Head,
                new RbxCFrame(0f, 1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f),
                new RbxCFrame(0f, -0.5f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f)),
            new("Right Shoulder", RbxR6Part.Torso, RbxR6Part.RightArm,
                new RbxCFrame(1f, 0.5f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f),
                new RbxCFrame(-0.5f, 0.5f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f)),
            new("Left Shoulder", RbxR6Part.Torso, RbxR6Part.LeftArm,
                new RbxCFrame(-1f, 0.5f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f),
                new RbxCFrame(0.5f, 0.5f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f)),
            new("Right Hip", RbxR6Part.Torso, RbxR6Part.RightLeg,
                new RbxCFrame(1f, -1f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f),
                new RbxCFrame(0.5f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f)),
            new("Left Hip", RbxR6Part.Torso, RbxR6Part.LeftLeg,
                new RbxCFrame(-1f, -1f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f),
                new RbxCFrame(-0.5f, 1f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f))
        };

        /// <summary>Each part's rest pose relative to the HumanoidRootPart, indexed by <see cref="RbxR6Part"/>.</summary>
        public static readonly RbxCFrame[] RestOffsets = BuildRestOffsets();

        /// <summary>
        /// The Head's centre above the HumanoidRootPart at rest, in studs: where Roblox's camera and eye-height
        /// scripts look for the head (3.5 studs here, 1.5 in Roblox, because of <see cref="TorsoLiftStuds"/>).
        /// </summary>
        public static RbxVector3 HeadOffsetFromRoot => RestOffsets[(int)RbxR6Part.Head].Position;

        /// <summary>The joint that moves <paramref name="part"/>; -1 for the HumanoidRootPart, which no joint moves.</summary>
        public static int JointOf(RbxR6Part part)
        {
            return (int)part - 1;
        }

        /// <summary>The Roblox name of <paramref name="part"/>.</summary>
        public static string NameOf(RbxR6Part part)
        {
            return PartNames[(int)part];
        }

        /// <summary>
        /// The properties a freshly spawned default character's <paramref name="part"/> starts with, for a root at
        /// <paramref name="rootCFrame"/>.
        /// </summary>
        /// <remarks>
        /// The root keeps the box the controls were tuned on (2x2x1, collidable) and is invisible, as Roblox's
        /// HumanoidRootPart is (Transparency 1). The Torso and Head report CanCollide true and the arms and legs
        /// false, as R6's do, so a camera occlusion test or a script sees Roblox's values; every part is
        /// unanchored, as in Roblox. DEV candidate: while the rig holds them none of them pushes a body — the
        /// engine runs a held part as a kinematic trigger — because without joints a part cannot join the root's
        /// physical body, and a solid Torso posed over the root would shove the root it follows. The root stays
        /// the character's only collider.
        /// </remarks>
        public static RbxCharacterPartSeed SeedFor(RbxR6Part part, RbxCFrame rootCFrame)
        {
            int index = (int)part;
            if (part == RbxR6Part.HumanoidRootPart)
            {
                return new RbxCharacterPartSeed(PartSizes[index], rootCFrame, default, false, 1f, true, false);
            }

            RbxColor3 color = part == RbxR6Part.Torso ? TorsoColor
                : part == RbxR6Part.RightLeg || part == RbxR6Part.LeftLeg ? LegColor
                : SkinColor;
            bool canCollide = part == RbxR6Part.Torso || part == RbxR6Part.Head;
            return new RbxCharacterPartSeed(PartSizes[index], rootCFrame * RestOffsets[index], color, true, 0f,
                canCollide, false);
        }

        private static RbxCFrame[] BuildRestOffsets()
        {
            RbxCFrame[] offsets = new RbxCFrame[PartCount];
            offsets[0] = RbxCFrame.Identity;
            RbxCFrame torso = Joints[0].Part1CFrame(RbxCFrame.Identity, RbxCFrame.Identity);
            offsets[1] = torso;
            for (int joint = 1; joint < JointCount; joint++)
            {
                offsets[joint + 1] = Joints[joint].Part1CFrame(torso, RbxCFrame.Identity);
            }

            return offsets;
        }
    }
}

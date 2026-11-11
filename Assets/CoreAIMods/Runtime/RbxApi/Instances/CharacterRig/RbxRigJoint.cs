using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// One joint of a character rig in the shape of Roblox's <c>Motor6D</c>:
    /// <c>Part1.CFrame = Part0.CFrame * C0 * Transform * C1:Inverse()</c>, where <c>Transform</c> is the
    /// pose an animator writes each frame and C0/C1 stay fixed.
    /// </summary>
    /// <remarks>
    /// WHY the Motor6D shape rather than plain offsets: <c>Motor6D</c> is still a loud stub, and when it lands
    /// as a real instance its C0, C1, Part0 and Part1 are exactly these fields, so the poser keeps writing
    /// <c>Transform</c> and the pose maths does not change. The values in <see cref="RbxR6Rig"/> are Roblox's
    /// own R6 joint values, so a script that later reads <c>Torso["Right Shoulder"].C0</c> sees the familiar
    /// numbers.
    /// </remarks>
    public readonly struct RbxRigJoint
    {
        /// <summary>Creates a joint from <paramref name="part0"/> to <paramref name="part1"/>.</summary>
        public RbxRigJoint(string name, RbxR6Part part0, RbxR6Part part1, RbxCFrame c0, RbxCFrame c1)
        {
            Name = name;
            Part0 = part0;
            Part1 = part1;
            C0 = c0;
            C1 = c1;
            C1Inverse = c1.Inverse();
        }

        /// <summary>The Roblox name of the joint (<c>Neck</c>, <c>Right Shoulder</c>, ...).</summary>
        public string Name { get; }

        /// <summary>The part the joint hangs from.</summary>
        public RbxR6Part Part0 { get; }

        /// <summary>The part the joint moves.</summary>
        public RbxR6Part Part1 { get; }

        /// <summary>The joint frame in <see cref="Part0"/>'s space (Motor6D.C0).</summary>
        public RbxCFrame C0 { get; }

        /// <summary>The joint frame in <see cref="Part1"/>'s space (Motor6D.C1).</summary>
        public RbxCFrame C1 { get; }

        /// <summary><see cref="C1"/> inverted once, so a pose costs two multiplies per joint.</summary>
        public RbxCFrame C1Inverse { get; }

        /// <summary>Where <see cref="Part1"/> sits for a <paramref name="part0"/> pose and a joint transform.</summary>
        public RbxCFrame Part1CFrame(RbxCFrame part0, RbxCFrame transform)
        {
            return part0 * C0 * transform * C1Inverse;
        }

        /// <summary>
        /// <see cref="Part1CFrame(RbxCFrame, RbxCFrame)"/> for a rig scaled by <paramref name="scale"/>
        /// (<c>Model:ScaleTo</c>): the joint's offsets, the positions of C0 and C1, grow with the scale, as the
        /// mirror scales joint lengths; the angles do not.
        /// </summary>
        public RbxCFrame Part1CFrame(RbxCFrame part0, RbxCFrame transform, double scale)
        {
            if (scale == 1d)
            {
                return Part1CFrame(part0, transform);
            }

            float factor = (float)scale;
            RbxCFrame c0 = C0.Rotation + C0.Position * factor;
            RbxCFrame c1 = C1.Rotation + C1.Position * factor;
            return part0 * c0 * transform * c1.Inverse();
        }
    }
}

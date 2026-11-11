using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// Engine-free contracts of a character's body and its scale: the body capsule reaches from the root's
    /// bottom face to the head's top at any scale (the root box alone was two studs tall, twin), a block-game
    /// character is 1.8 blocks tall with its eyes at 1.62 at ScaleTo(1.44), <c>Model:ScaleTo</c>'s geometry goes
    /// back exactly to scale 1 and refuses a factor that is not above 0, and a scaled joint keeps its angle while
    /// its offsets grow.
    /// </summary>
    [TestFixture]
    public sealed class CharacterBodyShapeEditModeTests
    {
        private static readonly RbxVector3 DefaultRoot = new(2f, 2f, 1f);

        [Test]
        public void TheBodyCapsule_ReachesFromTheFeetToTheTopOfTheHead()
        {
            RbxCharacterBodyShape shape = RbxCharacterBodyShape.ForR6Root(DefaultRoot);

            Assert.AreEqual(5f, shape.Height, 1e-4f, "legs, torso and head: five studs");
            Assert.AreEqual(1f, shape.BottomBelowRoot, 1e-4f, "the feet are the root's bottom face, on the floor");
            Assert.AreEqual(4f, shape.CentreAboveRoot + shape.Height * 0.5f, 1e-4f, "the top is the head's top");
            Assert.AreEqual(RbxR6Rig.HeadOffsetFromRoot.Y, shape.EyeAboveRoot, 1e-4f, "the eye is the head's centre");
            Assert.AreEqual(0.8f, shape.Radius, 1e-4f);
        }

        [Test]
        public void TheRootBoxAlone_IsShorterThanTheBody_NegativeTwin()
        {
            RbxCharacterBodyShape shape = RbxCharacterBodyShape.ForR6Root(DefaultRoot);

            Assert.Less(DefaultRoot.Y, shape.Height * 0.5f,
                "the old collider, the 2-stud root, let the head and torso through anything above two studs");
            Assert.Less(DefaultRoot.Y, 4f, "and fitted under a one-block gap the body cannot pass");
        }

        [Test]
        public void AtScale1_44_TheCharacterIsBlockGameSized()
        {
            RbxCharacterBodyShape shape = RbxCharacterBodyShape.ForR6Root(DefaultRoot * 1.44f);
            const float block = 4f;

            Assert.AreEqual(1.8f, shape.Height / block, 1e-3f, "1.8 blocks tall");
            Assert.AreEqual(1.62f, (shape.BottomBelowRoot + shape.EyeAboveRoot) / block, 1e-3f, "eyes at 1.62 blocks");
            Assert.AreEqual(0.6f, shape.Radius * 2f / block, 0.03f, "about 0.6 block wide");
            Assert.Greater(shape.Height, block, "it does not fit under a one-block gap");
            Assert.Less(shape.Height, 2f * block, "and fits under a two-block one");
        }

        [Test]
        public void TheLocalShape_IsTheSameAtEveryUniformScale()
        {
            RbxCharacterBodyShape small = RbxCharacterBodyShape.ForR6Root(DefaultRoot);
            RbxCharacterBodyShape large = RbxCharacterBodyShape.ForR6Root(DefaultRoot * 1.44f);

            Assert.AreEqual(small.LocalHeight, large.LocalHeight, 1e-5f);
            Assert.AreEqual(small.LocalCentreY, large.LocalCentreY, 1e-5f);
            Assert.AreEqual(small.LocalRadius, large.LocalRadius, 1e-5f,
                "a collider on the root object keeps its numbers when ScaleTo grows the root");
            Assert.AreEqual(2.5f, small.LocalHeight, 1e-5f);
        }

        [Test]
        public void ScaleTo_GrowsSizesAndOffsetsAroundThePivot_AndScaleOneGivesTheModelBack()
        {
            RbxCFrame pivot = RbxCFrame.FromPosition(10f, 2f, -4f) * RbxCFrame.Angles(0f, 0.7f, 0f);
            RbxCFrame part = pivot * RbxCFrame.FromPosition(0f, 3.5f, 0.25f) * RbxCFrame.Angles(0.2f, 0f, 0f);
            RbxVector3 size = new(2f, 1f, 1f);
            RbxModelScaling.OriginAtScaleOne(pivot, part, size, 1d, out RbxVector3 sizeAtOne, out RbxCFrame offsetAtOne);

            RbxModelScaling.Scaled(pivot, sizeAtOne, offsetAtOne, 1.44d, out RbxVector3 grown, out RbxCFrame moved);
            RbxCFrame local = pivot.Inverse() * moved;
            Assert.AreEqual(2.88f, grown.X, 1e-4f);
            Assert.AreEqual(3.5f * 1.44f, local.Y, 1e-3f, "the offset from the pivot grows with the scale");
            Assert.AreEqual(0.25f * 1.44f, local.Z, 1e-3f);
            Assert.AreEqual((pivot.Inverse() * part).LookVector.Y, local.LookVector.Y, 1e-4f, "the angle stays");

            RbxModelScaling.OriginAtScaleOne(pivot, moved, grown, 1.44d, out RbxVector3 readBack, out RbxCFrame _);
            Assert.AreEqual(size.X, readBack.X, 1e-4f, "scale-1 values read back at another scale agree");
            RbxModelScaling.Scaled(pivot, sizeAtOne, offsetAtOne, 1d, out RbxVector3 original, out RbxCFrame back);
            Assert.AreEqual(size.Y, original.Y, 1e-6f);
            Assert.AreEqual(part.Position.X, back.Position.X, 1e-3f, "ScaleTo(1) puts the part back");
            Assert.AreEqual(part.Position.Y, back.Position.Y, 1e-3f);
            Assert.AreEqual(part.Position.Z, back.Position.Z, 1e-3f);
        }

        [TestCase(0d)]
        [TestCase(-1d)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void ScaleTo_RefusesAFactorThatIsNotAboveZero_NegativeTwin(double factor)
        {
            Assert.IsFalse(RbxModelScaling.IsValidFactor(factor));
            Assert.Throws<ArgumentOutOfRangeException>(() => RbxModelScaling.Scaled(RbxCFrame.Identity,
                new RbxVector3(1f, 1f, 1f), RbxCFrame.Identity, factor, out RbxVector3 _, out RbxCFrame _));
        }

        [Test]
        public void AScaledJoint_GrowsItsOffsets_AndKeepsItsAngle()
        {
            RbxRigJoint neck = RbxR6Rig.Joints[RbxR6Rig.JointOf(RbxR6Part.Head)];
            RbxCFrame torso = RbxCFrame.FromPosition(0f, 2f, 0f);
            RbxCFrame plain = neck.Part1CFrame(torso, RbxCFrame.Identity);
            RbxCFrame same = neck.Part1CFrame(torso, RbxCFrame.Identity, 1d);
            RbxCFrame scaled = neck.Part1CFrame(torso, RbxCFrame.Identity, 1.44d);

            Assert.AreEqual(plain.Position.Y, same.Position.Y, 1e-6f, "scale 1 is the plain joint");
            Assert.AreEqual(1.5f * 1.44f, scaled.Position.Y - torso.Position.Y, 1e-4f,
                "the head sits 1.5 studs above the torso centre, times the scale");
            Assert.AreEqual(plain.LookVector.Z, scaled.LookVector.Z, 1e-5f, "the angle stays");
        }
    }
}

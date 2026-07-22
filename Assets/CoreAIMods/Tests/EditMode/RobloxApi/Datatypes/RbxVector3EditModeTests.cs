using CoreAI.Mods.Roblox.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Datatypes
{
    /// <summary>Golden fixtures for the pure-spec Vector3 (ROBLOX_API_ROADMAP.md §5.1.8 item 9).</summary>
    [TestFixture]
    public sealed class RbxVector3EditModeTests
    {
        private const float Epsilon = 1e-5f;

        [Test]
        public void Constants_MatchRobloxValues()
        {
            Assert.AreEqual(new RbxVector3(0f, 0f, 0f), RbxVector3.Zero);
            Assert.AreEqual(new RbxVector3(1f, 1f, 1f), RbxVector3.One);
            Assert.AreEqual(new RbxVector3(1f, 0f, 0f), RbxVector3.XAxis);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), RbxVector3.YAxis);
            Assert.AreEqual(new RbxVector3(0f, 0f, 1f), RbxVector3.ZAxis);
        }

        [Test]
        public void Arithmetic_OperatorTable()
        {
            var a = new RbxVector3(1f, 2f, 3f);
            var b = new RbxVector3(4f, 5f, 6f);

            Assert.AreEqual(new RbxVector3(5f, 7f, 9f), a + b);
            Assert.AreEqual(new RbxVector3(-3f, -3f, -3f), a - b);
            Assert.AreEqual(new RbxVector3(-1f, -2f, -3f), -a);
            Assert.AreEqual(new RbxVector3(4f, 10f, 18f), a * b, "Vector3 * Vector3 is component-wise");
            Assert.AreEqual(new RbxVector3(2f, 4f, 6f), a * 2f);
            Assert.AreEqual(new RbxVector3(2f, 4f, 6f), 2f * a);
            Assert.AreEqual(new RbxVector3(0.25f, 0.4f, 0.5f), a / b);
            Assert.AreEqual(new RbxVector3(0.5f, 1f, 1.5f), a / 2f);
        }

        [Test]
        public void MagnitudeAndUnit_Golden()
        {
            var v = new RbxVector3(3f, 4f, 0f);
            Assert.AreEqual(5f, v.Magnitude, Epsilon);
            Assert.IsTrue(v.Unit.FuzzyEq(new RbxVector3(0.6f, 0.8f, 0f), Epsilon));
        }

        [Test]
        public void DotAndCross_RightHanded()
        {
            Assert.AreEqual(0f, RbxVector3.XAxis.Dot(RbxVector3.YAxis), Epsilon);
            Assert.AreEqual(32f, new RbxVector3(1f, 2f, 3f).Dot(new RbxVector3(4f, 5f, 6f)), Epsilon);

            // WHY: x cross y = z pins the right-handed convention (D1).
            Assert.IsTrue(RbxVector3.XAxis.Cross(RbxVector3.YAxis).FuzzyEq(RbxVector3.ZAxis, Epsilon));
            Assert.IsTrue(RbxVector3.YAxis.Cross(RbxVector3.ZAxis).FuzzyEq(RbxVector3.XAxis, Epsilon));
            Assert.IsTrue(RbxVector3.ZAxis.Cross(RbxVector3.XAxis).FuzzyEq(RbxVector3.YAxis, Epsilon));
        }

        [Test]
        public void Lerp_Midpoint()
        {
            var result = RbxVector3.Zero.Lerp(new RbxVector3(10f, -4f, 2f), 0.5f);
            Assert.IsTrue(result.FuzzyEq(new RbxVector3(5f, -2f, 1f), Epsilon));
        }

        [Test]
        public void Angle_UnsignedAndSigned()
        {
            float angle = RbxVector3.XAxis.Angle(RbxVector3.YAxis);
            Assert.AreEqual(System.MathF.PI / 2f, angle, Epsilon);

            float signed = RbxVector3.XAxis.Angle(RbxVector3.YAxis, RbxVector3.ZAxis);
            Assert.AreEqual(System.MathF.PI / 2f, signed, Epsilon);
            float signedOpposite = RbxVector3.XAxis.Angle(RbxVector3.YAxis, -RbxVector3.ZAxis);
            Assert.AreEqual(-System.MathF.PI / 2f, signedOpposite, Epsilon);
        }

        [Test]
        public void ComponentHelpers_AbsCeilFloorSignMaxMin()
        {
            var v = new RbxVector3(-1.5f, 2.5f, -0.5f);
            Assert.AreEqual(new RbxVector3(1.5f, 2.5f, 0.5f), v.Abs());
            Assert.AreEqual(new RbxVector3(-1f, 3f, 0f), v.Ceil());
            Assert.AreEqual(new RbxVector3(-2f, 2f, -1f), v.Floor());
            Assert.AreEqual(new RbxVector3(-1f, 1f, -1f), v.Sign());
            Assert.AreEqual(new RbxVector3(1f, 5f, 3f),
                new RbxVector3(1f, 2f, 3f).Max(new RbxVector3(0f, 5f, 2f)));
            Assert.AreEqual(new RbxVector3(0f, 2f, 2f),
                new RbxVector3(1f, 2f, 3f).Min(new RbxVector3(0f, 5f, 2f)));
        }

        [Test]
        public void FromNormalId_FrontIsNegativeZ()
        {
            var registry = RbxEnumRegistry.CreateWithBuiltins();
            RbxEnum normalId = registry.Get("NormalId");
            Assert.AreEqual(new RbxVector3(0f, 0f, -1f), RbxVector3.FromNormalId(normalId["Front"]));
            Assert.AreEqual(new RbxVector3(0f, 0f, 1f), RbxVector3.FromNormalId(normalId["Back"]));
            Assert.AreEqual(RbxVector3.YAxis, RbxVector3.FromNormalId(normalId["Top"]));

            RbxEnum axis = registry.Get("Axis");
            Assert.AreEqual(RbxVector3.ZAxis, RbxVector3.FromAxis(axis["Z"]));
        }

        [Test]
        public void ToString_MatchesRobloxFormat()
        {
            // WHY: corpus scripts string-match on tostring output (§5.1.5).
            Assert.AreEqual("1, 2, 3", new RbxVector3(1f, 2f, 3f).ToString());
            Assert.AreEqual("0.5, -2, 0", new RbxVector3(0.5f, -2f, 0f).ToString());
        }
    }
}

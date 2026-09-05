using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>Golden fixtures for Vector2, Color3, UDim/UDim2 and the Enum registry.</summary>
    [TestFixture]
    public sealed class RbxDatatypeMiscEditModeTests
    {
        private const float Epsilon = 1e-5f;

        // ---- Vector2 -----------------------------------------------------------------

        [Test]
        public void Vector2_CoreMath()
        {
            RbxVector2 a = new(3f, 4f);
            Assert.AreEqual(5f, a.Magnitude, Epsilon);
            Assert.IsTrue(a.Unit.FuzzyEq(new RbxVector2(0.6f, 0.8f), Epsilon));
            Assert.AreEqual(11f, a.Dot(new RbxVector2(1f, 2f)), Epsilon);
            Assert.AreEqual(2f, a.Cross(new RbxVector2(1f, 2f)), Epsilon);
            Assert.AreEqual(new RbxVector2(4f, 6f), a + new RbxVector2(1f, 2f));
            Assert.AreEqual(new RbxVector2(6f, 8f), a * 2f);
            Assert.AreEqual("3, 4", a.ToString());
        }

        [Test]
        public void Vector2_Angle_SignedUsesCrossSign()
        {
            float unsigned = RbxVector2.XAxis.Angle(RbxVector2.YAxis);
            Assert.AreEqual(System.MathF.PI / 2f, unsigned, Epsilon);
            float signed = RbxVector2.YAxis.Angle(RbxVector2.XAxis, true);
            Assert.AreEqual(-System.MathF.PI / 2f, signed, Epsilon);
        }

        // ---- Color3 ------------------------------------------------------------------

        [Test]
        public void Color3_FromRGB_ScalesTo01()
        {
            RbxColor3 c = RbxColor3.FromRGB(255f, 128f, 0f);
            Assert.AreEqual(1f, c.R, Epsilon);
            Assert.AreEqual(128f / 255f, c.G, Epsilon);
            Assert.AreEqual(0f, c.B, Epsilon);
        }

        [Test]
        public void Color3_FromRGB_FractionalChannelsUseLockedCoreAiRounding()
        {
            // WHY: The offline Roblox mirror does not document fractional rounding, so this is a
            // CoreAI compatibility decision rather than a documented Roblox golden.
            RbxColor3 c = RbxColor3.FromRGB(1.49f, 1.5f, 2.5f);
            Assert.AreEqual(1f / 255f, c.R, Epsilon);
            Assert.AreEqual(2f / 255f, c.G, Epsilon);
            Assert.AreEqual(3f / 255f, c.B, Epsilon);
        }

        [Test]
        public void Color3_FromHSV_PrimaryGoldens()
        {
            Assert.AreEqual(new RbxColor3(1f, 0f, 0f), RbxColor3.FromHSV(0f, 1f, 1f));
            RbxColor3 green = RbxColor3.FromHSV(1f / 3f, 1f, 1f);
            Assert.AreEqual(0f, green.R, Epsilon);
            Assert.AreEqual(1f, green.G, Epsilon);
            Assert.AreEqual(0f, green.B, Epsilon);
            // Hue 1.0 wraps to red.
            Assert.AreEqual(1f, RbxColor3.FromHSV(1f, 1f, 1f).R, Epsilon);
        }

        [Test]
        public void Color3_HexRoundTrip()
        {
            RbxColor3 c = RbxColor3.FromHex("#FF7800");
            Assert.AreEqual(1f, c.R, Epsilon);
            Assert.AreEqual(120f / 255f, c.G, Epsilon);
            Assert.AreEqual(0f, c.B, Epsilon);
            Assert.AreEqual("FF7800", c.ToHex());
            // 3-digit shorthand expands per web rules.
            Assert.AreEqual("FF8800", RbxColor3.FromHex("F80").ToHex());
        }

        [Test]
        public void Color3_ToHSV_InvertsFromHSV()
        {
            (float h, float s, float v) = RbxColor3.FromHSV(0.61f, 0.5f, 0.8f).ToHSV();
            Assert.AreEqual(0.61f, h, 1e-3f);
            Assert.AreEqual(0.5f, s, 1e-3f);
            Assert.AreEqual(0.8f, v, 1e-3f);
        }

        [Test]
        public void Color3_Lerp_Midpoint()
        {
            RbxColor3 mid = new RbxColor3(0f, 0f, 0f).Lerp(new RbxColor3(1f, 0.5f, 0f), 0.5f);
            Assert.AreEqual(0.5f, mid.R, Epsilon);
            Assert.AreEqual(0.25f, mid.G, Epsilon);
            Assert.AreEqual(0f, mid.B, Epsilon);
        }

        // ---- UDim / UDim2 ------------------------------------------------------------

        [Test]
        public void UDim_ArithmeticAndFormat()
        {
            RbxUDim a = new(0.5f, 10);
            RbxUDim b = new(0.25f, -4);
            Assert.AreEqual(new RbxUDim(0.75f, 6), a + b);
            Assert.AreEqual(new RbxUDim(0.25f, 14), a - b);
            Assert.AreEqual(new RbxUDim(-0.5f, -10), -a);
            Assert.AreEqual("{0.5, 10}", a.ToString());
        }

        [Test]
        public void UDim2_ConstructorsAndAliases()
        {
            RbxUDim2 full = new(0.5f, 10, 0.25f, 5);
            Assert.AreEqual(new RbxUDim(0.5f, 10), full.X);
            Assert.AreEqual(new RbxUDim(0.25f, 5), full.Y);
            Assert.AreEqual(full.X, full.Width);
            Assert.AreEqual(full.Y, full.Height);

            Assert.AreEqual(new RbxUDim2(0.3f, 0, 0.6f, 0), RbxUDim2.FromScale(0.3f, 0.6f));
            Assert.AreEqual(new RbxUDim2(0f, 30, 0f, 60), RbxUDim2.FromOffset(30, 60));
            Assert.AreEqual("{0.5, 10}, {0.25, 5}", full.ToString());
        }

        [Test]
        public void UDim2_LerpRoundsOffsets()
        {
            RbxUDim2 mid = RbxUDim2.FromOffset(0, 0).Lerp(RbxUDim2.FromOffset(11, 5), 0.5f);
            Assert.AreEqual(6, mid.X.Offset, "5.5 rounds away from half to 6");
            Assert.AreEqual(2, mid.Y.Offset, "2.5 rounds to even 2 (MathF.Round banker's rounding)");
        }

        // ---- Enum plumbing -----------------------------------------------------------

        [Test]
        public void EnumRegistry_BuiltinsSeeded_MaterialValuesMatchRoblox()
        {
            RbxEnumRegistry registry = RbxEnumRegistry.CreateWithBuiltins();
            RbxEnum material = registry.Get("Material");
            Assert.AreEqual(256, material["Plastic"].Value);
            Assert.AreEqual(1088, material["Metal"].Value);
            Assert.AreEqual(288, material["Neon"].Value);
            Assert.AreEqual("Enum.Material.Plastic", material["Plastic"].ToString());
            Assert.AreEqual("Enum.Material", material.ToString());

            RbxEnum partType = registry.Get("PartType");
            Assert.AreEqual(0, partType["Ball"].Value);
            Assert.AreEqual(1, partType["Block"].Value);
        }

        [Test]
        public void EnumRegistry_ItemsAreInterned_IdentityEquality()
        {
            RbxEnumRegistry registry = RbxEnumRegistry.CreateWithBuiltins();
            Assert.AreSame(registry.Get("Material")["Wood"], registry.Get("Material")["Wood"]);
        }

        [Test]
        public void EnumRegistry_UnknownEnum_RaisesLoudStub()
        {
            RbxEnumRegistry registry = RbxEnumRegistry.CreateWithBuiltins();
            // WHY: KeyCode shipped with the MVP1 input slice; EasingStyle landed with
            // TweenService (MVP8 slice 8.4), so RaycastFilterType (slice 8.5) is the
            // loud-stub probe now.
            RbxApiStubException ex = Assert.Throws<RbxApiStubException>(() => registry.Get("RaycastFilterType"));
            Assert.AreEqual("NOT_IMPLEMENTED", ex.Code);
            StringAssert.Contains("Enum.RaycastFilterType", ex.Message);
            StringAssert.Contains("| fix:", ex.Message, "stub errors carry the machine-parsable fix section");
        }

        [Test]
        public void Enum_UnknownItem_RaisesBadArgument()
        {
            RbxEnumRegistry registry = RbxEnumRegistry.CreateWithBuiltins();
            RbxApiStubException ex =
                Assert.Throws<RbxApiStubException>(() => _ = registry.Get("Material")["Adamantium"]);
            Assert.AreEqual("BAD_ARGUMENT", ex.Code);
        }

        [Test]
        public void Enum_GetEnumItems_DeclarationOrder()
        {
            RbxEnumRegistry registry = RbxEnumRegistry.CreateWithBuiltins();
            IReadOnlyList<RbxEnumItem> items = registry.Get("NormalId").GetEnumItems();
            Assert.AreEqual(6, items.Count);
            Assert.AreEqual("Right", items[0].Name);
            Assert.AreEqual("Front", items[5].Name);
        }
    }
}

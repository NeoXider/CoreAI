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
            // TweenService (MVP8 slice 8.4), RaycastFilterType with Raycast (8.5) and
            // HumanoidStateType with Humanoid (8.6), so SurfaceType — the vocabulary of the legacy
            // surface members, which are explicitly not scheduled — is the loud-stub probe now.
            RbxApiStubException ex =
                Assert.Throws<RbxApiStubException>(() => registry.Get("SurfaceType"));
            Assert.AreEqual("NOT_IMPLEMENTED", ex.Code);
            StringAssert.Contains("Enum.SurfaceType", ex.Message);
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
        public void EnumRegistry_KeyCodeMatchesMirror_NoneIsValueZero()
        {
            RbxEnum keyCode = RbxEnumRegistry.CreateWithBuiltins().Get("KeyCode");
            IReadOnlyList<RbxEnumItem> items = keyCode.GetEnumItems();

            Assert.AreEqual(283, items.Count, "the mirror's KeyCode.yaml lists 283 items");
            Assert.AreEqual("None", items[0].Name, "value 0 is named None in the mirror");
            Assert.AreEqual(0, items[0].Value);
            Assert.IsTrue(keyCode.TryGetItemByValue(0, out RbxEnumItem byValue));
            Assert.AreSame(items[0], byValue);

            HashSet<string> names = new();
            HashSet<int> values = new();
            foreach (RbxEnumItem item in items)
            {
                Assert.IsTrue(names.Add(item.Name), "duplicate KeyCode name " + item.Name);
                Assert.IsTrue(values.Add(item.Value), "duplicate KeyCode value " + item.Value);
            }

            Assert.IsFalse(names.Contains("Unknown"), "GetEnumItems lists only the mirror's names");

            // WHY this list: these are the mirror items CoreAI was missing (gamepad thumbstick
            // directions, the Input Action System mouse/touch/trackpad codes and the extra gamepad
            // buttons), with the values KeyCode.yaml assigns them.
            (string Name, int Value)[] added =
            {
                ("Thumbstick1Up", 1018), ("Thumbstick1Down", 1019), ("Thumbstick1Left", 1020),
                ("Thumbstick1Right", 1021), ("Thumbstick2Up", 1022), ("Thumbstick2Down", 1023),
                ("Thumbstick2Left", 1024), ("Thumbstick2Right", 1025), ("MouseLeftButton", 1026),
                ("MouseRightButton", 1027), ("MouseMiddleButton", 1028), ("MouseBackButton", 1029),
                ("MouseNoButton", 1030), ("MouseX", 1031), ("MouseY", 1032), ("MousePosition", 1033),
                ("TouchPosition", 1034), ("MouseWheel", 1035), ("TrackpadPan", 1040),
                ("TrackpadPinch", 1045), ("MouseDelta", 1048), ("TouchDelta", 1049),
                ("TouchPinch", 1050), ("ButtonCenter", 1051), ("ButtonBack", 1052),
                ("ButtonUp", 1053), ("ButtonDown", 1054), ("ButtonLeft", 1055), ("ButtonRight", 1056)
            };
            foreach ((string name, int value) in added)
            {
                Assert.IsTrue(keyCode.TryGetItem(name, out RbxEnumItem item), "missing KeyCode." + name);
                Assert.AreEqual(value, item.Value, "KeyCode." + name);
            }
        }

        [Test]
        public void EnumRegistry_KeyCodeUnknown_IsAnAliasOfNone()
        {
            RbxEnum keyCode = RbxEnumRegistry.CreateWithBuiltins().Get("KeyCode");

            Assert.AreSame(keyCode["None"], keyCode["Unknown"],
                "the legacy name must hand back the same interned item so == and rawequal hold");
            Assert.IsTrue(keyCode.TryGetItem("Unknown", out RbxEnumItem alias));
            Assert.AreSame(keyCode["None"], alias);
            Assert.AreEqual("None", alias.Name);
            Assert.AreEqual("Enum.KeyCode.None", alias.ToString());
        }

        [Test]
        public void Enum_AddAlias_RefusesCollisionsAndMissingTargets()
        {
            RbxEnum axis = new("Axis", ("X", 0), ("Y", 1), ("Z", 2));
            axis.AddAlias("Horizontal", "X");

            Assert.AreSame(axis["X"], axis["Horizontal"]);
            Assert.AreEqual(3, axis.GetEnumItems().Count, "an alias is not an extra item");
            Assert.Throws<System.ArgumentException>(() => axis.AddAlias("Y", "X"));
            Assert.Throws<System.ArgumentException>(() => axis.AddAlias("Horizontal", "Y"));
            Assert.Throws<System.ArgumentException>(() => axis.AddAlias("Depth", "W"));
            Assert.IsFalse(axis.TryGetItem(null, out RbxEnumItem _));
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

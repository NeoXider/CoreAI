using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// The engine-free rules of the UxmlService surface, driven directly with fake claims: a screen
    /// lives while a claim of its mod is alive, ownership, the mod requirement, the write budget's frame
    /// reset, error shapes and disposal. The Lua path to the same rules is
    /// <see cref="UxmlServiceLuaEditModeTests"/>.
    /// </summary>
    [TestFixture]
    public sealed class RbxUiWorldEditModeTests
    {
        [Test]
        public void P18_Sweep_DestroysAScreenOnceEveryClaimDied()
        {
            FakeRbxUiBackend backend = new();
            using RbxUiWorld world = new(backend);
            FakeLease first = new();
            RbxUiScreenHandle hud = world.CreateScreen(new RbxUiCaller("m", first), "hud", "<ui:Label />", null);

            Assert.AreEqual(0, world.Sweep(), "A claimed screen survives the sweep.");
            first.IsAlive = false;
            Assert.AreEqual(1, world.Sweep());
            Assert.IsTrue(hud.IsDestroyed);
            Assert.AreEqual(0, backend.Screens.Count);
        }

        [Test]
        public void P18_Sweep_KeepsAScreenANewerClaimTookOver()
        {
            FakeRbxUiBackend backend = new();
            using RbxUiWorld world = new(backend);
            FakeLease outgoing = new();
            FakeLease incoming = new();
            world.CreateScreen(new RbxUiCaller("m", outgoing), "hud", "<ui:Label />", null);

            RbxUiScreenHandle again = world.GetScreen(new RbxUiCaller("m", incoming), "hud");
            outgoing.IsAlive = false;

            Assert.AreEqual(0, world.Sweep());
            Assert.IsFalse(again.IsDestroyed);
        }

        [Test]
        public void P18_AnotherMod_IsRefusedAccess_TheOwnerIsNot()
        {
            using RbxUiWorld world = new(new FakeRbxUiBackend());
            RbxUiScreenHandle hud = world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "hud", "", null);

            RbxError refused = Assert.Throws<RbxError>(() => hud.RequireAccess("b", "UiScreen:Query"));
            Assert.AreEqual(RbxErrorCode.NotAuthority, refused.Code);
            Assert.AreEqual(RbxUiErrors.NotOwner, RbxUiErrors.GetUiCode(refused));
            Assert.DoesNotThrow(() => hud.RequireAccess("a", "UiScreen:Query"));
            Assert.IsNull(world.GetScreen(new RbxUiCaller("b", new FakeLease()), "hud"),
                "Ids are private to the mod: b has no hud of its own.");
        }

        [Test]
        public void P18_TheOwnerlessOneOffSurface_OwnsNoScreens()
        {
            using RbxUiWorld world = new(new FakeRbxUiBackend());

            RbxError refused = Assert.Throws<RbxError>(() => world.CreateScreen(default, "hud", "", null));
            Assert.AreEqual(RbxErrorCode.ContextViolation, refused.Code);
            Assert.AreEqual(RbxUiErrors.ModRequired, RbxUiErrors.GetUiCode(refused));
            Assert.DoesNotThrow(() => world.CreateScreen(new RbxUiCaller("m", new FakeLease()), "hud", "", null));
        }

        [Test]
        public void P18_WriteBudget_ResetsAtTheEndOfTheFrame()
        {
            using RbxUiWorld world = new(new FakeRbxUiBackend(), new RbxUiBudgets { MaxWritesPerFrame = 2 });
            RbxUiScreenHandle hud = world.CreateScreen(new RbxUiCaller("m", new FakeLease()), "hud",
                "<ui:Label name=\"l\" />", null);
            RbxUiElementHandle label = hud.GetElement("l");
            label.SetText("1");
            label.SetText("2");

            RbxError refused = Assert.Throws<RbxError>(() => label.SetText("3"));
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, refused.Code);
            Assert.AreEqual(RbxUiErrors.BudgetWrites, RbxUiErrors.GetUiCode(refused));
            world.EndFrame();
            Assert.DoesNotThrow(() => label.SetText("3"));
        }

        [Test]
        public void P18_ElementBudget_IsCheckedBeforeTheHostBuildsAnything()
        {
            FakeRbxUiBackend backend = new();
            using RbxUiWorld world = new(backend, new RbxUiBudgets { MaxElementsPerMod = 1 });
            RbxUiCaller caller = new("m", new FakeLease());

            Assert.Throws<RbxError>(() => world.CreateScreen(caller, "hud", "<ui:Label /><ui:Label />", null));
            Assert.AreEqual(0, backend.Created.Count, "The refused screen never reached the host.");
            Assert.DoesNotThrow(() => world.CreateScreen(caller, "hud", "<ui:Label />", null));
        }

        [Test]
        public void P18_UiCodes_TravelInsideTheClosestStructuredCode()
        {
            Assert.AreEqual(RbxErrorCode.BadArgument, RbxUiErrors.OuterCode(RbxUiErrors.CapabilityRequired));
            Assert.AreEqual(RbxErrorCode.BadArgument, RbxUiErrors.OuterCode(RbxUiErrors.Unavailable));
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, RbxUiErrors.OuterCode(RbxUiErrors.BudgetScreens));
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, RbxUiErrors.OuterCode(RbxUiErrors.HostScreenLimit));
            Assert.AreEqual(RbxErrorCode.PayloadTooLarge, RbxUiErrors.OuterCode(RbxUiErrors.TextTooLarge));
            Assert.AreEqual(RbxErrorCode.InstanceDestroyed, RbxUiErrors.OuterCode(RbxUiErrors.ElementDetached));

            RbxError error = RbxUiErrors.MissingCapability("UxmlService:CreateScreen");
            StringAssert.StartsWith("BAD_ARGUMENT: UI_CAPABILITY_REQUIRED: UxmlService:CreateScreen", error.Message);
            StringAssert.Contains(" | fix: ", error.Message);
            Assert.IsNull(RbxUiErrors.GetUiCode(RbxError.BadArgument("plain", "fix")), "Other errors carry no UI code.");
        }

        [Test]
        public void P18_Dispose_DestroysEveryScreen_AndStopsListeningToTheHost()
        {
            FakeRbxUiBackend backend = new();
            RbxUiWorld world = new(backend);
            RbxUiScreenHandle a = world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "hud", "", null);
            RbxUiScreenHandle b = world.CreateScreen(new RbxUiCaller("b", new FakeLease()), "hud", "", null);

            world.Dispose();

            Assert.IsTrue(a.IsDestroyed && b.IsDestroyed);
            Assert.AreEqual(0, backend.Screens.Count);
            Assert.IsFalse(world.IsAvailable);
            RbxError refused = Assert.Throws<RbxError>(() =>
                world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "hud", "", null));
            Assert.AreEqual(RbxUiErrors.Unavailable, RbxUiErrors.GetUiCode(refused));
        }

        [Test]
        public void P18_DestroyScreensOwnedBy_RemovesOnlyThatModsScreens()
        {
            FakeRbxUiBackend backend = new();
            using RbxUiWorld world = new(backend);
            world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "hud", "", null);
            world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "menu", "", null);
            RbxUiScreenHandle other = world.CreateScreen(new RbxUiCaller("b", new FakeLease()), "hud", "", null);

            Assert.AreEqual(2, world.DestroyScreensOwnedBy("a"));
            Assert.AreEqual(0, world.CountScreens("a"));
            Assert.IsFalse(other.IsDestroyed);
            List<RbxUiScreenHandle> screens = new();
            world.GetScreens(new RbxUiCaller("b", new FakeLease()), screens);
            Assert.AreEqual(1, screens.Count);
        }

        [Test]
        public void P18_AnIdleFrame_AllocatesNothing()
        {
            using RbxUiWorld world = new(new FakeRbxUiBackend());
            world.CreateScreen(new RbxUiCaller("a", new FakeLease()), "hud", "<ui:Label />", null);
            world.CreateScreen(new RbxUiCaller("b", new FakeLease()), "hud", "<ui:Label />", null);
            TestDelegate idleFrames = () =>
            {
                for (int frame = 0; frame < 100; frame++)
                {
                    world.EndFrame();
                }
            };

            // WHY one warm-up run: the first run of a freshly compiled delegate can be charged the JIT's own
            // bookkeeping on Mono; only the steady state measures the frame.
            idleFrames();
            Assert.That(idleFrames, Is.Not.AllocatingGCMemory());
            Assert.AreEqual(2, world.LiveScreenCount, "Claimed screens survive idle frames.");
        }

        private sealed class FakeLease : IRbxUiLease
        {
            public bool IsAlive { get; set; } = true;
        }
    }
}

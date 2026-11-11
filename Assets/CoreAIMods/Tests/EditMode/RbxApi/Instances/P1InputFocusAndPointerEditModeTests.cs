using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// P1 stage A, engine-free: the input focus state behind <c>gameProcessedEvent</c>, the cursor port
    /// behind <c>MouseBehavior</c>, the device mouse delta and <c>GetMouseDelta</c>, the wheel.
    /// </summary>
    [TestFixture]
    public sealed class P1InputFocusAndPointerEditModeTests
    {
        private RbxEnumRegistry _enums;
        private RbxUserInputService _service;
        private InMemoryInputSource _source;

        [SetUp]
        public void SetUp()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _enums = RbxEnumRegistry.CreateWithBuiltins();
            _service = (RbxUserInputService)game.GetService("UserInputService");
            _service.AttachEnums(_enums);
            _source = new InMemoryInputSource();
            _service.AttachInputSource(_source);
        }

        private RbxEnumItem MouseBehavior(string name)
        {
            Assert.IsTrue(_enums.TryGet("MouseBehavior", out RbxEnum rbxEnum));
            Assert.IsTrue(rbxEnum.TryGetItem(name, out RbxEnumItem item));
            return item;
        }

        [Test]
        public void FocusState_TwoHolders_ReleasingOneKeepsTheCapture()
        {
            InputFocusState focus = new();
            int flips = 0;
            focus.CaptureChanged += _ => flips++;

            focus.SetCaptured("chat", true);
            focus.SetCaptured("hub", true);
            focus.SetCaptured("chat", false);

            Assert.IsTrue(focus.IsGameInputCaptured);
            Assert.IsTrue(focus.IsHeldBy("hub"));
            Assert.AreEqual(1, flips, "CaptureChanged fires on flips only");

            focus.SetCaptured("hub", false);
            Assert.IsFalse(focus.IsGameInputCaptured);
            Assert.AreEqual(2, flips);
        }

        [Test]
        public void FocusState_EmptyHolderId_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => new InputFocusState().SetCaptured(" ", true));
        }

        [Test]
        public void IsGameInputCaptured_FollowsTheSourceFocus_AndAnAttachedOneWins()
        {
            InputFocusState sourceFocus = new();
            _source.FocusState = sourceFocus;
            Assert.IsFalse(_service.IsGameInputCaptured);

            sourceFocus.SetCaptured("chat", true);
            Assert.IsTrue(_service.IsGameInputCaptured);

            InputFocusState attached = new();
            _service.AttachFocusState(attached);
            Assert.IsFalse(_service.IsGameInputCaptured, "the attached state replaces the source's");
        }

        [Test]
        public void MouseBehavior_ReachesTheCursorPort_IncludingOneAttachedLater()
        {
            InMemoryCursorPort port = new();
            _service.MouseBehavior = MouseBehavior("LockCenter");
            _service.AttachCursorPort(port);
            Assert.AreEqual(RbxMouseLock.LockCenter, port.Applied, "attach applies the current request");

            _service.MouseBehavior = MouseBehavior("Default");
            Assert.AreEqual(RbxMouseLock.Default, port.Applied);
            Assert.IsFalse(_service.IsPointerLocked);
        }

        [Test]
        public void GetMouseDelta_IsZeroUnlessThePointerIsLocked()
        {
            _service.AttachCursorPort(new InMemoryCursorPort());
            _source.SetMouseDelta(4f, -2f);
            _service.Step();
            Assert.AreEqual(RbxVector2.Zero, _service.GetMouseDelta(), "unlocked: zero, as the mirror says");
            Assert.AreEqual(new RbxVector2(4f, -2f), _service.LastFrameMouseDelta);

            _service.MouseBehavior = MouseBehavior("LockCenter");
            _source.SetMouseDelta(4f, -2f);
            _service.Step();
            Assert.AreEqual(new RbxVector2(4f, -2f), _service.GetMouseDelta());
        }

        [Test]
        public void GetMouseDelta_IsZero_WhileTheHostHasNotGrantedTheLock()
        {
            InMemoryCursorPort port = new() { GrantsLock = false };
            _service.AttachCursorPort(port);
            _service.MouseBehavior = MouseBehavior("LockCenter");
            _source.SetMouseDelta(4f, 1f);
            _service.Step();

            Assert.AreEqual(RbxMouseLock.LockCenter, port.Applied);
            Assert.IsFalse(_service.IsPointerLocked);
            Assert.AreEqual(RbxVector2.Zero, _service.GetMouseDelta());
        }

        [Test]
        public void GetMouseDelta_IsScaledBySensitivity_ClampedToTheMirrorRange()
        {
            _service.MouseBehavior = MouseBehavior("LockCenter");
            _service.MouseDeltaSensitivity = 0.5d;
            _source.SetMouseDelta(10f, 4f);
            _service.Step();
            Assert.AreEqual(new RbxVector2(5f, 2f), _service.GetMouseDelta());

            _service.MouseDeltaSensitivity = 25d;
            Assert.AreEqual(RbxUserInputService.MaximumMouseDeltaSensitivity, _service.MouseDeltaSensitivity);
            _service.MouseDeltaSensitivity = -1d;
            Assert.AreEqual(0d, _service.MouseDeltaSensitivity);
            _service.MouseDeltaSensitivity = double.NaN;
            Assert.AreEqual(0d, _service.MouseDeltaSensitivity, "NaN keeps the previous value");
        }

        [Test]
        public void MouseDelta_UsesTheDeviceDelta_WhileTheLocationDoesNotMove()
        {
            _source.SetMouseLocation(100f, 100f);
            _service.Step();
            _source.SetMouseDelta(7f, 3f);
            _service.Step();

            Assert.AreEqual(new RbxVector2(7f, 3f), _service.LastFrameMouseDelta,
                "a locked pointer stays put while the device still reports movement");
        }

        [Test]
        public void MouseDelta_FallsBackToTheLocationDifference_WithoutADeviceDelta()
        {
            _source.SetMouseLocation(10f, 10f);
            _service.Step();
            _source.SetMouseLocation(13f, 8f);
            _service.Step();

            Assert.AreEqual(new RbxVector2(3f, -2f), _service.LastFrameMouseDelta);
        }

        [Test]
        public void Wheel_IsReportedForOneStepOnly()
        {
            _source.ScrollWheel(120f);
            _service.Step();
            Assert.AreEqual(120f, _service.LastFrameWheel);

            _service.Step();
            Assert.AreEqual(0f, _service.LastFrameWheel);
        }
    }
}

using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// P1 stage C, engine-free: the orbit, zoom and pull-in maths of the default camera, the
    /// <see cref="RbxDefaultCameraModule"/> over fake frames (with negative twins), the player's camera
    /// settings, and the input-service additions the camera and the control script read.
    /// </summary>
    [TestFixture]
    public sealed class P1CameraModuleEditModeTests
    {
        private const float Tolerance = 1e-4f;
        private static readonly RbxVector3 Focus = new(0f, 6.5f, 0f);

        private static RbxCameraModuleInput Frame()
        {
            return new RbxCameraModuleInput
            {
                DeltaSeconds = 1d / 60d,
                CurrentCFrame = RbxCFrame.Identity,
                Focus = Focus,
                CameraMode = RbxCameraMode.Classic,
                MinZoomDistance = RbxStarterPlayer.DefaultCameraMinZoomDistance,
                MaxZoomDistance = RbxStarterPlayer.DefaultCameraMaxZoomDistance
            };
        }

        private static RbxCameraModuleInput Wheel(float wheel)
        {
            RbxCameraModuleInput input = Frame();
            input.Wheel = wheel;
            return input;
        }

        private sealed class FakeOcclusion : IRbxCameraOcclusion
        {
            public double? Hit;
            public RbxVector3 LastFocus;
            public RbxVector3 LastCamera;

            public bool TryFindObstruction(RbxVector3 focus, RbxVector3 camera, out double distanceStuds)
            {
                LastFocus = focus;
                LastCamera = camera;
                distanceStuds = Hit ?? 0d;
                return Hit.HasValue;
            }
        }

        // ---- maths ------------------------------------------------------------------------------

        [Test]
        public void OrbitCFrame_LooksAtTheFocus_FromTheZoomDistance()
        {
            RbxCFrame pose = RbxCameraOrbitMath.OrbitCFrame(Focus, 0.7d, -0.4d, 12.5d);

            RbxVector3 expectedLook = RbxCameraOrbitMath.LookVector(0.7d, -0.4d);
            Assert.AreEqual(expectedLook.X, pose.LookVector.X, Tolerance);
            Assert.AreEqual(expectedLook.Y, pose.LookVector.Y, Tolerance);
            Assert.AreEqual(expectedLook.Z, pose.LookVector.Z, Tolerance);
            Assert.AreEqual(12.5f, (Focus - pose.Position).Magnitude, 1e-3f);
            Assert.AreEqual(0f, pose.RightVector.Y, Tolerance, "no roll");
        }

        [Test]
        public void YawPitchFromLook_RoundTripsLookVector()
        {
            RbxVector3 look = RbxCameraOrbitMath.LookVector(-2.1d, 0.6d);

            RbxCameraOrbitMath.YawPitchFromLook(look, out double yaw, out double pitch);

            Assert.AreEqual(-2.1d, yaw, 1e-5d);
            Assert.AreEqual(0.6d, pitch, 1e-5d);
        }

        [Test]
        public void ApplyLook_MovingRightTurnsRight_AndMovingDownLooksDown()
        {
            double yaw = 0d;
            double pitch = 0d;

            RbxCameraOrbitMath.ApplyLook(ref yaw, ref pitch, new RbxVector2(10f, 10f), 0.01d, 0.01d);

            RbxVector3 look = RbxCameraOrbitMath.LookVector(yaw, pitch);
            Assert.Greater(look.X, 0f, "right of -Z is +X");
            Assert.Less(look.Y, 0f);
        }

        [Test]
        public void ApplyLook_ClampsThePitchAtEightyDegrees()
        {
            double yaw = 0d;
            double pitch = 0d;

            RbxCameraOrbitMath.ApplyLook(ref yaw, ref pitch, new RbxVector2(0f, -100000f), 0.01d, 0.01d);
            Assert.AreEqual(80d * Math.PI / 180d, pitch, 1e-9d);

            RbxCameraOrbitMath.ApplyLook(ref yaw, ref pitch, new RbxVector2(0f, 100000f), 0.01d, 0.01d);
            Assert.AreEqual(-80d * Math.PI / 180d, pitch, 1e-9d);
        }

        [Test]
        public void StepZoom_FollowsThePlayerModuleCurve()
        {
            Assert.AreEqual(11.5d / 1.5d, RbxCameraOrbitMath.StepZoom(12.5d, -1d), 1e-9d, "one notch in");
            Assert.AreEqual(0.5d + 1.25d, RbxCameraOrbitMath.StepZoom(0.5d, 1d), 1e-9d, "one notch out of first person");
            Assert.AreEqual(12.5d, RbxCameraOrbitMath.StepZoom(12.5d, 0d), "no notch, no change");
        }

        [TestCase(12.5d, 0.5d, 400d, 12.5d)]
        [TestCase(900d, 0.5d, 400d, 400d)]
        [TestCase(0.8d, 0.5d, 400d, 0.5d)]
        [TestCase(0.2d, 0.5d, 400d, 0.5d)]
        [TestCase(0.2d, 10d, 400d, 10d)]
        [TestCase(30d, 0.5d, 20d, 20d)]
        public void ResolveZoom_ClampsToMinAndMax_AndSnapsBelowOneStudToFirstPerson(double desired, double min,
            double max, double expected)
        {
            Assert.AreEqual(expected,
                RbxCameraOrbitMath.ResolveZoom(desired, min, max, RbxCameraMode.Classic), 1e-9d);
        }

        [Test]
        public void ResolveZoom_LockFirstPerson_IsAlwaysFirstPerson()
        {
            Assert.AreEqual(RbxCameraOrbitMath.FirstPersonDistance,
                RbxCameraOrbitMath.ResolveZoom(50d, 0.5d, 400d, RbxCameraMode.LockFirstPerson));
            Assert.IsTrue(RbxCameraOrbitMath.IsFirstPerson(RbxCameraOrbitMath.FirstPersonDistance));
        }

        [Test]
        public void PullIn_StopsShortOfACloserObstruction_AndIgnoresAFartherOne()
        {
            Assert.AreEqual(4.75d, RbxCameraOrbitMath.PullIn(12.5d, 5d, 0.25d), 1e-9d);
            Assert.AreEqual(12.5d, RbxCameraOrbitMath.PullIn(12.5d, 20d, 0.25d), "a wall behind the camera");
            Assert.AreEqual(0d, RbxCameraOrbitMath.PullIn(12.5d, 0.1d, 0.25d), "never behind the focus");
        }

        // ---- module -----------------------------------------------------------------------------

        [Test]
        public void Module_FreshCamera_OrbitsBehindTheFocusAtTheDefaultZoom()
        {
            RbxDefaultCameraModule module = new();

            RbxCameraModuleOutput output = module.Update(Frame());

            Assert.IsTrue(output.HasCFrame);
            Assert.IsFalse(output.IsFirstPerson);
            Assert.AreEqual(12.5d, output.Distance, 1e-9d);
            Assert.AreEqual(Focus.Z + 12.5f, output.CFrame.Position.Z, 1e-3f, "seeded from the identity look, -Z");
            Assert.AreEqual(RbxMouseLock.Default, output.MouseLock);
        }

        [Test]
        public void Module_SeedsTheYawFromTheCurrentCamera()
        {
            RbxDefaultCameraModule module = new();
            RbxCameraModuleInput input = Frame();
            input.CurrentCFrame = RbxCFrame.LookAlong(RbxVector3.Zero, new RbxVector3(1f, 0f, 0f));

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(1f, output.CFrame.LookVector.X, Tolerance, "the module keeps the view it took over");
            Assert.AreEqual(Focus.X - 12.5f, output.CFrame.Position.X, 1e-3f);
        }

        [Test]
        public void Module_WheelZoomsIntoFirstPerson_AndLocksThePointerToTheCentre()
        {
            RbxDefaultCameraModule module = new();
            RbxCameraModuleOutput output = default;
            for (int notch = 0; notch < 10; notch++)
            {
                output = module.Update(Wheel(120f));
            }

            Assert.IsTrue(output.IsFirstPerson);
            Assert.AreEqual(RbxCameraOrbitMath.FirstPersonDistance, output.Distance, 1e-9d);
            Assert.AreEqual(RbxMouseLock.LockCenter, output.MouseLock);

            output = module.Update(Wheel(-1f));
            Assert.IsFalse(output.IsFirstPerson, "one notch back leaves first person");
            Assert.AreEqual(RbxMouseLock.Default, output.MouseLock);
        }

        [Test]
        public void Module_WheelZoom_StaysInsideMinAndMax()
        {
            RbxDefaultCameraModule module = new();
            RbxCameraModuleOutput output = default;
            for (int notch = 0; notch < 40; notch++)
            {
                output = module.Update(Wheel(-1f));
            }

            Assert.AreEqual(RbxStarterPlayer.DefaultCameraMaxZoomDistance, output.ZoomDistance, 1e-9d);

            RbxCameraModuleInput limited = Wheel(120f);
            limited.MinZoomDistance = 8d;
            limited.MaxZoomDistance = 20d;
            for (int notch = 0; notch < 40; notch++)
            {
                output = module.Update(limited);
            }

            Assert.AreEqual(8d, output.ZoomDistance, 1e-9d, "a minimum above one stud never reaches first person");
            Assert.IsFalse(output.IsFirstPerson);
        }

        [Test]
        public void Module_LockFirstPerson_HoldsFirstPersonWithoutAnyWheel()
        {
            RbxDefaultCameraModule module = new();
            RbxCameraModuleInput input = Frame();
            input.CameraMode = RbxCameraMode.LockFirstPerson;

            RbxCameraModuleOutput output = module.Update(input);
            for (int notch = 0; notch < 5; notch++)
            {
                input.Wheel = -1f;
                output = module.Update(input);
            }

            Assert.IsTrue(output.IsFirstPerson);
            Assert.AreEqual(RbxMouseLock.LockCenter, output.MouseLock);
        }

        [Test]
        public void Module_ChatHoldsTheInput_WheelAndLookDoNothing_AndThePointerIsFree()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Wheel(120f);
            input.InputCaptured = true;
            input.PointerLocked = true;
            input.PointerDelta = new RbxVector2(300f, 0f);

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(12.5d, output.ZoomDistance, 1e-9d);
            Assert.AreEqual(0d, module.Yaw, 1e-12d);
            Assert.AreEqual(RbxMouseLock.Default, output.MouseLock);
        }

        [Test]
        public void Module_ChatClosed_TheSameWheelAndLockedLookApply_NegativeTwin()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Wheel(120f);
            input.PointerLocked = true;
            input.PointerDelta = new RbxVector2(300f, 0f);

            RbxCameraModuleOutput output = module.Update(input);

            Assert.Less(output.ZoomDistance, 12.5d);
            Assert.AreNotEqual(0d, module.Yaw);
        }

        [Test]
        public void Module_PointerOverUi_KeepsTheWheelAndTheRightButtonForThePanel()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Wheel(120f);
            input.PointerOverUi = true;
            input.RotateHeld = true;
            input.PointerDelta = new RbxVector2(300f, 0f);

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(12.5d, output.ZoomDistance, 1e-9d);
            Assert.AreEqual(0d, module.Yaw, 1e-12d);
            Assert.AreEqual(RbxMouseLock.Default, output.MouseLock);
        }

        private static RbxCameraModuleInput FirstPerson()
        {
            RbxCameraModuleInput input = Frame();
            input.CameraMode = RbxCameraMode.LockFirstPerson;
            input.PointerDelta = new RbxVector2(100f, 0f);
            return input;
        }

        [Test]
        public void Module_FirstPerson_AFreePointerOverUi_StaysFree_AndTurnsNothing()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = FirstPerson();
            input.PointerOverUi = true;

            RbxCameraModuleOutput output = module.Update(input);

            Assert.IsTrue(output.IsFirstPerson);
            Assert.AreEqual(RbxMouseLock.Default, output.MouseLock,
                "a lock asked for now would take the click meant for the button under the pointer");
            Assert.AreEqual(0d, module.Yaw, 1e-12d, "the view holds still while the pointer is on the panel");
        }

        [Test]
        public void Module_FirstPerson_AFreePointerOffTheUi_IsLockedAndTurns_NegativeTwin()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());

            RbxCameraModuleOutput output = module.Update(FirstPerson());

            Assert.AreEqual(RbxMouseLock.LockCenter, output.MouseLock);
            Assert.AreNotEqual(0d, module.Yaw);
        }

        [Test]
        public void Module_FirstPerson_ALockedPointer_StaysLocked_WhateverIsUnderTheCentre()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = FirstPerson();
            input.PointerLocked = true;
            input.PointerOverUi = true;

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(RbxMouseLock.LockCenter, output.MouseLock, "a pickable element under the crosshair frees nothing");
            Assert.AreNotEqual(0d, module.Yaw);
        }

        [Test]
        public void Module_RightDragTurns_AndAsksForLockCurrentPosition()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Frame();
            input.RotateHeld = true;
            input.PointerDelta = new RbxVector2(100f, 0f);

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(-100d * 0.5d * Math.PI / 180d, module.Yaw, 1e-9d, "half a degree per pixel");
            Assert.AreEqual(RbxMouseLock.LockCurrentPosition, output.MouseLock);
        }

        [Test]
        public void Module_FreePointerWithoutTheButton_TurnsNothing_NegativeTwin()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Frame();
            input.PointerDelta = new RbxVector2(100f, 50f);

            module.Update(input);

            Assert.AreEqual(0d, module.Yaw, 1e-12d);
            Assert.AreEqual(0d, module.Pitch, 1e-12d);
        }

        [Test]
        public void Module_RightThumbstickTurnsWithTime_AndTheDeadZoneTurnsNothing()
        {
            RbxDefaultCameraModule module = new();
            module.Update(Frame());
            RbxCameraModuleInput input = Frame();
            input.LookStick = new RbxVector2(0.1f, 0.1f);
            module.Update(input);
            Assert.AreEqual(0d, module.Yaw, 1e-12d, "inside the 0.2 dead zone");

            input.LookStick = new RbxVector2(1f, 0f);
            input.DeltaSeconds = 0.5d;
            module.Update(input);
            Assert.AreEqual(-120d * Math.PI / 180d, module.Yaw, 1e-9d, "240 degrees per second, turning right");
        }

        [Test]
        public void Module_Obstruction_PullsTheCameraIn_WithoutChangingTheZoom()
        {
            RbxDefaultCameraModule module = new();
            FakeOcclusion occlusion = new() { Hit = 5d };
            RbxCameraModuleInput input = Frame();
            input.Occlusion = occlusion;

            RbxCameraModuleOutput blocked = module.Update(input);

            Assert.AreEqual(4.75d, blocked.Distance, 1e-9d);
            Assert.AreEqual(12.5d, blocked.ZoomDistance, 1e-9d);
            Assert.AreEqual(4.75f, (Focus - blocked.CFrame.Position).Magnitude, 1e-3f);
            Assert.AreEqual(Focus.Z, occlusion.LastFocus.Z, Tolerance, "the ray starts at the focus");
            Assert.AreEqual(Focus.Z + 12.5f, occlusion.LastCamera.Z, 1e-3f, "and ends where the camera wants to be");

            occlusion.Hit = null;
            RbxCameraModuleOutput clear = module.Update(input);
            Assert.AreEqual(12.5d, clear.Distance, 1e-9d, "the wall is gone, the zoom comes back");
        }

        [Test]
        public void Module_Reset_ReseedsFromTheCameraPose()
        {
            RbxDefaultCameraModule module = new();
            RbxCameraModuleInput input = Frame();
            input.RotateHeld = true;
            input.PointerDelta = new RbxVector2(200f, 0f);
            module.Update(input);
            Assert.AreNotEqual(0d, module.Yaw);

            module.Reset();
            module.Update(Frame());

            Assert.AreEqual(0d, module.Yaw, 1e-9d, "the identity camera looks along -Z again");
        }

        [Test]
        public void Module_Options_TuneTheZoomAndTheTurnRate()
        {
            RbxDefaultCameraModule module = new(new RbxCameraModuleOptions
            {
                DefaultZoomDistance = 20d,
                YawDegreesPerPixel = 1d
            });
            RbxCameraModuleInput input = Frame();
            input.RotateHeld = true;
            input.PointerDelta = new RbxVector2(10f, 0f);

            RbxCameraModuleOutput output = module.Update(input);

            Assert.AreEqual(20d, output.ZoomDistance, 1e-9d);
            Assert.AreEqual(-10d * Math.PI / 180d, module.Yaw, 1e-9d);
        }

        // ---- player settings and input ---------------------------------------------------------

        private static (InstanceRegistry Registry, RbxDataModel Game) NewWorld()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return (registry, game);
        }

        [Test]
        public void Player_CopiesTheStarterPlayerCameraSettings_WhenItJoins()
        {
            (InstanceRegistry registry, RbxDataModel game) = NewWorld();
            RbxStarterPlayer starter = (RbxStarterPlayer)game.GetService("StarterPlayer");
            starter.CameraMode = RbxCameraMode.LockFirstPerson;
            starter.CameraMaxZoomDistance = 30d;
            starter.CameraMinZoomDistance = 2d;
            RbxPlayers players = (RbxPlayers)game.GetService("Players");

            RbxPlayer player = players.EnsureActor(registry, "player-1");
            starter.CameraMaxZoomDistance = 99d;

            Assert.AreEqual(RbxCameraMode.LockFirstPerson, player.CameraMode);
            Assert.AreEqual(30d, player.CameraMaxZoomDistance, "copied at join, not read live");
            Assert.AreEqual(2d, player.CameraMinZoomDistance);
        }

        [Test]
        public void Player_ZoomLimits_ClampEachOther_AsTheMirrorDescribes()
        {
            (InstanceRegistry registry, RbxDataModel game) = NewWorld();
            RbxPlayer player = ((RbxPlayers)game.GetService("Players")).EnsureActor(registry, "player-1");

            player.CameraMinZoomDistance = 10d;
            player.CameraMaxZoomDistance = 4d;
            Assert.AreEqual(10d, player.CameraMaxZoomDistance, "a max below the min is raised to it");

            player.CameraMinZoomDistance = 50d;
            Assert.AreEqual(10d, player.CameraMinZoomDistance, "a min above the max is lowered to it");
        }

        [Test]
        public void GetSoleListedPlayer_IsTheOnlyPlayer_AndNullWithTwo()
        {
            (InstanceRegistry registry, RbxDataModel game) = NewWorld();
            RbxPlayers players = (RbxPlayers)game.GetService("Players");
            Assert.IsNull(players.GetSoleListedPlayer());

            RbxPlayer first = players.EnsureActor(registry, "player-1");
            Assert.AreSame(first, players.GetSoleListedPlayer());

            players.EnsureActor(registry, "player-2");
            Assert.IsNull(players.GetSoleListedPlayer(), "two players: nobody is the obvious local one");
        }

        [Test]
        public void UserInputService_ReadsBothThumbsticks_ClampedToOne()
        {
            (InstanceRegistry _, RbxDataModel game) = NewWorld();
            RbxUserInputService service = (RbxUserInputService)game.GetService("UserInputService");
            service.AttachEnums(RbxEnumRegistry.CreateWithBuiltins());
            InMemoryInputSource source = new();
            service.AttachInputSource(source);

            source.SetThumbstick(1, 0.5f, -2f);
            source.SetThumbstick(2, -0.25f, 0.75f);
            service.Step();

            Assert.AreEqual(0.5f, service.GetThumbstick(1).X);
            Assert.AreEqual(-1f, service.GetThumbstick(1).Y, "clamped to the stick's range");
            Assert.AreEqual(0.75f, service.GetThumbstick(2).Y);
            Assert.AreEqual(0f, service.GetThumbstick(3).X, "there is no third stick");
        }

        [Test]
        public void UserInputService_PointerOverUi_ProcessesMouseInputButNotKeys()
        {
            (InstanceRegistry _, RbxDataModel game) = NewWorld();
            RbxUserInputService service = (RbxUserInputService)game.GetService("UserInputService");
            InputFocusState focus = new();
            service.AttachInputSource(new InMemoryInputSource { FocusState = focus });

            Assert.IsFalse(service.IsPointerInputProcessed);
            focus.SetPointerOverUi("hub", true);
            Assert.IsTrue(service.IsPointerInputProcessed);
            Assert.IsFalse(service.IsGameInputCaptured);
            Assert.IsNull(service.GetFocusedTextBox(), "CoreAI's own fields are not script TextBoxes");
        }
    }
}

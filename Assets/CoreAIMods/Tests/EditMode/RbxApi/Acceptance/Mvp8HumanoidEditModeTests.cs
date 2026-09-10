using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP2.5 slice 8.6 gate (plan §E.1 row P8.2): <c>Humanoid</c> health, movement parameters and
    /// the state machine, through the production composition.
    /// </summary>
    [TestFixture]
    public sealed class Mvp8HumanoidEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void Humanoid_ShipsTheMirrorsDefaults()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            Assert.AreEqual(100d, humanoid.MaxHealth, 1e-9d);
            Assert.AreEqual(100d, humanoid.Health, 1e-9d);
            Assert.AreEqual(16d, humanoid.WalkSpeed, 1e-9d, "StarterPlayer.CharacterWalkSpeed");
            Assert.AreEqual(50d, humanoid.JumpPower, 1e-9d);
            Assert.AreEqual(7.2d, humanoid.JumpHeight, 1e-9d);
            Assert.IsTrue(humanoid.UseJumpPower, "CharacterUseJumpPower defaults to true");
        }

        [Test]
        public void TakeDamage_LowersHealthAndFiresHealthChanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("dmg");

            harness.Stack.Runtime.LoadMod(actor, "dmg-mod", @"
                local h = Instance.new('Humanoid')
                h.Name = 'Humanoid'
                h.Parent = workspace
                h.HealthChanged:Connect(function(health) store_set('health', tostring(health)) end)
                h:TakeDamage(30)
                store_set('after', tostring(h.Health))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("70", harness.Store.Get("dmg-mod", "after"),
                "log: " + string.Join(" || ", harness.LogLines));
            Assert.AreEqual("70", harness.Store.Get("dmg-mod", "health"));
        }

        [Test]
        public void TakeDamage_WithANegativeAmount_Heals()
        {
            // The mirror says TakeDamage accepts negative values and they increase Health.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            humanoid.TakeDamage(40d);
            humanoid.TakeDamage(-15d);

            Assert.AreEqual(75d, humanoid.Health, 1e-9d);
        }

        [Test]
        public void Health_ReachingZero_FiresDiedExactlyOnce()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("died");

            harness.Stack.Runtime.LoadMod(actor, "died-mod", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                h.Died:Connect(function()
                    store_set('died', tostring((tonumber(store_get('died')) or 0) + 1))
                end)
                h.Health = 0
                h.Health = 0
                h:TakeDamage(10)
                store_set('state', h:GetState().Name)",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("1", harness.Store.Get("died-mod", "died"),
                "a dead humanoid must not die again on every further write");
            Assert.AreEqual("Dead", harness.Store.Get("died-mod", "state"));
        }

        [Test]
        public void Negative_Health_AboveMaxHealth_ClampsInsteadOfRaising()
        {
            // The mirror: Health is "restricted to the range between 0 and MaxHealth". A refusal
            // would break every script that heals to a round number above the cap.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            Assert.DoesNotThrow(() => humanoid.Health = 150d);
            Assert.AreEqual(100d, humanoid.Health, 1e-9d);

            humanoid.MaxHealth = 60d;
            Assert.AreEqual(60d, humanoid.Health, 1e-9d, "lowering MaxHealth clamps Health with it");
        }

        [Test]
        public void Negative_HealingACorpse_DoesNotResurrectIt()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            int died = 0;
            harness.Connect(humanoid.Died, _ => died++);

            humanoid.Health = 0d;
            humanoid.Health = 100d;
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(0d, humanoid.Health, 1e-9d);
            Assert.IsTrue(humanoid.IsDead);
            Assert.AreEqual(1, died);
        }

        [Test]
        public void MoveTo_ReachingTheTarget_FiresMoveToFinishedTrue()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<bool> finished = new();
            harness.Connect(humanoid.MoveToFinished, args => finished.Add((bool)args[0]));

            humanoid.MoveTo(new RbxVector3(10f, 0f, 0f));
            harness.Motor.PositionValue = new RbxVector3(9f, 0f, 0f);
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(new[] { true }, finished);
            Assert.IsNull(harness.Motor.Target, "arriving must also stop the walk");
        }

        [Test]
        public void Negative_MoveTo_AnUnreachablePoint_FinishesFalseAtEightSecondsAndNotBefore()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<bool> finished = new();
            harness.Connect(humanoid.MoveToFinished, args => finished.Add((bool)args[0]));

            humanoid.MoveTo(new RbxVector3(1000f, 0f, 0f));
            harness.Bindings.Scheduler.Advance(7.9d);
            Assert.IsEmpty(finished, "the mirror's timeout is eight seconds, not seven");

            harness.Bindings.Scheduler.Advance(0.2d);

            CollectionAssert.AreEqual(new[] { false }, finished);
        }

        [Test]
        public void MoveTo_TimeoutRunsOnScaledTime()
        {
            // WHY it matters: a paused world must not give up on a walk. The Heartbeat delta is the
            // scaled frame time, so a zero-delta pump is a paused game, and eight seconds of it are
            // still zero seconds of gameplay.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<bool> finished = new();
            harness.Connect(humanoid.MoveToFinished, args => finished.Add((bool)args[0]));

            humanoid.MoveTo(new RbxVector3(1000f, 0f, 0f));
            for (int step = 0; step < 500; step++)
            {
                harness.Bindings.Scheduler.Advance(0d);
            }

            Assert.IsEmpty(finished, "a paused world cannot time a walk out");
        }

        [Test]
        public void WalkSpeed_ReachesTheMotorInStuds()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            humanoid.WalkSpeed = 24d;

            Assert.AreEqual(24d, harness.Motor.WalkSpeed, 1e-9d,
                "the Humanoid speaks studs; converting to metres is the motor's job");
        }

        [Test]
        public void Jump_RequestsOneJumpWithTheActiveParameters()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("jump");

            harness.Stack.Runtime.LoadMod(actor, "jump-mod", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                h.UseJumpPower = false
                h.JumpHeight = 12
                h.Jumping:Connect(function(active) store_set('jumping', tostring(active)) end)
                h.Jump = true",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, harness.Motor.JumpCount);
            Assert.AreEqual(12d, harness.Motor.LastJumpHeight, 1e-9d);
            Assert.IsFalse(harness.Motor.LastUseJumpPower,
                "UseJumpPower false means JumpHeight decides the jump");
            Assert.AreEqual("true", harness.Store.Get("jump-mod", "jumping"));
        }

        [Test]
        public void Negative_Jump_WhileDead_DoesNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            humanoid.Health = 0d;
            humanoid.RequestJump();
            humanoid.MoveTo(new RbxVector3(5f, 0f, 0f));

            Assert.AreEqual(0, harness.Motor.JumpCount);
            Assert.IsNull(harness.Motor.Target, "a corpse does not walk off");
        }

        [Test]
        public void State_FollowsTheMotorsGroundContact()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<string> states = new();
            harness.Connect(humanoid.StateChanged,
                args => states.Add(args[0] + "->" + args[1]));

            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual(RbxHumanoidState.Freefall, humanoid.GetState());

            harness.Motor.Grounded = true;
            harness.Bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual(RbxHumanoidState.Landed, humanoid.GetState());

            harness.Bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual(RbxHumanoidState.Running, humanoid.GetState());
            CollectionAssert.Contains(states, "Running->Freefall");
        }

        [Test]
        public void MoveDirection_IsReadFromTheMotor()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            harness.Motor.MoveDirectionValue = new RbxVector3(0f, 0f, 1f);

            Assert.AreEqual(1f, humanoid.MoveDirection.Z, 1e-6f,
                "the adapter is the only reader of the controller's motion");
        }

        [Test]
        public void Negative_UnsupportedHumanoidMembers_RaiseTheLoudStub()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("stub");

            harness.Stack.Runtime.LoadMod(actor, "stub-mod", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                local okSit, errSit = pcall(function() h.Sit = true end)
                store_set('sit', tostring(okSit) .. '|' .. tostring(errSit))
                local okState, errState = pcall(function()
                    h:ChangeState(Enum.HumanoidStateType.Seated)
                end)
                store_set('state', tostring(okState) .. '|' .. tostring(errState))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            string sit = harness.Store.Get("stub-mod", "sit");
            StringAssert.StartsWith("false|", sit);
            StringAssert.Contains("Humanoid.Sit", sit);
            string state = harness.Store.Get("stub-mod", "state");
            StringAssert.StartsWith("false|", state);
            StringAssert.Contains("NOT_IMPLEMENTED", state);
            StringAssert.Contains("Seated", state);
        }

        [Test]
        public void ChangeState_ToJumping_IsTheOneStateAScriptMayForce()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("cs");

            harness.Stack.Runtime.LoadMod(actor, "cs-mod", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                h:ChangeState(Enum.HumanoidStateType.Jumping)
                store_set('state', h:GetState().Name)",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("Jumping", harness.Store.Get("cs-mod", "state"));
            Assert.AreEqual(1, harness.Motor.JumpCount);
        }

        [Test]
        public void Negative_MoveTo_WithAPartToFollow_IsRefusedNotIgnored()
        {
            // Following a moving target needs the character rig. Silently ignoring the argument
            // would leave a script convinced its NPC is chasing something.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("follow");

            harness.Stack.Runtime.LoadMod(actor, "follow-mod", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                local target = Instance.new('Part')
                target.Parent = workspace
                local ok, err = pcall(function() h:MoveTo(Vector3.new(1, 0, 0), target) end)
                store_set('result', tostring(ok) .. '|' .. tostring(err))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            string result = harness.Store.Get("follow-mod", "result");
            StringAssert.StartsWith("false|", result);
            StringAssert.Contains("BAD_ARGUMENT", result);
        }

        [Test]
        public void Negative_NoPassiveRegeneration_InTheClassItself()
        {
            // Decision (g): the mirror puts regeneration in a SCRIPT inserted into the character,
            // and documents disabling it by adding an empty Script named Health. A Humanoid that
            // healed itself would make that documented opt-out impossible.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();

            humanoid.TakeDamage(50d);
            harness.Bindings.Scheduler.Advance(10d);

            Assert.AreEqual(50d, humanoid.Health, 1e-9d,
                "ten seconds must heal nothing without a regeneration script");
        }

        [Test]
        public void Running_ReportsTheMeasuredSpeedAsItChanges_NotOneFullSpeedEvent()
        {
            // The mirror: Running "fires when the speed at which a Humanoid is running changes".
            // A controller accelerating from a standstill reports each rate it passes through,
            // not the configured WalkSpeed once on entering the state.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);

            double[] ramp = { 4d, 8d, 12d, 16d };
            for (int step = 0; step < ramp.Length; step++)
            {
                harness.Motor.MeasuredSpeedValue = ramp[step];
                harness.Bindings.Scheduler.Advance(0.1d);
            }

            CollectionAssert.AreEqual(ramp, speeds,
                "every measured rate on the way up is a change the signal has to report");
        }

        [Test]
        public void Running_FiresZero_WhenTheCharacterStops()
        {
            // The mirror: "When the Humanoid stops running this event will fire with a speed of 0."
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            harness.Motor.MoveDirectionValue = RbxVector3.Zero;
            harness.Motor.MeasuredSpeedValue = 0.02d;
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(new[] { 16d, 0d }, speeds,
                "a resting body still reads solver jitter; the mirror promises exactly 0, not almost 0");
        }

        [Test]
        public void Negative_Running_DoesNotRefire_WhileTheSpeedHoldsWithinTheResolution()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            double jitter = RbxHumanoid.RunningSpeedResolutionStuds * 0.3d;
            for (int frame = 0; frame < 20; frame++)
            {
                harness.Motor.MeasuredSpeedValue = 16d + (frame % 2 == 0 ? jitter : -jitter);
                harness.Bindings.Scheduler.Advance(0.1d);
            }

            CollectionAssert.AreEqual(new[] { 16d }, speeds,
                "physics jitter around a steady speed is not a change in running speed");

            harness.Motor.MeasuredSpeedValue = 16d + RbxHumanoid.RunningSpeedResolutionStuds * 2d;
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual(2, speeds.Count, "a change past the resolution is still reported");
        }

        [Test]
        public void Running_ReportsZeroOnce_BeforeDied_WhenARunningCharacterDies()
        {
            // WHY death has to report the stop: Advance refuses a dead Humanoid, so nothing after
            // this ever looks at the speed again, and a walk cycle driven by Running alone would
            // keep the last rate forever with auto-respawn off.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<object> events = harness.RecordTimeline(humanoid);
            harness.Connect(humanoid.Died, _ => events.Add("Died"));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            humanoid.Health = 0d;
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(new object[] { 16d, 0d, "Running->Dead", "Died" }, events,
                "one stop, ahead of the state change and of Died, so nothing a handler of either "
                + "starts is followed by a stale idle");
        }

        [Test]
        public void Running_AMovingCharacterLeavingTheGround_ReportsTheStopBeforeFreeFalling()
        {
            // WHY the order is the whole point: the mirror's character animation script picks the
            // fall pose on FreeFalling and the idle pose on Running(0), and the last one delivered
            // wins. Leaving Running is what ends the walk, so the stop is the last thing said
            // before the state goes — a stop heard after the fall leaves the character standing
            // in mid-air.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<object> events = harness.RecordTimeline(humanoid);
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(
                new object[] { 16d, 0d, "Running->Freefall", "FreeFalling(True)" },
                events,
                "the stop is reported before the state leaves Running and before FreeFalling");
        }

        [Test]
        public void Running_AMovingCharacterJumping_ReportsTheStopBeforeJumping()
        {
            // WHY a jump gets the same bracket: Jumping(true) selects the jump pose exactly as
            // FreeFalling selects the fall, and a Running(0) arriving after either of them puts
            // the character back into idle in the air.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<object> events = harness.RecordTimeline(humanoid);
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            humanoid.RequestJump();
            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(
                new object[]
                {
                    16d, 0d, "Running->Jumping", "Jumping(True)",
                    "Jumping->Freefall", "FreeFalling(True)"
                },
                events,
                "one stop, ahead of Jumping; the airborne step then has no second stop to report");
        }

        [Test]
        public void Running_AnIdleCharacterLanding_ReportsZeroOnTheLanding()
        {
            // WHY an unchanged value still has to be reported here: the fall already reported 0
            // (or nothing, for a character that never moved), an idle landing measures 0 again,
            // and Running is the only signal that takes the animation script out of the falling
            // pose. A change-only report would leave the character falling on the ground forever.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<object> events = harness.RecordTimeline(humanoid);

            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Motor.Grounded = true;
            harness.Bindings.Scheduler.Advance(0.1d);
            CollectionAssert.AreEqual(
                new object[] { "Running->Freefall", "FreeFalling(True)", "Freefall->Landed" },
                events,
                "a character that never moved is silent on the way up and on the Landed frame");

            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(
                new object[]
                {
                    "Running->Freefall", "FreeFalling(True)", "Freefall->Landed",
                    "Landed->Running", 0d
                },
                events,
                "entering Running always reports, even a 0 that equals the last report");

            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual(5, events.Count, "the landing report is one event, not one per step");
        }

        [Test]
        public void Running_AMovingCharacterLanding_ReportsItsSpeedAgain_AfterTheStopAndTheLanding()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<object> events = harness.RecordTimeline(humanoid);
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Motor.Grounded = true;
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(
                new object[]
                {
                    16d, 0d, "Running->Freefall", "FreeFalling(True)",
                    "Freefall->Landed", "Landed->Running", 16d
                },
                events,
                "the whole round trip in one FIFO: stop, fall, land, and the speed again only "
                + "once the state is Running — nothing on the Landed frame");
        }

        [Test]
        public void Negative_Running_DoesNotReportASecondZero_WhenAStoppedCharacterDies()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            int died = 0;
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Connect(humanoid.Died, _ => died++);
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Motor.MoveDirectionValue = RbxVector3.Zero;
            harness.Motor.MeasuredSpeedValue = 0d;
            harness.Bindings.Scheduler.Advance(0.1d);
            CollectionAssert.AreEqual(new[] { 16d, 0d }, speeds);

            humanoid.Health = 0d;
            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(new[] { 16d, 0d }, speeds,
                "the character had already stopped; dying is not a second stop");
            Assert.AreEqual(1, died);
        }

        [Test]
        public void Negative_Running_DoesNotFlipEveryStep_WhileTheSpeedHoversAtTheStopThreshold()
        {
            // A body settling against a contact reads 0.099, 0.101, 0.099 — a real change of two
            // millistuds per second. One cutoff snapped that to 0, then 0.101, then 0 again, a
            // queued signal on every step; the zero boundary was the most sensitive speed of all.
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);
            harness.Motor.MeasuredSpeedValue = 16d;
            harness.Bindings.Scheduler.Advance(0.1d);

            double wobble = RbxHumanoid.RunningSpeedResolutionStuds * 0.01d;
            for (int frame = 0; frame < 20; frame++)
            {
                harness.Motor.MeasuredSpeedValue =
                    RbxHumanoid.RunningStopSpeedStuds + (frame % 2 == 0 ? -wobble : wobble);
                harness.Bindings.Scheduler.Advance(0.1d);
            }

            CollectionAssert.AreEqual(new[] { 16d, 0d }, speeds,
                "a wobble under the resolution at the stop line is one stop, not twenty signals");
        }

        [Test]
        public void Running_AGenuineStop_ReportsExactlyZero_AndStaysStoppedUnderTheStartThreshold()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            harness.Motor.MoveDirectionValue = new RbxVector3(1f, 0f, 0f);

            double[] deceleration = { 16d, 6d, 1d, 0.05d };
            for (int step = 0; step < deceleration.Length; step++)
            {
                harness.Motor.MeasuredSpeedValue = deceleration[step];
                harness.Bindings.Scheduler.Advance(0.1d);
            }

            CollectionAssert.AreEqual(new[] { 16d, 6d, 1d, 0d }, speeds,
                "hysteresis or not, a stop is the mirror's exact 0, not the resting jitter");

            double nudge =
                (RbxHumanoid.RunningStopSpeedStuds + RbxHumanoid.RunningStartSpeedStuds) / 2d;
            for (int frame = 0; frame < 5; frame++)
            {
                harness.Motor.MeasuredSpeedValue = nudge;
                harness.Bindings.Scheduler.Advance(0.1d);
            }

            Assert.AreEqual(4, speeds.Count,
                "a resting body nudged past the stop line but short of the start line has not set off");

            harness.Motor.MeasuredSpeedValue = RbxHumanoid.RunningStartSpeedStuds;
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual(5, speeds.Count, "reaching the start line is setting off");
            Assert.AreEqual(RbxHumanoid.RunningStartSpeedStuds, speeds[4], 1e-9d);
        }

        [Test]
        public void Jump_RefusedByTheMotor_LeavesTheStateMachineWhereItWas_AcceptedEntersJumping()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = harness.Humanoid();
            List<bool> jumping = new();
            harness.Connect(humanoid.Jumping, args => jumping.Add((bool)args[0]));
            harness.Motor.Grounded = false;
            harness.Bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual(RbxHumanoidState.Freefall, humanoid.GetState());

            harness.Motor.AcceptsJumps = false;
            humanoid.RequestJump();
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(RbxHumanoidState.Freefall, humanoid.GetState(),
                "a refused jump is not a jump; the character is still falling");
            Assert.IsEmpty(jumping, "Jumping announces a state the character enters, not a request");
            Assert.AreEqual(0, harness.Motor.JumpCount);

            harness.Motor.AcceptsJumps = true;
            humanoid.RequestJump();

            Assert.AreEqual(RbxHumanoidState.Jumping, humanoid.GetState());
            Assert.AreEqual(1, harness.Motor.JumpCount);
            harness.Bindings.Scheduler.Advance(0d);
            CollectionAssert.AreEqual(new[] { true }, jumping);
        }

        [Test]
        public void ExternalMotor_ImplementingOnlyTheOriginalMembers_StillCompilesAndBehavesAsBefore()
        {
            // WHY a second fake: IRbxCharacterMotor is a public seam, so the two members added for
            // the contract gaps carry default bodies. A host motor written against the original six
            // members has to keep compiling, keep reporting the derived speed (|MoveDirection| x
            // WalkSpeed) and keep being taken as accepting every jump request.
            LegacyCharacterMotor legacy = new() { MoveDirectionValue = new RbxVector3(0f, 0f, 1f) };
            using ProductionHarness harness = new ProductionHarness(legacy);
            RbxHumanoid humanoid = harness.Humanoid();
            List<double> speeds = new();
            harness.Connect(humanoid.Running, args => speeds.Add((double)args[0]));
            humanoid.WalkSpeed = 20d;

            harness.Bindings.Scheduler.Advance(0.1d);

            CollectionAssert.AreEqual(new[] { 20d }, speeds,
                "with no measurement the Humanoid falls back to the configured WalkSpeed");
            Assert.AreEqual(20d, legacy.WalkSpeed, 1e-9d);

            humanoid.RequestJump();

            Assert.AreEqual(1, legacy.JumpCount);
            Assert.AreEqual(RbxHumanoidState.Jumping, humanoid.GetState(),
                "a motor that cannot answer is taken as accepting, as before");
        }

        [Test]
        public void UnityMotor_MeasuresTheBodysResolvedPlanarSpeedInStuds_AndRefusesAnAirborneJump()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject bodyObject = new GameObject("MeasuredSpeedBody");
            try
            {
                floor.transform.position = new Vector3(0f, -0.5f, 0f);
                floor.transform.localScale = new Vector3(10f, 1f, 10f);
                Rigidbody body = bodyObject.AddComponent<Rigidbody>();
                body.useGravity = false;
                bodyObject.transform.position = new Vector3(0f, 0.3f, 0f);
                Physics.SyncTransforms();
                UnityRbxCharacterMotor motor = new(body);

                body.linearVelocity = new Vector3(
                    RbxSpace.LengthToUnity(3f), 9f, RbxSpace.LengthToUnity(4f));

                Assert.IsTrue(motor.MeasuredSpeed.HasValue, "CoreAI's own motor measures");
                Assert.AreEqual(5d, motor.MeasuredSpeed.Value, 1e-3d,
                    "a 3-4-5 planar velocity in studs; the vertical component is not running");
                Assert.IsTrue(motor.TryJump(50d, 7.2d, useJumpPower: true),
                    "standing on the floor, the jump is taken");

                bodyObject.transform.position = new Vector3(0f, 5f, 0f);
                Physics.SyncTransforms();

                Assert.IsFalse(motor.TryJump(50d, 7.2d, useJumpPower: true),
                    "airborne, there is nothing to push against and the motor says so");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bodyObject);
                UnityEngine.Object.DestroyImmediate(floor);
                RbxSpace.ResetForTests();
            }
        }

        // ---- Harness -------------------------------------------------------------------------

        /// <summary>A character controller that records what the Humanoid asked of it.</summary>
        private sealed class FakeCharacterMotor : IRbxCharacterMotor
        {
            public double WalkSpeed { get; private set; } = double.NaN;

            public int JumpCount { get; private set; }

            public double LastJumpHeight { get; private set; }

            public double LastJumpPower { get; private set; }

            public bool LastUseJumpPower { get; private set; }

            public RbxVector3? Target { get; private set; }

            public RbxVector3 PositionValue { get; set; }

            public RbxVector3 MoveDirectionValue { get; set; }

            public bool Grounded { get; set; } = true;

            /// <summary>Null keeps the Humanoid's derived fallback; a value is the measured rate.</summary>
            public double? MeasuredSpeedValue { get; set; }

            public bool AcceptsJumps { get; set; } = true;

            public RbxVector3 Position => PositionValue;

            public RbxVector3 MoveDirection => MoveDirectionValue;

            public bool IsGrounded => Grounded;

            public double? MeasuredSpeed => MeasuredSpeedValue;

            public void SetWalkSpeed(double studsPerSecond)
            {
                WalkSpeed = studsPerSecond;
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
                JumpCount++;
                LastJumpPower = jumpPower;
                LastJumpHeight = jumpHeight;
                LastUseJumpPower = useJumpPower;
            }

            public bool TryJump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
                if (!AcceptsJumps)
                {
                    return false;
                }

                Jump(jumpPower, jumpHeight, useJumpPower);
                return true;
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
                Target = targetStuds;
            }
        }

        /// <summary>
        /// A host motor written against the seam's original six members and nothing else.
        /// </summary>
        /// <remarks>
        /// WHY it must stay minimal: it is the source-compatibility pin. If a member added to
        /// <see cref="IRbxCharacterMotor"/> ever loses its default body, this class stops compiling,
        /// which is exactly the break an external implementer would see.
        /// </remarks>
        private sealed class LegacyCharacterMotor : IRbxCharacterMotor
        {
            public double WalkSpeed { get; private set; } = double.NaN;

            public int JumpCount { get; private set; }

            public RbxVector3 MoveDirectionValue { get; set; }

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => MoveDirectionValue;

            public bool IsGrounded => true;

            public void SetWalkSpeed(double studsPerSecond)
            {
                WalkSpeed = studsPerSecond;
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
                JumpCount++;
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }
        }

        private sealed class ProductionHarness : IDisposable
        {
            private RbxHumanoid _humanoid;

            /// <param name="motor">
            /// The motor every Humanoid gets; null attaches the recording <see cref="Motor"/>.
            /// </param>
            public ProductionHarness(IRbxCharacterMotor motor = null)
            {
                LogLines = new List<string>();
                Binder = new InMemoryInstanceBackingBinder();
                Registry = new InstanceRegistry(
                    binder: Binder,
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "humanoid-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game, log: LogLines.Add);
                Motor = new FakeCharacterMotor();
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
                Bindings.AttachCharacterMotorFactory(_ => motor ?? Motor);
            }

            public List<string> LogLines { get; }

            public InMemoryInstanceBackingBinder Binder { get; }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public FakeCharacterMotor Motor { get; }

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            /// <summary>Creates one Humanoid in the world with the fake motor attached.</summary>
            public RbxHumanoid Humanoid()
            {
                if (_humanoid != null)
                {
                    return _humanoid;
                }

                _humanoid = (RbxHumanoid)Registry.Create("Humanoid");
                _humanoid.Parent = Registry.WorldRoot;
                return _humanoid;
            }

            /// <summary>Connects a C# handler to a signal that already has a scheduler.</summary>
            public void Connect(RbxScriptSignal signal, Action<object[]> handler)
            {
                signal.Connect(handler);
            }

            /// <summary>
            /// Records every state-machine signal of <paramref name="humanoid"/> in the order the
            /// scheduler's single FIFO delivers them: Running as its speed, the rest as text.
            /// </summary>
            public List<object> RecordTimeline(RbxHumanoid humanoid)
            {
                List<object> events = new();
                Connect(humanoid.Running, args => events.Add(args[0]));
                Connect(humanoid.StateChanged, args => events.Add(args[0] + "->" + args[1]));
                Connect(humanoid.FreeFalling, args => events.Add("FreeFalling(" + args[0] + ")"));
                Connect(humanoid.Jumping, args => events.Add("Jumping(" + args[0] + ")"));
                return events;
            }

            public ActorContext Actor(string actorId)
            {
                return new LocalActorIdentityProvider(
                        actorId,
                        "session-" + actorId,
                        Registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}

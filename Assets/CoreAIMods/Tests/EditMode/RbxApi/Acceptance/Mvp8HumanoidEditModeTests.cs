using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
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

            public RbxVector3 Position => PositionValue;

            public RbxVector3 MoveDirection => MoveDirectionValue;

            public bool IsGrounded => Grounded;

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

            public void MoveTo(RbxVector3? targetStuds)
            {
                Target = targetStuds;
            }
        }

        private sealed class ProductionHarness : IDisposable
        {
            private RbxHumanoid _humanoid;

            public ProductionHarness()
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
                Bindings.AttachCharacterMotorFactory(_ => Motor);
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

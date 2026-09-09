using System;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// Pins the <see cref="IRbxCharacterMotorProvider"/> seam through the real
    /// <c>CoreAiModsInstaller.RegisterCoreAiMods</c> composition and a real <see cref="RbxWorldHost"/>:
    /// a registered provider is asked for every joining character, its motor is what a Humanoid actually
    /// drives, declining leaves CoreAI's own motor in charge, the gravity delegate handed to the provider
    /// tracks the world's live physics port (not a value captured once), and a world staged and committed
    /// at runtime still reaches the provider for its own characters.
    /// </summary>
    [TestFixture]
    public sealed class RbxCharacterMotorProviderEditModeTests
    {
        private const float Epsilon = 1e-4f;

        // WHY: the Lua-CSharp runtime bridges its async VM to a synchronous call site; detaching Unity's
        // SynchronizationContext lets VM continuations complete on the thread pool instead of deadlocking
        // the blocked main thread (mirrors RbxWorldHostDiWiringEditModeTests).
        private SynchronizationContext _savedContext;
        private CoreAISettingsAsset _settings;
        private RecordingLog _log;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _log = new RecordingLog();
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
            UnityEngine.Object.DestroyImmediate(_settings);
            RbxSpace.ResetForTests();
        }

        [Test]
        public void RegisteredProvider_MotorObservablyDrivesTheHumanoid()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer player = bindings.ConnectActor(Actor("host-motor-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                Assert.IsNotNull(humanoid);

                Assert.GreaterOrEqual(provider.TryCreateCalls, 1,
                    "the registered provider must be asked for a motor for the joining character.");
                Assert.AreSame(humanoid, provider.LastHumanoid);
                RecordingCharacterMotor hostMotor = provider.LastCreatedMotor;
                Assert.IsNotNull(hostMotor, "the provider must have handed back its own motor.");
                Assert.AreSame(hostMotor, humanoid.Motor,
                    "the Humanoid must actually be wired to the motor the provider returned.");

                // WHY these two checks and not just the factory field: MoveTo must reach the host's own
                // motor, and a Humanoid read (MoveDirection) must come back through it too.
                RbxVector3 target = new(20f, 0f, 0f);
                int moveToCallsBefore = hostMotor.MoveToCallCount;
                humanoid.MoveTo(target);
                Assert.AreEqual(moveToCallsBefore + 1, hostMotor.MoveToCallCount,
                    "Humanoid.MoveTo must be forwarded to the provider's motor.");
                Assert.AreEqual(target, hostMotor.LastMoveToTarget);
                Assert.AreEqual(hostMotor.MoveDirection, humanoid.MoveDirection,
                    "Humanoid.MoveDirection must be read through the host motor, not CoreAI's own.");

                // WHY this must also hold: the fixed-step pump now steps EVERY motor, host or not, so
                // the host motor is what receives the step and CoreAI's bundled one must not be
                // quietly driving the same body behind it. The bound Rigidbody staying still is what
                // proves only one motor is in charge.
                RbxInstance rootPart = player.Character.FindFirstChild("HumanoidRootPart");
                Assert.IsNotNull(rootPart);
                Assert.IsTrue(host.Binder.TryGetBoundObject(rootPart.Id, out GameObject body));
                Rigidbody rigidbody = body.GetComponent<Rigidbody>();
                Assert.IsNotNull(rigidbody);
                bindings.StepCharacterMotors(1f / 60f);
                bindings.StepCharacterMotors(1f / 60f);
                Assert.AreEqual(0f, rigidbody.linearVelocity.magnitude, Epsilon,
                    "once a host motor is in charge, CoreAI's bundled UnityRbxCharacterMotor must not " +
                    "also be moving the body.");
                Assert.AreEqual(2, hostMotor.StepCallCount,
                    "the fixed-step pump must reach a host motor too; testing for the " +
                    "concrete bundled type left a host motor's MoveTo never advancing.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void ProviderDeclines_CoreAisOwnMotorStillMovesTheCharacter()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new() { ReturnNull = true };

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer player = bindings.ConnectActor(Actor("declining-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                Assert.IsNotNull(humanoid);

                Assert.GreaterOrEqual(provider.TryCreateCalls, 1,
                    "a registered provider must still be asked even if it always declines.");
                Assert.IsInstanceOf<UnityRbxCharacterMotor>(humanoid.Motor,
                    "declining (returning null) must leave CoreAI's bundled motor in charge.");

                RbxInstance rootPart = player.Character.FindFirstChild("HumanoidRootPart");
                Assert.IsTrue(host.Binder.TryGetBoundObject(rootPart.Id, out GameObject body));
                Rigidbody rigidbody = body.GetComponent<Rigidbody>();
                Assert.IsNotNull(rigidbody);

                humanoid.MoveTo(new RbxVector3(20f, 0f, 0f));
                bindings.StepCharacterMotors(1f / 60f);

                Assert.Greater(rigidbody.linearVelocity.magnitude, 0f,
                    "the character must still actually move once CoreAI's own motor is driving it.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void GravityDelegate_ReadsTheWorldsCurrentGravity_AcrossARuntimeWorldReload()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new() { ReturnNull = true };
            string storeId = "motor-gravity-" + Guid.NewGuid().ToString("N");

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry, storeId);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer player = bindings.ConnectActor(Actor("gravity-a"));
                bindings.Scheduler.Advance(1d / 60d);
                Assert.IsNotNull(player.Character);
                Func<float> gravityFunc = provider.LastGravityFunc;
                Assert.IsNotNull(gravityFunc,
                    "CreateCharacterMotor must hand the provider a gravity delegate for every character.");

                UnityRbxPhysicsPort firstPort = host.PhysicsPort;
                firstPort.SetGravity(50d);
                Assert.AreEqual(RbxSpace.AccelerationToUnity(50f), gravityFunc(), Epsilon,
                    "the delegate must read the world's LIVE gravity, not a value captured at " +
                    "TryCreate time.");

                // WHY the world is reloaded now: PublishReplacement disposes this port and builds a fresh
                // one on the SAME RbxWorldHost. The captured delegate closes over the host, not the port,
                // so it must follow the swap.
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldPackagePayload payload = worlds.CaptureCurrent();
                RbxWorldLoadResult result = worlds.LoadConfirmedAsync(payload).GetAwaiter().GetResult();
                Assert.IsTrue(result.Success, result.Error);

                UnityRbxPhysicsPort secondPort = host.PhysicsPort;
                Assert.AreNotSame(firstPort, secondPort,
                    "sanity: a runtime world load must replace the physics port.");

                // WHY a third, distinct value: a stale capture of the disposed first port would still
                // answer 50 here (Dispose never clears its last gravity value) -- exactly the class of bug
                // an earlier audit found twice. Setting a THIRD value on the NEW port proves the delegate
                // keeps reading live, not merely "whatever the new session started with".
                secondPort.SetGravity(20d);
                Assert.AreEqual(RbxSpace.AccelerationToUnity(20f), gravityFunc(), Epsilon,
                    "after a runtime world load the SAME delegate must read the NEW port's current " +
                    "gravity, not the disposed port's last value and not a snapshot taken at load time.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void StagedAndCommittedWorld_StillReachesTheHostProvider()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();
            string storeId = "motor-staged-" + Guid.NewGuid().ToString("N");

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry, storeId);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsModStack stack = container.Resolve<LuaCsModStack>();
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                LuaCsRbxApiBindings initialBindings = stack.GameplayBindings.RbxApi;

                // WHY captured and loaded back with no character connected yet: the gate this test pins
                // is whether the STAGED session's own motor factory reaches the provider, not whether a
                // character restored from the outgoing world does -- capturing an empty world keeps the
                // later assertions unambiguously about the committed session's own join.
                RbxWorldPackagePayload payload = worlds.CaptureCurrent();
                RbxWorldLoadResult result = worlds.LoadConfirmedAsync(payload).GetAwaiter().GetResult();
                Assert.IsTrue(result.Success, result.Error);

                LuaCsRbxApiBindings committedBindings = stack.GameplayBindings.RbxApi;
                Assert.AreNotSame(initialBindings, committedBindings,
                    "sanity: a runtime world load must replace the active session's Rbx bindings.");

                // WHY this is the gate: the staged session used to ship with NO motor factory attached at
                // all, so every Humanoid in a world loaded at runtime silently got the null motor -- see
                // CoreAiModsInstaller's rbxApiFactory comment. A character joining the committed session
                // must still reach the host provider.
                int callsBeforeJoin = provider.TryCreateCalls;
                RbxPlayer committedPlayer = committedBindings.ConnectActor(Actor("staged-committed"));
                committedBindings.Scheduler.Advance(1d / 60d);
                Assert.IsNotNull(committedPlayer.Character,
                    "the committed world must still be able to spawn characters.");

                Assert.Greater(provider.TryCreateCalls, callsBeforeJoin,
                    "the STAGED-then-COMMITTED session must also ask the host provider for a motor.");

                RbxHumanoid committedHumanoid =
                    (RbxHumanoid)committedPlayer.Character.FindFirstChild("Humanoid");
                Assert.AreSame(committedHumanoid, provider.LastHumanoid,
                    "the most recent provider call must be for the committed world's own character.");
                RecordingCharacterMotor committedMotor = provider.LastCreatedMotor;
                Assert.IsNotNull(committedMotor);
                Assert.AreSame(committedMotor, committedHumanoid.Motor);

                RbxVector3 target = new(5f, 0f, 0f);
                int moveToCallsBefore = committedMotor.MoveToCallCount;
                committedHumanoid.MoveTo(target);
                Assert.AreEqual(moveToCallsBefore + 1, committedMotor.MoveToCallCount,
                    "the committed world's Humanoid must forward movement to the host motor too.");
                Assert.AreEqual(target, committedMotor.LastMoveToTarget);
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void ReplacingTheMotorFactory_ReleasesTheDisplacedMotorExactlyOnce()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer player = bindings.ConnectActor(Actor("replace-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                RecordingCharacterMotor originalMotor = provider.LastCreatedMotor;
                Assert.IsNotNull(originalMotor);
                Assert.AreSame(originalMotor, humanoid.Motor);

                RecordingCharacterMotor replacementMotor = new();
                // WHY calling AttachCharacterMotorFactory directly instead of reconnecting an actor:
                // this is exactly what a runtime factory swap does -- the same entry point the
                // composition uses once at startup -- so it drives the general "a live motor gets
                // replaced" path directly, without also entangling staleness or unregistration.
                bindings.AttachCharacterMotorFactory(_ => replacementMotor);

                Assert.AreEqual(1, originalMotor.ReleaseCallCount,
                    "replacing a live motor must release the one it displaced exactly once.");
                Assert.AreSame(replacementMotor, humanoid.Motor,
                    "the Humanoid must be driven by the replacement motor after the swap.");
                Assert.AreEqual(0, replacementMotor.ReleaseCallCount,
                    "a motor that is still in use must never be released.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void AnchoredRootPart_RebuildsAndReleasesTheStaleHostMotor()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer player = bindings.ConnectActor(Actor("anchor-rebuild-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                RbxInstance rootPart = player.Character.FindFirstChild("HumanoidRootPart");
                Assert.IsNotNull(rootPart);
                RecordingCharacterMotor firstMotor = provider.LastCreatedMotor;
                Assert.IsNotNull(firstMotor);
                Assert.AreSame(firstMotor, humanoid.Motor);

                // WHY Anchored specifically: writing it is the real mechanism that destroys and
                // recreates a root part's Rigidbody (InstanceGameObjectBinder.ApplyAnchored) --
                // exactly how a motor's body goes stale in production, per
                // IRbxCharacterMotor.IsAvailable's remarks.
                host.Binder.SetAnchored(rootPart.Id, true);
                host.Binder.SetAnchored(rootPart.Id, false);
                Assert.IsFalse(firstMotor.IsAvailable,
                    "sanity: the motor's captured Rigidbody must actually have gone stale.");

                bindings.PumpPreSimulation(1f / 60f);

                Assert.AreEqual(1, firstMotor.ReleaseCallCount,
                    "a motor that went stale from a rebuilt body must be released exactly once.");
                RecordingCharacterMotor rebuiltMotor = provider.LastCreatedMotor;
                Assert.AreNotSame(firstMotor, rebuiltMotor,
                    "sanity: the rebuild must actually have asked the provider for a fresh motor.");
                Assert.AreSame(rebuiltMotor, humanoid.Motor,
                    "the Humanoid must be driven by the freshly built motor after the rebuild.");
                Assert.AreEqual(0, rebuiltMotor.ReleaseCallCount,
                    "the freshly installed motor must not have been released.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void DisconnectingTheActor_ReleasesTheMotorExactlyOnce()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                ActorContext actor = Actor("unregister-a");
                bindings.ConnectActor(actor);
                bindings.Scheduler.Advance(1d / 60d);
                RecordingCharacterMotor motor = provider.LastCreatedMotor;
                Assert.IsNotNull(motor);
                Assert.AreEqual(0, motor.ReleaseCallCount);

                // WHY DisconnectActor and not Destroy() directly: this is the real path a departing
                // (or kicked) player takes -- RbxPlayers.RemoveActor -> RbxCharacterFactory.Unload ->
                // character.Destroy() -- which is what unregisters the Humanoid through the registry,
                // the same route InstanceRegistry.Unregistered notifies OnInstanceUnregistered from.
                bool disconnected = bindings.DisconnectActor(actor);
                Assert.IsTrue(disconnected);

                Assert.AreEqual(1, motor.ReleaseCallCount,
                    "unregistering the Humanoid that owned this motor must release it exactly once.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void DeathTriggeredRespawn_ReleasesTheOldMotorExactlyOnceForTheNewCharacter()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                bindings.Players.RespawnTime = 0d;
                RbxPlayer player = bindings.ConnectActor(Actor("death-respawn-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid firstHumanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                RecordingCharacterMotor firstMotor = provider.LastCreatedMotor;
                Assert.IsNotNull(firstMotor);
                Assert.AreSame(firstMotor, firstHumanoid.Motor);

                firstHumanoid.Health = 0d;
                Assert.AreEqual(0, firstMotor.ReleaseCallCount,
                    "dying by itself must not release the motor -- only the respawn's replacement " +
                    "does, through the same unregistration path as any other character teardown.");

                // WHY two ticks: Died is a deferred signal (RbxScriptSignal.Fire queues, it does not
                // call synchronously), so the first Advance may only drain it and schedule the
                // RespawnTime host callback; the second guarantees that callback -- due immediately,
                // since RespawnTime is zero -- has actually been serviced.
                bindings.Scheduler.Advance(1d / 60d);
                bindings.Scheduler.Advance(1d / 60d);

                Assert.AreEqual(1, firstMotor.ReleaseCallCount,
                    "the respawn must have destroyed the old character and released its motor " +
                    "exactly once.");
                RbxHumanoid secondHumanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                Assert.AreNotSame(firstHumanoid, secondHumanoid,
                    "sanity: the respawn must actually have built a new character.");
                Assert.AreSame(secondHumanoid, provider.LastHumanoid);
                RecordingCharacterMotor secondMotor = provider.LastCreatedMotor;
                Assert.AreNotSame(firstMotor, secondMotor);
                Assert.AreSame(secondMotor, secondHumanoid.Motor);
                Assert.AreEqual(0, secondMotor.ReleaseCallCount,
                    "the new character's motor must not have been released.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void DisposingBindings_ReleasesEveryLiveMotorExactlyOnce()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingCharacterMotorProvider provider = new();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);
            builder.RegisterInstance<IRbxCharacterMotorProvider>(provider);

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsRbxApiBindings bindings = container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
                RbxPlayer playerA = bindings.ConnectActor(Actor("dispose-a"));
                bindings.Scheduler.Advance(1d / 60d);
                RbxPlayer playerB = bindings.ConnectActor(Actor("dispose-b"));
                bindings.Scheduler.Advance(1d / 60d);

                RecordingCharacterMotor motorA = (RecordingCharacterMotor)
                    ((RbxHumanoid)playerA.Character.FindFirstChild("Humanoid")).Motor;
                RecordingCharacterMotor motorB = (RecordingCharacterMotor)
                    ((RbxHumanoid)playerB.Character.FindFirstChild("Humanoid")).Motor;
                Assert.AreEqual(0, motorA.ReleaseCallCount);
                Assert.AreEqual(0, motorB.ReleaseCallCount);

                container.Dispose();

                Assert.AreEqual(1, motorA.ReleaseCallCount,
                    "Dispose must release every motor it still holds exactly once.");
                Assert.AreEqual(1, motorB.ReleaseCallCount,
                    "Dispose must release every motor it still holds exactly once.");
            }
            finally
            {
                container.Dispose();
                UnityEngine.Object.DestroyImmediate(registry);
                UnityEngine.Object.DestroyImmediate(hostGo);
            }
        }

        /// <summary>Mirrors RbxWorldHostDiWiringEditModeTests' minimal registrations: just enough for
        /// RegisterCoreAiMods' LuaCsModStack factory to resolve, plus the world-command executors it
        /// expects.</summary>
        private void RegisterMinimalModStack(
            ContainerBuilder builder,
            CoreAiPrefabRegistryAsset registry,
            string modStoreId = null)
        {
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            builder.RegisterInstance<ILog>(_log);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.RegisterInstance<ICoreAISettings>(_settings);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

            builder.RegisterWorldCommands(registry);
            builder.RegisterCoreAiMods(modStoreId: modStoreId);
        }

        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId).GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        /// <summary>Test double for <see cref="IRbxCharacterMotorProvider"/>: records every call and, by
        /// default, hands back a <see cref="RecordingCharacterMotor"/> so a test can observe whether the
        /// Humanoid actually drives through it.</summary>
        private sealed class RecordingCharacterMotorProvider : IRbxCharacterMotorProvider
        {
            public bool ReturnNull { get; set; }

            public int TryCreateCalls { get; private set; }

            public RbxHumanoid LastHumanoid { get; private set; }

            public GameObject LastBody { get; private set; }

            public Func<float> LastGravityFunc { get; private set; }

            public RecordingCharacterMotor LastCreatedMotor { get; private set; }

            public IRbxCharacterMotor TryCreate(
                RbxHumanoid humanoid, GameObject body, Func<float> worldGravityMetresPerSecondSquared)
            {
                TryCreateCalls++;
                LastHumanoid = humanoid;
                LastBody = body;
                LastGravityFunc = worldGravityMetresPerSecondSquared;
                if (ReturnNull)
                {
                    return null;
                }

                RecordingCharacterMotor motor = new(body);
                LastCreatedMotor = motor;
                return motor;
            }
        }

        /// <summary>Records every call so a test can assert Humanoid movement was forwarded here, instead
        /// of only checking that a factory field was set.</summary>
        private sealed class RecordingCharacterMotor : IRbxCharacterMotor
        {
            // WHY a captured Rigidbody, not a settable flag: the rebuild test reproduces the real
            // staleness mechanism (writing Anchored destroys and recreates the root part's
            // Rigidbody), and tracking THAT the same way CoreAI's own UnityRbxCharacterMotor does
            // proves the pipeline releases a host motor that went stale for a genuine reason, not
            // a test-only trapdoor. A motor built with no body (unused by any current test) stays
            // always-available, matching the interface's own default.
            private readonly Rigidbody _trackedRigidbody;
            private readonly bool _tracksAvailability;

            public RecordingCharacterMotor(GameObject body = null)
            {
                _trackedRigidbody = body != null ? body.GetComponent<Rigidbody>() : null;
                // WHY a separate flag rather than reading _trackedRigidbody == null later: once
                // Unity destroys the Rigidbody, its reference reads as == null too (fake-null), so
                // "never had one to track" and "the tracked one just died" would be indistinguishable
                // without recording which case this was at construction time.
                _tracksAvailability = _trackedRigidbody != null;
            }

            public int MoveToCallCount { get; private set; }

            public RbxVector3? LastMoveToTarget { get; private set; }

            public RbxVector3 MoveDirection { get; set; } = new(1f, 0f, 0f);

            public RbxVector3 Position { get; set; } = RbxVector3.Zero;

            public bool IsGrounded { get; set; } = true;

            public bool IsAvailable => !_tracksAvailability || _trackedRigidbody != null;

            public int ReleaseCallCount { get; private set; }

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public int StepCallCount { get; private set; }

            public void MoveTo(RbxVector3? targetStuds)
            {
                MoveToCallCount++;
                LastMoveToTarget = targetStuds;
            }

            public void Step(double deltaSeconds)
            {
                StepCallCount++;
            }

            public void Release()
            {
                ReleaseCallCount++;
            }
        }
    }
}

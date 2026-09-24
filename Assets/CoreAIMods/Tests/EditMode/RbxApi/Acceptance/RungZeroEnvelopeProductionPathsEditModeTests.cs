using System;
using System.Collections.Generic;
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
    /// <summary>Production-path coverage for server-generated mutation envelopes.</summary>
    [TestFixture]
    public sealed class RungZeroEnvelopeProductionPathsEditModeTests
    {
        private const LuaCapabilities ProductionCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        [Test]
        public void LoadMod_MainChunkMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("main-actor");
            int before = harness.Registry.RetainedMutationOperationCount;

            harness.Stack.Runtime.LoadMod(actor, "main-envelope", @"
                workspace.Name = 'MainEnvelopeWorkspace'", persistToStore: false);

            Assert.AreEqual("MainEnvelopeWorkspace", harness.Registry.WorldRoot.Name);
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, before);
        }

        [Test]
        public void Scheduler_ResumedTaskMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("task-actor");
            harness.Stack.Runtime.LoadMod(actor, "task-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'TaskBefore'
                marker.Parent = workspace
                task.spawn(function()
                    task.wait()
                    marker.Name = 'TaskAfter'
                end)", persistToStore: false);
            int beforeResume = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("TaskAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeResume);
        }

        [Test]
        public void Heartbeat_HandlerMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("heartbeat-actor");
            harness.Stack.Runtime.LoadMod(actor, "heartbeat-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'HeartbeatBefore'
                marker.Parent = workspace
                game:GetService('RunService').Heartbeat:Connect(function()
                    marker.Name = 'HeartbeatAfter'
                end)", persistToStore: false);
            int beforeDispatch = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.PumpHeartbeat(0.016f);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("HeartbeatAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeDispatch);
        }

        [Test]
        public void RemoteEvent_HandlerMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext host = CoreAI.Composition.CoreServicesInstaller
                .DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            harness.Stack.Runtime.LoadMod(host, "remote-server-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'RemoteBefore'
                marker.Parent = workspace
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'EnvelopeRemote'
                remote.Parent = workspace
                remote.OnServerEvent:Connect(function()
                    marker.Name = 'RemoteAfter'
                end)", persistToStore: false);
            ActorContext client = harness.Actor("remote-client-actor");
            harness.Stack.Runtime.LoadMod(client, "remote-client-envelope", @"
                workspace.EnvelopeRemote:FireServer()", persistToStore: false);
            int beforeDispatch = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("RemoteAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeDispatch);
        }

        [Test]
        public void CrossModExportMutation_UsesCalleeActorEnvelope()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext callee = harness.Actor("callee-actor");
            harness.Stack.Runtime.LoadMod(callee, "callee-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'CrossBefore'
                marker.Parent = workspace
                mods_export('mutate', function()
                    marker.Name = 'CrossAfter'
                    return marker.Name
                end)", persistToStore: false);
            ActorContext caller = harness.Actor("caller-actor");
            int before = harness.Registry.RetainedMutationOperationCount;

            harness.Stack.Runtime.LoadMod(caller, "caller-envelope", @"
                assert(mods_call('callee-envelope', 'mutate') == 'CrossAfter')",
                persistToStore: false);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("CrossAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, before);
        }

        [Test]
        public void BareRegistryDestroy_WithoutEnvelope_InAclWorld_IsRefused()
        {
            InstanceRegistry registry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "bare-registry-world");
            RbxInstance owned = registry.Create(
                "Folder", ownerActorId: "bare-actor", accessScope: InstanceAccessScope.Owned);

            RbxError error = Assert.Throws<RbxError>(() =>
                registry.DestroyInstance(owned, "bare-actor", false, "bare-registry-world"));

            StringAssert.Contains("actor 'bare-actor'", error.RawMessage);
            StringAssert.Contains("envelope", error.RawMessage.ToLowerInvariant());
            Assert.IsFalse(owned.IsDestroyed);
        }

        [Test]
        public void HumanoidMethods_CrossActor_AreRefusedByTheWorldAcl()
        {
            // WHY (M8-05): the three Humanoid mutators called the engine class directly, so actor B
            // could kill, walk or launch actor A's character although `h.Health = 0` was refused.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("hum-a");
            harness.Stack.Runtime.LoadMod(owner, "hum-owner", @"
                local h = Instance.new('Humanoid')
                h.Name = 'Victim'
                h.Parent = workspace", persistToStore: false);
            RbxHumanoid humanoid = harness.Registry.WorldRoot.FindFirstChild("Victim") as RbxHumanoid;
            Assert.IsNotNull(humanoid);
            // WHY no scheduler Advance anywhere below: the PreSimulation pump may rebuild character
            // motors, which would replace this recording motor before it is read.
            RecordingMotor motor = new RecordingMotor();
            humanoid.AttachHost(harness.Bindings.Scheduler, motor, null);
            Assert.IsTrue(harness.Registry.TryGetRecord(humanoid.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;

            ActorContext attacker = harness.Actor("hum-b");
            harness.Stack.Runtime.LoadMod(attacker, "hum-attacker", @"
                local h = workspace:FindFirstChild('Victim')
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('damage', function() h:TakeDamage(1e308) end)
                try('move', function() h:MoveTo(Vector3.new(50, 0, 0)) end)
                try('jump', function() h:ChangeState(Enum.HumanoidStateType.Jumping) end)",
                persistToStore: false);

            foreach (string label in new[] { "damage", "move", "jump" })
            {
                string result = harness.Store.Get("hum-attacker", label);
                StringAssert.StartsWith("false|", result, label + " must be refused");
                StringAssert.Contains("actor 'hum-b'", result, label);
                StringAssert.Contains("Owned by actor 'hum-a'", result, label);
            }

            Assert.AreEqual(100d, humanoid.Health, 1e-9d, "the refused TakeDamage changed nothing");
            Assert.IsFalse(humanoid.IsDead);
            Assert.AreEqual(0, motor.MoveTargetCount, "the refused MoveTo never reached the motor");
            Assert.AreEqual(0, motor.JumpCount, "the refused ChangeState never jumped");
            Assert.AreNotEqual(RbxHumanoidState.Jumping, humanoid.GetState());
            Assert.IsTrue(harness.Registry.TryGetRecord(humanoid.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore, after.Revision);
        }

        [Test]
        public void HumanoidTakeDamage_OwnHumanoid_AppliesAndAdvancesTheRevision()
        {
            // WHY: the positive twin — the owner still damages its own Humanoid, and the Health
            // change now advances the revision that stale-write checks and replication read.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("hum-self");
            harness.Stack.Runtime.LoadMod(owner, "hum-self-setup", @"
                local h = Instance.new('Humanoid')
                h.Name = 'Self'
                h.Parent = workspace", persistToStore: false);
            RbxHumanoid humanoid = harness.Registry.WorldRoot.FindFirstChild("Self") as RbxHumanoid;
            Assert.IsNotNull(humanoid);
            RecordingMotor motor = new RecordingMotor();
            humanoid.AttachHost(harness.Bindings.Scheduler, motor, null);
            Assert.IsTrue(harness.Registry.TryGetRecord(humanoid.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;

            harness.Stack.Runtime.LoadMod(owner, "hum-self-act", @"
                local h = workspace:FindFirstChild('Self')
                h:TakeDamage(10)
                h:MoveTo(Vector3.new(5, 0, 0))
                h:ChangeState(Enum.HumanoidStateType.Jumping)", persistToStore: false);

            Assert.AreEqual(90d, humanoid.Health, 1e-9d);
            Assert.AreEqual(1, motor.MoveTargetCount);
            Assert.AreEqual(1, motor.JumpCount);
            Assert.IsTrue(harness.Registry.TryGetRecord(humanoid.Id, out InstanceRecord after));
            Assert.Greater(after.Revision, revisionBefore,
                "TakeDamage is a Health write and advances the revision like one");
        }

        [Test]
        public void HumanoidTakeDamage_ReadOnlyModOfTheOwner_IsRefusedForMissingWorldEdit()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("hum-ro");
            harness.Stack.Runtime.LoadMod(owner, "hum-ro-setup", @"
                local h = Instance.new('Humanoid')
                h.Name = 'ReadOnlyVictim'
                h.Parent = workspace", persistToStore: false);
            RbxHumanoid humanoid =
                harness.Registry.WorldRoot.FindFirstChild("ReadOnlyVictim") as RbxHumanoid;
            Assert.IsNotNull(humanoid);

            harness.Stack.Runtime.LoadMod(owner, "hum-ro-attempt", @"
                local ok, err = pcall(function()
                    workspace:FindFirstChild('ReadOnlyVictim'):TakeDamage(10)
                end)
                store_set('result', tostring(ok) .. '|' .. tostring(err))",
                LuaCapabilities.Read, persistToStore: false);

            string result = harness.Store.Get("hum-ro-attempt", "result");
            StringAssert.StartsWith("false|", result);
            StringAssert.Contains("WorldEdit", result);
            Assert.AreEqual(100d, humanoid.Health, 1e-9d);
        }

        [Test]
        public void TweenService_ReadOnlyMod_CannotCreatePlayOrCancel()
        {
            // WHY (M8-06): TweenService:Create and Tween:Play skipped WorldEdit, so a Read-tier mod
            // could move, recolour and resize anything its actor may write, frame by frame.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("tween-ro");
            harness.Stack.Runtime.LoadMod(owner, "tween-ro-setup", @"
                local part = Instance.new('Part')
                part.Name = 'TweenTarget'
                part.Parent = workspace
                local tween = game:GetService('TweenService'):Create(
                    part, TweenInfo.new(1), {Transparency = 1})
                local ref = Instance.new('ObjectValue')
                ref.Name = 'TweenRef'
                ref.Value = tween
                ref.Parent = workspace", persistToStore: false);
            RbxObjectValue reference =
                harness.Registry.WorldRoot.FindFirstChild("TweenRef") as RbxObjectValue;
            Assert.IsNotNull(reference);
            RbxTween tween = reference.Value as RbxTween;
            Assert.IsNotNull(tween);

            harness.Stack.Runtime.LoadMod(owner, "tween-ro-attempt", @"
                local part = workspace:FindFirstChild('TweenTarget')
                local tween = workspace:FindFirstChild('TweenRef').Value
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('create', function()
                    return game:GetService('TweenService'):Create(
                        part, TweenInfo.new(1), {Transparency = 1})
                end)
                try('play', function() tween:Play() end)
                try('cancel', function() tween:Cancel() end)",
                LuaCapabilities.Read, persistToStore: false);

            foreach (string label in new[] { "create", "play", "cancel" })
            {
                string result = harness.Store.Get("tween-ro-attempt", label);
                StringAssert.StartsWith("false|", result, label + " must be refused");
                StringAssert.Contains("WorldEdit", result, label);
            }

            Assert.AreEqual(RbxTweenPlaybackState.Begin, tween.PlaybackState,
                "the refused Play never started the tween");
        }

        [Test]
        public void TweenCancel_CrossActor_IsAuthorizedAgainstTheCallingActor()
        {
            // WHY (M8-20 binding half): Play/Pause/Cancel ignored the calling actor, so any actor
            // holding a reference to another actor's tween could stop it with the creator's rights.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("tw-a");
            harness.Stack.Runtime.LoadMod(owner, "tw-owner", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedTweenTarget'
                part.Parent = workspace
                local tween = game:GetService('TweenService'):Create(
                    part, TweenInfo.new(10), {Transparency = 1})
                local ref = Instance.new('ObjectValue')
                ref.Name = 'OwnedTweenRef'
                ref.Value = tween
                ref.Parent = workspace
                tween:Play()", persistToStore: false);
            RbxTween tween =
                (harness.Registry.WorldRoot.FindFirstChild("OwnedTweenRef") as RbxObjectValue)?.Value
                    as RbxTween;
            Assert.IsNotNull(tween);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);

            ActorContext other = harness.Actor("tw-b");
            harness.Stack.Runtime.LoadMod(other, "tw-other", @"
                local tween = workspace:FindFirstChild('OwnedTweenRef').Value
                local ok, err = pcall(function() tween:Cancel() end)
                store_set('result', tostring(ok) .. '|' .. tostring(err))", persistToStore: false);

            string result = harness.Store.Get("tw-other", "result");
            StringAssert.StartsWith("false|", result);
            StringAssert.Contains("actor 'tw-b'", result);
            StringAssert.Contains("Owned by actor 'tw-a'", result);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState,
                "another actor's refused Cancel leaves the tween playing");
        }

        /// <summary>A character controller that counts what the Humanoid asked of it.</summary>
        private sealed class RecordingMotor : IRbxCharacterMotor
        {
            public int JumpCount { get; private set; }

            public int MoveTargetCount { get; private set; }

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
                JumpCount++;
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
                if (targetStuds.HasValue)
                {
                    MoveTargetCount++;
                }
            }
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness()
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "production-envelope-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = ProductionCapabilities,
                    OneOffCapabilities = ProductionCapabilities,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

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

                foreach ((string ModId, string Key) key in removed)
                {
                    _values.Remove(key);
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

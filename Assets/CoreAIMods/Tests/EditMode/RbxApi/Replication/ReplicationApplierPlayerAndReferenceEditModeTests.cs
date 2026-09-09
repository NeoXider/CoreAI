using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// A replicated Player is a real player, and a replicated reference is kept in step with the
    /// server: the replica's <c>Players</c> service answers for what the server admitted, and a
    /// reference whose target arrives, leaves or returns later follows it.
    /// </summary>
    /// <remarks>
    /// WHY the service and not the tree is asserted: a Player node that restores with the right
    /// class and name but no identity satisfies every tree comparison and still leaves
    /// <c>Players:GetPlayers()</c> empty — the way a client script actually looks a player up.
    /// WHY a real server registry rather than hand-built state: the payload under test is what
    /// <see cref="InstanceTreeSerializer.CaptureNode"/> reads off a Player the server admitted, so
    /// a hand-built node would only prove the applier agrees with the test's own guess.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationApplierPlayerAndReferenceEditModeTests
    {
        private const string Alice = "alice";

        private ReplicatedPair _pair;
        private ModScheduler _scheduler;

        [SetUp]
        public void CreatePair()
        {
            _pair = new ReplicatedPair(Alice);
            _scheduler = new ModScheduler(new NoScripts(), new FakeTime());
        }

        [TearDown]
        public void DisposePair()
        {
            _pair.Dispose();
        }

        [Test]
        public void ASeededPlayer_IsFoundThroughTheReplicasPlayersService_WithTheServersIdentity()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            Assert.IsNotNull(alice.Character, "the server auto-loaded a character to replicate");

            ReplicationApplyResult seed = _pair.Seed();

            Assert.AreEqual(ReplicationApplyStatus.Applied, seed.Status, seed.Detail);
            RbxPlayers players = _pair.ReplicaPlayers;
            IReadOnlyList<RbxPlayer> found = players.GetPlayers();
            Assert.AreEqual(1, found.Count, "Players:GetPlayers() on the replica returns the admitted player");
            RbxPlayer replicaAlice = found[0];
            Assert.AreEqual(alice.Id, replicaAlice.Id, "the replica's Player is the server's, by id");
            Assert.AreEqual(alice.UserId, replicaAlice.UserId);
            Assert.AreNotEqual(0L, replicaAlice.UserId, "a hollow player has UserId 0");
            Assert.AreEqual(alice.Name, replicaAlice.Name);
            Assert.AreEqual(alice.DisplayName, replicaAlice.DisplayName);
            Assert.AreSame(players, replicaAlice.Parent);
            Assert.AreSame(replicaAlice, players.GetPlayerByUserId(alice.UserId), "GetPlayerByUserId finds it");
            Assert.IsTrue(players.TryGetByActorId(Alice, out RbxPlayer byActor));
            Assert.AreSame(replicaAlice, byActor);
            Assert.AreSame(replicaAlice, players.GetLocalPlayer(Alice), "Players.LocalPlayer resolves on the client");
            RbxInstance replicaCharacter = _pair.Replica(alice.Character);
            Assert.IsNotNull(replicaCharacter, "the character under Workspace replicated");
            Assert.AreSame(replicaCharacter, replicaAlice.Character,
                "Player.Character is a reference and resolves to the replica's instance for the server's id");
            Assert.AreSame(replicaAlice, players.GetPlayerFromCharacter(replicaCharacter));
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void APlayerJoiningAfterTheSeed_FiresPlayerAddedOnTheReplica_AndIsFound()
        {
            _pair.Seed();
            RbxPlayers players = _pair.ReplicaPlayers;
            List<RbxPlayer> added = new();
            players.PlayerAdded.BindScheduler(_scheduler);
            players.PlayerAdded.Connect((Action<object[]>)(args => added.Add((RbxPlayer)args[0])));

            RbxPlayer bob = _pair.ServerPlayers.EnsureActor(_pair.Server, "bob");
            ReplicationApplyResult result = _pair.Step();
            _scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(1, added.Count, "PlayerAdded fires on the replica's service for a later join");
            Assert.AreEqual(bob.Id, added[0].Id);
            Assert.AreEqual(bob.UserId, added[0].UserId);
            Assert.AreEqual(1, players.GetPlayers().Count);
            Assert.AreSame(added[0], players.GetPlayerByUserId(bob.UserId));
        }

        [Test]
        public void ARemovedPlayer_LeavesTheReplicasPlayersService_AndPlayerRemovingFires()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            InstanceId characterId = alice.Character.Id;
            _pair.Seed();
            RbxPlayers players = _pair.ReplicaPlayers;
            RbxPlayer replicaAlice = players.GetPlayers()[0];
            List<string> removing = new();
            players.PlayerRemoving.BindScheduler(_scheduler);
            players.PlayerRemoving.Connect((Action<object[]>)(args =>
            {
                RbxPlayer leaving = (RbxPlayer)args[0];
                removing.Add(leaving.Name + "#" + leaving.UserId);
            }));

            Assert.IsTrue(_pair.ServerPlayers.RemoveActor(Alice));
            ReplicationApplyResult result = _pair.Step();
            _scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsEmpty(players.GetPlayers(), "a removed player leaves the service's collections");
            Assert.IsNull(players.GetPlayerByUserId(alice.UserId));
            Assert.IsFalse(players.TryGetByActorId(Alice, out _));
            Assert.IsNull(players.GetLocalPlayer(Alice));
            Assert.IsTrue(replicaAlice.IsDestroyed);
            Assert.IsFalse(_pair.ReplicaRegistry.TryGet(characterId, out _), "the character left with the player");
            CollectionAssert.AreEqual(new[] { alice.Name + "#" + alice.UserId }, removing,
                "PlayerRemoving fires on the replica with a player whose identity is still readable");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void APlayerThatLeavesVisibility_LeavesTheService_AndIsAdmittedAgainWhenItReturns()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            _pair.Seed();
            RbxPlayers players = _pair.ReplicaPlayers;
            Assert.AreEqual(1, players.GetPlayers().Count);

            _pair.Filter.Hidden.Add(alice.Id.Value);
            alice.SetAttribute("Touched", 1d);
            ReplicationApplyResult gone = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, gone.Status, gone.Detail);
            Assert.Greater(gone.Removed, 0, "leaving visibility is a removal on the replica");
            Assert.IsEmpty(players.GetPlayers(), "a player the recipient may no longer see is not a connected player");
            Assert.IsNull(players.GetPlayerByUserId(alice.UserId));
            Assert.IsFalse(players.TryGetByActorId(Alice, out _));

            _pair.Filter.Hidden.Remove(alice.Id.Value);
            alice.SetAttribute("Touched", 2d);
            ReplicationApplyResult back = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, back.Status, back.Detail);
            IReadOnlyList<RbxPlayer> found = players.GetPlayers();
            Assert.AreEqual(1, found.Count, "a player that returns to visibility is admitted again");
            Assert.AreEqual(alice.Id, found[0].Id);
            Assert.AreEqual(alice.UserId, found[0].UserId);
            Assert.AreSame(_pair.Replica(alice.Character), found[0].Character,
                "the character stayed visible, so the returning player's reference resolves at once");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void Negative_APlayerSpawnWhoseStateCarriesNoIdentity_IsAProtocolViolation_NotAHollowPlayer()
        {
            _pair.Seed();
            RbxPlayers players = _pair.ReplicaPlayers;
            const ulong ghostId = 900UL;
            TableState state = new();
            state.Add(new InstanceSnapshot
            {
                Id = ghostId,
                ParentId = players.Id.Value,
                ClassName = "Player",
                Name = "Ghost",
                Archivable = true
            });
            ReplicationBatchPlan plan = new(Alice, _pair.Applier.ExpectedSequence, new[]
            {
                new ReplicationOperation(ReplicationOperationKind.Spawn, new InstanceId(ghostId), 1L,
                    new[] { ReplicationMembers.Name, ReplicationMembers.Archivable })
            });

            ReplicationApplyResult result = _pair.Applier.Apply(plan, state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status,
                "a Player without identity is not something the replica can honour");
            StringAssert.Contains("identity", result.Detail);
            Assert.IsEmpty(players.GetPlayers(), "nothing hollow is admitted");
            Assert.AreEqual(1, _pair.Resyncs.Count);
        }

        [Test]
        public void AReferenceWhoseTargetIsNotYetVisible_IsRepairedWhenTheTargetArrives()
        {
            RbxInstance secret = _pair.CreateOnServer("Part", "Secret", _pair.ServerService("ServerStorage"));
            RbxObjectValue pointer = (RbxObjectValue)_pair.CreateOnServer("ObjectValue", "Pointer", _pair.ServerWorkspace);
            pointer.Value = secret;
            _pair.Seed();
            RbxObjectValue replicaPointer = (RbxObjectValue)_pair.Replica(pointer);
            Assert.IsNull(replicaPointer.Value, "a reference to something the recipient may not see is nil");
            Assert.IsNull(_pair.Replica(secret));
            List<object> changed = new();
            replicaPointer.Changed.BindScheduler(_scheduler);
            replicaPointer.Changed.Connect((Action<object[]>)(args => changed.Add(args[0])));

            secret.Parent = _pair.ServerWorkspace;
            ReplicationApplyResult result = _pair.Step();
            _scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            RbxInstance replicaSecret = _pair.Replica(secret);
            Assert.IsNotNull(replicaSecret, "the target moved into Workspace and spawned");
            Assert.AreSame(replicaSecret, replicaPointer.Value,
                "the reference is repaired when its target becomes visible, not left nil for good");
            CollectionAssert.AreEqual(new[] { replicaSecret }, changed,
                "Changed fires when the referenced object streams in, as ObjectValue.yaml describes");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void AReferenceReassignedBeforeItsTargetArrives_KeepsTheNewerValue()
        {
            RbxInstance secret = _pair.CreateOnServer("Part", "Secret", _pair.ServerService("ServerStorage"));
            RbxInstance other = _pair.CreateOnServer("Part", "Other", _pair.ServerWorkspace);
            RbxObjectValue toOther = (RbxObjectValue)_pair.CreateOnServer("ObjectValue", "ToOther", _pair.ServerWorkspace);
            RbxObjectValue toNil = (RbxObjectValue)_pair.CreateOnServer("ObjectValue", "ToNil", _pair.ServerWorkspace);
            toOther.Value = secret;
            toNil.Value = secret;
            _pair.Seed();

            toOther.Value = other;
            toNil.Value = null;
            Assert.AreEqual(ReplicationApplyStatus.Applied, _pair.Step().Status);
            secret.Parent = _pair.ServerWorkspace;
            ReplicationApplyResult result = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsNotNull(_pair.Replica(secret));
            Assert.AreSame(_pair.Replica(other), ((RbxObjectValue)_pair.Replica(toOther)).Value,
                "a reference the server changed is not repaired to the value it no longer has");
            Assert.IsNull(((RbxObjectValue)_pair.Replica(toNil)).Value,
                "a reference the server cleared stays nil when its old target arrives");
        }

        [Test]
        public void AReferenceWhoseHolderLeft_IsNotRepaired_WhenItsTargetArrives()
        {
            RbxInstance secret = _pair.CreateOnServer("Part", "Secret", _pair.ServerService("ServerStorage"));
            RbxObjectValue pointer = (RbxObjectValue)_pair.CreateOnServer("ObjectValue", "Pointer", _pair.ServerWorkspace);
            pointer.Value = secret;
            _pair.Seed();
            RbxObjectValue replicaPointer = (RbxObjectValue)_pair.Replica(pointer);

            pointer.Destroy();
            Assert.AreEqual(1, _pair.Step().Removed);
            Assert.IsTrue(replicaPointer.IsDestroyed);
            secret.Parent = _pair.ServerWorkspace;
            ReplicationApplyResult result = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsNotNull(_pair.Replica(secret), "the target still spawns for everyone else");
            Assert.IsEmpty(_pair.Resyncs, "a holder that is gone is dropped, not written to");
            Assert.IsEmpty(_pair.ReplicaDiagnostics);
        }

        [Test]
        public void AReferenceWhoseTargetLeavesVisibility_GoesNil_AndIsRepairedWhenItReturns()
        {
            RbxInstance ball = _pair.CreateOnServer("Part", "Ball", _pair.ServerWorkspace);
            RbxObjectValue pointer = (RbxObjectValue)_pair.CreateOnServer("ObjectValue", "Pointer", _pair.ServerWorkspace);
            pointer.Value = ball;
            _pair.Seed();
            RbxObjectValue replicaPointer = (RbxObjectValue)_pair.Replica(pointer);
            Assert.AreSame(_pair.Replica(ball), replicaPointer.Value);

            ball.Parent = _pair.ServerService("ServerStorage");
            ReplicationApplyResult gone = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, gone.Status, gone.Detail);
            Assert.IsNull(_pair.Replica(ball), "the target left visibility");
            Assert.IsNull(replicaPointer.Value,
                "a reference whose target streamed out reads nil, not a destroyed instance");

            ball.Parent = _pair.ServerWorkspace;
            ReplicationApplyResult back = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, back.Status, back.Detail);
            RbxInstance returned = _pair.Replica(ball);
            Assert.IsNotNull(returned);
            Assert.AreSame(returned, replicaPointer.Value, "the reference follows the target back in");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void AModelsPrimaryPart_IsRepairedWhenThePartArrives()
        {
            RbxModel rig = (RbxModel)_pair.CreateOnServer("Model", "Rig", _pair.ServerWorkspace);
            RbxInstance root = _pair.CreateOnServer("Part", "Root", rig);
            rig.SetPrimaryPart(root);
            _pair.Filter.Hidden.Add(root.Id.Value);
            _pair.Seed();
            RbxModel replicaRig = (RbxModel)_pair.Replica(rig);
            Assert.IsNull(replicaRig.PrimaryPart, "the part the recipient may not see leaves PrimaryPart nil");
            Assert.IsNull(_pair.Replica(root));

            _pair.Filter.Hidden.Remove(root.Id.Value);
            root.SetAttribute("Seen", true);
            ReplicationApplyResult result = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            RbxInstance replicaRoot = _pair.Replica(root);
            Assert.IsNotNull(replicaRoot);
            Assert.AreSame(replicaRoot, replicaRig.PrimaryPart, "PrimaryPart goes through the same repair as any reference");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void APlayersCharacter_IsRepairedWhenTheCharacterArrives_AndCharacterAddedFires()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            RbxInstance character = alice.Character;
            _pair.Filter.Hidden.Add(character.Id.Value);
            _pair.Seed();
            RbxPlayer replicaAlice = _pair.ReplicaPlayers.GetPlayers()[0];
            Assert.IsNull(replicaAlice.Character, "a character the recipient may not see leaves Character nil");
            List<object> added = new();
            replicaAlice.CharacterAdded.BindScheduler(_scheduler);
            replicaAlice.CharacterAdded.Connect((Action<object[]>)(args => added.Add(args[0])));

            _pair.Filter.Hidden.Remove(character.Id.Value);
            character.SetAttribute("Seen", true);
            ReplicationApplyResult result = _pair.Step();
            _scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            RbxInstance replicaCharacter = _pair.Replica(character);
            Assert.IsNotNull(replicaCharacter);
            Assert.AreSame(replicaCharacter, replicaAlice.Character, "Character follows the same repair as any reference");
            CollectionAssert.AreEqual(new[] { replicaCharacter }, added,
                "CharacterAdded fires on the client when the character replicates, as a Roblox client sees it");
            Assert.AreSame(replicaAlice, _pair.ReplicaPlayers.GetPlayerFromCharacter(replicaCharacter));
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void APlayersCharacterClearedOnTheServer_ReadsNilOnTheReplica_AndCharacterRemovingFires()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            RbxInstance character = alice.Character;
            _pair.Seed();
            RbxPlayer replicaAlice = _pair.ReplicaPlayers.GetPlayers()[0];
            RbxInstance replicaCharacter = _pair.Replica(character);
            Assert.AreSame(replicaCharacter, replicaAlice.Character);
            List<object> removing = new();
            replicaAlice.CharacterRemoving.BindScheduler(_scheduler);
            replicaAlice.CharacterRemoving.Connect((Action<object[]>)(args => removing.Add(args[0])));

            alice.Character = null;
            ReplicationApplyResult result = _pair.Step();
            _scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.Greater(result.Patched, 0, "clearing Character is a patch, not a removal: the model still stands");
            Assert.IsNull(replicaAlice.Character, "Character follows the server to nil");
            Assert.IsFalse(replicaCharacter.IsDestroyed, "the character model itself stays visible");
            CollectionAssert.AreEqual(new[] { replicaCharacter }, removing,
                "CharacterRemoving fires on the client for the character being left");
            Assert.IsEmpty(_pair.Resyncs);
        }

        [Test]
        public void APlayersDisplayNameChangedOnTheServer_ReplicatesAsAPatch()
        {
            RbxPlayer alice = _pair.ServerPlayers.EnsureActor(_pair.Server, Alice);
            _pair.Seed();
            RbxPlayer replicaAlice = _pair.ReplicaPlayers.GetPlayers()[0];
            Assert.AreEqual(alice.DisplayName, replicaAlice.DisplayName);

            alice.DisplayName = "Ally";
            ReplicationApplyResult result = _pair.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual("Ally", replicaAlice.DisplayName, "DisplayName replicates (Player.yaml tags it neither ReadOnly nor NotReplicated)");
            Assert.AreEqual(alice.Name, replicaAlice.Name, "the username is untouched");
            Assert.IsEmpty(_pair.Resyncs);
        }

        /// <summary>
        /// An authoritative registry and one replica, joined directly: the server's stream plans,
        /// the replica applies, and the node state is read off the live server at apply time.
        /// </summary>
        /// <remarks>
        /// WHY not the convergence harness: this fixture needs a filter it can change between
        /// steps to move one instance in and out of the recipient's sight, and it must stand on
        /// its own while the harness is being reworked next to it.
        /// </remarks>
        private sealed class ReplicatedPair : IReplicationStateSource, IDisposable
        {
            private readonly string _actorId;

            public ReplicatedPair(string actorId)
            {
                _actorId = actorId;
                Server = new InstanceRegistry(
                    binder: new InMemoryInstanceBackingBinder(),
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "player-world");
                Filter = new HidingFilter();
                Dirty = new ReplicationDirtySet(Server, Filter);
                ServerGame = DataModelBootstrap.CreateGame(Server);
                Stream = new ReplicationStream(Dirty, actorId);
                ReplicaRegistry = new InstanceRegistry(
                    binder: new InMemoryInstanceBackingBinder(),
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "player-world",
                    authority: RegistryAuthority.Replica);
                ReplicaRegistry.Diagnostics = ReplicaDiagnostics.Add;
                Applier = new ReplicationApplier(ReplicaRegistry);
                Applier.ResyncRequested += Resyncs.Add;
            }

            public InstanceRegistry Server { get; }

            public RbxDataModel ServerGame { get; }

            public ReplicationDirtySet Dirty { get; }

            public HidingFilter Filter { get; }

            public ReplicationStream Stream { get; }

            public InstanceRegistry ReplicaRegistry { get; }

            public ReplicationApplier Applier { get; }

            public List<string> Resyncs { get; } = new();

            public List<string> ReplicaDiagnostics { get; } = new();

            public RbxInstance ServerWorkspace => Server.WorldRoot;

            public RbxPlayers ServerPlayers => (RbxPlayers)ServerGame.GetService("Players");

            public RbxDataModel ReplicaGame => (RbxDataModel)Replica(ServerGame);

            public RbxPlayers ReplicaPlayers => (RbxPlayers)ReplicaGame.GetService("Players");

            public RbxInstance ServerService(string className)
            {
                return ServerGame.GetService(className);
            }

            public RbxInstance CreateOnServer(string className, string name, RbxInstance parent)
            {
                RbxInstance instance = Server.Create(className);
                instance.Name = name;
                instance.Parent = parent;
                return instance;
            }

            /// <summary>The replica's live instance for a server instance's id, or null.</summary>
            public RbxInstance Replica(RbxInstance serverInstance)
            {
                return ReplicaRegistry.TryGet(serverInstance.Id, out RbxInstance instance) && !instance.IsDestroyed
                    ? instance
                    : null;
            }

            public ReplicationApplyResult Seed()
            {
                ReplicationBatchPlan plan = Stream.PlanWorld();
                Dirty.Clear();
                Assert.IsNotNull(plan, "the recipient sees a world");
                return Applier.Apply(plan, this);
            }

            public ReplicationApplyResult Step()
            {
                ReplicationBatchPlan plan = Stream.Plan();
                Dirty.Clear();
                Assert.IsNotNull(plan, "the step changed something the recipient sees");
                return Applier.Apply(plan, this);
            }

            InstanceSnapshot IReplicationStateSource.Describe(InstanceId id)
            {
                return Server.TryGet(id, out RbxInstance instance) && !instance.IsDestroyed
                    ? InstanceTreeSerializer.CaptureNode(instance)
                    : null;
            }

            public void Dispose()
            {
                Dirty.Dispose();
            }
        }

        /// <summary>The default filter, minus whichever instances the test hides by id.</summary>
        private sealed class HidingFilter : IReplicationFilter
        {
            public HashSet<ulong> Hidden { get; } = new();

            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                return instance != null && !Hidden.Contains(instance.Id.Value)
                       && DefaultReplicationFilter.Instance.IsVisibleTo(recipientActorId, instance);
            }
        }

        private sealed class TableState : IReplicationStateSource
        {
            private readonly Dictionary<ulong, InstanceSnapshot> _nodes = new();

            public void Add(InstanceSnapshot node)
            {
                _nodes.Add(node.Id, node);
            }

            public InstanceSnapshot Describe(InstanceId id)
            {
                return _nodes.TryGetValue(id.Value, out InstanceSnapshot node) ? node : null;
            }
        }

        private sealed class NoScripts : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("no scripts run in this test");
            }
        }

        private sealed class FakeTime : IRbxTimeSource
        {
            public double CurrentTime { get; private set; }

            public void Advance(double deltaSeconds)
            {
                CurrentTime += deltaSeconds;
            }
        }
    }
}

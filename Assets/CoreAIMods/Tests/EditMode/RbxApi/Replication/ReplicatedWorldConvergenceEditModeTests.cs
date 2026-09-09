using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0, the test that matters most: an authoritative registry and a replica in
    /// one process, joined by the loopback bridge. The server mutates; the replica converges.
    /// </summary>
    /// <remarks>
    /// WHY convergence is asserted as a whole-tree comparison and not property by property: a
    /// pipeline that gets one property right can still leave a ghost, a duplicate or a stale
    /// revision somewhere else; only "the trees are the same" catches what nobody thought to check.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicatedWorldConvergenceEditModeTests
    {
        private const string Alice = "alice";

        private ReplicatedWorldHarness _world;
        private ReplicaEndpoint _alice;
        private RbxInstance _remote;

        [SetUp]
        public void JoinTheWorld()
        {
            _world = new ReplicatedWorldHarness();
            _remote = _world.CreateOnServer("RemoteEvent", "RemoteX", _world.ServerService("ReplicatedStorage"));
            _alice = _world.AddClient(Alice);
            _world.Step();
        }

        [TearDown]
        public void LeaveTheWorld()
        {
            _world.Dispose();
        }

        [Test]
        public void TheFirstStep_DeliversTheVisibleWorld_AndClientCodeResolvesReplicatedStorageByReference()
        {
            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.Greater(_alice.LastResult.Spawned, 5);
            Assert.IsNotNull(_alice.Game);
            RbxInstance remote = _alice.Game.GetService("ReplicatedStorage").FindFirstChild("RemoteX");
            Assert.IsNotNull(remote, "the TODO's complaint: client code could not resolve ReplicatedStorage.RemoteX");
            Assert.AreEqual(_remote.Id, remote.Id, "the same instance, by identity");
            Assert.AreEqual("RemoteEvent", remote.ClassName);
            Assert.AreSame(_alice.Find(_world.ServerWorkspace), _alice.Registry.WorldRoot);
            Assert.IsNull(_alice.Find(_world.ServerService("ServerStorage")));
            Assert.IsNull(_alice.Find(_world.ServerWorkspace.FindFirstChildOfClass("Camera")));
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AMixedSequence_OfCreatesWritesReparentsAndDestroys_Converges()
        {
            RbxInstance enemies = _world.CreateOnServer("Folder", "Enemies", _world.ServerWorkspace);
            RbxInstance grunt = _world.CreateOnServer("Part", "Grunt", enemies);
            RbxInstance archer = _world.CreateOnServer("Part", "Archer", enemies);
            RbxInstance boss = _world.CreateOnServer("Part", "Boss", enemies);
            grunt.SetAttribute("Hp", 10d);
            grunt.AddTag("Enemy");
            archer.AddTag("Enemy");
            RbxModel squad = (RbxModel)_world.CreateOnServer("Model", "Squad", _world.ServerWorkspace);
            RbxInstance leader = _world.CreateOnServer("Part", "Leader", squad);
            squad.SetPrimaryPart(leader);
            RbxStringValue banner = (RbxStringValue)_world.CreateOnServer("StringValue", "Banner",
                _world.ServerService("ReplicatedStorage"));
            banner.Value = "Round 1";
            _world.Step();
            _world.AssertConverged(Alice);

            grunt.Name = "Veteran";
            grunt.SetAttribute("Hp", 12d);
            grunt.SetAttribute("Poisoned", true);
            archer.RemoveTag("Enemy");
            archer.Parent = _world.ServerService("ReplicatedStorage");
            boss.Destroy();
            banner.Value = "Round 2";
            squad.Parent = _world.ServerService("ServerStorage");
            RbxIntValue score = (RbxIntValue)_world.CreateOnServer("IntValue", "Score", _world.ServerWorkspace);
            score.Value = 42L;
            _world.Step();
            _world.AssertConverged(Alice);

            squad.Parent = _world.ServerWorkspace;
            squad.SetPrimaryPart(null);
            enemies.Destroy();
            grunt = null;
            score.Value = 43L;
            _world.Step();
            _world.AssertConverged(Alice);

            Assert.IsTrue(_alice.Results.All(result => result.IsApplied),
                string.Join("; ", _alice.Results.Select(result => result.ToString())));
            Assert.IsEmpty(_alice.ResyncRequests);
            Assert.IsEmpty(_alice.Diagnostics, string.Join("\n", _alice.Diagnostics));
            Assert.IsFalse(_alice.Registry.GetLiveInstances().Any(instance =>
                    _alice.Registry.TryGetRecord(instance.Id, out InstanceRecord record) && record.IsLocallyDiverged),
                "nothing the server wrote counts as the client's divergence");
            Assert.AreEqual("Round 2", ((RbxStringValue)_alice.Find(banner)).Value);
            Assert.AreEqual(43L, ((RbxIntValue)_alice.Find(score)).Value);
            Assert.IsNull(_alice.Find(boss));
            Assert.IsNull(_alice.Find(enemies));
            Assert.AreSame(_alice.Find(_world.ServerService("ReplicatedStorage")), _alice.Find(archer).Parent);
        }

        [Test]
        public void ADuplicateBatch_ChangesNothing()
        {
            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            _world.Step();
            List<string> before = Snapshot(_alice);
            long sequence = _alice.LastPlan.Sequence;

            _world.Resend(Alice, sequence);

            Assert.AreEqual(ReplicationApplyStatus.Duplicate, _alice.LastResult.Status);
            Assert.AreEqual(1, _alice.Applier.DuplicateCount);
            CollectionAssert.AreEqual(before, Snapshot(_alice));
            _world.AssertConverged(Alice);
            Assert.IsNotNull(_alice.Find(door));
        }

        [Test]
        public void AGap_RequestsResync_InsteadOfGuessing()
        {
            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            _world.Step();
            _world.HoldOutgoing = true;
            door.Name = "Ajar";
            _world.Step();
            long first = _alice.LastPlan.Sequence;
            door.Name = "Open";
            _world.Step();
            long second = _alice.LastPlan.Sequence;

            _world.ReleaseHeld(Alice, second);
            Assert.AreEqual(ReplicationApplyStatus.GapDetected, _alice.LastResult.Status);
            Assert.AreEqual(1, _alice.ResyncRequests.Count);
            StringAssert.Contains("missing", _alice.ResyncRequests[0]);
            Assert.AreEqual("Door", _alice.Find(door).Name, "a future batch is never applied");

            _world.ReleaseHeld(Alice, first);
            Assert.AreEqual(ReplicationApplyStatus.AwaitingResync, _alice.LastResult.Status);
            Assert.AreEqual("Door", _alice.Find(door).Name, "nothing is applied until the world is sent again");
            Assert.IsTrue(_alice.Applier.NeedsResync);
            Assert.AreEqual(first, _alice.Applier.ExpectedSequence);
        }

        [Test]
        public void LeavingAndReenteringVisibility_ArrivesAsRemoveThenSpawn()
        {
            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            door.SetAttribute("Locked", true);
            _world.Step();
            RbxInstance firstCopy = _alice.Find(door);

            door.Parent = _world.ServerService("ServerStorage");
            _world.Step();
            ReplicationOperation leaving = _alice.LastPlan.Operations.Single(op => op.InstanceId == door.Id);
            Assert.AreEqual(ReplicationOperationKind.Remove, leaving.Kind);
            Assert.IsNull(_alice.Find(door));
            Assert.IsTrue(firstCopy.IsDestroyed);
            _world.AssertConverged(Alice);

            door.Parent = _world.ServerWorkspace;
            _world.Step();
            ReplicationOperation returning = _alice.LastPlan.Operations.Single(op => op.InstanceId == door.Id);
            Assert.AreEqual(ReplicationOperationKind.Spawn, returning.Kind);
            RbxInstance secondCopy = _alice.Find(door);
            Assert.IsNotNull(secondCopy);
            Assert.AreNotSame(firstCopy, secondCopy, "a fresh instance at the same server id");
            Assert.AreEqual(true, secondCopy.GetAttribute("Locked"));
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AKnownInstance_MovedUnderARootLeavingVisibility_IsRemovedToo_AndReturnsCleanly()
        {
            RbxInstance folder = _world.CreateOnServer("Folder", "Crates", _world.ServerWorkspace);
            RbxInstance part = _world.CreateOnServer("Part", "Crate", _world.ServerWorkspace);
            _world.Step();
            _world.AssertConverged(Alice);

            // WHY this mutation order: the folder is dirtied first, so the planner meets it before
            // the part and walks the server's tree, where the part already hangs under the folder —
            // while the replica still holds the part under Workspace and destroys a childless folder.
            folder.Parent = _world.ServerService("ServerStorage");
            part.Parent = folder;
            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsNull(_alice.Find(folder));
            Assert.IsNull(_alice.Find(part), "the part left visibility with its new parent");
            Assert.IsFalse(_alice.Stream.Knows(part.Id));
            _world.AssertConverged(Alice);

            part.Parent = _world.ServerWorkspace;
            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsNotNull(_alice.Find(part));
            Assert.IsEmpty(_alice.ResyncRequests);
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AParentMovedUnderItsOwnChild_Converges_EvenWhenAnEarlierWriteDirtiedTheParentFirst()
        {
            RbxInstance a = _world.CreateOnServer("Folder", "A", _world.ServerWorkspace);
            RbxInstance b = _world.CreateOnServer("Folder", "B", a);
            _world.Step();
            _world.AssertConverged(Alice);

            // WHY the Name write comes first: it puts A ahead of B in the dirty set; a batch in that
            // order asks the replica to parent A under B while B is still A's child there.
            a.Name = "renamed";
            b.Parent = _world.ServerWorkspace;
            a.Parent = b;
            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsEmpty(_alice.ResyncRequests);
            Assert.AreSame(_alice.Find(b), _alice.Find(a).Parent);
            Assert.AreSame(_alice.Registry.WorldRoot, _alice.Find(b).Parent);
            Assert.AreEqual("renamed", _alice.Find(a).Name);
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AReparentUnderANodeSpawnedThisStep_Converges_WhenTheSpawnsParentMovedToo()
        {
            RbxInstance x = _world.CreateOnServer("Folder", "X", _world.ServerWorkspace);
            RbxInstance k = _world.CreateOnServer("Folder", "K", x);
            _world.Step();
            _world.AssertConverged(Alice);

            // WHY the Name write comes first: it dirties X before K; F is spawned under K, so X's
            // move under F closes a cycle on the replica unless K has left X first.
            x.Name = "renamed";
            RbxInstance f = _world.CreateOnServer("Folder", "F", k);
            k.Parent = _world.ServerWorkspace;
            x.Parent = f;
            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsEmpty(_alice.ResyncRequests);
            Assert.AreSame(_alice.Find(f), _alice.Find(x).Parent);
            Assert.AreSame(_alice.Find(k), _alice.Find(f).Parent);
            Assert.AreSame(_alice.Registry.WorldRoot, _alice.Find(k).Parent);
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AWriteLandingBetweenTwoRecipientsPlans_ReachesBothReplicas()
        {
            using ReplicatedWorldHarness world = new();
            ReplicaEndpoint alice = world.AddClient(Alice);
            ReplicaEndpoint bob = world.AddClient("bob");
            RbxInstance door = world.CreateOnServer("Part", "Door", world.ServerWorkspace);
            world.Step();
            world.AssertConverged(Alice);
            world.AssertConverged("bob");

            // WHY the write is made from the step itself: it stands in for a mutation another thread
            // commits while recipients are being planned — after the first read the set, before the
            // second did — and the step's Clear must not take it with the rest.
            bool written = false;
            world.AfterPlan = _ =>
            {
                if (!written)
                {
                    written = true;
                    door.Name = "Ajar";
                }
            };
            world.Step();
            world.AfterPlan = null;
            Assert.IsTrue(written);
            world.Step();

            Assert.AreEqual("Ajar", alice.Find(door).Name);
            Assert.AreEqual("Ajar", bob.Find(door).Name);
            AssertAllApplied(alice);
            AssertAllApplied(bob);
            Assert.IsEmpty(alice.ResyncRequests);
            Assert.IsEmpty(bob.ResyncRequests);
            world.AssertConverged(Alice);
            world.AssertConverged("bob");
        }

        [Test]
        public void AMemberRemoved_AndAWholeNodeMark_InTheSameStep_StillClearsItOnTheReplica()
        {
            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            door.SetAttribute("Target", "Gate");
            door.AddTag("Locked");
            _world.Step();
            _world.AssertConverged(Alice);

            // WHY a bare revision advance: it is what every part-property write and every tween tick
            // records — a whole-node mark that names no member — and it lands between two removals.
            door.SetAttribute("Target", null);
            _world.Server.AdvanceRevision(door.Id);
            door.RemoveTag("Locked");
            _world.Step();

            RbxInstance replica = _alice.Find(door);
            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsNull(replica.GetAttribute("Target"), "the attribute set to nil was cleared");
            Assert.IsFalse(replica.HasTag("Locked"), "the removed tag was removed");
            Assert.IsEmpty(_alice.Diagnostics, string.Join("\n", _alice.Diagnostics));
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AWorldBuiltBeforeItsReplication_IsSeededToTheClient_AndConvergesFromThereOn()
        {
            using ReplicatedWorldHarness world = new(worldBeforeReplication: true);
            ReplicaEndpoint alice = world.AddClient(Alice);
            Assert.Greater(world.Dirty.UnobservedInstanceCount, 0, "the bootstrap predates the dirty set");

            ReplicationBatchPlan seed = world.Seed(Alice);

            Assert.AreEqual(1L, seed.Sequence);
            Assert.AreEqual(ReplicationApplyStatus.Applied, alice.LastResult.Status, alice.LastResult.Detail);
            Assert.Greater(alice.LastResult.Spawned, 5);
            Assert.IsNotNull(alice.Game, "the client is not left empty");
            Assert.AreSame(alice.Find(world.ServerWorkspace), alice.Registry.WorldRoot);
            Assert.IsNotNull(alice.Find(world.ServerService("ReplicatedStorage")));
            Assert.IsNull(alice.Find(world.ServerService("ServerStorage")));
            world.AssertConverged(Alice);

            RbxInstance door = world.CreateOnServer("Part", "Door", world.ServerWorkspace);
            world.Step();
            Assert.AreEqual(ReplicationOperationKind.Spawn,
                alice.LastPlan.Operations.Single(op => op.InstanceId == door.Id).Kind);
            door.Name = "Gate";
            world.Step();
            Assert.AreEqual(ReplicationOperationKind.Patch,
                alice.LastPlan.Operations.Single(op => op.InstanceId == door.Id).Kind);

            AssertAllApplied(alice);
            Assert.IsEmpty(alice.ResyncRequests);
            Assert.AreEqual("Gate", alice.Find(door).Name);
            world.AssertConverged(Alice);
        }

        [Test]
        public void AWorldBuiltBeforeItsReplication_RefusesToPlanUnseeded_RatherThanSendingNothing()
        {
            using ReplicatedWorldHarness world = new(worldBeforeReplication: true);
            ReplicaEndpoint alice = world.AddClient(Alice);
            world.CreateOnServer("Part", "Door", world.ServerWorkspace);

            System.InvalidOperationException refusal =
                Assert.Throws<System.InvalidOperationException>(() => world.Step());

            StringAssert.Contains("PlanWorld", refusal.Message);
            Assert.IsNull(alice.LastResult, "nothing was shipped");
            Assert.IsNull(alice.Game, "and nothing was guessed into place");
        }

        [Test]
        public void ALateJoiner_SeededWithTheWorld_ConvergesLikeAnyOtherClient()
        {
            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            _world.Step();
            ReplicaEndpoint late = _world.AddClient("late");

            _world.Seed("late");

            Assert.AreEqual(ReplicationApplyStatus.Applied, late.LastResult.Status, late.LastResult.Detail);
            Assert.IsNotNull(late.Find(door));
            _world.AssertConverged("late");

            door.Name = "Gate";
            _world.Step();

            AssertAllApplied(late);
            Assert.IsEmpty(late.ResyncRequests);
            Assert.AreEqual("Gate", late.Find(door).Name);
            _world.AssertConverged("late");
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AReplicasOwnInstanceNew_GetsALocalId_TheWireRefuses_AndTheServerIsUnaffected()
        {
            RbxInstance local = _alice.Registry.CreateScripted("Part");
            local.Name = "ClientOnly";
            local.Parent = _alice.Registry.WorldRoot;

            Assert.IsTrue(local.Id.IsLocallyAssigned);
            RbxError refusal = Assert.Throws<RbxError>(() => new RbxNetworkEventMessage(local.Id,
                RbxNetworkDirection.ClientToServer, RbxNetworkReliability.ReliableOrdered, Alice, null, null));
            Assert.AreEqual(RbxErrorCode.NotAuthority, refusal.Code);
            _alice.Registry.TryGetRecord(local.Id, out InstanceRecord record);
            Assert.IsTrue(record.IsLocallyDiverged);
            Assert.IsFalse(_world.Server.TryGet(local.Id, out _), "the server never hears of it");

            RbxInstance door = _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);
            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status, _alice.LastResult.Detail);
            Assert.IsNotNull(_alice.Find(door));
            Assert.AreSame(_alice.Registry.WorldRoot, local.Parent, "the client keeps its own object");
            _world.AssertConverged(Alice);
        }

        [Test]
        public void AnotherPlayersBackpack_IsVisibleOnlyToItsOwner()
        {
            // WHY a second world: both clients must be present at the join, because phase 0 has no
            // join snapshot for a late arrival (see the late-joiner test below).
            using ReplicatedWorldHarness world = new();
            ReplicaEndpoint alice = world.AddClient(Alice);
            ReplicaEndpoint bob = world.AddClient("bob");
            RbxPlayers players = (RbxPlayers)world.ServerService("Players");
            players.CharacterAutoLoads = false;
            RbxPlayer alicePlayer = players.EnsureActor(world.Server, Alice);
            RbxPlayer bobPlayer = players.EnsureActor(world.Server, "bob");
            RbxInstance sword = world.CreateOnServer("Folder", "Sword",
                alicePlayer.FindFirstChildOfClass("Backpack"));

            world.Step();

            AssertAllApplied(alice);
            AssertAllApplied(bob);
            Assert.IsNotNull(alice.Find(sword), "a player's own inventory arrives");
            Assert.IsNotNull(alice.Find(bobPlayer), "every client sees every Player");
            Assert.IsNotNull(bob.Find(alicePlayer));
            Assert.IsNull(bob.Find(sword), "another player's inventory never arrives");
            Assert.IsNull(bob.Find(alicePlayer.FindFirstChildOfClass("Backpack")));
            Assert.IsNull(alice.Find(alicePlayer.FindFirstChildOfClass("PlayerScripts")),
                "PlayerScripts is NotReplicated even to its owner");
            world.AssertConverged(Alice);
            world.AssertConverged("bob");
        }

        [Test]
        public void ALateJoiner_WithoutTheJoinSnapshot_RequestsResyncRatherThanGuessing()
        {
            // WHY this is pinned rather than fixed: the join projection is a later phase. Until it
            // exists, a client that arrives after the first step receives deltas against a world it
            // never got, and the only correct answer is the one the applier gives: refuse, and ask
            // for the world. The phase that adds the projection replaces this test's expectation.
            ReplicaEndpoint late = _world.AddClient("late");
            _world.CreateOnServer("Part", "Door", _world.ServerWorkspace);

            _world.Step();

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, late.LastResult.Status);
            StringAssert.Contains("does not hold", late.LastResult.Detail);
            Assert.AreEqual(1, late.ResyncRequests.Count);
            Assert.IsNull(late.Game, "nothing was guessed into place");
            Assert.AreEqual(ReplicationApplyStatus.Applied, _alice.LastResult.Status,
                "the established client is unaffected");
        }

        private static void AssertAllApplied(ReplicaEndpoint endpoint)
        {
            Assert.IsTrue(endpoint.Results.All(result => result.IsApplied),
                endpoint.ActorId + ": " + string.Join("; ", endpoint.Results.Select(result => result.ToString()))
                + "\n" + string.Join("\n", endpoint.Diagnostics));
        }

        private static List<string> Snapshot(ReplicaEndpoint endpoint)
        {
            List<string> lines = endpoint.Registry.GetLiveInstances()
                .Where(instance => instance.Id.IsServerAssigned)
                .Select(ReplicatedWorldHarness.Describe)
                .ToList();
            lines.Sort(System.StringComparer.Ordinal);
            return lines;
        }
    }
}

using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0: the replica-side state machine, driven with hand-built plans so each
    /// rule is pinned on its own, without a server.
    /// </summary>
    /// <remarks>
    /// WHY hand-built plans: the harness proves the pipeline converges; this proves the applier
    /// refuses the batches a converging pipeline never sends — the older, the future, the
    /// impossible — and that its refusals are visible.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationApplierEditModeTests
    {
        private const ulong GameId = 1UL;
        private const ulong WorkspaceId = 2UL;
        private const ulong ValueId = 3UL;

        private InstanceRegistry _replica;
        private ReplicationApplier _applier;
        private TableState _state;
        private List<string> _resyncs;
        private List<string> _diagnostics;

        [SetUp]
        public void CreateReplica()
        {
            _replica = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "applier-world",
                authority: RegistryAuthority.Replica);
            _diagnostics = new List<string>();
            _replica.Diagnostics = _diagnostics.Add;
            _applier = new ReplicationApplier(_replica);
            _resyncs = new List<string>();
            _applier.ResyncRequested += _resyncs.Add;
            _state = new TableState();
        }

        [Test]
        public void Negative_ARegistryThatIsNotAReplica_IsRefused()
        {
            InstanceRegistry authoritative = new(binder: new InMemoryInstanceBackingBinder());

            Assert.Throws<ArgumentException>(() => new ReplicationApplier(authoritative));
        }

        [Test]
        public void TheExpectedBatch_Applies_AndAdvancesTheSequence()
        {
            ReplicationApplyResult result = _applier.Apply(Join(), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(3, result.Spawned);
            Assert.AreEqual(2L, _applier.ExpectedSequence);
            Assert.IsTrue(_replica.TryGet(new InstanceId(WorkspaceId), out RbxInstance workspace));
            Assert.AreSame(workspace, _replica.WorldRoot, "the replica's roots are the server's, by id");
            Assert.AreEqual("Workspace", workspace.Name);
            Assert.IsTrue(_replica.TryGet(new InstanceId(GameId), out RbxInstance game));
            Assert.AreSame(game, workspace.Parent);
            _replica.TryGetRecord(workspace.Id, out InstanceRecord record);
            Assert.AreEqual(5L, record.Revision, "the server's revision is stamped");
            Assert.IsFalse(record.IsLocallyDiverged);
            Assert.IsFalse(_replica.IsApplyingReplication);
        }

        [Test]
        public void AnOlderBatch_IsADuplicate_ThatChangesNothing()
        {
            _applier.Apply(Join(), _state);
            int before = _replica.Count;

            ReplicationApplyResult result = _applier.Apply(Join(), _state);

            Assert.AreEqual(ReplicationApplyStatus.Duplicate, result.Status);
            Assert.AreEqual(1, _applier.DuplicateCount);
            Assert.AreEqual(before, _replica.Count);
            Assert.AreEqual(2L, _applier.ExpectedSequence);
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void ANewerBatch_IsAGap_ThatRequestsResync_AndNothingIsGuessed()
        {
            _applier.Apply(Join(), _state);
            ReplicationBatchPlan future = Batch(3L, Spawn(9UL, 1L));
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Late"));

            ReplicationApplyResult gap = _applier.Apply(future, _state);
            ReplicationApplyResult dropped = _applier.Apply(Batch(2L), _state);

            Assert.AreEqual(ReplicationApplyStatus.GapDetected, gap.Status);
            StringAssert.Contains("missing", gap.Detail);
            Assert.IsTrue(_applier.NeedsResync);
            Assert.AreEqual(1, _resyncs.Count);
            Assert.IsFalse(_replica.TryGet(new InstanceId(9UL), out _), "a future batch must not be applied");
            Assert.AreEqual(ReplicationApplyStatus.AwaitingResync, dropped.Status);
            Assert.AreEqual(1, _applier.DroppedAwaitingResyncCount);
            Assert.AreEqual(2L, _applier.ExpectedSequence);
        }

        [Test]
        public void CompleteResync_ResumesFromTheSequenceTheSnapshotNames()
        {
            _applier.Apply(Join(), _state);
            _applier.Apply(Batch(5L), _state);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "AfterResync"));

            _applier.CompleteResync(7L);
            ReplicationApplyResult result = _applier.Apply(Batch(7L, Spawn(9UL, 1L)), _state);

            Assert.IsFalse(_applier.NeedsResync);
            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(8L, _applier.ExpectedSequence);
        }

        [Test]
        public void AWorldAfterBeginResync_IsAppliedOntoTheReplicaThatHoldsIt_InPlace()
        {
            // WHY this is the regression: before BeginResync, the only cure for a gap was
            // CompleteResync plus the world batch, and the world's first spawn for an id the replica
            // held was a protocol violation, so a replica that lost one batch stayed in resync for good.
            _applier.Apply(Join(), _state);
            RbxInstance workspace = Get(WorkspaceId);
            RbxStringValue greeting = (RbxStringValue)Get(ValueId);
            Assert.AreEqual(ReplicationApplyStatus.GapDetected, _applier.Apply(Batch(3L), _state).Status);
            InstanceSnapshot renamed = Node(ValueId, WorkspaceId, "StringValue", "Farewell");
            renamed.Value = new ValueSnapshot { StringValue = "bye" };
            _state.Replace(renamed);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Arrived"));

            _applier.BeginResync(4L);
            Assert.IsFalse(_applier.NeedsResync, "the world is on its way; nothing is dropped any more");
            Assert.IsTrue(_applier.IsAwaitingWorld);
            ReplicationApplyResult result = _applier.Apply(Batch(4L, Spawn(GameId, 1L), Spawn(WorkspaceId, 6L),
                Spawn(ValueId, 7L, ReplicationMembers.Name, ReplicationMembers.Archivable, ReplicationMembers.Value),
                Spawn(9UL, 1L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(1, result.Spawned, "only the id the replica lacked is created");
            Assert.AreEqual(3, result.Patched, "the ids the replica held are brought up to date in place");
            Assert.AreEqual(0, result.Removed);
            Assert.AreSame(workspace, Get(WorkspaceId), "an instance the server still has keeps its identity");
            Assert.AreSame(greeting, Get(ValueId));
            Assert.AreSame(workspace, _replica.WorldRoot);
            Assert.AreEqual("Farewell", greeting.Name);
            Assert.AreEqual("bye", greeting.Value);
            Assert.AreSame(workspace, Get(9UL).Parent);
            _replica.TryGetRecord(greeting.Id, out InstanceRecord record);
            Assert.AreEqual(7L, record.Revision, "the world's revision is stamped on the held instance");
            Assert.IsFalse(record.IsLocallyDiverged, "the world is the server's write, not the client's divergence");
            Assert.AreEqual(5L, _applier.ExpectedSequence);
            Assert.IsFalse(_applier.IsAwaitingWorld);
            Assert.IsFalse(_applier.NeedsResync);
            Assert.AreEqual(1, _resyncs.Count, "only the gap asked for the world; the world itself is no fault");
            Assert.IsFalse(_replica.IsApplyingReplication);
        }

        [Test]
        public void Negative_CompleteResyncAlone_StillRefusesAWorldOntoAReplicaThatHoldsIt()
        {
            // WHY pinned: CompleteResync declares a replica rebuilt by other means, so an ordinary
            // spawn for a held id stays a violation after it; only BeginResync arms the in-place world.
            _applier.Apply(Join(), _state);
            _applier.Apply(Batch(3L), _state);
            _applier.CompleteResync(4L);

            ReplicationApplyResult result = _applier.Apply(
                Batch(4L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("already holds", result.Detail);
            Assert.IsFalse(_applier.IsAwaitingWorld);
        }

        [Test]
        public void AWorld_RemovesTheServerInstancesItNoLongerNames_AndKeepsTheReplicasOwn()
        {
            _applier.Apply(Join(), _state);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Crates"));
            _state.Add(Node(10UL, 9UL, "Part", "Crate"));
            _applier.Apply(Batch(2L, Spawn(9UL, 1L), Spawn(10UL, 1L)), _state);
            RbxInstance crates = Get(9UL);
            RbxInstance crate = Get(10UL);
            RbxInstance kept = _replica.CreateScripted("Part");
            kept.Name = "LocalMarker";
            kept.Parent = Get(WorkspaceId);
            RbxInstance underCrates = _replica.CreateScripted("Part");
            underCrates.Parent = crates;

            _applier.BeginResync(3L);
            ReplicationApplyResult result = _applier.Apply(
                Batch(3L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(2, result.Removed, "both server instances the world no longer names are removed");
            Assert.IsTrue(crates.IsDestroyed);
            Assert.IsTrue(crate.IsDestroyed);
            Assert.IsFalse(_replica.TryGet(new InstanceId(9UL), out _));
            Assert.IsTrue(kept.Id.IsLocallyAssigned);
            Assert.IsFalse(kept.IsDestroyed, "the replica's own instance is not the world's to remove");
            Assert.AreSame(Get(WorkspaceId), kept.Parent);
            Assert.AreEqual("LocalMarker", kept.Name);
            Assert.IsTrue(underCrates.IsDestroyed, "a local instance under a removed one goes with it");
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void AWorld_ClearsTheAttributesTagsAndReferencesItNoLongerNames()
        {
            _applier.Apply(Join(), _state);
            InstanceSnapshot tagged = Node(ValueId, WorkspaceId, "StringValue", "Greeting");
            tagged.Attributes.Add(new AttributeSnapshot { Name = "Hp", Kind = AttributeValueKind.Number, NumberValue = 3d });
            tagged.Tags.Add("Enemy");
            _state.Replace(tagged);
            InstanceSnapshot pointer = Node(11UL, WorkspaceId, "ObjectValue", "Pointer");
            pointer.Value = new ValueSnapshot { ObjectTargetId = ValueId };
            _state.Add(pointer);
            _applier.Apply(Batch(2L, Spawn(11UL, 1L, ReplicationMembers.Name, ReplicationMembers.Value),
                Patch(ValueId, 3L, ReplicationMembers.Attribute("Hp"), ReplicationMembers.Tag("Enemy"))), _state);
            RbxInstance greeting = Get(ValueId);
            RbxObjectValue objectValue = (RbxObjectValue)Get(11UL);
            Assert.AreEqual(3d, greeting.GetAttribute("Hp"), "precondition: the attribute arrived");
            Assert.IsTrue(greeting.HasTag("Enemy"), "precondition: the tag arrived");
            Assert.AreSame(greeting, objectValue.Value, "precondition: the reference resolved");
            Assert.AreEqual(1, _applier.TrackedReferenceCount);
            _state.Replace(Node(ValueId, WorkspaceId, "StringValue", "Greeting"));
            _state.Replace(Node(11UL, WorkspaceId, "ObjectValue", "Pointer"));

            _applier.BeginResync(3L);
            ReplicationApplyResult result = _applier.Apply(Batch(3L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L),
                Spawn(ValueId, 4L), Spawn(11UL, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsNull(greeting.GetAttribute("Hp"), "an attribute the world does not name was cleared on the server");
            Assert.IsFalse(greeting.HasTag("Enemy"), "a tag the world does not name was removed on the server");
            Assert.IsNull(objectValue.Value, "a reference the world does not name is nil, as on a fresh spawn");
            Assert.AreEqual(0, _applier.TrackedReferenceCount, "and it leaves the ledger");
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void AnEmptyWorld_RemovesEveryServerInstance_AndTheReplicaCanBeSeededAgain()
        {
            _applier.Apply(Join(), _state);
            RbxInstance detached = _replica.CreateScripted("Folder");

            _applier.BeginResync(2L);
            ReplicationApplyResult emptied = _applier.Apply(Batch(2L), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, emptied.Status, emptied.Detail);
            Assert.AreEqual(3, emptied.Removed);
            Assert.AreEqual(1, _replica.Count, "only the replica's own instance is left");
            Assert.IsFalse(detached.IsDestroyed);
            Assert.IsNull(_replica.WorldRoot, "the removed Workspace is no longer the world root");

            ReplicationApplyResult reseeded = _applier.Apply(
                Batch(3L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, reseeded.Status, reseeded.Detail);
            Assert.AreSame(Get(WorkspaceId), _replica.WorldRoot);
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void AFailedWorld_AsksForTheWorldAgain_AndTheNextWorldStillConverges()
        {
            _applier.Apply(Join(), _state);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Landed"));
            InstanceSnapshot counter = Node(10UL, WorkspaceId, "IntValue", "Counter");
            counter.Value = new ValueSnapshot { StringValue = "not-a-number" };
            _state.Add(counter);
            _applier.BeginResync(2L);

            ReplicationApplyResult failed = _applier.Apply(Batch(2L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L),
                Spawn(ValueId, 2L), Spawn(9UL, 1L), Spawn(10UL, 1L, ReplicationMembers.Name, ReplicationMembers.Value)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, failed.Status);
            Assert.IsTrue(_applier.NeedsResync);
            Assert.IsFalse(_applier.IsAwaitingWorld, "a refused world is spent; the next resync arms a new one");
            Assert.AreEqual(1, _resyncs.Count);
            RbxInstance landed = Get(9UL);
            Assert.AreEqual(ReplicationApplyStatus.AwaitingResync, _applier.Apply(Batch(2L), _state).Status);

            counter.Value.StringValue = "4";
            _applier.BeginResync(3L);
            ReplicationApplyResult result = _applier.Apply(Batch(3L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L),
                Spawn(ValueId, 2L), Spawn(9UL, 1L), Spawn(10UL, 1L, ReplicationMembers.Name, ReplicationMembers.Value)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(0, result.Spawned, "the half-applied world left every id behind; each is reconciled");
            Assert.AreSame(landed, Get(9UL));
            Assert.AreEqual(4L, ((RbxIntValue)Get(10UL)).Value);
            Assert.AreSame(Get(WorkspaceId), Get(10UL).Parent, "the instance the failed world left unparented is placed");
            Assert.AreEqual(4L, _applier.ExpectedSequence);
            Assert.AreEqual(1, _resyncs.Count);
        }

        [Test]
        public void WhileTheWorldIsPending_AnOlderBatchIsADuplicate_AndTheWorldStillApplies()
        {
            _applier.Apply(Join(), _state);
            _applier.Apply(Batch(3L), _state);
            _applier.BeginResync(5L);

            ReplicationApplyResult older = _applier.Apply(Batch(3L, Spawn(9UL, 1L)), _state);
            ReplicationApplyResult world = _applier.Apply(
                Batch(5L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Duplicate, older.Status, "a batch planned before the world is superseded by it");
            Assert.AreEqual(ReplicationApplyStatus.Applied, world.Status, world.Detail);
            Assert.AreEqual(6L, _applier.ExpectedSequence);
            Assert.AreEqual(1, _resyncs.Count);
        }

        [Test]
        public void Negative_WhileTheWorldIsPending_ANewerBatchIsAGap_ThatAsksForTheWorldAgain()
        {
            _applier.Apply(Join(), _state);
            _applier.BeginResync(5L);

            ReplicationApplyResult newer = _applier.Apply(Batch(6L), _state);

            Assert.AreEqual(ReplicationApplyStatus.GapDetected, newer.Status);
            Assert.IsTrue(_applier.NeedsResync);
            Assert.IsFalse(_applier.IsAwaitingWorld, "the world that was lost is not waited for any longer");
            Assert.AreEqual(1, _resyncs.Count);
        }

        [Test]
        public void Negative_BeginResync_BehindTheExpectedSequence_Throws_AndChangesNothing()
        {
            _applier.Apply(Join(), _state);
            _applier.Apply(Batch(3L), _state);

            Assert.Throws<ArgumentOutOfRangeException>(() => _applier.BeginResync(1L));

            Assert.IsTrue(_applier.NeedsResync);
            Assert.IsFalse(_applier.IsAwaitingWorld);
            Assert.AreEqual(2L, _applier.ExpectedSequence);
        }

        [Test]
        public void Negative_AWorldSpawnNamingAnotherClassForAHeldId_IsAProtocolViolation()
        {
            _applier.Apply(Join(), _state);
            _state.Replace(Node(ValueId, WorkspaceId, "IntValue", "Greeting"));
            _applier.BeginResync(2L);

            ReplicationApplyResult result = _applier.Apply(
                Batch(2L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("names class IntValue", result.Detail);
            Assert.IsInstanceOf<RbxStringValue>(Get(ValueId), "the held instance is not replaced by a guess");
            Assert.IsTrue(_applier.NeedsResync);
            Assert.AreEqual(1, _resyncs.Count);
        }

        [Test]
        public void Negative_AWorldSpawnUnderAParentTheWorldHasNotPlaced_IsAProtocolViolation()
        {
            // WHY the parent is held but not placed: the replica still has Workspace, but a world that
            // does not name it is about to remove it, and the child would go with it.
            _applier.Apply(Join(), _state);
            _applier.BeginResync(2L);

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Spawn(GameId, 1L), Spawn(ValueId, 2L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("has not placed", result.Detail);
            Assert.IsTrue(_replica.TryGet(new InstanceId(WorkspaceId), out _), "a refused world removes nothing");
        }

        [Test]
        public void Negative_AWorldBatchThatRemoves_IsAProtocolViolation()
        {
            _applier.Apply(Join(), _state);
            _applier.BeginResync(2L);

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Spawn(GameId, 1L), Remove(ValueId)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("may only spawn", result.Detail);
            Assert.IsTrue(_replica.TryGet(new InstanceId(ValueId), out _));
        }

        [Test]
        public void ASpawn_DoesNotRestoreTheServersOwnershipOrAccessFields()
        {
            // WHY: these fields are the server's authorization metadata — another player's durable
            // actor id among them — and a client has no use for them but to read them.
            _applier.Apply(Join(), _state);
            InstanceSnapshot owned = Node(9UL, WorkspaceId, "Folder", "Loot");
            owned.OwnerModId = "server-mod";
            owned.OriginTag = OriginTag.FromMod("server-mod");
            owned.OwnerActorId = "durable-bob";
            owned.AccessScope = InstanceAccessScope.HostProtected;
            _state.Add(owned);

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Spawn(9UL, 1L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsTrue(_replica.TryGetRecord(new InstanceId(9UL), out InstanceRecord record));
            Assert.IsNull(record.OwnerModId);
            Assert.IsNull(record.OriginTag);
            Assert.IsNull(record.OwnerActorId, "another player's durable actor id never lands on a client");
            Assert.AreEqual(InstanceAccessScope.SharedWritable, record.AccessScope,
                "the replica's own default, not the server's verdict");
        }

        [Test]
        public void Negative_APatchForAnIdTheReplicaDoesNotHold_IsAProtocolViolation_NeverASilentCreate()
        {
            _applier.Apply(Join(), _state);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Ghost"));

            ReplicationApplyResult result = _applier.Apply(
                Batch(2L, Patch(9UL, 1L, ReplicationMembers.Name)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("does not hold", result.Detail);
            Assert.IsFalse(_replica.TryGet(new InstanceId(9UL), out _), "a patch must never create");
            Assert.IsTrue(_applier.NeedsResync);
            Assert.AreEqual(1, _resyncs.Count);
            Assert.AreEqual(2L, _applier.ExpectedSequence, "a refused batch does not advance");
        }

        [Test]
        public void ARemoveForAnIdTheReplicaNeverHad_IsIgnored_NotRefused()
        {
            _applier.Apply(Join(), _state);

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Remove(77UL)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual(1, result.IgnoredRemovals);
            Assert.AreEqual(0, result.Removed);
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void Negative_ASpawnNamingAParentTheReplicaDoesNotHold_IsAProtocolViolation()
        {
            _applier.Apply(Join(), _state);
            _state.Add(Node(9UL, 8UL, "Folder", "Orphan"));

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Spawn(9UL, 1L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("parent 8", result.Detail);
            Assert.IsFalse(_replica.TryGet(new InstanceId(9UL), out _), "nothing is placed under a guessed parent");
        }

        [Test]
        public void Negative_ASpawnForAnIdTheReplicaAlreadyHolds_IsAProtocolViolation()
        {
            _applier.Apply(Join(), _state);

            ReplicationApplyResult result = _applier.Apply(Batch(2L, Spawn(WorkspaceId, 5L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("already holds", result.Detail);
        }

        [Test]
        public void Negative_AnOperationWhoseStateDidNotTravel_IsAProtocolViolation()
        {
            ReplicationApplyResult result = _applier.Apply(Batch(1L, Spawn(GameId, 1L)), new TableState());

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains("carried no state", result.Detail);
        }

        [Test]
        public void Negative_AMalformedValueInTheState_IsAProtocolViolation_NotAnEscapedParseException()
        {
            _applier.Apply(Join(), _state);
            InstanceSnapshot counter = Node(9UL, WorkspaceId, "IntValue", "Counter");
            counter.Value = new ValueSnapshot { StringValue = "not-a-number" };
            _state.Add(counter);

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(
                Batch(2L, Spawn(9UL, 1L, ReplicationMembers.Name, ReplicationMembers.Value)), _state));

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains(nameof(FormatException), result.Detail);
            Assert.IsTrue(_applier.NeedsResync, "a replica that could not apply what it was sent is out of sync");
            Assert.AreEqual(1, _resyncs.Count);
            Assert.IsFalse(_replica.IsApplyingReplication, "the apply scope is closed even though the batch failed");
            Assert.AreEqual(2L, _applier.ExpectedSequence, "a refused batch does not advance");
            Assert.AreEqual(ReplicationApplyStatus.AwaitingResync, _applier.Apply(Batch(3L), _state).Status,
                "nothing further is applied to a replica that knows it is broken");
            Assert.IsTrue(_diagnostics.Exists(line => line.Contains(nameof(FormatException))),
                "the full exception reaches the diagnostics so a programmer error stays visible");
        }

        [Test]
        public void Negative_AMissingValueInTheState_IsAProtocolViolation_NotAnEscapedArgumentException()
        {
            _applier.Apply(Join(), _state);
            InstanceSnapshot rig = Node(9UL, WorkspaceId, "Humanoid", "Humanoid");
            rig.Humanoid = new HumanoidSnapshot();
            _state.Add(rig);

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(Batch(2L, Spawn(9UL, 1L)), _state));

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            StringAssert.Contains(nameof(ArgumentNullException), result.Detail);
            Assert.IsTrue(_applier.NeedsResync);
            Assert.AreEqual(1, _resyncs.Count);
            Assert.IsFalse(_replica.IsApplyingReplication);
        }

        [Test]
        public void Negative_ADiagnosticsSinkThatThrows_DoesNotStopRecovery_AfterAPartiallyAppliedBatch()
        {
            // WHY a half-applied batch: the first spawn lands, the second throws, and the report of
            // that exception is written before the resync is requested — the one place a logger
            // that throws could leave a replica inconsistent with nobody ever asking for the world.
            _applier.Apply(Join(), _state);
            _replica.Diagnostics = _ => throw new InvalidOperationException("the host's logger is down");
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Landed"));
            InstanceSnapshot counter = Node(10UL, WorkspaceId, "IntValue", "Counter");
            counter.Value = new ValueSnapshot { StringValue = "not-a-number" };
            _state.Add(counter);

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(Batch(2L,
                Spawn(9UL, 1L), Spawn(10UL, 1L, ReplicationMembers.Name, ReplicationMembers.Value)), _state));

            Assert.AreEqual(ReplicationApplyStatus.ProtocolViolation, result.Status);
            Assert.AreEqual(1, result.Spawned, "the first spawn landed before the second threw: the batch is half-applied");
            Assert.IsTrue(_replica.TryGet(new InstanceId(9UL), out _));
            Assert.IsTrue(_applier.NeedsResync,
                "a half-applied batch must leave the replica asking for the world again, whatever the logger does");
            Assert.AreEqual(1, _resyncs.Count, "ResyncRequested still reaches its subscribers");
            Assert.IsFalse(_replica.IsApplyingReplication);
            Assert.AreEqual(2L, _applier.ExpectedSequence, "a refused batch does not advance");
            Assert.AreEqual(2, _replica.DiagnosticsFaults,
                "the exception report and the resync report were both refused by the sink, and both counted");
            Assert.AreEqual(ReplicationApplyStatus.AwaitingResync, _applier.Apply(Batch(3L), _state).Status,
                "nothing further is applied until the world arrives");
        }

        [Test]
        public void Negative_ADiagnosticsSinkThatThrows_DoesNotStopResyncRequested_FromReachingItsSubscribers()
        {
            _applier.Apply(Join(), _state);
            _replica.Diagnostics = _ => throw new InvalidOperationException("the host's logger is down");

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(Batch(3L), _state));

            Assert.AreEqual(ReplicationApplyStatus.GapDetected, result.Status);
            Assert.IsTrue(_applier.NeedsResync);
            CollectionAssert.AreEqual(new[] { result.Detail }, _resyncs,
                "the recovery event carries the reason to whoever fetches the world, logger or no logger");
            Assert.AreEqual(1, _replica.DiagnosticsFaults, "the refused report is counted, not lost");
        }

        [Test]
        public void ADiagnosticsSinkThatThrows_OnASkippedMemberReport_LeavesTheBatchApplied()
        {
            _applier.Apply(Join(), _state);
            _replica.Diagnostics = _ => throw new InvalidOperationException("the host's logger is down");
            _state.Add(Node(9UL, WorkspaceId, "Part", "Crate"));

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(
                Batch(2L, Spawn(9UL, 1L, ReplicationMembers.Name, "Size")), _state));

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsTrue(_replica.TryGet(new InstanceId(9UL), out _));
            Assert.IsFalse(_applier.NeedsResync, "a report the sink refused is not a fault in the batch");
            Assert.IsEmpty(_resyncs);
            Assert.AreEqual(1, _replica.DiagnosticsFaults);
        }

        [Test]
        public void ADiagnosticsSinkThatThrows_OnAStrayPlayerReport_LeavesTheBatchApplied()
        {
            _applier.Apply(Join(), _state);
            _replica.Diagnostics = _ => throw new InvalidOperationException("the host's logger is down");
            InstanceSnapshot stray = Node(9UL, WorkspaceId, "Player", "Stray");
            stray.Player = new PlayerSnapshot { ActorId = "stray", UserId = 7L, DisplayName = "Stray", CharacterId = 0UL };
            _state.Add(stray);

            ReplicationApplyResult result = null;
            Assert.DoesNotThrow(() => result = _applier.Apply(Batch(2L, Spawn(9UL, 1L)), _state));

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.IsTrue(_replica.TryGet(new InstanceId(9UL), out RbxInstance player));
            Assert.AreSame(Get(WorkspaceId), player.Parent, "the tree is mirrored as the server sent it");
            Assert.IsFalse(_applier.NeedsResync);
            Assert.AreEqual(1, _replica.DiagnosticsFaults);
        }

        [Test]
        public void APatch_GoesThroughTheOrdinarySetters_SoTheClientsSignalsFire()
        {
            _applier.Apply(Join(), _state);
            RbxStringValue value = (RbxStringValue)Get(ValueId);
            ModScheduler scheduler = new(new NoScripts(), new FakeTime());
            List<string> fired = new();
            value.Changed.BindScheduler(scheduler);
            value.Changed.Connect((Action<object[]>)(args => fired.Add("Changed:" + args[0])));
            value.GetPropertyChangedSignal("Value").BindScheduler(scheduler);
            value.GetPropertyChangedSignal("Value").Connect((Action<object[]>)(_ => fired.Add("Value")));
            value.GetPropertyChangedSignal("Name").BindScheduler(scheduler);
            value.GetPropertyChangedSignal("Name").Connect((Action<object[]>)(_ => fired.Add("Name")));
            List<string> tagsAdded = new();
            _replica.TagAdded += (instance, tag, first) => tagsAdded.Add(instance.Name + ":" + tag);
            InstanceSnapshot node = Node(ValueId, WorkspaceId, "StringValue", "Renamed");
            node.Value = new ValueSnapshot { StringValue = "hello" };
            node.Tags.Add("Chatty");
            _state.Replace(node);

            ReplicationApplyResult result = _applier.Apply(Batch(2L,
                Patch(ValueId, 6L, ReplicationMembers.Value, ReplicationMembers.Name, ReplicationMembers.Tag("Chatty"))), _state);
            scheduler.Advance(0.016d);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreEqual("hello", value.Value);
            Assert.AreEqual("Renamed", value.Name);
            CollectionAssert.AreEquivalent(new[] { "Changed:hello", "Value", "Name" }, fired);
            CollectionAssert.AreEqual(new[] { "Renamed:Chatty" }, tagsAdded);
            _replica.TryGetRecord(value.Id, out InstanceRecord record);
            Assert.AreEqual(6L, record.Revision);
            Assert.IsFalse(record.IsLocallyDiverged, "the server's write is not the client's divergence");
        }

        [Test]
        public void AReference_ResolvesAfterTheBatch_AndToNilWhenTheTargetIsAbsent()
        {
            _applier.Apply(Join(), _state);
            InstanceSnapshot pointer = Node(10UL, WorkspaceId, "ObjectValue", "Pointer");
            pointer.Value = new ValueSnapshot { ObjectTargetId = 11UL };
            InstanceSnapshot dangling = Node(12UL, WorkspaceId, "ObjectValue", "Dangling");
            dangling.Value = new ValueSnapshot { ObjectTargetId = 999UL };
            _state.Add(pointer);
            _state.Add(Node(11UL, WorkspaceId, "Folder", "Target"));
            _state.Add(dangling);

            ReplicationApplyResult result = _applier.Apply(Batch(2L,
                Spawn(10UL, 1L, ReplicationMembers.Value), Spawn(11UL, 1L), Spawn(12UL, 1L, ReplicationMembers.Value)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreSame(Get(11UL), ((RbxObjectValue)Get(10UL)).Value, "spawned later in the same batch, still resolved");
            Assert.IsNull(((RbxObjectValue)Get(12UL)).Value, "a reference the recipient may not see is nil, as on Roblox");
            Assert.AreEqual(2, _applier.TrackedReferenceCount,
                "both references stay on the ledger, the dangling one waiting for id 999");
        }

        [Test]
        public void ADanglingReference_IsRepairedByTheBatchThatSpawnsItsTarget()
        {
            _applier.Apply(Join(), _state);
            InstanceSnapshot dangling = Node(12UL, WorkspaceId, "ObjectValue", "Dangling");
            dangling.Value = new ValueSnapshot { ObjectTargetId = 999UL };
            _state.Add(dangling);
            _applier.Apply(Batch(2L, Spawn(12UL, 1L, ReplicationMembers.Value)), _state);
            RbxObjectValue pointer = (RbxObjectValue)Get(12UL);
            Assert.IsNull(pointer.Value);
            _state.Add(Node(999UL, WorkspaceId, "Folder", "LateTarget"));

            ReplicationApplyResult result = _applier.Apply(Batch(3L, Spawn(999UL, 1L)), _state);

            Assert.AreEqual(ReplicationApplyStatus.Applied, result.Status, result.Detail);
            Assert.AreSame(Get(999UL), pointer.Value, "the batch that spawns the target repairs the reference");
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void TheReferenceLedger_IsBoundedByLiveHolders_AndForgetsWhatTheServerRewrites()
        {
            _applier.Apply(Join(), _state);
            for (ulong id = 20UL; id <= 22UL; id++)
            {
                InstanceSnapshot pointer = Node(id, WorkspaceId, "ObjectValue", "Pointer" + id);
                pointer.Value = new ValueSnapshot { ObjectTargetId = 900UL + id };
                _state.Add(pointer);
            }

            _applier.Apply(Batch(2L, Spawn(20UL, 1L, ReplicationMembers.Value),
                Spawn(21UL, 1L, ReplicationMembers.Value), Spawn(22UL, 1L, ReplicationMembers.Value)), _state);
            Assert.AreEqual(3, _applier.TrackedReferenceCount, "one slot per reference the recipient cannot resolve yet");

            InstanceSnapshot cleared = Node(20UL, WorkspaceId, "ObjectValue", "Pointer20");
            cleared.Value = new ValueSnapshot { ObjectTargetId = 0UL };
            _state.Replace(cleared);
            _applier.Apply(Batch(3L, Patch(20UL, 2L, ReplicationMembers.Value)), _state);
            Assert.AreEqual(2, _applier.TrackedReferenceCount, "a reference the server cleared is forgotten");

            InstanceSnapshot retargeted = Node(21UL, WorkspaceId, "ObjectValue", "Pointer21");
            retargeted.Value = new ValueSnapshot { ObjectTargetId = 777UL };
            _state.Replace(retargeted);
            _applier.Apply(Batch(4L, Patch(21UL, 2L, ReplicationMembers.Value)), _state);
            Assert.AreEqual(2, _applier.TrackedReferenceCount, "retargeting replaces the slot rather than adding one");

            _applier.Apply(Batch(5L, Remove(22UL)), _state);
            Assert.AreEqual(1, _applier.TrackedReferenceCount, "a holder that is gone takes its slot with it");

            _applier.Apply(Batch(6L, Remove(WorkspaceId)), _state);
            Assert.AreEqual(0, _applier.TrackedReferenceCount, "a holder destroyed with its ancestor is dropped too");
            Assert.IsEmpty(_resyncs);
        }

        [Test]
        public void ARemove_DestroysTheSubtree()
        {
            _applier.Apply(Join(), _state);
            _state.Add(Node(9UL, WorkspaceId, "Folder", "Crates"));
            _state.Add(Node(10UL, 9UL, "Part", "Inside"));
            _applier.Apply(Batch(2L, Spawn(9UL, 1L), Spawn(10UL, 1L)), _state);

            ReplicationApplyResult result = _applier.Apply(Batch(3L, Remove(9UL)), _state);

            Assert.AreEqual(1, result.Removed);
            Assert.IsFalse(_replica.TryGet(new InstanceId(9UL), out _));
            Assert.IsFalse(_replica.TryGet(new InstanceId(10UL), out _));
        }

        private ReplicationBatchPlan Join()
        {
            _state.Replace(Node(GameId, 0UL, "DataModel", "Game"));
            _state.Replace(Node(WorkspaceId, GameId, "Workspace", "Workspace"));
            _state.Replace(Node(ValueId, WorkspaceId, "StringValue", "Greeting"));
            return Batch(1L, Spawn(GameId, 1L), Spawn(WorkspaceId, 5L), Spawn(ValueId, 2L));
        }

        private RbxInstance Get(ulong id)
        {
            Assert.IsTrue(_replica.TryGet(new InstanceId(id), out RbxInstance instance), "replica holds " + id);
            return instance;
        }

        private static ReplicationBatchPlan Batch(long sequence, params ReplicationOperation[] operations)
        {
            return new ReplicationBatchPlan("alice", sequence, operations);
        }

        private static ReplicationOperation Spawn(ulong id, long revision, params string[] members)
        {
            return new ReplicationOperation(ReplicationOperationKind.Spawn, new InstanceId(id), revision,
                members.Length == 0 ? new[] { ReplicationMembers.Name, ReplicationMembers.Archivable } : members);
        }

        private static ReplicationOperation Patch(ulong id, long revision, params string[] members)
        {
            return new ReplicationOperation(ReplicationOperationKind.Patch, new InstanceId(id), revision, members);
        }

        private static ReplicationOperation Remove(ulong id)
        {
            return new ReplicationOperation(ReplicationOperationKind.Remove, new InstanceId(id), 0L);
        }

        private static InstanceSnapshot Node(ulong id, ulong parentId, string className, string name)
        {
            return new InstanceSnapshot
            {
                Id = id,
                ParentId = parentId,
                ClassName = className,
                Name = name,
                Archivable = true
            };
        }

        private sealed class TableState : IReplicationStateSource
        {
            private readonly Dictionary<ulong, InstanceSnapshot> _nodes = new();

            public void Add(InstanceSnapshot node)
            {
                _nodes.Add(node.Id, node);
            }

            public void Replace(InstanceSnapshot node)
            {
                _nodes[node.Id] = node;
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

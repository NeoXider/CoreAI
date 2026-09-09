using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0: what one recipient is told to do, given what it already has.
    /// </summary>
    /// <remarks>
    /// WHY every case checks the operation kind and not just "something was planned": a spawn
    /// where a patch was due duplicates the instance on the client, a patch where a spawn was due
    /// is a protocol violation, and a missing remove leaves a ghost. The kinds are the contract.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationStreamPlanEditModeTests
    {
        private InstanceRegistry _registry;
        private ReplicationDirtySet _dirty;
        private RbxDataModel _game;
        private ReplicationStream _stream;
        private RbxInstance _part;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "plan-world");
            _dirty = new ReplicationDirtySet(_registry);
            _game = DataModelBootstrap.CreateGame(_registry);
            _part = Under(_registry.WorldRoot, "Part", "Door");
            _stream = new ReplicationStream(_dirty, "alice");
        }

        [TearDown]
        public void Cleanup()
        {
            _dirty.Dispose();
        }

        [Test]
        public void TheFirstPlan_SpawnsTheVisibleTree_ParentFirst()
        {
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.AreEqual(1L, plan.Sequence);
            Assert.IsTrue(plan.Operations.All(op => op.Kind == ReplicationOperationKind.Spawn));
            Assert.AreEqual(_game.Id, plan.Operations[0].InstanceId, "the root comes first");
            HashSet<ulong> seen = new();
            foreach (ReplicationOperation operation in plan.Operations)
            {
                _registry.TryGet(operation.InstanceId, out RbxInstance instance);
                Assert.IsTrue(instance.Parent == null || seen.Contains(instance.Parent.Id.Value),
                    instance.Name + " was spawned before its parent");
                seen.Add(operation.InstanceId.Value);
            }

            Assert.IsTrue(seen.Contains(_registry.WorldRoot.Id.Value));
            Assert.IsTrue(seen.Contains(_part.Id.Value));
            Assert.IsTrue(seen.Contains(_game.GetService("ReplicatedStorage").Id.Value));
            Assert.IsFalse(seen.Contains(_game.GetService("ServerStorage").Id.Value));
            Assert.IsFalse(seen.Contains(_registry.WorldRoot.FindFirstChildOfClass("Camera").Id.Value));
            Assert.IsTrue(_stream.Knows(_part.Id));
            ReplicationOperation partSpawn = plan.Operations.Single(op => op.InstanceId == _part.Id);
            CollectionAssert.Contains(partSpawn.Members, "Name");
            CollectionAssert.DoesNotContain(partSpawn.Members, "Parent");
            Assert.AreEqual(RevisionOf(_part), partSpawn.Revision);
        }

        [Test]
        public void AChangeToAKnownInstance_IsAPatchNamingTheMembers()
        {
            Join();

            _part.Name = "Gate";
            _part.SetAttribute("Hp", 1d);
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.AreEqual(2L, plan.Sequence);
            ReplicationOperation only = plan.Operations.Single();
            Assert.AreEqual(ReplicationOperationKind.Patch, only.Kind);
            Assert.AreEqual(_part.Id, only.InstanceId);
            CollectionAssert.AreEquivalent(new[] { "Name", "Attribute:Hp" }, only.Members);
            Assert.AreEqual(RevisionOf(_part), only.Revision);
        }

        [Test]
        public void AParentsChildrenMember_IsNeverPlanned_OnlyItsRevisionTravels()
        {
            RbxInstance folder = Under(_registry.WorldRoot, "Folder", "Crates");
            Join();

            _part.Parent = folder;
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.IsTrue(plan.Operations.All(op => op.Kind == ReplicationOperationKind.Patch));
            CollectionAssert.AreEqual(new[] { "Parent" }, Op(plan, _part).Members);
            Assert.IsEmpty(Op(plan, folder).Members, "a revision-only patch carries no member");
            Assert.AreEqual(RevisionOf(folder), Op(plan, folder).Revision);
            Assert.IsEmpty(Op(plan, _registry.WorldRoot).Members);
            Assert.IsFalse(plan.Operations.Any(op => op.Members.Contains("Children")));
        }

        [Test]
        public void AParentMovedUnderItsOwnChild_IsPatchedAfterTheChildsMove_EvenWhenAnEarlierWriteDirtiedItFirst()
        {
            RbxInstance a = Under(_registry.WorldRoot, "Folder", "A");
            RbxInstance b = Under(a, "Folder", "B");
            Join();

            // WHY the Name write comes first: it puts A ahead of B in the dirty set, so a plan that
            // trusts dirty order tells the replica to parent A under B while B still hangs under A.
            a.Name = "renamed";
            b.Parent = _registry.WorldRoot;
            a.Parent = b;
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.IsTrue(plan.Operations.All(op => op.Kind == ReplicationOperationKind.Patch));
            Assert.Less(IndexOf(plan, b), IndexOf(plan, a),
                "B must reach Workspace before A is parented under it");
            CollectionAssert.AreEquivalent(new[] { "Name", "Parent" }, Op(plan, a).Members);
            CollectionAssert.AreEqual(new[] { "Parent" }, Op(plan, b).Members);
        }

        [Test]
        public void AReparentUnderANodeSpawnedThisStep_IsPatchedAfterTheSpawnsOwnParentHasMoved()
        {
            RbxInstance x = Under(_registry.WorldRoot, "Folder", "X");
            RbxInstance k = Under(x, "Folder", "K");
            Join();

            // WHY the Name write comes first: it dirties X before K, and X's move targets F, which
            // is spawned under K — a K that still hangs under X until K's own patch has run.
            x.Name = "renamed";
            RbxInstance f = Under(k, "Folder", "F");
            k.Parent = _registry.WorldRoot;
            x.Parent = f;
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(plan, f).Kind);
            Assert.Less(IndexOf(plan, f), IndexOf(plan, k), "spawns precede every patch");
            Assert.Less(IndexOf(plan, k), IndexOf(plan, x), "K must reach Workspace before X goes under F");
            CollectionAssert.AreEquivalent(new[] { "Name", "Parent" }, Op(plan, x).Members);
        }

        [Test]
        public void AnInstanceBecomingInvisible_IsARemove_AndSoIsEachKnownDescendant()
        {
            RbxInstance folder = Under(_registry.WorldRoot, "Folder", "Crates");
            RbxInstance inside = Under(folder, "Part", "Inside");
            Join();

            folder.Parent = _game.GetService("ServerStorage");
            ReplicationBatchPlan plan = _stream.Plan();

            Assert.AreEqual(ReplicationOperationKind.Remove, Op(plan, folder).Kind);
            Assert.AreEqual(ReplicationOperationKind.Remove, Op(plan, inside).Kind,
                "the replica may hold the descendant somewhere other than under this root");
            Assert.IsFalse(_stream.Knows(folder.Id));
            Assert.IsFalse(_stream.Knows(inside.Id));
        }

        [Test]
        public void AKnownInstance_MovedUnderARootLeavingVisibility_GetsItsOwnRemove_AndSpawnsOnReturn()
        {
            RbxInstance folder = Under(_registry.WorldRoot, "Folder", "Crates");
            Join();

            // WHY this mutation order: the folder is dirtied first, so the planner meets it before
            // the part and walks the server's tree, where the part already hangs under the folder —
            // while the recipient still holds the part under Workspace.
            folder.Parent = _game.GetService("ServerStorage");
            _part.Parent = folder;
            ReplicationBatchPlan leaving = _stream.Plan();
            _dirty.Clear();

            Assert.AreEqual(ReplicationOperationKind.Remove, Op(leaving, folder).Kind);
            Assert.AreEqual(ReplicationOperationKind.Remove, Op(leaving, _part).Kind,
                "the part left visibility too, and the recipient does not know it moved");
            Assert.IsFalse(_stream.Knows(_part.Id));

            _part.Parent = _registry.WorldRoot;

            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(_stream.Plan(), _part).Kind);
        }

        [Test]
        public void AnInstanceBecomingVisible_IsASpawnWithItsSubtree()
        {
            RbxInstance folder = Under(_game.GetService("ServerStorage"), "Folder", "Crates");
            RbxInstance inside = Under(folder, "Part", "Inside");
            Join();

            folder.Parent = _registry.WorldRoot;
            ReplicationBatchPlan plan = _stream.Plan();

            List<ReplicationOperation> spawns = plan.Operations
                .Where(op => op.Kind == ReplicationOperationKind.Spawn).ToList();
            Assert.AreEqual(2, spawns.Count);
            Assert.AreEqual(folder.Id, spawns[0].InstanceId, "parent first");
            Assert.AreEqual(inside.Id, spawns[1].InstanceId);
            Assert.IsFalse(plan.Operations.Any(op => op.Kind == ReplicationOperationKind.Patch && op.InstanceId == folder.Id));
            Assert.IsTrue(_stream.Knows(inside.Id));
        }

        [Test]
        public void LeavingAndReenteringVisibility_PlansRemoveThenSpawn()
        {
            Join();

            _part.Parent = _game.GetService("ServerStorage");
            ReplicationBatchPlan leaving = _stream.Plan();
            _dirty.Clear();
            _part.Parent = _registry.WorldRoot;
            ReplicationBatchPlan returning = _stream.Plan();

            Assert.AreEqual(ReplicationOperationKind.Remove, Op(leaving, _part).Kind);
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(returning, _part).Kind);
            Assert.AreEqual(leaving.Sequence + 1L, returning.Sequence);
        }

        [Test]
        public void Negative_APatchIsNeverPlannedForAnInstanceTheRecipientDoesNotHave()
        {
            RbxInstance secret = Under(_game.GetService("ServerStorage"), "Part", "Secret");
            Join();

            secret.Name = "Renamed";
            secret.Destroy();

            Assert.IsNull(_stream.Plan(), "an invisible instance produces nothing, changed or gone");
            Assert.AreEqual(1L, _stream.LastSequence, "an empty step consumes no sequence");
        }

        [Test]
        public void NothingToSend_PlansNull_AndKeepsTheSequence()
        {
            Join();

            Assert.IsNull(_stream.Plan());
            Assert.AreEqual(1L, _stream.LastSequence);
        }

        [Test]
        public void AWholeNodeMark_IsExpandedIntoNamedMembers()
        {
            _part.SetAttribute("Hp", 2d);
            _part.AddTag("Enemy");
            Join();

            _dirty.MarkDirty(_part.Id, RevisionOf(_part));
            ReplicationBatchPlan plan = _stream.Plan();

            CollectionAssert.AreEquivalent(
                new[] { "Name", "Archivable", "Parent", "Attribute:Hp", "Tag:Enemy" },
                Op(plan, _part).Members);
        }

        [Test]
        public void AWholeNodeMark_StillCarriesTheMembersRemovedInTheSameStep()
        {
            _part.SetAttribute("Target", "Door");
            _part.AddTag("Enemy");
            Join();

            // WHY a bare revision advance between the two removals: it is the whole-node mark every
            // part-property write produces, and it must not swallow the names on either side of it.
            _part.SetAttribute("Target", null);
            _registry.AdvanceRevision(_part.Id);
            _part.RemoveTag("Enemy");
            ReplicationBatchPlan plan = _stream.Plan();

            ReplicationOperation patch = Op(plan, _part);
            Assert.AreEqual(ReplicationOperationKind.Patch, patch.Kind);
            CollectionAssert.AreEquivalent(
                new[] { "Name", "Archivable", "Parent", "Attribute:Target", "Tag:Enemy" },
                patch.Members);
            Assert.AreEqual(RevisionOf(_part), patch.Revision);
        }

        [Test]
        public void PlanWorld_SpawnsEveryVisibleInstance_ParentFirst_WithoutTheDirtySet()
        {
            _dirty.Clear();

            ReplicationBatchPlan plan = _stream.PlanWorld();

            Assert.AreEqual(1L, plan.Sequence);
            Assert.IsTrue(plan.Operations.All(op => op.Kind == ReplicationOperationKind.Spawn));
            Assert.AreEqual(_game.Id, plan.Operations[0].InstanceId, "the root comes first");
            HashSet<ulong> seen = new();
            foreach (ReplicationOperation operation in plan.Operations)
            {
                _registry.TryGet(operation.InstanceId, out RbxInstance instance);
                Assert.IsTrue(instance.Parent == null || seen.Contains(instance.Parent.Id.Value),
                    instance.Name + " was spawned before its parent");
                seen.Add(operation.InstanceId.Value);
            }

            Assert.IsTrue(seen.Contains(_part.Id.Value));
            Assert.IsTrue(seen.Contains(_game.GetService("ReplicatedStorage").Id.Value));
            Assert.IsFalse(seen.Contains(_game.GetService("ServerStorage").Id.Value));
            Assert.IsFalse(seen.Contains(_registry.WorldRoot.FindFirstChildOfClass("Camera").Id.Value));
            Assert.IsTrue(_stream.Knows(_part.Id));
            Assert.IsNull(_stream.Plan(), "the seed left nothing for the dirty set to say");

            _part.Name = "Gate";
            Assert.AreEqual(ReplicationOperationKind.Patch, Op(_stream.Plan(), _part).Kind,
                "what the seed spawned is known from then on");
        }

        [Test]
        public void PlanWorld_ForgetsWhatTheRecipientHeld_AndSpawnsItAgain()
        {
            Join();

            ReplicationBatchPlan again = _stream.PlanWorld();

            Assert.AreEqual(2L, again.Sequence);
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(again, _part).Kind);
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(again, _registry.WorldRoot).Kind);
        }

        [Test]
        public void Negative_AStreamOverAWorldItsDirtySetNeverSaw_RefusesToPlan_UntilSeeded()
        {
            InstanceRegistry registry = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "late-world");
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance part = registry.Create("Part");
            part.Parent = registry.WorldRoot;
            using ReplicationDirtySet late = new(registry);
            ReplicationStream stream = new(late, "alice");
            part.Name = "Gate";

            System.InvalidOperationException refusal =
                Assert.Throws<System.InvalidOperationException>(() => stream.Plan());
            StringAssert.Contains("PlanWorld", refusal.Message);
            StringAssert.Contains(late.UnobservedInstanceCount.ToString(), refusal.Message);
            Assert.AreEqual(0L, stream.LastSequence, "nothing was planned");

            ReplicationBatchPlan seed = stream.PlanWorld();
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(seed, game).Kind);
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(seed, part).Kind);
            Assert.AreEqual(ReplicationOperationKind.Patch, Op(stream.Plan(), part).Kind,
                "after the seed the dirty set is served as usual");
        }

        [Test]
        public void TheMemberFilter_DropsHiddenMembers_ButTheRevisionStillTravels()
        {
            InstanceRegistry registry = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "member-world");
            using ReplicationDirtySet dirty = new(registry, new HideArchivable());
            DataModelBootstrap.CreateGame(registry);
            RbxInstance part = registry.Create("Part");
            part.Parent = registry.WorldRoot;
            ReplicationStream stream = new(dirty, "alice");
            stream.Plan();
            dirty.Clear();

            part.Archivable = false;
            part.Name = "Gate";
            ReplicationBatchPlan plan = stream.Plan();

            ReplicationOperation only = plan.Operations.Single();
            CollectionAssert.AreEqual(new[] { "Name" }, only.Members);

            dirty.Clear();
            part.Archivable = true;
            ReplicationOperation revisionOnly = stream.Plan().Operations.Single();
            Assert.IsEmpty(revisionOnly.Members, "a hidden member leaves a revision-only patch");
        }

        [Test]
        public void EachRecipientHasItsOwnKnownSet_AndItsOwnSequence()
        {
            ReplicationStream bob = new(_dirty, "bob");
            ReplicationBatchPlan alicesFirst = _stream.Plan();
            ReplicationBatchPlan bobsFirst = bob.Plan();
            _dirty.Clear();
            Assert.AreEqual(1L, alicesFirst.Sequence);
            Assert.AreEqual(1L, bobsFirst.Sequence);
            Assert.IsTrue(bobsFirst.Operations.All(op => op.Kind == ReplicationOperationKind.Spawn));
            Assert.IsTrue(bob.Knows(_part.Id));

            RbxInstance late = Under(_registry.WorldRoot, "Part", "Late");
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(_stream.Plan(), late).Kind);
            _dirty.Clear();
            late.Name = "Renamed";

            Assert.AreEqual(ReplicationOperationKind.Patch, Op(_stream.Plan(), late).Kind, "alice has it");
            Assert.AreEqual(ReplicationOperationKind.Spawn, Op(bob.Plan(), late).Kind, "bob never received it");
            Assert.AreEqual(3L, _stream.LastSequence);
            Assert.AreEqual(2L, bob.LastSequence);
        }

        private void Join()
        {
            Assert.IsNotNull(_stream.Plan());
            _dirty.Clear();
        }

        private static ReplicationOperation Op(ReplicationBatchPlan plan, RbxInstance instance)
        {
            return plan.Operations.Single(op => op.InstanceId == instance.Id);
        }

        private static int IndexOf(ReplicationBatchPlan plan, RbxInstance instance)
        {
            for (int index = 0; index < plan.Operations.Count; index++)
            {
                if (plan.Operations[index].InstanceId == instance.Id)
                {
                    return index;
                }
            }

            Assert.Fail(instance.Name + " was not planned");
            return -1;
        }

        private RbxInstance Under(RbxInstance parent, string className, string name)
        {
            RbxInstance instance = _registry.Create(className);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        private long RevisionOf(RbxInstance instance)
        {
            _registry.TryGetRecord(instance.Id, out InstanceRecord record);
            return record.Revision;
        }

        private sealed class HideArchivable : IReplicationFilter
        {
            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                return DefaultReplicationFilter.Instance.IsVisibleTo(recipientActorId, instance);
            }

            public bool IsMemberVisibleTo(string recipientActorId, RbxInstance instance, string member)
            {
                return member != ReplicationMembers.Archivable;
            }
        }
    }
}

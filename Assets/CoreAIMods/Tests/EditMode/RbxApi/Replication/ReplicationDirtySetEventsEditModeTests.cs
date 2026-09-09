using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0: the dirty set fills itself from the registry, member by member.
    /// </summary>
    /// <remarks>
    /// WHY this fixture exists next to the older filter tests: those mark the set by hand, which is
    /// exactly what production never did. Every case here is a real setter on a real instance.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationDirtySetEventsEditModeTests
    {
        private InstanceRegistry _registry;
        private ReplicationDirtySet _dirty;
        private RbxInstance _part;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "dirty-world");
            DataModelBootstrap.CreateGame(_registry);
            _dirty = new ReplicationDirtySet(_registry);
            _part = _registry.Create("Part");
            _part.Name = "Door";
            _part.Parent = _registry.WorldRoot;
            _dirty.Clear();
        }

        [TearDown]
        public void Cleanup()
        {
            _dirty.Dispose();
        }

        [Test]
        public void ANameWrite_MarksTheNameMember_WithTheRecordsRevision()
        {
            _part.Name = "Gate";

            ReplicationDelta delta = Only(_part);
            Assert.IsFalse(delta.Removed);
            Assert.IsFalse(delta.IsWholeNode);
            CollectionAssert.AreEqual(new[] { "Name" }, delta.Members);
            Assert.AreEqual(RevisionOf(_part), delta.Revision);
        }

        [Test]
        public void AReparent_MarksParentOnTheChild_AndOnlyStructureOnBothParents()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _registry.WorldRoot;
            _dirty.Clear();

            _part.Parent = folder;

            CollectionAssert.AreEqual(new[] { "Parent" }, Only(_part).Members);
            CollectionAssert.AreEqual(new[] { "Children" }, Only(_registry.WorldRoot).Members);
            CollectionAssert.AreEqual(new[] { "Children" }, Only(folder).Members);
            Assert.AreEqual(3, _dirty.PendingCount);
        }

        [Test]
        public void ADestroy_IsARemoval_ThatNoLaterChangeCanDowngrade()
        {
            _part.Name = "Changed";

            _part.Destroy();
            _dirty.MarkDirty(_part.Id, 99L, ReplicationMembers.Name);

            ReplicationDelta delta = Only(_part);
            Assert.IsTrue(delta.Removed);
            Assert.IsEmpty(delta.Members);
            CollectionAssert.AreEqual(new[] { "Children" }, Only(_registry.WorldRoot).Members,
                "the parent lost a child; the child's removal carries the change");
        }

        [Test]
        public void AttributesAndTags_MarkPrefixedMembers_OncePerName()
        {
            _part.SetAttribute("Hp", 3d);
            _part.SetAttribute("Hp", 4d);
            _part.SetAttribute("Hp", null);
            _part.AddTag("Enemy");
            _part.RemoveTag("Enemy");

            CollectionAssert.AreEquivalent(new[] { "Attribute:Hp", "Tag:Enemy" }, Only(_part).Members);
        }

        [Test]
        public void AWholeNodeMark_KeepsTheMemberMarks_BeforeAndAfterIt()
        {
            _part.Name = "Gate";
            _dirty.MarkDirty(_part.Id, RevisionOf(_part));
            _part.Archivable = false;

            ReplicationDelta delta = Only(_part);
            Assert.IsTrue(delta.IsWholeNode);
            CollectionAssert.AreEquivalent(new[] { "Name", "Archivable" }, delta.Members,
                "a whole-node mark says nothing about which members changed; the names must survive it");
            Assert.AreEqual(RevisionOf(_part), delta.Revision, "the newest revision still wins");
        }

        [Test]
        public void AMemberRemoved_ThenAWholeNodeMark_StillNamesTheMember()
        {
            _part.SetAttribute("Target", "Door");
            _part.AddTag("Enemy");
            _dirty.Clear();

            _part.SetAttribute("Target", null);
            _part.RemoveTag("Enemy");
            _registry.AdvanceRevision(_part.Id);

            ReplicationDelta delta = Only(_part);
            Assert.IsTrue(delta.IsWholeNode);
            CollectionAssert.AreEquivalent(new[] { "Attribute:Target", "Tag:Enemy" }, delta.Members,
                "the instance no longer has either, so only the names can tell the replica to clear them");
        }

        [Test]
        public void ALaterRevision_IsNotOverwrittenByAnEarlierOneArrivingAfterIt()
        {
            _dirty.MarkDirty(_part.Id, 7L, ReplicationMembers.Name);
            _dirty.MarkDirty(_part.Id, 5L, ReplicationMembers.Archivable);
            _dirty.MarkDirty(_part.Id, 6L);

            ReplicationDelta delta = Only(_part);
            Assert.AreEqual(7L, delta.Revision);
            CollectionAssert.AreEquivalent(new[] { "Name", "Archivable" }, delta.Members);
            Assert.IsTrue(delta.IsWholeNode);
        }

        [Test]
        public void AWriteDuringAFlush_ReachesTheSetOutOfOrder_AndTheNewestRevisionStillWins()
        {
            InstanceRegistry registry = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "nested-world");
            DataModelBootstrap.CreateGame(registry);
            RbxInstance part = registry.Create("Part");
            part.Parent = registry.WorldRoot;
            bool reacted = false;
            // WHY the reacting handler subscribes before the set: handlers run in subscription order,
            // so it writes while the set has not yet seen the outer event; the nested flush then hands
            // the set the newer revision before the outer one arrives.
            registry.RevisionAdvanced += (id, revision, member) =>
            {
                if (!reacted && member == ReplicationMembers.Name)
                {
                    reacted = true;
                    part.Archivable = false;
                }
            };
            using ReplicationDirtySet dirty = new(registry);

            part.Name = "Gate";

            ReplicationDelta delta = dirty.Pending.Single(candidate => candidate.InstanceId == part.Id);
            registry.TryGetRecord(part.Id, out InstanceRecord record);
            Assert.IsTrue(reacted);
            Assert.AreEqual(record.Revision, delta.Revision, "the older revision arrived last and lost");
            CollectionAssert.AreEqual(new[] { "Archivable", "Name" }, delta.Members,
                "the nested write's member reached the set first");
        }

        [Test]
        public void MarksPublishedFromTwoThreads_ForTheSameInstance_AreAllKept()
        {
            // WHY the registry publishes rather than the test marking by hand: RevisionAdvanced is
            // raised after the mutation gate is released, so two commits on two threads reach the set
            // at the same time — that path, not a direct MarkDirty, is what has to stay whole.
            const int perThread = 2000;
            using ManualResetEventSlim start = new(false);
            Exception[] failures = new Exception[2];
            Thread[] publishers = new Thread[2];
            for (int lane = 0; lane < publishers.Length; lane++)
            {
                int owner = lane;
                publishers[lane] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (int index = 0; index < perThread; index++)
                        {
                            _registry.AdvanceRevision(_part.Id, ReplicationMembers.Attribute("t" + owner + "-" + index));
                            if ((index & 63) == 0)
                            {
                                _registry.AdvanceRevision(_part.Id);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        failures[owner] = exception;
                    }
                }) { IsBackground = true };
                publishers[lane].Start();
            }

            start.Set();
            foreach (Thread publisher in publishers)
            {
                Assert.IsTrue(publisher.Join(TimeSpan.FromSeconds(30)), "a publisher never finished");
            }

            Assert.IsNull(failures[0], failures[0]?.ToString());
            Assert.IsNull(failures[1], failures[1]?.ToString());
            ReplicationDelta delta = Only(_part);
            Assert.AreEqual(2 * perThread, delta.Members.Count, "a mark published from one thread was lost");
            Assert.AreEqual(RevisionOf(_part), delta.Revision, "the newest revision still wins");
            Assert.IsTrue(delta.IsWholeNode);
        }

        [Test]
        public void AMarkThatLandsAfterTheStepsView_SurvivesTheStepsClear()
        {
            _part.Name = "Gate";
            IReadOnlyList<ReplicationDelta> served = _dirty.Pending;
            // WHY a plain write stands in for the other thread: what matters is that it lands after
            // the step took its view and before the step cleared, whichever thread it comes from.
            _part.SetAttribute("Hp", 1d);
            _dirty.Clear();

            CollectionAssert.AreEqual(new[] { "Name" }, served.Single().Members, "the view is what was served");
            ReplicationDelta carried = Only(_part);
            CollectionAssert.AreEquivalent(new[] { "Name", "Attribute:Hp" }, carried.Members,
                "the mark nobody was served survives whole; re-sending Name is harmless, losing Hp is not");
            Assert.AreEqual(RevisionOf(_part), carried.Revision);

            _dirty.Clear();

            Assert.AreEqual(0, _dirty.PendingCount, "once a view has carried it, the next Clear drops it");
        }

        [Test]
        public void ASetBuiltOverAnExistingWorld_CountsTheInstancesItNeverSaw()
        {
            Assert.AreEqual(_registry.Count - 1, _dirty.UnobservedInstanceCount,
                "everything but the part created after it predates the set");

            InstanceRegistry empty = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "empty-world");
            using ReplicationDirtySet first = new(empty);
            DataModelBootstrap.CreateGame(empty);

            Assert.AreEqual(0, first.UnobservedInstanceCount, "a set built before the world misses nothing");
            Assert.Greater(first.PendingCount, 0);
        }

        [Test]
        public void ValueAndModelSetters_NameTheirMembers()
        {
            RbxStringValue value = (RbxStringValue)_registry.Create("StringValue");
            value.Parent = _registry.WorldRoot;
            RbxModel model = (RbxModel)_registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            _part.Parent = model;
            _dirty.Clear();

            value.Value = "hello";
            model.SetPrimaryPart(_part);
            model.SetWorldPivot(RbxCFrame.Identity);

            CollectionAssert.AreEqual(new[] { "Value" }, Only(value).Members);
            CollectionAssert.AreEqual(new[] { "PrimaryPart", "WorldPivot" }, Only(model).Members);
        }

        [Test]
        public void Dispose_StopsObservingTheRegistry()
        {
            _dirty.Dispose();

            _part.Name = "Unseen";
            _part.Destroy();

            Assert.AreEqual(0, _dirty.PendingCount);
        }

        [Test]
        public void Pending_IsUnfiltered_WhileDeltasForIsNot()
        {
            RbxInstance secret = _registry.Create("Folder");
            secret.Parent = _registry.Create("Folder");
            RbxInstance storage = ((RbxDataModel)_registry.WorldRoot.Parent).GetService("ServerStorage");
            secret.Parent.Parent = storage;
            _dirty.Clear();

            secret.Name = "Vault";
            _part.Name = "Gate";

            Assert.AreEqual(2, _dirty.Pending.Count);
            Assert.AreEqual(1, _dirty.DeltasFor("anyone").Count);
        }

        private ReplicationDelta Only(RbxInstance instance)
        {
            IEnumerable<ReplicationDelta> matches = _dirty.Pending.Where(delta => delta.InstanceId == instance.Id);
            Assert.AreEqual(1, matches.Count(), "exactly one delta per instance and step");
            return matches.First();
        }

        private long RevisionOf(RbxInstance instance)
        {
            _registry.TryGetRecord(instance.Id, out InstanceRecord record);
            return record.Revision;
        }
    }
}

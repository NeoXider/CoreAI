using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP12 gate: what each client is told about, and what it must never be told.
    /// </summary>
    /// <remarks>
    /// WHY filtering at the source is the whole point: a client that receives the full tree can read
    /// ServerStorage, other players' private state and the answer to every puzzle in the world — no
    /// exploit needed, just looking at what arrived. Filtering anywhere but the sender can be
    /// bypassed by a modified client.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationFilterEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private ReplicationDirtySet _dirty;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "replication-world");
            _game = DataModelBootstrap.CreateGame(_registry);
            _dirty = new ReplicationDirtySet(_registry);
        }

        [Test]
        public void WorkspaceContent_IsReplicated()
        {
            RbxInstance part = PartUnder(_registry.WorldRoot, "Door");

            Assert.IsTrue(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", part));
        }

        [Test]
        public void ReplicatedStorageAndLighting_AreReplicated()
        {
            RbxInstance shared = PartUnder(_game.GetService("ReplicatedStorage"), "Shared");
            RbxInstance lit = PartUnder(_game.GetService("Lighting"), "Lit");

            Assert.IsTrue(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", shared));
            Assert.IsTrue(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", lit));
        }

        [Test]
        public void Negative_ServerStorageIsNeverReplicated()
        {
            // "Server" in the name is the contract. A client that received this would hold the
            // server's private content on its own disk.
            RbxInstance secret = PartUnder(_game.GetService("ServerStorage"), "Secret");

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", secret));
        }

        [Test]
        public void Negative_ServerScriptServiceIsNeverReplicated()
        {
            RbxInstance secret = PartUnder(_game.GetService("ServerScriptService"), "Logic");

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", secret));
        }

        [Test]
        public void Negative_AnInstanceUnderNoListedContainerIsNotReplicated()
        {
            // An instance nobody decided to share is one nobody decided to share; the safe reading
            // of an undecided case is silence.
            RbxInstance orphan = _registry.Create("Part");

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", orphan));
        }

        [Test]
        public void Negative_ADestroyedInstanceIsNotReplicated()
        {
            RbxInstance part = PartUnder(_registry.WorldRoot, "Gone");
            part.Destroy();

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("actor-a", part));
        }

        [Test]
        public void DirtySet_CollapsesRepeatedChangesToOneDeltaPerInstance()
        {
            // A script that writes five properties in one frame must produce one delta, not five
            // packets carrying four stale views of the same instance.
            RbxInstance part = PartUnder(_registry.WorldRoot, "Door");

            _dirty.MarkDirty(part.Id, 1L);
            _dirty.MarkDirty(part.Id, 2L);
            _dirty.MarkDirty(part.Id, 3L);

            Assert.AreEqual(1, _dirty.PendingCount);
            IReadOnlyList<ReplicationDelta> deltas = _dirty.DeltasFor("actor-a");
            Assert.AreEqual(1, deltas.Count);
            Assert.AreEqual(3L, deltas[0].Revision, "the newest revision is the one that matters");
        }

        [Test]
        public void DirtySet_KeepsARemovalOverAChangeInTheSameStep()
        {
            // A client told "it changed" and never told "it is gone" keeps drawing something that
            // does not exist.
            RbxInstance part = PartUnder(_registry.WorldRoot, "Door");

            _dirty.MarkDirty(part.Id, 5L);
            _dirty.MarkRemoved(part.Id, 6L);
            _dirty.MarkDirty(part.Id, 7L);

            IReadOnlyList<ReplicationDelta> deltas = _dirty.DeltasFor("actor-a");
            Assert.AreEqual(1, deltas.Count);
            Assert.IsTrue(deltas[0].Removed, "a removal must not be overwritten by a later change");
        }

        [Test]
        public void DirtySet_SendsEachRecipientOnlyWhatItMaySee()
        {
            // Two clients in the same world do not see the same things, so one shared batch would
            // either leak to the narrower client or starve the wider one.
            RbxInstance visible = PartUnder(_registry.WorldRoot, "Door");
            RbxInstance hidden = PartUnder(_game.GetService("ServerStorage"), "Secret");
            _dirty.MarkDirty(visible.Id, 1L);
            _dirty.MarkDirty(hidden.Id, 1L);

            IReadOnlyList<ReplicationDelta> deltas = _dirty.DeltasFor("actor-a");

            Assert.AreEqual(1, deltas.Count, "only the Workspace part may cross");
            Assert.AreEqual(visible.Id.Value, deltas[0].InstanceId.Value);
        }

        [Test]
        public void DirtySet_UsesTheFilterItWasGiven()
        {
            // The default filter is a default, not a law: a host with its own visibility rules must
            // be able to supply them without editing CoreAI.
            RbxInstance part = PartUnder(_registry.WorldRoot, "Door");
            ReplicationDirtySet strict = new(_registry, new DenyAllFilter());
            strict.MarkDirty(part.Id, 1L);

            Assert.IsEmpty(strict.DeltasFor("actor-a"));
        }

        [Test]
        public void Clear_EmptiesTheSetForTheNextStep()
        {
            RbxInstance part = PartUnder(_registry.WorldRoot, "Door");
            _dirty.MarkDirty(part.Id, 1L);

            _dirty.Clear();

            Assert.AreEqual(0, _dirty.PendingCount);
            Assert.IsEmpty(_dirty.DeltasFor("actor-a"));
        }

        [Test]
        public void Negative_ADirtySetWithoutARegistryIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new ReplicationDirtySet(null));
        }

        private RbxInstance PartUnder(RbxInstance parent, string name)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = name;
            part.Parent = parent;
            return part;
        }

        private sealed class DenyAllFilter : IReplicationFilter
        {
            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                return false;
            }
        }
    }
}

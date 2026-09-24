using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Backing-object seam per D5 (§5.1.8 items 1–2 at registry level): unparented
    /// instances have no backing; entering the DataModel (scene) subtree materializes — the whole
    /// explorer mirrors, storage services included; detaching deactivates; Destroy releases the
    /// backing object. Which materialized objects are physical (only Workspace and its descendants,
    /// Workspace.yaml) is the Unity adapter's job; this seam only tracks tree membership.</summary>
    [TestFixture]
    public sealed class BackingBinderSeamEditModeTests
    {
        private InMemoryInstanceBackingBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            _binder = new InMemoryInstanceBackingBinder();
            _registry = new InstanceRegistry(null, _binder);
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [Test]
        public void D5_FreshInstance_HasNoBackingObject()
        {
            RbxInstance part = _registry.Create("Part");
            Assert.IsFalse(_binder.IsMaterialized(part.Id));
        }

        [Test]
        public void D5_ParentingIntoWorkspace_MaterializesTheSubtree()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;

            Assert.IsFalse(_binder.IsMaterialized(model.Id));

            model.Parent = _registry.WorldRoot;
            Assert.IsTrue(_binder.IsMaterialized(model.Id));
            Assert.IsTrue(_binder.IsMaterialized(part.Id));
        }

        [Test]
        public void D5_Detaching_DeactivatesNotDestroys()
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;
            part.Parent = null;

            Assert.IsFalse(_binder.IsMaterialized(part.Id));
            CollectionAssert.Contains(_binder.Events, "leave:" + part.Id.Value);
            CollectionAssert.DoesNotContain(_binder.Events, "destroy:" + part.Id.Value);
            Assert.IsFalse(part.IsDestroyed);
        }

        [Test]
        public void D5_StorageSubtrees_MaterializeThroughTheSameSeam()
        {
            // WHY: the Unity hierarchy mirrors the whole Roblox explorer, so storage-service
            // contents materialize too (they are NOT skipped) — the Unity adapter keeps them inactive
            // because they sit outside Workspace; the seam itself only tracks tree membership, so the
            // fake reports them materialized.
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _game.GetService("ReplicatedStorage");
            Assert.IsTrue(_binder.IsMaterialized(folder.Id));
        }

        [Test]
        public void D5_NonWorkspaceSceneContent_MaterializesThroughTheSameSeam()
        {
            // WHY: "only Workspace is physical" is decided by the Unity adapter's active flag, not by
            // skipping materialization here. A Part in Lighting or directly under game must still get
            // a backing, or moving it back into Workspace would have nothing to reactivate.
            RbxInstance stored = _registry.Create("Part");
            stored.Parent = _game.GetService("Lighting");
            RbxInstance loose = _registry.Create("Part");
            loose.Parent = _game;

            Assert.IsTrue(_binder.IsMaterialized(stored.Id), "Lighting content is in the scene tree");
            Assert.IsTrue(_binder.IsMaterialized(loose.Id), "a Part parented straight to game is in the scene tree");

            int eventsBefore = _binder.Events.Count;
            stored.Parent = _registry.WorldRoot;
            Assert.IsTrue(_binder.IsMaterialized(stored.Id));
            Assert.AreEqual("reparent:" + stored.Id.Value, _binder.Events[_binder.Events.Count - 1],
                "Lighting -> Workspace is a move inside the scene: the adapter hears a re-parent, " +
                "which is where it recomputes the active flag");
            Assert.AreEqual(eventsBefore + 1, _binder.Events.Count);
        }

        [Test]
        public void D5_ReparentWithinWorkspace_KeepsTheBackingObjectAndMirrorsTheMove()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Parent = _registry.WorldRoot;
            part.Parent = _registry.WorldRoot;
            int eventsBefore = _binder.Events.Count;

            part.Parent = model;

            Assert.IsTrue(_binder.IsMaterialized(part.Id));
            Assert.AreEqual(eventsBefore + 1, _binder.Events.Count);
            Assert.AreEqual("reparent:" + part.Id.Value, _binder.Events[_binder.Events.Count - 1]);
        }

        [Test]
        public void NameChange_OnMaterializedInstance_ReachesTheBinder()
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;

            part.Name = "SpawnPad";

            CollectionAssert.Contains(_binder.Events, "rename:" + part.Id.Value);
        }

        [Test]
        public void NameChange_OutsideTheWorld_StaysSilent()
        {
            RbxInstance part = _registry.Create("Part");

            part.Name = "SpawnPad";

            CollectionAssert.DoesNotContain(_binder.Events, "rename:" + part.Id.Value);
        }

        [Test]
        public void D6_Destroy_LeavesTheWorldThenReleasesTheBacking()
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;

            part.Destroy();

            Assert.IsFalse(_binder.IsMaterialized(part.Id));
            int leaveIndex = IndexOf(_binder.Events, "leave:" + part.Id.Value);
            int destroyIndex = IndexOf(_binder.Events, "destroy:" + part.Id.Value);
            Assert.GreaterOrEqual(leaveIndex, 0);
            Assert.Greater(destroyIndex, leaveIndex, "backing must deactivate before release");
        }

        private static int IndexOf(System.Collections.Generic.IReadOnlyList<string> events,
            string entry)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] == entry)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

using CoreAI.RobloxApi.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Backing-object seam per D5 (§5.1.8 items 1–2 at registry level): unparented
    /// instances have no backing; entering the workspace subtree materializes; detaching
    /// deactivates; Destroy releases the backing object.</summary>
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
        public void D5_StorageOnlySubtrees_NeverMaterialize()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _game.GetService("ReplicatedStorage");
            Assert.IsFalse(_binder.IsMaterialized(folder.Id));
        }

        [Test]
        public void D5_ReparentWithinWorkspace_KeepsTheBackingObject()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Parent = _registry.WorldRoot;
            part.Parent = _registry.WorldRoot;
            int eventsBefore = _binder.Events.Count;

            part.Parent = model;

            Assert.IsTrue(_binder.IsMaterialized(part.Id));
            Assert.AreEqual(eventsBefore, _binder.Events.Count);
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

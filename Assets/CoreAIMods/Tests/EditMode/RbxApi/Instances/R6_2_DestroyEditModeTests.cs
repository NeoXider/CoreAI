using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Destroy semantics per R6.2/D6 at registry level (§5.1.8 item 6): parent nil and
    /// locked, recursive child destroy, record unregistered, tombstone reads (DEV-7).</summary>
    [TestFixture]
    public sealed class R6_2_DestroyEditModeTests
    {
        private InstanceRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            DataModelBootstrap.CreateGame(_registry);
        }

        [Test]
        public void R6_2_DestroySetsParentNilLocksItAndDestroysChildren()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Parent = _registry.WorldRoot;
            part.Parent = model;

            model.Destroy();

            Assert.IsTrue(model.IsDestroyed);
            Assert.IsTrue(part.IsDestroyed);
            Assert.IsNull(model.Parent);
            Assert.IsNull(part.Parent);
            Assert.IsFalse(_registry.TryGet(model.Id, out _));
            Assert.IsFalse(_registry.TryGet(part.Id, out _));

            RbxError locked = Assert.Throws<RbxError>(() => model.Parent = _registry.WorldRoot);
            Assert.AreEqual(RbxErrorCode.ParentLocked, locked.Code);
            Assert.AreEqual("The Parent property of Model is locked, use a new Instance instead",
                locked.RawMessage);
        }

        [Test]
        public void R6_2_DestroyedInstance_TombstoneReadsWorkMutationsThrow()
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = "Trap";
            part.Destroy();

            // DEV-7 tombstone: Name/ClassName/Parent stay readable at the Domain level.
            Assert.AreEqual("Trap", part.Name);
            Assert.AreEqual("Part", part.ClassName);
            Assert.IsNull(part.Parent);

            RbxError rename = Assert.Throws<RbxError>(() => part.Name = "New");
            Assert.AreEqual(RbxErrorCode.InstanceDestroyed, rename.Code);
            Assert.Throws<RbxError>(() => part.SetAttribute("X", 1));
            Assert.Throws<RbxError>(() => part.AddTag("T"));
            Assert.Throws<RbxError>(() => part.GetChildren());
            Assert.Throws<RbxError>(() => part.Clone());
        }

        [Test]
        public void R6_2_DestroyIsIdempotent()
        {
            RbxInstance part = _registry.Create("Part");
            part.Destroy();
            Assert.DoesNotThrow(() => part.Destroy());
        }

        [Test]
        public void R6_2_DestroyClearsTags()
        {
            RbxInstance part = _registry.Create("Part");
            part.AddTag("KillBrick");
            InstanceId id = part.Id;

            part.Destroy();

            Assert.AreEqual(0, _registry.Tags.GetTags(id).Count);
            Assert.AreEqual(0, _registry.Tags.GetTagged("KillBrick").Count);
        }

        [Test]
        public void ClearAllChildren_DestroysEveryChild()
        {
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance a = _registry.Create("Part");
            RbxInstance b = _registry.Create("Part");
            a.Parent = folder;
            b.Parent = folder;

            folder.ClearAllChildren();

            Assert.IsFalse(folder.IsDestroyed);
            Assert.AreEqual(0, folder.GetChildren().Count);
            Assert.IsTrue(a.IsDestroyed);
            Assert.IsTrue(b.IsDestroyed);
        }

        [Test]
        public void ParentingIntoADestroyedInstance_Throws()
        {
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance part = _registry.Create("Part");
            folder.Destroy();

            RbxError error = Assert.Throws<RbxError>(() => part.Parent = folder);
            Assert.AreEqual(RbxErrorCode.InstanceDestroyed, error.Code);
        }
    }
}

using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Destroy semantics per R6.2/D6 at registry level (§5.1.8 item 6): parent nil and
    /// locked, child destroy order, record unregistered, tombstone reads (DEV-7), and the
    /// AncestryChanged pair a descendant receives (R6.12).</summary>
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

        [Test]
        public void R6_2_Destroy_FiresDestroyingInPreorder_AndUnregistersChildrenBeforeTheirParent()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxInstance root = Named("Folder", "Root", null);
            RbxInstance a = Named("Folder", "A", root);
            RbxInstance a1 = Named("Part", "A1", a);
            RbxInstance a2 = Named("Part", "A2", a);
            RbxInstance b = Named("Part", "B", root);
            List<string> destroying = new();
            foreach (RbxInstance node in new[] { root, a, a1, a2, b })
            {
                string name = node.Name;
                node.Destroying.BindScheduler(scheduler);
                node.Destroying.Connect((Action<object[]>)(_ => destroying.Add(name)));
            }

            List<string> unregistered = new();
            _registry.Unregistered += record => unregistered.Add(record.Instance.Name);

            root.Destroy();
            scheduler.Advance(0d);

            CollectionAssert.AreEqual(new[] { "Root", "A", "A1", "A2", "B" }, destroying,
                "D6: each instance fires Destroying before any of its children is torn down");
            CollectionAssert.AreEqual(new[] { "A1", "A2", "A", "B", "Root" }, unregistered,
                "D6 step 5 runs for an instance only after all of its children are destroyed");
        }

        [Test]
        public void R6_12_Destroy_EveryAncestryChangedDeliveryToADescendantCarriesANilParent()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxInstance model = Named("Model", "Rig", _registry.WorldRoot);
            RbxInstance part = Named("Part", "Arm", model);
            List<object[]> partEvents = new();
            part.AncestryChanged.BindScheduler(scheduler);
            part.AncestryChanged.Connect((Action<object[]>)(arguments => partEvents.Add(arguments)));

            model.Destroy();
            scheduler.Advance(0d);

            Assert.AreEqual(2, partEvents.Count, "the model's detach, then the part's own detach");
            Assert.AreSame(model, partEvents[0][0],
                "the first delivery names the destroyed model, whose Parent changed");
            Assert.IsNull(partEvents[0][1],
                "the cleanup idiom 'if parent == nil' must see nil when an ancestor is destroyed");
            Assert.AreSame(part, partEvents[1][0]);
            Assert.IsNull(partEvents[1][1]);
        }

        private RbxInstance Named(string className, string name, RbxInstance parent)
        {
            RbxInstance instance = _registry.Create(className);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        private static ModScheduler CreateScheduler()
        {
            return new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
        }

        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }
    }
}

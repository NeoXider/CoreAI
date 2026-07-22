using CoreAI.Mods.Roblox.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Clone per R6.5/D8 (§5.1.8 item 5): deep copy, Archivable rules, fresh ids,
    /// attributes and tags copy, clone parent is nil.</summary>
    [TestFixture]
    public sealed class R6_5_CloneEditModeTests
    {
        private InstanceRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
        }

        [Test]
        public void R6_5_CloneDeepCopiesWithFreshIds()
        {
            RbxInstance model = _registry.Create("Model", "mod_a", OriginTag.FromMod("mod_a"));
            RbxInstance part = _registry.Create("Part", "mod_a", OriginTag.FromMod("mod_a"));
            model.Name = "Rig";
            part.Name = "Head";
            part.Parent = model;
            model.SetAttribute("Health", 100);
            model.AddTag("Spawner");

            RbxInstance copy = model.Clone();

            Assert.IsNotNull(copy);
            Assert.IsNull(copy.Parent);
            Assert.AreEqual("Rig", copy.Name);
            Assert.AreNotEqual(model.Id, copy.Id);
            Assert.AreEqual(1, copy.GetChildren().Count);
            RbxInstance copiedChild = copy.GetChildren()[0];
            Assert.AreEqual("Head", copiedChild.Name);
            Assert.AreNotEqual(part.Id, copiedChild.Id);
            Assert.AreEqual(100d, copy.GetAttribute("Health"));
            Assert.IsTrue(copy.HasTag("Spawner"));

            // The ownership ledger follows the source.
            Assert.IsTrue(_registry.TryGetRecord(copy.Id, out InstanceRecord record));
            Assert.AreEqual("mod_a", record.OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("mod_a"), record.OriginTag);
        }

        [Test]
        public void R6_5_NonArchivableRootClonesToNull()
        {
            RbxInstance part = _registry.Create("Part");
            part.Archivable = false;
            Assert.IsNull(part.Clone());
        }

        [Test]
        public void R6_5_NonArchivableChildrenAreSkipped()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance keep = _registry.Create("Part");
            RbxInstance skip = _registry.Create("Part");
            keep.Name = "Keep";
            skip.Name = "Skip";
            skip.Archivable = false;
            keep.Parent = model;
            skip.Parent = model;

            RbxInstance copy = model.Clone();

            Assert.AreEqual(1, copy.GetChildren().Count);
            Assert.AreEqual("Keep", copy.GetChildren()[0].Name);
        }
    }
}

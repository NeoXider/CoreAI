using CoreAI.RobloxApi.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Stable-id serialization (roadmap §2 world file, Q3, §3.3): capture → restore →
    /// capture is identical, ids never remap, and the allocator never re-issues restored ids.</summary>
    [TestFixture]
    public sealed class InstanceTreeSerializationEditModeTests
    {
        private static InstanceRegistry BuildSourceRegistry(out RbxDataModel game)
        {
            var registry = new InstanceRegistry();
            game = DataModelBootstrap.CreateGame(registry);

            RbxInstance model = registry.Create("Model", "mod_a", OriginTag.FromMod("mod_a"));
            model.Name = "Rig";
            model.Parent = registry.WorldRoot;
            model.SetAttribute("Health", 100);
            model.SetAttribute("Label", "boss");
            model.AddTag("Spawner");

            RbxInstance part = registry.Create("Part", null, OriginTag.FromConsole("7"));
            part.Name = "Head";
            part.Archivable = false;
            part.Parent = model;
            part.SetAttribute("Enabled", true);

            RbxInstance stored = registry.Create("Folder");
            stored.Name = "Config";
            stored.Parent = game.GetService("ReplicatedStorage");
            return registry;
        }

        [Test]
        public void CaptureRestoreCapture_IsStable()
        {
            InstanceRegistry source = BuildSourceRegistry(out RbxDataModel game);
            InstanceTreeSnapshot first = InstanceTreeSerializer.Capture(game);

            var target = new InstanceRegistry();
            var restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(first, target);
            DataModelBootstrap.AttachWorldRoot(target, restoredGame);
            InstanceTreeSnapshot second = InstanceTreeSerializer.Capture(restoredGame);

            Assert.AreEqual(first.Instances.Count, second.Instances.Count);
            for (int i = 0; i < first.Instances.Count; i++)
            {
                InstanceSnapshot a = first.Instances[i];
                InstanceSnapshot b = second.Instances[i];
                Assert.AreEqual(a.Id, b.Id, "id drift at index " + i);
                Assert.AreEqual(a.ParentId, b.ParentId);
                Assert.AreEqual(a.ClassName, b.ClassName);
                Assert.AreEqual(a.Name, b.Name);
                Assert.AreEqual(a.Archivable, b.Archivable);
                Assert.AreEqual(a.OwnerModId, b.OwnerModId);
                Assert.AreEqual(a.OriginTag, b.OriginTag);
                CollectionAssert.AreEqual(a.Tags, b.Tags);
                Assert.AreEqual(a.Attributes.Count, b.Attributes.Count);
                for (int j = 0; j < a.Attributes.Count; j++)
                {
                    Assert.AreEqual(a.Attributes[j].Name, b.Attributes[j].Name);
                    Assert.AreEqual(a.Attributes[j].Kind, b.Attributes[j].Kind);
                    Assert.AreEqual(a.Attributes[j].StringValue, b.Attributes[j].StringValue);
                    Assert.AreEqual(a.Attributes[j].NumberValue, b.Attributes[j].NumberValue);
                    Assert.AreEqual(a.Attributes[j].BoolValue, b.Attributes[j].BoolValue);
                }
            }
        }

        [Test]
        public void Restore_PreservesIdentityLedgerAndTags()
        {
            InstanceRegistry source = BuildSourceRegistry(out RbxDataModel game);
            RbxInstance sourceModel = source.WorldRoot.FindFirstChild("Rig");
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);

            var target = new InstanceRegistry();
            var restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, target);
            DataModelBootstrap.AttachWorldRoot(target, restoredGame);

            Assert.IsTrue(target.TryGet(sourceModel.Id, out RbxInstance restoredModel));
            Assert.AreEqual("Rig", restoredModel.Name);
            Assert.AreEqual(100d, restoredModel.GetAttribute("Health"));
            Assert.IsTrue(restoredModel.HasTag("Spawner"));
            Assert.IsTrue(target.TryGetRecord(restoredModel.Id, out InstanceRecord record));
            Assert.AreEqual("mod_a", record.OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("mod_a"), record.OriginTag);

            RbxInstance restoredHead = restoredModel.FindFirstChild("Head");
            Assert.IsNotNull(restoredHead);
            Assert.IsFalse(restoredHead.Archivable);
            Assert.AreEqual(true, restoredHead.GetAttribute("Enabled"));
        }

        [Test]
        public void Restore_AdvancesTheAllocatorPastRestoredIds()
        {
            InstanceRegistry source = BuildSourceRegistry(out RbxDataModel game);
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);

            ulong maxRestored = 0UL;
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id > maxRestored)
                {
                    maxRestored = node.Id;
                }
            }

            var target = new InstanceRegistry();
            InstanceTreeSerializer.Restore(snapshot, target);
            RbxInstance fresh = target.Create("Part");
            Assert.Greater(fresh.Id.Value, maxRestored);
        }
    }
}

using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Stable-id serialization (roadmap §2 world file, Q3, §3.3): capture → restore →
    /// capture is identical, ids never remap, and the allocator never re-issues restored ids.</summary>
    [TestFixture]
    public sealed class InstanceTreeSerializationEditModeTests
    {
        private static InstanceRegistry BuildSourceRegistry(out RbxDataModel game)
        {
            InstanceRegistry registry = new();
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

            InstanceRegistry target = new();
            RbxDataModel restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(first, target);
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

            InstanceRegistry target = new();
            RbxDataModel restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, target);
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
        public void DatatypeAttributes_SurviveCaptureRestoreWithStableStringRoundTrip()
        {
            InstanceRegistry source = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxInstance part = source.Create("Part");
            part.Name = "Node";
            part.Parent = source.WorldRoot;
            part.SetAttribute("Spawn", new RbxVector3(1.5f, -2f, 3.25f));
            part.SetAttribute("Screen", new RbxVector2(10f, 20f));
            part.SetAttribute("Tint", RbxColor3.FromRGB(255f, 128f, 0f));
            part.SetAttribute("Pad", new RbxUDim(0.5f, 12));

            InstanceTreeSnapshot first = InstanceTreeSerializer.Capture(game);

            InstanceRegistry target = new();
            RbxDataModel restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(first, target);
            DataModelBootstrap.AttachWorldRoot(target, restoredGame);

            Assert.IsTrue(target.TryGet(part.Id, out RbxInstance restored));
            Assert.AreEqual(new RbxVector3(1.5f, -2f, 3.25f), restored.GetAttribute("Spawn"));
            Assert.AreEqual(new RbxVector2(10f, 20f), restored.GetAttribute("Screen"));
            Assert.AreEqual(RbxColor3.FromRGB(255f, 128f, 0f), restored.GetAttribute("Tint"));
            Assert.AreEqual(new RbxUDim(0.5f, 12), restored.GetAttribute("Pad"));

            // WHY: capture→restore→capture must be byte-identical, including the datatype string codec.
            InstanceTreeSnapshot second = InstanceTreeSerializer.Capture(restoredGame);
            Assert.AreEqual(first.Instances.Count, second.Instances.Count);
            for (int i = 0; i < first.Instances.Count; i++)
            {
                Assert.AreEqual(first.Instances[i].Attributes.Count, second.Instances[i].Attributes.Count);
                for (int j = 0; j < first.Instances[i].Attributes.Count; j++)
                {
                    AttributeSnapshot a = first.Instances[i].Attributes[j];
                    AttributeSnapshot b = second.Instances[i].Attributes[j];
                    Assert.AreEqual(a.Name, b.Name);
                    Assert.AreEqual(a.Kind, b.Kind);
                    Assert.AreEqual(a.StringValue, b.StringValue);
                }
            }
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

            InstanceRegistry target = new();
            InstanceTreeSerializer.Restore(snapshot, target);
            RbxInstance fresh = target.Create("Part");
            Assert.Greater(fresh.Id.Value, maxRestored);
        }
    }
}

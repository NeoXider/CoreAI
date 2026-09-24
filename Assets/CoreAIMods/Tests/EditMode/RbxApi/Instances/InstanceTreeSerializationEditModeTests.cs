using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
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
        public void AnUnadmittedPlayer_RoundTripsWithNoIdentityPayload_WhileAnAdmittedOneKeepsIts()
        {
            // WHY both halves in one test: the rule is a pair. Identity exists only once the Players
            // service admits a connection, so a bare Instance.new("Player") has none to carry and must
            // still round-trip as the plain node it is — the tree serializer is the general save/load
            // path, not only the replication capture. A player that WAS admitted must come back whole,
            // otherwise omitting the payload would quietly become a way to lose identity.
            InstanceRegistry registry = new();
            DataModelBootstrap.CreateGame(registry);

            RbxInstance bare = registry.Create("Player");
            bare.Name = "Unadmitted";
            InstanceTreeSnapshot bareSnapshot = InstanceTreeSerializer.Capture(bare);
            Assert.IsNull(bareSnapshot.Instances[0].Player,
                "an un-admitted Player has no identity to capture");

            InstanceRegistry bareTarget = new(binder: new InMemoryInstanceBackingBinder());
            RbxPlayer restoredBare = (RbxPlayer)InstanceTreeSerializer.Restore(bareSnapshot, bareTarget);
            Assert.AreEqual("Unadmitted", restoredBare.Name);
            Assert.AreEqual(0L, restoredBare.UserId, "nothing may be invented for it on the way back");

            RbxPlayer admitted = (RbxPlayer)registry.Create("Player");
            admitted.Initialize("actor-7", 4242L, "Admitted", "Admitted The Brave");
            InstanceTreeSnapshot admittedSnapshot = InstanceTreeSerializer.Capture(admitted);
            Assert.IsNotNull(admittedSnapshot.Instances[0].Player);

            InstanceRegistry admittedTarget = new(binder: new InMemoryInstanceBackingBinder());
            RbxPlayer restored =
                (RbxPlayer)InstanceTreeSerializer.Restore(admittedSnapshot, admittedTarget);
            Assert.AreEqual("actor-7", restored.NetworkActorId);
            Assert.AreEqual(4242L, restored.UserId);
            Assert.AreEqual("Admitted The Brave", restored.DisplayName);
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

            Assert.AreEqual(first.WorldAclVersion, second.WorldAclVersion);
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
                Assert.AreEqual(a.OwnerActorId, b.OwnerActorId);
                Assert.AreEqual(a.AccessScope, b.AccessScope);
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
        public void Restore_PreservesWorldAclOwnerAndExplicitScopeOverride()
        {
            InstanceRegistry source = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxInstance owned = source.Create(
                "Folder", "mod-a", OriginTag.FromMod("mod-a"),
                ownerActorId: "actor-a");
            owned.Name = "Owned";
            owned.Parent = source.WorldRoot;
            RbxInstance protectedOverride = source.Create(
                "Folder", "mod-a", OriginTag.FromMod("mod-a"),
                ownerActorId: "actor-a", accessScope: InstanceAccessScope.HostProtected);
            protectedOverride.Name = "ProtectedOverride";
            protectedOverride.Parent = source.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);
            InstanceRegistry target = new();
            RbxDataModel restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, target);
            DataModelBootstrap.AttachWorldRoot(target, restoredGame);

            Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, target.WorldAclVersion);
            RbxInstance restoredOwned = target.WorldRoot.FindFirstChild("Owned");
            Assert.IsTrue(target.TryGetRecord(restoredOwned.Id, out InstanceRecord ownedRecord));
            Assert.AreEqual("actor-a", ownedRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, ownedRecord.AccessScope);
            RbxInstance restoredProtected = target.WorldRoot.FindFirstChild("ProtectedOverride");
            Assert.IsTrue(target.TryGetRecord(
                restoredProtected.Id, out InstanceRecord protectedRecord));
            Assert.AreEqual("actor-a", protectedRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.HostProtected, protectedRecord.AccessScope);
        }

        [Test]
        public void Restore_LegacySnapshotWithoutAclFieldsRemainsLegacy()
        {
            InstanceTreeSnapshot snapshot = new();
            snapshot.Instances.Add(new InstanceSnapshot
            {
                Id = 1UL,
                ClassName = "Folder",
                Name = "Legacy",
                Archivable = true
            });

            InstanceRegistry target = new();
            RbxInstance restored = InstanceTreeSerializer.Restore(snapshot, target);

            Assert.IsNull(target.WorldAclVersion);
            Assert.IsTrue(target.TryGetRecord(restored.Id, out InstanceRecord record));
            Assert.IsNull(record.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.SharedWritable, record.AccessScope);
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
        public void Validate_MalformedClickDetectorDistance_IsBadArgumentNotAnEscapedParseException()
        {
            foreach (string malformed in new[] { "abc", null, "" })
            {
                InstanceTreeSnapshot snapshot = SingleNodeSnapshot("ClickDetector");
                snapshot.Instances[0].ClickDetector = new ClickDetectorSnapshot
                {
                    MaxActivationDistance = malformed
                };

                RbxError error = Assert.Throws<RbxError>(
                    () => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()),
                    "MaxActivationDistance '" + malformed + "' must fail as a named snapshot error");
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
                StringAssert.Contains("MaxActivationDistance", error.RawMessage);
            }
        }

        [Test]
        public void Validate_MalformedMaterialVariantStudsPerTile_IsBadArgumentNotAnEscapedParseException()
        {
            foreach (string malformed in new[] { "abc", null, "" })
            {
                InstanceTreeSnapshot snapshot = SingleNodeSnapshot("MaterialVariant");
                snapshot.Instances[0].MaterialVariant = new MaterialVariantSnapshot
                {
                    BaseMaterial = "Plastic",
                    BaseMaterialValue = 256,
                    ColorMap = "",
                    NormalMap = "",
                    RoughnessMap = "",
                    MetalnessMap = "",
                    StudsPerTile = malformed
                };

                RbxError error = Assert.Throws<RbxError>(
                    () => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()),
                    "StudsPerTile '" + malformed + "' must fail as a named snapshot error");
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
                StringAssert.Contains("StudsPerTile", error.RawMessage);
            }
        }

        [Test]
        public void Validate_MalformedDatatypeComponent_IsBadArgumentNotAnEscapedParseException()
        {
            foreach (string malformed in new[] { "1,abc,3", "1,,3" })
            {
                InstanceTreeSnapshot snapshot = SingleNodeSnapshot("Vector3Value");
                snapshot.Instances[0].Value = new ValueSnapshot { StringValue = malformed };

                RbxError error = Assert.Throws<RbxError>(
                    () => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()),
                    "Vector3Value '" + malformed + "' must fail as a named snapshot error");
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
                StringAssert.Contains(malformed, error.RawMessage);
            }
        }

        [Test]
        public void Validate_WellFormedSpecializedState_StillPasses()
        {
            InstanceTreeSnapshot detector = SingleNodeSnapshot("ClickDetector");
            detector.Instances[0].ClickDetector = new ClickDetectorSnapshot
            {
                MaxActivationDistance = "32.5"
            };
            InstanceTreeSnapshot vector = SingleNodeSnapshot("Vector3Value");
            vector.Instances[0].Value = new ValueSnapshot { StringValue = "1,-2.5,3E-2" };

            Assert.DoesNotThrow(() => InstanceTreeSerializer.Validate(detector, new InstanceRegistry()));
            Assert.DoesNotThrow(() => InstanceTreeSerializer.Validate(vector, new InstanceRegistry()));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void ReplaceNonFiniteValues_NonFiniteNumberValue_SnapshotHoldsZeroWhileLiveValueKeepsIt(
            double nonFinite)
        {
            InstanceRegistry source = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxNumberValue score = (RbxNumberValue)source.Create("NumberValue");
            score.Name = "Score";
            score.Parent = source.WorldRoot;
            score.Value = nonFinite;
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);
            Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()),
                "precondition: the world format rejects a raw non-finite NumberValue");

            List<string> replaced = new();
            InstanceTreeSerializer.ReplaceNonFiniteValues(
                snapshot, (instanceId, member) => replaced.Add(instanceId + ":" + member));

            CollectionAssert.AreEqual(new[] { score.Id.Value + ":Value" }, replaced);
            Assert.AreEqual(
                CaptureFresh("NumberValue").Value.StringValue,
                FindSnapshotNode(snapshot, score.Id).Value.StringValue,
                "the snapshot must hold exactly what a fresh NumberValue captures");
            Assert.AreEqual("0", FindSnapshotNode(snapshot, score.Id).Value.StringValue);
            Assert.DoesNotThrow(() => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()));
            Assert.IsTrue(nonFinite.Equals(score.Value),
                "the live NumberValue must keep the value the script wrote");

            InstanceRegistry target = new();
            InstanceTreeSerializer.Restore(snapshot, target);
            Assert.IsTrue(target.TryGet(score.Id, out RbxInstance restored));
            Assert.AreEqual(0d, ((RbxNumberValue)restored).Value);
        }

        [Test]
        public void ReplaceNonFiniteValues_EveryScriptReachableTreeMember_ProjectsItsDefaultAndSparesFiniteTwins()
        {
            InstanceRegistry source = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxModel holder = (RbxModel)source.Create("Model");
            holder.Name = "Holder";
            holder.Parent = source.WorldRoot;
            RbxCFrame nonFinitePivot = RbxCFrame.FromPosition(float.NaN, 0f, 0f);
            holder.SetWorldPivot(in nonFinitePivot);
            holder.SetAttribute("Speed", double.NaN);
            holder.SetAttribute("Spawn", new RbxVector3(float.PositiveInfinity, 0f, 0f));
            holder.SetAttribute("Screen", new RbxVector2(0f, float.NaN));
            holder.SetAttribute("Tint", new RbxColor3(float.NegativeInfinity, 0f, 0f));
            holder.SetAttribute("Pad", new RbxUDim(float.NaN, 3));
            holder.SetAttribute("Kept", 2.5d);
            holder.SetAttribute("Label", "boss");
            RbxVector3Value vector = (RbxVector3Value)CreateChild(source, "Vector3Value", holder);
            vector.Value = new RbxVector3(float.NaN, 1f, 2f);
            RbxCFrameValue cframe = (RbxCFrameValue)CreateChild(source, "CFrameValue", holder);
            cframe.Value = RbxCFrame.FromPosition(0f, float.PositiveInfinity, 0f);
            RbxColor3Value color = (RbxColor3Value)CreateChild(source, "Color3Value", holder);
            color.Value = new RbxColor3(0.5f, float.NaN, 0.5f);
            RbxClickDetector detector = (RbxClickDetector)CreateChild(source, "ClickDetector", holder);
            detector.MaxActivationDistance = double.PositiveInfinity;
            RbxMaterialVariant variant = (RbxMaterialVariant)CreateChild(source, "MaterialVariant", holder);
            variant.StudsPerTile = float.NaN;
            RbxVector3Value finiteVector = (RbxVector3Value)CreateChild(source, "Vector3Value", holder);
            finiteVector.Value = new RbxVector3(1f, 2f, 3f);
            RbxNumberValue finiteNumber = (RbxNumberValue)CreateChild(source, "NumberValue", holder);
            finiteNumber.Value = 4.5d;
            RbxClickDetector finiteDetector = (RbxClickDetector)CreateChild(source, "ClickDetector", holder);
            finiteDetector.MaxActivationDistance = 12.5d;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);
            string finiteVectorBefore = FindSnapshotNode(snapshot, finiteVector.Id).Value.StringValue;
            List<string> replaced = new();
            InstanceTreeSerializer.ReplaceNonFiniteValues(
                snapshot, (instanceId, member) => replaced.Add(instanceId + ":" + member));

            ulong holderId = holder.Id.Value;
            CollectionAssert.AreEquivalent(
                new[]
                {
                    holderId + ":WorldPivot",
                    holderId + ":Attributes.Speed",
                    holderId + ":Attributes.Spawn",
                    holderId + ":Attributes.Screen",
                    holderId + ":Attributes.Tint",
                    holderId + ":Attributes.Pad",
                    vector.Id.Value + ":Value",
                    cframe.Id.Value + ":Value",
                    color.Id.Value + ":Value",
                    detector.Id.Value + ":MaxActivationDistance",
                    variant.Id.Value + ":StudsPerTile"
                },
                replaced,
                "every non-finite member is reported exactly once and no finite one is");

            InstanceSnapshot holderNode = FindSnapshotNode(snapshot, holder.Id);
            Assert.IsFalse(holderNode.Model.HasStoredWorldPivot);
            Assert.IsNull(holderNode.Model.StoredWorldPivot);
            List<string> keptAttributes = new();
            foreach (AttributeSnapshot attribute in holderNode.Attributes)
            {
                keptAttributes.Add(attribute.Name);
            }

            CollectionAssert.AreEqual(new[] { "Kept", "Label" }, keptAttributes,
                "a non-finite attribute is omitted and every finite one is kept in sorted order");
            Assert.AreEqual(CaptureFresh("Vector3Value").Value.StringValue,
                FindSnapshotNode(snapshot, vector.Id).Value.StringValue);
            Assert.AreEqual(CaptureFresh("CFrameValue").Value.StringValue,
                FindSnapshotNode(snapshot, cframe.Id).Value.StringValue);
            Assert.AreEqual(CaptureFresh("Color3Value").Value.StringValue,
                FindSnapshotNode(snapshot, color.Id).Value.StringValue);
            Assert.AreEqual(CaptureFresh("ClickDetector").ClickDetector.MaxActivationDistance,
                FindSnapshotNode(snapshot, detector.Id).ClickDetector.MaxActivationDistance);
            Assert.AreEqual(CaptureFresh("MaterialVariant").MaterialVariant.StudsPerTile,
                FindSnapshotNode(snapshot, variant.Id).MaterialVariant.StudsPerTile);
            Assert.AreEqual(finiteVectorBefore, FindSnapshotNode(snapshot, finiteVector.Id).Value.StringValue);
            Assert.AreEqual("4.5", FindSnapshotNode(snapshot, finiteNumber.Id).Value.StringValue);
            Assert.AreEqual("12.5",
                FindSnapshotNode(snapshot, finiteDetector.Id).ClickDetector.MaxActivationDistance);
            Assert.DoesNotThrow(() => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()));

            Assert.IsTrue(holder.HasStoredWorldPivot, "the live Model keeps its stored pivot");
            Assert.IsTrue(float.IsNaN(holder.StoredWorldPivot.GetComponents()[0]));
            Assert.IsTrue(double.IsNaN((double)holder.GetAttribute("Speed")));
            Assert.IsTrue(float.IsNaN(((RbxUDim)holder.GetAttribute("Pad")).Scale));
            Assert.IsTrue(float.IsNaN(vector.Value.X));
            Assert.IsTrue(float.IsInfinity(cframe.Value.GetComponents()[1]));
            Assert.IsTrue(float.IsNaN(color.Value.G));
            Assert.IsTrue(double.IsPositiveInfinity(detector.MaxActivationDistance));
            Assert.IsTrue(float.IsNaN(variant.StudsPerTile));

            InstanceRegistry target = new();
            InstanceTreeSerializer.Restore(snapshot, target);
            Assert.IsTrue(target.TryGet(holder.Id, out RbxInstance restoredHolder));
            Assert.IsFalse(((RbxModel)restoredHolder).HasStoredWorldPivot);
            Assert.IsNull(restoredHolder.GetAttribute("Speed"));
            Assert.IsNull(restoredHolder.GetAttribute("Spawn"));
            Assert.IsNull(restoredHolder.GetAttribute("Screen"));
            Assert.IsNull(restoredHolder.GetAttribute("Tint"));
            Assert.IsNull(restoredHolder.GetAttribute("Pad"));
            Assert.AreEqual(2.5d, restoredHolder.GetAttribute("Kept"));
            Assert.AreEqual("boss", restoredHolder.GetAttribute("Label"));
            Assert.IsTrue(target.TryGet(vector.Id, out RbxInstance restoredVector));
            Assert.AreEqual(RbxVector3.Zero, ((RbxVector3Value)restoredVector).Value);
            Assert.IsTrue(target.TryGet(cframe.Id, out RbxInstance restoredCFrame));
            Assert.AreEqual(RbxCFrame.Identity, ((RbxCFrameValue)restoredCFrame).Value);
            Assert.IsTrue(target.TryGet(color.Id, out RbxInstance restoredColor));
            Assert.AreEqual(new RbxColor3(0f, 0f, 0f), ((RbxColor3Value)restoredColor).Value);
            Assert.IsTrue(target.TryGet(detector.Id, out RbxInstance restoredDetector));
            Assert.AreEqual(32d, ((RbxClickDetector)restoredDetector).MaxActivationDistance);
            Assert.IsTrue(target.TryGet(variant.Id, out RbxInstance restoredVariant));
            Assert.AreEqual(1f, ((RbxMaterialVariant)restoredVariant).StudsPerTile);
        }

        [TestCase("NumberValue", "NaN")]
        [TestCase("NumberValue", "-Infinity")]
        [TestCase("Vector3Value", "1,NaN,3")]
        [TestCase("CFrameValue", "0,0,0,1,0,0,0,1,0,0,0,Infinity")]
        [TestCase("Color3Value", "Infinity,0,0")]
        public void Validate_NonFiniteValueOutsideTheCaptureProjection_IsStillRejected(
            string className, string serialized)
        {
            InstanceTreeSnapshot snapshot = SingleNodeSnapshot(className);
            snapshot.Instances[0].Value = new ValueSnapshot { StringValue = serialized };

            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()),
                className + " '" + serialized + "' must still be refused: only capture projects defaults");
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("non-finite", error.RawMessage);
        }

        private static RbxInstance CreateChild(InstanceRegistry registry, string className, RbxInstance parent)
        {
            RbxInstance child = registry.Create(className);
            child.Parent = parent;
            return child;
        }

        /// <summary>Captures a freshly created, never-written instance: the member defaults the projection must reproduce.</summary>
        private static InstanceSnapshot CaptureFresh(string className)
        {
            InstanceRegistry registry = new();
            return InstanceTreeSerializer.CaptureNode(registry.Create(className));
        }

        private static InstanceSnapshot FindSnapshotNode(InstanceTreeSnapshot snapshot, InstanceId id)
        {
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == id.Value)
                {
                    return node;
                }
            }

            Assert.Fail("Missing snapshot node " + id.Value + ".");
            return null;
        }

        private static InstanceTreeSnapshot SingleNodeSnapshot(string className)
        {
            InstanceTreeSnapshot snapshot = new();
            snapshot.Instances.Add(new InstanceSnapshot
            {
                Id = 1UL,
                ClassName = className,
                Name = className,
                Archivable = true
            });
            return snapshot;
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

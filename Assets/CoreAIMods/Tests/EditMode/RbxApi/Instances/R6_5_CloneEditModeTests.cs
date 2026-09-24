using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Clone per R6.5/D8 (§5.1.8 item 5): deep copy, Archivable rules, fresh ids,
    /// attributes and tags copy, clone parent is nil, (finding 3) BasePart backing state
    /// copies through the IInstanceBackingBinder.CopyBackingState seam, reference properties
    /// follow the mirror's clone rule, and the DataModel and services are never copied.</summary>
    [TestFixture]
    public sealed class R6_5_CloneEditModeTests
    {
        private InstanceRegistry _registry;
        private FakePartStateBinder _binder;

        [SetUp]
        public void SetUp()
        {
            _binder = new FakePartStateBinder();
            _registry = new InstanceRegistry(binder: _binder);
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

        [Test]
        public void R6_5_ClonesBasePartBackingState()
        {
            RbxInstance part = _registry.Create("Part");
            PartProperties properties = PartProperties.CreateDefault();
            properties.Shape = RbxPartShape.Ball;
            properties.CFrame = RbxCFrame.FromPosition(new RbxVector3(1f, 2f, 3f));
            properties.Size = new RbxVector3(9f, 8f, 7f);
            properties.Color = RbxColor3.FromRGB(10f, 20f, 30f);
            properties.Anchored = true;
            _binder.SetPartProperties(part.Id, in properties);

            RbxInstance copy = part.Clone();

            Assert.IsTrue(_binder.TryGetPartProperties(copy.Id, out PartProperties copied));
            Assert.AreEqual(properties.Size, copied.Size);
            Assert.AreEqual(properties.CFrame, copied.CFrame);
            Assert.AreEqual(properties.Color, copied.Color);
            Assert.IsTrue(copied.Anchored);
        }

        [Test]
        public void R6_5_NonArchivableSiblingDoesNotMisalignBackingStateCopy()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance first = _registry.Create("Part");
            RbxInstance skipped = _registry.Create("Part");
            RbxInstance second = _registry.Create("Part");
            first.Name = "First";
            skipped.Name = "Skip";
            second.Name = "Second";
            skipped.Archivable = false;
            first.Parent = model;
            skipped.Parent = model;
            second.Parent = model;

            _binder.SetPartProperties(first.Id, MakeProperties(1f));
            _binder.SetPartProperties(skipped.Id, MakeProperties(2f));
            _binder.SetPartProperties(second.Id, MakeProperties(3f));

            RbxInstance copy = model.Clone();

            Assert.AreEqual(2, copy.GetChildren().Count);
            RbxInstance copiedFirst = copy.GetChildren()[0];
            RbxInstance copiedSecond = copy.GetChildren()[1];
            Assert.AreEqual("First", copiedFirst.Name);
            Assert.AreEqual("Second", copiedSecond.Name);

            // A stale index-based walk would pair "Second" (source index 2) with the copy at
            // index 1 (which is really "Second"'s own copy, since "Skip" made no copy) only by
            // coincidence; asserting the exact stored Size catches a shift either direction.
            Assert.IsTrue(_binder.TryGetPartProperties(copiedFirst.Id, out PartProperties firstCopy));
            Assert.AreEqual(1f, firstCopy.Size.X);
            Assert.IsTrue(_binder.TryGetPartProperties(copiedSecond.Id, out PartProperties secondCopy));
            Assert.AreEqual(3f, secondCopy.Size.X);
        }

        /// <summary>MVP1 finding 3 was closed against FakePartStateBinder above, not against the
        /// binder Unity actually ships — this exercises InstanceGameObjectBinder.CopyBackingState
        /// directly, on a part materialized into the world before it is cloned.</summary>
        [Test]
        public void R6_5_ClonesBasePartBackingState_ThroughRealBinder()
        {
            GameObject root = new("RealBinderCloneTestRoot");
            InstanceGameObjectBinder binder = new(root.transform);
            InstanceRegistry registry = new(null, binder);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            try
            {
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                binder.SetShape(part.Id, RbxPartShape.Ball);
                binder.SetSize(part.Id, new RbxVector3(9f, 8f, 7f));
                binder.SetColor(part.Id, RbxColor3.FromRGB(10f, 20f, 30f));
                binder.SetAnchored(part.Id, true);

                RbxInstance copy = part.Clone();

                Assert.IsTrue(binder.TryGetPartProperties(copy.Id, out PartProperties copied));
                Assert.AreEqual(new RbxVector3(9f, 8f, 7f), copied.Size);
                Assert.AreEqual(RbxColor3.FromRGB(10f, 20f, 30f), copied.Color);
                Assert.AreEqual(RbxPartShape.Ball, copied.Shape);
                Assert.IsTrue(copied.Anchored);
            }
            finally
            {
                game.Destroy();
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static PartProperties MakeProperties(float sizeX)
        {
            PartProperties properties = PartProperties.CreateDefault();
            properties.Size = new RbxVector3(sizeX, 1f, 1f);
            return properties;
        }

        [Test]
        public void Clone_BackingsFailureDestroysPartialSubtree()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;
            _binder.FailForSource = part.Id;

            Assert.Throws<InvalidOperationException>(() => model.Clone());

            Assert.AreEqual(2, _registry.GetLiveInstances().Count);
            Assert.IsFalse(model.IsDestroyed);
            Assert.IsFalse(part.IsDestroyed);
        }

        [Test]
        public void R6_5_CloneRemapsPrimaryPartToTheCopiedPart()
        {
            RbxModel model = (RbxModel)_registry.Create("Model");
            RbxInstance root = _registry.Create("Part");
            RbxInstance other = _registry.Create("Part");
            root.Name = "Root";
            other.Name = "Other";
            root.Parent = model;
            other.Parent = model;
            model.SetPrimaryPart(root);

            RbxModel copy = (RbxModel)model.Clone();

            Assert.IsNotNull(copy.PrimaryPart,
                "Instance.yaml Clone: a reference to an instance that was also cloned points at its copy");
            Assert.AreSame(copy.FindFirstChild("Root"), copy.PrimaryPart);
            Assert.AreSame(root, model.PrimaryPart, "the source keeps its own PrimaryPart");
        }

        [Test]
        public void R6_5_PrimaryPartThatWasNotCloned_KeepsTheSameValue()
        {
            RbxModel model = (RbxModel)_registry.Create("Model");
            RbxInstance root = _registry.Create("Part");
            root.Parent = model;
            root.Archivable = false;
            model.SetPrimaryPart(root);

            RbxModel copy = (RbxModel)model.Clone();

            Assert.AreEqual(0, copy.GetChildren().Count);
            Assert.AreSame(root, copy.PrimaryPart,
                "Instance.yaml Clone: a reference to an instance that was not cloned keeps the same value");
        }

        [Test]
        public void R6_5_CloneKeepsAnExplicitWorldPivot()
        {
            RbxModel model = (RbxModel)_registry.Create("Model");
            RbxCFrame pivot = RbxCFrame.FromPosition(new RbxVector3(1f, 2f, 3f));
            model.SetWorldPivot(pivot);

            RbxModel copy = (RbxModel)model.Clone();

            Assert.IsTrue(copy.HasStoredWorldPivot,
                "a clone that forgets the stored pivot pivots around its bounding box instead");
            Assert.AreEqual(pivot, copy.StoredWorldPivot);
        }

        [Test]
        public void R6_5_CloneOfAModelWithoutAStoredPivotOrPrimaryPart_StaysUnset()
        {
            RbxModel model = (RbxModel)_registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;

            RbxModel copy = (RbxModel)model.Clone();

            Assert.IsFalse(copy.HasStoredWorldPivot);
            Assert.IsNull(copy.PrimaryPart);
        }

        [Test]
        public void R6_5_CloneRemapsObjectValuesInsideTheClonedSubtree_AndKeepsOutsideReferences()
        {
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance inner = _registry.Create("Part");
            RbxInstance outside = _registry.Create("Part");
            inner.Name = "Inner";
            inner.Parent = folder;
            RbxObjectValue toInner = NamedObjectValue("ToInner", folder, inner);
            RbxObjectValue toRoot = NamedObjectValue("ToRoot", folder, folder);
            NamedObjectValue("ToOutside", folder, outside);
            NamedObjectValue("Empty", folder, null);

            RbxInstance copy = folder.Clone();

            Assert.AreSame(copy.FindFirstChild("Inner"), ObjectValueTarget(copy, "ToInner"),
                "Instance.yaml Clone: a reference to an instance that was also cloned points at its copy");
            Assert.AreSame(copy, ObjectValueTarget(copy, "ToRoot"));
            Assert.AreSame(outside, ObjectValueTarget(copy, "ToOutside"),
                "a reference to an instance that was not cloned keeps the same value");
            Assert.IsNull(ObjectValueTarget(copy, "Empty"));
            Assert.AreSame(inner, toInner.Value, "the source keeps pointing at its own target");
            Assert.AreSame(folder, toRoot.Value);
        }

        [Test]
        public void R6_5_ObjectValuePointingAtItself_ClonePointsAtTheCopy()
        {
            RbxObjectValue value = (RbxObjectValue)_registry.Create("ObjectValue");
            value.Value = value;

            RbxObjectValue copy = (RbxObjectValue)value.Clone();

            Assert.AreSame(copy, copy.Value);
            Assert.AreSame(value, value.Value);
        }

        [Test]
        public void Clone_OfTheDataModelOrAService_ReturnsNull_AndCreatesNoRecords()
        {
            RbxDataModel game = DataModelBootstrap.CreateGame(_registry);
            int recordsBefore = _registry.Count;

            Assert.IsNull(game.Clone(), "a second DataModel would duplicate every service");
            Assert.IsNull(_registry.WorldRoot.Clone());
            Assert.IsNull(game.FindFirstChild("Lighting").Clone("mod_a", OriginTag.FromMod("mod_a")));
            Assert.AreEqual(recordsBefore, _registry.Count);
        }

        [Test]
        public void Clone_SkipsAServiceParentedBelowTheClonedRoot()
        {
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance service = _registry.Create("ReplicatedStorage");
            RbxInstance part = _registry.Create("Part");
            service.Parent = folder;
            part.Parent = folder;

            RbxInstance copy = folder.Clone();

            Assert.AreEqual(1, copy.GetChildren().Count, "a service below the cloned root is not copied");
            Assert.AreEqual("Part", copy.GetChildren()[0].ClassName);
        }

        [Test]
        public void Clone_CreatesCopiesInPreorder_AndParentsEachUnderItsOwnParentsCopy()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance a = _registry.Create("Folder");
            RbxInstance a1 = _registry.Create("Part");
            RbxInstance b = _registry.Create("Part");
            a.Name = "A";
            a1.Name = "A1";
            b.Name = "B";
            a.Parent = model;
            a1.Parent = a;
            b.Parent = model;

            RbxInstance copy = model.Clone();

            RbxInstance copyA = copy.FindFirstChild("A");
            RbxInstance copyA1 = copyA.FindFirstChild("A1");
            RbxInstance copyB = copy.FindFirstChild("B");
            Assert.AreSame(copyA, copy.GetChildren()[0], "sibling order survives the copy");
            Assert.AreSame(copyB, copy.GetChildren()[1]);
            Assert.Less(copy.Id.Value, copyA.Id.Value, "copies are created in preorder");
            Assert.Less(copyA.Id.Value, copyA1.Id.Value);
            Assert.Less(copyA1.Id.Value, copyB.Id.Value);
        }

        [Test]
        public void Clone_FailureDeepInTheSubtree_DestroysEveryCopyMadeSoFar()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance first = _registry.Create("Part");
            RbxInstance failing = _registry.Create("Part");
            RbxInstance after = _registry.Create("Part");
            folder.Parent = model;
            first.Parent = folder;
            failing.Parent = folder;
            after.Parent = model;
            int liveBefore = _registry.GetLiveInstances().Count;
            _binder.FailForSource = failing.Id;

            Assert.Throws<InvalidOperationException>(() => model.Clone());

            Assert.AreEqual(liveBefore, _registry.GetLiveInstances().Count,
                "the copies of Model, Folder, First and the failing part itself are all destroyed");
            Assert.IsFalse(model.IsDestroyed);
            Assert.IsFalse(failing.IsDestroyed);
            Assert.AreEqual(2, model.GetChildren().Count);
        }

        private RbxObjectValue NamedObjectValue(string name, RbxInstance parent, RbxInstance target)
        {
            RbxObjectValue value = (RbxObjectValue)_registry.Create("ObjectValue");
            value.Name = name;
            value.Value = target;
            value.Parent = parent;
            return value;
        }

        private static RbxInstance ObjectValueTarget(RbxInstance root, string name)
        {
            return ((RbxObjectValue)root.FindFirstChild(name)).Value;
        }

        /// <summary>Test double standing in for InstanceGameObjectBinder: stores BasePart state
        /// the same way (an engine-free IPartPropertySink) and implements the clone-completion
        /// seam identically, without pulling in Unity.</summary>
        private sealed class FakePartStateBinder : IInstanceBackingBinder
        {
            private readonly InMemoryPartPropertySink _sink = new();
            public InstanceId? FailForSource { get; set; }

            public void SetPartProperties(InstanceId id, in PartProperties properties)
            {
                _sink.SetPartProperties(id, in properties);
            }

            public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
            {
                return _sink.TryGetPartProperties(id, out properties);
            }

            public void OnEnteredWorld(InstanceRecord record)
            {
            }

            public void OnLeftWorld(InstanceRecord record)
            {
            }

            public void OnDestroyed(InstanceRecord record)
            {
            }

            public void OnReparented(InstanceRecord record)
            {
            }

            public void OnNameChanged(InstanceRecord record)
            {
            }

            public void CopyBackingState(InstanceId sourceId, InstanceId destinationId)
            {
                if (FailForSource.HasValue && FailForSource.Value.Equals(sourceId))
                {
                    throw new InvalidOperationException("Injected backing-state copy failure.");
                }

                if (_sink.TryGetPartProperties(sourceId, out PartProperties properties))
                {
                    _sink.SetPartProperties(destinationId, in properties);
                }
            }
        }
    }
}

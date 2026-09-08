using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Clone per R6.5/D8 (§5.1.8 item 5): deep copy, Archivable rules, fresh ids,
    /// attributes and tags copy, clone parent is nil, and (finding 3) BasePart backing state
    /// copies through the IInstanceBackingBinder.CopyBackingState seam.</summary>
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

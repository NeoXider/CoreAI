using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Navigation and hierarchy rules (§5.1.8 items 3 and 4, R6.10) plus GetFullName,
    /// the Name and depth caps, and the hierarchy and property signals (R6.11, R6.12).</summary>
    [TestFixture]
    public sealed class InstanceHierarchyEditModeTests
    {
        private const int SmallStackBytes = 256 * 1024;

        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [Test]
        public void GetChildren_PreservesInsertionOrder()
        {
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance a = _registry.Create("Part");
            RbxInstance b = _registry.Create("Part");
            RbxInstance c = _registry.Create("Model");
            a.Name = "A";
            b.Name = "B";
            c.Name = "C";
            a.Parent = folder;
            b.Parent = folder;
            c.Parent = folder;

            IReadOnlyList<RbxInstance> children = folder.GetChildren();
            Assert.AreEqual(3, children.Count);
            Assert.AreSame(a, children[0]);
            Assert.AreSame(b, children[1]);
            Assert.AreSame(c, children[2]);
        }

        [Test]
        public void GetDescendants_IsPreorder()
        {
            RbxInstance root = _registry.Create("Folder");
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            RbxInstance sibling = _registry.Create("Folder");
            model.Parent = root;
            part.Parent = model;
            sibling.Parent = root;

            IReadOnlyList<RbxInstance> descendants = root.GetDescendants();
            Assert.AreEqual(3, descendants.Count);
            Assert.AreSame(model, descendants[0]);
            Assert.AreSame(part, descendants[1]);
            Assert.AreSame(sibling, descendants[2]);
        }

        [Test]
        public void FindFirstChild_DirectAndRecursive()
        {
            RbxInstance root = _registry.Create("Folder");
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            part.Name = "Deep";
            model.Parent = root;
            part.Parent = model;

            Assert.IsNull(root.FindFirstChild("Deep"));
            Assert.AreSame(part, root.FindFirstChild("Deep", true));
            Assert.AreSame(model, root.FindFirstChild("Model"));
        }

        [Test]
        public void FindFirstChild_RecursiveIsDepthFirst()
        {
            // WHY: Roblox recursive FindFirstChild descends into each child before the next
            // sibling, so a deep descendant of an earlier child beats a later direct child.
            RbxInstance root = _registry.Create("Folder");
            RbxInstance first = _registry.Create("Model");
            RbxInstance deep = _registry.Create("Part");
            RbxInstance shallow = _registry.Create("Part");
            first.Name = "First";
            deep.Name = "Target";
            shallow.Name = "Target";
            first.Parent = root;
            deep.Parent = first;
            shallow.Parent = root;

            Assert.AreSame(deep, root.FindFirstChild("Target", true));
            Assert.AreSame(shallow, root.FindFirstChild("Target"));
        }

        [Test]
        public void FindFirstChildWhichIsA_RecursiveIsDepthFirst()
        {
            RbxInstance root = _registry.Create("Folder");
            RbxInstance first = _registry.Create("Folder");
            RbxInstance deep = _registry.Create("Part");
            RbxInstance shallow = _registry.Create("Part");
            first.Parent = root;
            deep.Parent = first;
            shallow.Parent = root;

            Assert.AreSame(deep, root.FindFirstChildWhichIsA("BasePart", true));
            Assert.AreSame(shallow, root.FindFirstChildWhichIsA("BasePart"));
        }

        [Test]
        public void FindFirstChildOfClassAndWhichIsA()
        {
            RbxInstance root = _registry.Create("Folder");
            RbxInstance part = _registry.Create("Part");
            part.Parent = root;

            Assert.AreSame(part, root.FindFirstChildOfClass("Part"));
            Assert.IsNull(root.FindFirstChildOfClass("BasePart"));
            Assert.AreSame(part, root.FindFirstChildWhichIsA("BasePart"));
            Assert.IsNull(root.FindFirstChildWhichIsA("Folder"));
        }

        [Test]
        public void FindFirstAncestor_Trio()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Name = "Rig";
            model.Parent = _registry.WorldRoot;
            part.Parent = model;

            Assert.AreSame(model, part.FindFirstAncestor("Rig"));
            Assert.AreSame(_registry.WorldRoot, part.FindFirstAncestorOfClass("Workspace"));
            Assert.AreSame(model, part.FindFirstAncestorWhichIsA("PVInstance"));
            Assert.IsNull(part.FindFirstAncestor("Nope"));
        }

        [Test]
        public void IsA_WalksClassAncestry()
        {
            RbxInstance part = _registry.Create("Part");
            Assert.IsTrue(part.IsA("Part"));
            Assert.IsTrue(part.IsA("BasePart"));
            Assert.IsTrue(part.IsA("PVInstance"));
            Assert.IsTrue(part.IsA("Instance"));
            Assert.IsFalse(part.IsA("Folder"));
            Assert.IsFalse(part.IsA("Model"));

            // Roblox parity: Workspace -> WorldRoot -> Model.
            Assert.IsTrue(_registry.WorldRoot.IsA("Model"));
            Assert.IsTrue(_game.IsA("ServiceProvider"));
        }

        [Test]
        public void IsDescendantOfAndIsAncestorOf()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Parent = _registry.WorldRoot;
            part.Parent = model;

            Assert.IsTrue(part.IsDescendantOf(model));
            Assert.IsTrue(part.IsDescendantOf(_game));
            Assert.IsTrue(model.IsAncestorOf(part));
            Assert.IsFalse(model.IsDescendantOf(part));
            RbxError nilAncestor = Assert.Throws<RbxError>(() => part.IsDescendantOf(null),
                "Instance.yaml: IsDescendantOf cannot be used with a nil ancestor, so nil must raise "
                + "instead of quietly answering false");
            Assert.AreEqual(RbxErrorCode.BadArgument, nilAncestor.Code);
        }

        [Test]
        public void Parent_CircularReferenceIsRejected()
        {
            RbxInstance a = _registry.Create("Folder");
            RbxInstance b = _registry.Create("Folder");
            b.Parent = a;

            RbxError self = Assert.Throws<RbxError>(() => a.Parent = a);
            Assert.AreEqual(RbxErrorCode.BadArgument, self.Code);
            StringAssert.Contains("circular reference", self.RawMessage);

            RbxError cycle = Assert.Throws<RbxError>(() => a.Parent = b);
            Assert.AreEqual(RbxErrorCode.BadArgument, cycle.Code);
        }

        [Test]
        public void GetFullName_ExcludesTheDataModel()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            model.Name = "Rig";
            part.Name = "Head";
            model.Parent = _registry.WorldRoot;
            part.Parent = model;

            Assert.AreEqual("game", _game.GetFullName());
            Assert.AreEqual("Workspace", _registry.WorldRoot.GetFullName());
            Assert.AreEqual("Workspace.Rig.Head", part.GetFullName());

            RbxInstance detached = _registry.Create("Part");
            detached.Name = "Loose";
            Assert.AreEqual("Loose", detached.GetFullName());
        }

        [Test]
        public void Name_LongerThanTheCap_KeepsTheFirst100Characters()
        {
            RbxInstance part = _registry.Create("Part");

            part.Name = new string('a', 5000);

            Assert.AreEqual(new string('a', RbxInstance.MaxNameLength), part.Name,
                "Instance.yaml: a name cannot exceed 100 characters, so a longer one keeps its first 100");
        }

        [Test]
        public void Name_AtTheCapIsKeptWhole_AndAPairCutByTheCapIsDroppedWhole()
        {
            RbxInstance part = _registry.Create("Part");
            string exact = new string('b', RbxInstance.MaxNameLength);

            part.Name = exact;
            Assert.AreEqual(exact, part.Name, "a name of exactly 100 characters is not shortened");

            part.Name = new string('c', RbxInstance.MaxNameLength - 1) + "\U0001F600" + "tail";
            Assert.AreEqual(new string('c', RbxInstance.MaxNameLength - 1), part.Name,
                "a surrogate pair straddling the cap must not be split into half a character");
        }

        [Test]
        public void R6_11_NameWrite_FiresChangedWithThePropertyName_AndOnlyThatPropertySignal()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxInstance part = _registry.Create("Part");
            List<object[]> changed = Listen(scheduler, part.Changed);
            List<object[]> nameSignal = Listen(scheduler, part.GetPropertyChangedSignal("Name"));
            List<object[]> archivableSignal =
                Listen(scheduler, part.GetPropertyChangedSignal("Archivable"));

            part.Name = "Door";
            part.Name = "Door";
            scheduler.Advance(0d);

            Assert.AreEqual(1, changed.Count,
                "one real change fires Changed once; assigning the same name again is not a change");
            CollectionAssert.AreEqual(new object[] { "Name" }, changed[0],
                "Object.Changed passes the name of the property that changed");
            Assert.AreEqual(1, nameSignal.Count);
            Assert.AreEqual(0, nameSignal[0].Length, "GetPropertyChangedSignal fires with no arguments");
            Assert.AreEqual(0, archivableSignal.Count, "a Name write must not fire another property's signal");
        }

        [Test]
        public void R6_11_ArchivableAndParentWrites_FireChangedWithTheirNamesInOrder()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxInstance folder = _registry.Create("Folder");
            RbxInstance part = _registry.Create("Part");
            List<object[]> changed = Listen(scheduler, part.Changed);
            List<object[]> parentSignal = Listen(scheduler, part.GetPropertyChangedSignal("Parent"));

            part.Archivable = false;
            part.Parent = folder;
            part.Parent = folder;
            scheduler.Advance(0d);

            Assert.AreEqual(2, changed.Count);
            CollectionAssert.AreEqual(new object[] { "Archivable" }, changed[0]);
            CollectionAssert.AreEqual(new object[] { "Parent" }, changed[1]);
            Assert.AreEqual(1, parentSignal.Count, "re-assigning the same parent is not a change");
        }

        [Test]
        public void R6_11_ModelPrimaryPartAndWorldPivot_FireChangedWithTheirNames()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxModel model = (RbxModel)_registry.Create("Model");
            RbxInstance root = _registry.Create("Part");
            root.Parent = model;
            List<object[]> changed = Listen(scheduler, model.Changed);
            List<object[]> primaryPartSignal =
                Listen(scheduler, model.GetPropertyChangedSignal("PrimaryPart"));
            List<object[]> pivotSignal = Listen(scheduler, model.GetPropertyChangedSignal("WorldPivot"));

            model.SetPrimaryPart(root);
            model.SetWorldPivot(RbxCFrame.FromPosition(new RbxVector3(1f, 2f, 3f)));
            scheduler.Advance(0d);

            Assert.AreEqual(2, changed.Count);
            CollectionAssert.AreEqual(new object[] { "PrimaryPart" }, changed[0]);
            CollectionAssert.AreEqual(new object[] { "WorldPivot" }, changed[1]);
            Assert.AreEqual(1, primaryPartSignal.Count);
            Assert.AreEqual(1, pivotSignal.Count);
        }

        [Test]
        public void R6_11_ValueObject_NameWriteFiresOnlyItsPropertySignal_ValueWriteFiresChangedWithTheValue()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxIntValue value = (RbxIntValue)_registry.Create("IntValue");
            List<object[]> changed = Listen(scheduler, value.Changed);
            List<object[]> nameSignal = Listen(scheduler, value.GetPropertyChangedSignal("Name"));
            List<object[]> valueSignal = Listen(scheduler, value.GetPropertyChangedSignal("Value"));

            value.Name = "Coins";
            value.Value = 5L;
            scheduler.Advance(0d);

            Assert.AreEqual(1, changed.Count,
                "Object.yaml: a ValueBase's Changed fires only when Value changes, never for Name");
            CollectionAssert.AreEqual(new object[] { 5L }, changed[0],
                "a ValueBase's Changed carries the new value, not a property name");
            Assert.AreEqual(1, nameSignal.Count, "the Name property signal still fires on a value object");
            Assert.AreEqual(1, valueSignal.Count);
        }

        [Test]
        public void R6_11_GetPropertyChangedSignal_OneSignalPerName_AndAnEmptyNameIsRejected()
        {
            RbxInstance part = _registry.Create("Part");

            Assert.AreSame(part.GetPropertyChangedSignal("Name"), part.GetPropertyChangedSignal("Name"));
            Assert.AreNotSame(part.GetPropertyChangedSignal("Name"),
                part.GetPropertyChangedSignal("Archivable"));
            RbxError empty = Assert.Throws<RbxError>(() => part.GetPropertyChangedSignal(""),
                "an empty property name names no property, so it must raise instead of returning a "
                + "signal that can never fire");
            Assert.AreEqual(RbxErrorCode.BadArgument, empty.Code);
            Assert.Throws<RbxError>(() => part.GetPropertyChangedSignal(null));
        }

        [Test]
        public void R6_12_AncestryChanged_DescendantReceivesTheMovedAncestorAndItsNewParent()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxInstance rig = _registry.Create("Model");
            RbxInstance arm = _registry.Create("Part");
            rig.Name = "Rig";
            arm.Name = "Arm";
            rig.Parent = _registry.WorldRoot;
            arm.Parent = rig;
            List<object[]> armEvents = Listen(scheduler, arm.AncestryChanged);
            List<object[]> rigEvents = Listen(scheduler, rig.AncestryChanged);

            rig.Parent = null;
            rig.Parent = _registry.WorldRoot;
            scheduler.Advance(0d);

            Assert.AreEqual(2, armEvents.Count);
            Assert.AreSame(rig, armEvents[0][0],
                "Instance.yaml AncestryChanged: child is the instance whose Parent changed, not the listener");
            Assert.IsNull(armEvents[0][1], "parent is the moved instance's new parent, nil on a detach");
            Assert.AreSame(rig, armEvents[1][0]);
            Assert.AreSame(_registry.WorldRoot, armEvents[1][1]);
            Assert.AreEqual(2, rigEvents.Count);
            Assert.AreSame(rig, rigEvents[0][0], "the moved instance itself still receives its own pair");
            Assert.IsNull(rigEvents[0][1]);
            Assert.AreSame(rig, rigEvents[1][0]);
            Assert.AreSame(_registry.WorldRoot, rigEvents[1][1]);
        }

        [Test]
        public void M1_13_ParentingBeyondTheSnapshotDepth_IsRefused_AndTheTreeIsUnchanged()
        {
            int limit = InstanceTreeSerializer.MaximumSnapshotDepth;
            RbxInstance root = _registry.Create("Folder");
            RbxInstance deepest = AppendChain(root, limit - 1);
            RbxInstance extra = _registry.Create("Part");

            RbxError error = Assert.Throws<RbxError>(() => extra.Parent = deepest,
                "a world deeper than the snapshot depth cannot be saved, so the live tree must refuse it");

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("depth limit is " + limit, error.RawMessage);
            Assert.IsNull(extra.Parent);
            Assert.AreEqual(0, deepest.GetChildren().Count);
            Assert.AreEqual(limit - 1, root.GetDescendants().Count);
        }

        [Test]
        public void M1_13_DepthCapCountsTheMovedSubtree_AndATreeExactlyAtTheLimitIsAllowed()
        {
            int limit = InstanceTreeSerializer.MaximumSnapshotDepth;
            RbxInstance root = _registry.Create("Folder");
            RbxInstance deepest = AppendChain(root, limit - 1);
            RbxInstance top = _registry.Create("Model");
            RbxInstance bottom = _registry.Create("Part");
            bottom.Parent = top;

            Assert.Throws<RbxError>(() => top.Parent = deepest.Parent,
                "two moved levels under the second-deepest node reach one level past the limit");
            Assert.DoesNotThrow(() => top.Parent = deepest.Parent.Parent,
                "a tree exactly at the limit is still saveable and must be allowed");
            Assert.AreSame(deepest.Parent.Parent, top.Parent);
            Assert.DoesNotThrow(() => deepest.Parent = null, "detaching never deepens a tree");
        }

        [Test]
        public void M1_13_DeepestAllowedChain_WalksClonesMovesAndDestroys_OnASmallStackThread()
        {
            int limit = InstanceTreeSerializer.MaximumSnapshotDepth;
            RbxInstance root = _registry.Create("Folder");
            root.Name = "Root";
            RbxInstance leafParent = AppendChain(root, limit - 3);
            RbxInstance leaf = _registry.Create("Part");
            leaf.Name = "Leaf";
            leaf.Parent = leafParent;
            RbxInstance holder = _registry.Create("Folder");
            int descendantCount = -1;
            RbxInstance foundByName = null;
            RbxInstance foundByClass = null;
            int copyDescendantCount = -1;
            Exception failure = null;

            // WHY a small stack: a recursive walk spends one frame per level, and 2048 levels of the
            // old recursive Clone overflowed a 1 MB stack. A stack overflow cannot be caught — it
            // ends the editor process — so the walks must not depend on the call stack at all.
            Thread worker = new(() =>
            {
                try
                {
                    descendantCount = root.GetDescendants().Count;
                    foundByName = root.FindFirstChild("Leaf", true);
                    foundByClass = root.FindFirstChildWhichIsA("Part", true);
                    RbxInstance copy = root.Clone();
                    copyDescendantCount = copy.GetDescendants().Count;
                    copy.Destroy();
                    root.Parent = holder;
                    holder.Destroy();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }, SmallStackBytes);
            worker.Start();
            worker.Join();

            Assert.IsNull(failure, failure?.ToString());
            Assert.AreEqual(limit - 2, descendantCount);
            Assert.AreSame(leaf, foundByName);
            Assert.AreSame(leaf, foundByClass);
            Assert.AreEqual(limit - 2, copyDescendantCount);
            Assert.IsTrue(root.IsDestroyed);
            Assert.IsTrue(leaf.IsDestroyed);
            Assert.IsFalse(_registry.TryGet(leaf.Id, out _));
        }

        private RbxInstance AppendChain(RbxInstance top, int count)
        {
            RbxInstance parent = top;
            for (int index = 0; index < count; index++)
            {
                RbxInstance child = _registry.Create("Folder");
                child.Parent = parent;
                parent = child;
            }

            return parent;
        }

        private static ModScheduler CreateScheduler()
        {
            return new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
        }

        private static List<object[]> Listen(ModScheduler scheduler, RbxScriptSignal signal)
        {
            List<object[]> deliveries = new();
            signal.BindScheduler(scheduler);
            signal.Connect((Action<object[]>)(arguments => deliveries.Add(arguments)));
            return deliveries;
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

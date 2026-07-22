using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Navigation and hierarchy rules (§5.1.8 items 3 and 4, R6.10) plus GetFullName.</summary>
    [TestFixture]
    public sealed class InstanceHierarchyEditModeTests
    {
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

            var children = folder.GetChildren();
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

            var descendants = root.GetDescendants();
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
            Assert.IsFalse(part.IsDescendantOf(null));
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
    }
}

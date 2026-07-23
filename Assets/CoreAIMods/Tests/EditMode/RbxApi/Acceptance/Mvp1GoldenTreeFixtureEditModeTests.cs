using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.Acceptance
{
    /// <summary>
    /// MVP1 golden fixture (§5.1.8): one canonical tree — a Folder with a Block, a Ball and a
    /// Wedge Part — built through the Lua surface, asserted against the expected registry
    /// records (unique server-partition ids in insertion order, owner attribution) and the
    /// expected RobloxSpace-transposed Unity transforms; rebuilt at 1:1 to prove a scale switch
    /// touches only the RobloxSpace constant, and rebuilt twice to prove id determinism.
    /// </summary>
    [TestFixture]
    public sealed class Mvp1GoldenTreeFixtureEditModeTests
    {
        private const float Epsilon = 1e-4f;

        /// <summary>Canonical fixture source — every test in this class builds exactly this.</summary>
        private const string GoldenTreeLua = @"
            local root = Instance.new('Folder')
            root.Name = 'GoldenTree'
            root.Parent = workspace
            local block = Instance.new('Part')
            block.Name = 'Block'
            block.Parent = root
            block.Position = Vector3.new(10, 5, -4)
            block.Size = Vector3.new(4, 1, 2)
            local ball = Instance.new('Part')
            ball.Name = 'Ball'
            ball.Shape = Enum.PartType.Ball
            ball.Parent = root
            ball.Position = Vector3.new(0, 3, 6)
            ball.Size = Vector3.new(6, 6, 6)
            local wedge = Instance.new('Part')
            wedge.Name = 'Wedge'
            wedge.Shape = Enum.PartType.Wedge
            wedge.Parent = root
            wedge.Position = Vector3.new(-2, 0.5, 8)
            wedge.Size = Vector3.new(2, 1, 4)";

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static RbxInstance BuildGoldenTree(Mvp1AcceptanceWorld world)
        {
            world.Stack.Runtime.LoadMod("golden", GoldenTreeLua);
            RbxInstance root = world.Workspace.FindFirstChild("GoldenTree");
            Assert.IsNotNull(root, "the golden fixture root must exist");
            return root;
        }

        [Test]
        public void GoldenTree_RegistryRecords_MatchTheExpectedShape()
        {
            using var world = new Mvp1AcceptanceWorld();
            RbxInstance root = BuildGoldenTree(world);

            IReadOnlyList<RbxInstance> children = root.GetChildren();
            Assert.AreEqual(3, children.Count);
            Assert.AreEqual("Block", children[0].Name, "GetChildren order = insertion order");
            Assert.AreEqual("Ball", children[1].Name);
            Assert.AreEqual("Wedge", children[2].Name);
            Assert.AreEqual("Folder", root.ClassName);

            var seenIds = new HashSet<ulong>();
            ulong previous = 0;
            foreach (RbxInstance node in new[] { root, children[0], children[1], children[2] })
            {
                Assert.IsTrue(node.Id.IsValid);
                Assert.IsTrue(node.Id.IsServerAssigned, node.Name + " is server-partition in solo");
                Assert.IsTrue(seenIds.Add(node.Id.Value), "ids are unique");
                Assert.Greater(node.Id.Value, previous, "ids ascend in creation order");
                previous = node.Id.Value;

                Assert.IsTrue(world.Registry.TryGetRecord(node.Id, out InstanceRecord record));
                Assert.AreSame(node, record.Instance);
                Assert.AreEqual("golden", record.OwnerModId);
                Assert.IsTrue(record.IsMaterialized, node.Name + " sits under Workspace");
                Assert.AreEqual(0u, record.NetId, "NetId stays 0 until Mirror binds it (MVP12)");
            }

            Assert.AreEqual("Workspace.GoldenTree.Ball", children[1].GetFullName());
            Assert.AreEqual(RbxPartShape.Block,
                world.Binder.GetPartPropertiesOrDefault(children[0].Id).Shape);
            Assert.AreEqual(RbxPartShape.Ball,
                world.Binder.GetPartPropertiesOrDefault(children[1].Id).Shape);
            Assert.AreEqual(RbxPartShape.Wedge,
                world.Binder.GetPartPropertiesOrDefault(children[2].Id).Shape);
        }

        [Test]
        public void GoldenTree_At028_TransformsMatchTheTransposedGoldens()
        {
            using var world = new Mvp1AcceptanceWorld(0.28f);
            RbxInstance root = BuildGoldenTree(world);

            // WHY: the golden table below is the §5.1.8 fixture contract — position is
            // studs * 0.28 with z mirrored (D2/D3), localScale is Size * 0.28 with NO mirror.
            AssertPartGoldens(world, root, "Block",
                expectedPosition: new Vector3(2.8f, 1.4f, 1.12f),
                expectedScale: new Vector3(1.12f, 0.28f, 0.56f),
                expectedStudPosition: new RbxVector3(10f, 5f, -4f),
                expectedStudSize: new RbxVector3(4f, 1f, 2f));
            AssertPartGoldens(world, root, "Ball",
                expectedPosition: new Vector3(0f, 0.84f, -1.68f),
                expectedScale: new Vector3(1.68f, 1.68f, 1.68f),
                expectedStudPosition: new RbxVector3(0f, 3f, 6f),
                expectedStudSize: new RbxVector3(6f, 6f, 6f));
            AssertPartGoldens(world, root, "Wedge",
                expectedPosition: new Vector3(-0.56f, 0.14f, -2.24f),
                expectedScale: new Vector3(0.56f, 0.28f, 1.12f),
                expectedStudPosition: new RbxVector3(-2f, 0.5f, 8f),
                expectedStudSize: new RbxVector3(2f, 1f, 4f));
        }

        [Test]
        public void GoldenTree_At1To1_OnlyTheRobloxSpaceConstantChanges()
        {
            using var world = new Mvp1AcceptanceWorld(1f);
            RbxInstance root = BuildGoldenTree(world);

            // WHY: identical Lua, identical registry-side studs; the Unity numbers become the
            // stud numbers with only the z-mirror left — proof the scale switch touches zero
            // assets and zero mod code (§5.1.8 / D3).
            AssertPartGoldens(world, root, "Block",
                expectedPosition: new Vector3(10f, 5f, 4f),
                expectedScale: new Vector3(4f, 1f, 2f),
                expectedStudPosition: new RbxVector3(10f, 5f, -4f),
                expectedStudSize: new RbxVector3(4f, 1f, 2f));
            AssertPartGoldens(world, root, "Wedge",
                expectedPosition: new Vector3(-2f, 0.5f, -8f),
                expectedScale: new Vector3(2f, 1f, 4f),
                expectedStudPosition: new RbxVector3(-2f, 0.5f, 8f),
                expectedStudSize: new RbxVector3(2f, 1f, 4f));
        }

        [Test]
        public void GoldenTree_RebuiltInAFreshWorld_YieldsTheSameIdSequence()
        {
            List<ulong> first = CollectFixtureIds();
            List<ulong> second = CollectFixtureIds();
            CollectionAssert.AreEqual(first, second,
                "the id allocation for the canonical fixture must be deterministic — the world "
                + "file (MVP3) and RBXL round trip (MVP4) rely on stable, reproducible ids");
        }

        private List<ulong> CollectFixtureIds()
        {
            using var world = new Mvp1AcceptanceWorld();
            RbxInstance root = BuildGoldenTree(world);
            var ids = new List<ulong> { root.Id.Value };
            foreach (RbxInstance child in root.GetChildren())
            {
                ids.Add(child.Id.Value);
            }

            return ids;
        }

        private static void AssertPartGoldens(Mvp1AcceptanceWorld world, RbxInstance root,
            string name, Vector3 expectedPosition, Vector3 expectedScale,
            RbxVector3 expectedStudPosition, RbxVector3 expectedStudSize)
        {
            RbxInstance part = root.FindFirstChild(name);
            Assert.IsNotNull(part, name + " missing from the golden tree");

            PartProperties props = world.Binder.GetPartPropertiesOrDefault(part.Id);
            Assert.IsTrue(props.CFrame.Position.FuzzyEq(expectedStudPosition, Epsilon),
                name + " registry-side position must stay pure studs: " + props.CFrame.Position);
            Assert.IsTrue(props.Size.FuzzyEq(expectedStudSize, Epsilon),
                name + " registry-side size must stay pure studs: " + props.Size);

            Transform transform = world.BoundObject(part).transform;
            Assert.Less((transform.position - expectedPosition).magnitude, Epsilon,
                name + " world position " + transform.position + " != golden " + expectedPosition);
            Assert.Less((transform.localScale - expectedScale).magnitude, Epsilon,
                name + " localScale " + transform.localScale + " != golden " + expectedScale);
        }
    }
}

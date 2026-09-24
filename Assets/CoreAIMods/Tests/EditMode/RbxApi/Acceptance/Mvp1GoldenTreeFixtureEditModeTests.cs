using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP1 golden fixture (§5.1.8): one canonical tree — a Folder with a Block, a Ball and a
    /// Wedge Part — built through the Lua surface, asserted against the expected registry
    /// records (unique server-partition ids in insertion order, owner attribution) and the
    /// expected RbxSpace-transposed Unity transforms; rebuilt at 1:1 to prove a scale switch
    /// touches only the RbxSpace constant, and rebuilt twice to prove id determinism. The same tree
    /// moved out of Workspace (to Lighting, or straight under game) leaves the physical world and
    /// comes back unchanged. A part nested under one of its parts keeps the item-11 formula for its
    /// own Size and pose, and a Destroying handler on one of its parts reads that part's last values
    /// in both the rendered and the headless world.
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

        /// <summary>Appended to <see cref="GoldenTreeLua"/> (same chunk, so its locals are in
        /// scope): a Blade parented under the Block, then the Block moved and turned.</summary>
        private const string NestBladeUnderBlockLua = @"
            block.Anchored = true
            local blade = Instance.new('Part')
            blade.Name = 'Blade'
            blade.Anchored = true
            blade.Parent = block
            blade.Position = Vector3.new(10, 8, -4)
            blade.Size = Vector3.new(1, 4, 1)
            block.CFrame = CFrame.new(30, 5, -4) * CFrame.Angles(0, math.rad(45), 0)";

        /// <summary>Appended to <see cref="GoldenTreeLua"/>: the Ball's own Destroying handler records
        /// what it reads, then the Ball is destroyed.</summary>
        private const string DestroyBallLua = @"
            ball.Destroying:Connect(function()
                local p = ball.Position
                local s = ball.Size
                store_set('destroyed_position', p.X .. ',' .. p.Y .. ',' .. p.Z)
                store_set('destroyed_size', s.X .. ',' .. s.Y .. ',' .. s.Z)
            end)
            ball:Destroy()";

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
            using Mvp1AcceptanceWorld world = new();
            RbxInstance root = BuildGoldenTree(world);

            IReadOnlyList<RbxInstance> children = root.GetChildren();
            Assert.AreEqual(3, children.Count);
            Assert.AreEqual("Block", children[0].Name, "GetChildren order = insertion order");
            Assert.AreEqual("Ball", children[1].Name);
            Assert.AreEqual("Wedge", children[2].Name);
            Assert.AreEqual("Folder", root.ClassName);

            HashSet<ulong> seenIds = new();
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
            using Mvp1AcceptanceWorld world = new(0.28f);
            RbxInstance root = BuildGoldenTree(world);

            // WHY: the golden table below is the §5.1.8 fixture contract — position is
            // studs * 0.28 with z mirrored (D2/D3), localScale is Size * 0.28 with NO mirror.
            AssertPartGoldens(world, root, "Block",
                new Vector3(2.8f, 1.4f, 1.12f),
                new Vector3(1.12f, 0.28f, 0.56f),
                new RbxVector3(10f, 5f, -4f),
                new RbxVector3(4f, 1f, 2f));
            AssertPartGoldens(world, root, "Ball",
                new Vector3(0f, 0.84f, -1.68f),
                new Vector3(1.68f, 1.68f, 1.68f),
                new RbxVector3(0f, 3f, 6f),
                new RbxVector3(6f, 6f, 6f));
            AssertPartGoldens(world, root, "Wedge",
                new Vector3(-0.56f, 0.14f, -2.24f),
                new Vector3(0.56f, 0.28f, 1.12f),
                new RbxVector3(-2f, 0.5f, 8f),
                new RbxVector3(2f, 1f, 4f));
        }

        [Test]
        public void GoldenTree_At1To1_OnlyTheRobloxSpaceConstantChanges()
        {
            using Mvp1AcceptanceWorld world = new(1f);
            RbxInstance root = BuildGoldenTree(world);

            // WHY: identical Lua, identical registry-side studs; the Unity numbers become the
            // stud numbers with only the z-mirror left — proof the scale switch touches zero
            // assets and zero mod code (§5.1.8 / D3).
            AssertPartGoldens(world, root, "Block",
                new Vector3(10f, 5f, 4f),
                new Vector3(4f, 1f, 2f),
                new RbxVector3(10f, 5f, -4f),
                new RbxVector3(4f, 1f, 2f));
            AssertPartGoldens(world, root, "Wedge",
                new Vector3(-2f, 0.5f, -8f),
                new Vector3(2f, 1f, 4f),
                new RbxVector3(-2f, 0.5f, 8f),
                new RbxVector3(2f, 1f, 4f));
        }

        [Test]
        public void GoldenTree_MovedOutOfWorkspace_LeavesThePhysicalWorld_AndReturnsIntact()
        {
            using Mvp1AcceptanceWorld world = new(0.28f);
            RbxInstance root = BuildGoldenTree(world);
            IReadOnlyList<RbxInstance> parts = root.GetChildren();
            AssertPartsActive(world, parts, true, "built under Workspace");

            // WHY both destinations: Lighting is the classic storage trick, and a tree parented
            // straight to game is outside Workspace just the same; neither may render or collide.
            root.Parent = world.Game.GetService("Lighting");
            AssertPartsActive(world, parts, false, "stored in Lighting");

            root.Parent = world.Game;
            AssertPartsActive(world, parts, false, "parented straight to game");

            root.Parent = world.Workspace;
            AssertPartsActive(world, parts, true, "moved back into Workspace");
            AssertPartGoldens(world, root, "Block",
                new Vector3(2.8f, 1.4f, 1.12f),
                new Vector3(1.12f, 0.28f, 0.56f),
                new RbxVector3(10f, 5f, -4f),
                new RbxVector3(4f, 1f, 2f));
        }

        [Test]
        public void GoldenTree_PartNestedUnderBlock_KeepsItsOwnScale_AndStaysWhenBlockMoves()
        {
            using Mvp1AcceptanceWorld world = new(0.28f);
            world.Stack.Runtime.LoadMod("golden", GoldenTreeLua + "\n" + NestBladeUnderBlockLua);
            RbxInstance root = world.Workspace.FindFirstChild("GoldenTree");
            Assert.IsNotNull(root, "the golden fixture root must exist");
            RbxInstance block = root.FindFirstChild("Block");
            RbxInstance blade = block.FindFirstChild("Blade");
            Assert.IsNotNull(blade, "the Blade must be parented under the Block");
            Assert.Less((world.BoundObject(block).transform.position - new Vector3(8.4f, 1.4f, 1.12f)).magnitude,
                Epsilon, "precondition: the Block moved");

            // WHY: item 11 is Size * 0.28 for EVERY part; under the Block's scaled, posed GameObject the
            // Blade came out as the product of both sizes and was dragged 20 studs along with the Block.
            Transform bladeTransform = world.BoundObject(blade).transform;
            Assert.Less((bladeTransform.lossyScale - new Vector3(0.28f, 1.12f, 0.28f)).magnitude, Epsilon,
                "Blade world scale " + bladeTransform.lossyScale + " must be its own Size * 0.28");
            Assert.Less((bladeTransform.position - new Vector3(2.8f, 2.24f, 1.12f)).magnitude, Epsilon,
                "Blade world position " + bladeTransform.position + " must stay where the script put it");
            Assert.IsTrue(world.Binder.GetPartPropertiesOrDefault(blade.Id).Position
                    .FuzzyEq(new RbxVector3(10f, 8f, -4f), Epsilon),
                "and Blade.Position reads the same place");
        }

        [Test]
        public void GoldenTree_DestroyingHandler_ReadsTheDestroyedPartsLastValues()
        {
            using Mvp1AcceptanceWorld world = new(0.28f);
            world.Stack.Runtime.LoadMod("golden", GoldenTreeLua + "\n" + DestroyBallLua);
            world.Bindings.Scheduler.Advance(0d);

            Assert.IsNull(world.Workspace.FindFirstChild("GoldenTree").FindFirstChild("Ball"),
                "precondition: the Ball is destroyed");
            // WHY: Destroying handlers run after the destruction completed, when the part's state had
            // already been dropped, so the canonical 'explode at part.Position' idiom read the origin.
            AssertStoredVector(world.Store, "destroyed_position", new RbxVector3(0f, 3f, 6f));
            AssertStoredVector(world.Store, "destroyed_size", new RbxVector3(6f, 6f, 6f));
        }

        [Test]
        public void Headless_DestroyingHandler_ReadsTheDestroyedPartsLastValues()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            InMemoryPartPropertySink sink = new(registry);
            LuaCsRbxApiBindings bindings = new(registry, game, partSink: sink);
            Mvp1AcceptanceMemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
            try
            {
                stack.Runtime.LoadMod("golden", GoldenTreeLua + "\n" + DestroyBallLua);
                bindings.Scheduler.Advance(0d);

                AssertStoredVector(store, "destroyed_position", new RbxVector3(0f, 3f, 6f));
                AssertStoredVector(store, "destroyed_size", new RbxVector3(6f, 6f, 6f));
                Assert.AreEqual(1, sink.RetainedDestroyedPartCount,
                    "the headless sink released the destroyed Ball from its live store and kept one " +
                    "bounded last-known copy for the handler");
            }
            finally
            {
                game.Destroy();
            }
        }

        private static void AssertStoredVector(Mvp1AcceptanceMemoryStore store, string key, RbxVector3 expected)
        {
            string stored = store.Get("golden", key);
            string[] parts = stored.Split(',');
            Assert.AreEqual(3, parts.Length, key + " must hold three components, got '" + stored + "'");
            RbxVector3 actual = new(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
            Assert.IsTrue(actual.FuzzyEq(expected, Epsilon),
                key + " read inside the Destroying handler was " + actual + ", expected the part's last " + expected);
        }

        private static void AssertPartsActive(Mvp1AcceptanceWorld world, IReadOnlyList<RbxInstance> parts,
            bool expected, string situation)
        {
            Assert.AreEqual(3, parts.Count, "the golden tree has three parts");
            foreach (RbxInstance part in parts)
            {
                Assert.AreEqual(expected, world.BoundObject(part).activeInHierarchy,
                    part.Name + " " + situation + ": only Workspace content is the physical world");
            }
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
            using Mvp1AcceptanceWorld world = new();
            RbxInstance root = BuildGoldenTree(world);
            List<ulong> ids = new() { root.Id.Value };
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

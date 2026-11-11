using System;
using System.Collections;
using System.Diagnostics;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, PlayMode (FastNoLlm): the Terrain in a running player loop. A FillBlock collides in the frame
    /// after the call wherever a ray or a falling part meets it, even with the re-mesh budget starved, and is
    /// drawn within one frame per chunk; chunk colliders hold up a falling body and a character, a ray
    /// straight down hits a chunk collider that resolves to Terrain, Grass and the up normal, and placing
    /// one cell re-meshes only the two chunks it touches. The last test is the MS capture of the reference
    /// world.
    /// </summary>
    /// <remarks>
    /// The simulation is stepped from script so every physics assertion advances a known amount of
    /// simulated time; rendering frames still run, and the presenter re-meshes in LateUpdate.
    /// </remarks>
    [TestFixture]
    public sealed class P4TerrainPlayModeTests
    {
        private const float FixedStep = 0.02f;

        private SimulationMode _savedSimulationMode;
        private GameObject _host;
        private RbxTerrain _terrain;
        private RbxTerrainPresenter _presenter;

        [SetUp]
        public void CreateWorld()
        {
            _savedSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("P4 terrain host");
            _presenter = RbxTerrainPresenter.Attach(_host, game);
        }

        [TearDown]
        public void DestroyWorld()
        {
            try
            {
                if (_host != null)
                {
                    UnityEngine.Object.Destroy(_host);
                }
            }
            finally
            {
                Physics.simulationMode = _savedSimulationMode;
            }
        }

        private RbxTerrainRenderer Renderer => (RbxTerrainRenderer)_presenter.Renderer;

        private static byte Slot(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        /// <summary>The DoD slab: 64 x 4 x 64 cells of Grass, top face at y = 16 studs.</summary>
        private void FillSlab()
        {
            _terrain.FillBlock(RbxCFrame.FromPosition(128f, 8f, 128f), new RbxVector3(256f, 16f, 256f), Slot("Grass"));
        }

        private static void Simulate(float seconds)
        {
            int steps = Mathf.CeilToInt(seconds / FixedStep);
            for (int step = 0; step < steps; step++)
            {
                Physics.Simulate(FixedStep);
            }
        }

        /// <summary>
        /// Frames until nothing waits for a re-mesh; fails past <paramref name="limit"/>.
        /// </summary>
        private IEnumerator DrainWithin(int limit, Action<int> frames)
        {
            int count = 0;
            while (Renderer.PendingChunkCount > 0 && count < limit)
            {
                count++;
                yield return null;
            }

            Assert.AreEqual(0, Renderer.PendingChunkCount, "every chunk was re-meshed within " + limit + " frames");
            frames(count);
        }

        /// <summary>
        /// What a player needs from a big write: it collides in the frame after the call wherever something
        /// touches it (a ray, a falling part), and it is drawn within one frame per chunk at most, because the
        /// presenter re-meshes at least one waiting chunk every frame. The budget is starved to one chunk a frame,
        /// so the slab cannot be drawn in one frame by luck of machine speed.
        /// </summary>
        [UnityTest]
        public IEnumerator P4_FillBlockSlab_CollidesInTheFrameAfterTheCall_AndIsDrawnWithinOneFramePerChunk()
        {
            _presenter.MaxChunksPerFrame = 1;
            _presenter.MaxMillisecondsPerFrame = 0f;
            using BodyWorld bodies = new(_host.transform, _presenter);
            FillSlab();

            yield return null;

            Assert.AreEqual(15, Renderer.PendingChunkCount, "the starved budget drew one chunk");
            Assert.IsTrue(bodies.Port.TryRaycast(new RbxVector3(232f, 60f, 232f), new RbxVector3(0f, -100f, 0f), false,
                _ => true, out RbxPhysicsRaycastHit hit), "a ray at the last chunk in the queue hits the slab");
            Assert.AreEqual(_terrain.Id, hit.Instance);
            Assert.AreEqual(16f, hit.Position.Y, 1e-3f);
            Assert.AreEqual(14, Renderer.PendingChunkCount, "only the chunk under the ray was built out of turn");
            int frames = 0;
            yield return DrainWithin(16, counted => frames = counted);
            Assert.LessOrEqual(frames, 14);
            Assert.AreEqual(16, Renderer.ChunkObjectCount);
            foreach (MeshRenderer chunkRenderer in Renderer.Root.GetComponentsInChildren<MeshRenderer>())
            {
                Assert.IsTrue(chunkRenderer.enabled);
                Assert.Greater(chunkRenderer.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);
                Assert.IsNotNull(chunkRenderer.GetComponent<MeshCollider>().sharedMesh);
            }
        }

        [UnityTest]
        public IEnumerator P4_ChunkCollider_HoldsUpAFallingBody()
        {
            FillSlab();
            yield return DrainWithin(16, _ => { });
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.SetParent(_host.transform, false);
            float top = RbxSpace.LengthToUnity(16f);
            ball.transform.position = RbxSpace.ToUnity(new RbxVector3(100f, 30f, 100f));
            ball.AddComponent<Rigidbody>();
            Physics.SyncTransforms();

            Simulate(2f);

            Assert.AreEqual(top + 0.5f, ball.transform.position.y, 0.05f, "the ball rests on the slab's top face");
        }

        /// <summary>
        /// A part falling onto Terrain whose chunk is still queued behind a starved budget lands on it, because the
        /// physics port builds the colliders around every simulated body before it moves the body. The twin
        /// without a Terrain on the port shows the same setup lets the part fall through.
        /// </summary>
        [UnityTest]
        public IEnumerator P4_APartOverAChunkStillQueued_LandsOnIt_BecauseThePortBuildsTheColliderFirst()
        {
            yield return DropAPartOnTheLastQueuedChunk(true);
        }

        [UnityTest]
        public IEnumerator P4_APartOverAChunkStillQueued_FallsThrough_WhenThePortIsNotToldAboutTheTerrain()
        {
            yield return DropAPartOnTheLastQueuedChunk(false);
        }

        private IEnumerator DropAPartOnTheLastQueuedChunk(bool portKnowsTheTerrain)
        {
            _presenter.MaxChunksPerFrame = 1;
            _presenter.MaxMillisecondsPerFrame = 0f;
            using BodyWorld bodies = new(_host.transform, portKnowsTheTerrain ? _presenter : null);
            FillSlab();
            yield return null;
            Assert.IsTrue(Renderer.IsGeometryPending(new TerrainChunkCoord(3, 0, 3)), "the chunk under the part is queued");
            RbxInstance part = bodies.CreatePart(new RbxVector3(224f, 24f, 224f), new RbxVector3(4f, 4f, 4f));
            bodies.Port.ApplyGravity();
            Assert.AreEqual(portKnowsTheTerrain, !Renderer.IsGeometryPending(new TerrainChunkCoord(3, 0, 3)),
                "the port builds the chunk under the part before its first step only when it knows the Terrain; part at "
                + bodies.ReadPosition(part));

            bodies.Simulate(2f);

            float landed = bodies.ReadPosition(part).Y;
            if (portKnowsTheTerrain)
            {
                Assert.AreEqual(18f, landed, 0.1f, "the part rests on the slab's top face");
                Assert.Greater(Renderer.PendingChunkCount, 0, "the rest of the slab still waits for its frames");
            }
            else
            {
                Assert.Less(landed, 0f, "without the port's help the part falls through the queued chunk");
            }
        }

        [UnityTest]
        public IEnumerator P4_CharacterMotor_IsGrounded_OnChunkColliders()
        {
            FillSlab();
            yield return null;
            GameObject character = new("P4 character");
            character.transform.SetParent(_host.transform, false);
            CapsuleCollider capsule = character.AddComponent<CapsuleCollider>();
            capsule.height = 2f;
            capsule.radius = 0.4f;
            Rigidbody body = character.AddComponent<Rigidbody>();
            character.transform.position = RbxSpace.ToUnity(new RbxVector3(60f, 30f, 60f));
            Physics.SyncTransforms();
            UnityRbxCharacterMotor motor = new(body, 1f);

            Simulate(2f);

            Assert.IsTrue(motor.IsGrounded, "the motor's ground probe finds the chunk collider");
            Assert.AreEqual(RbxSpace.LengthToUnity(16f) + 1f, body.position.y, 0.05f);
        }

        [UnityTest]
        public IEnumerator P4_RayStraightDown_ResolvesToTerrainGrassAndTheUpNormal()
        {
            FillSlab();
            yield return null;
            Physics.SyncTransforms();
            Vector3 origin = RbxSpace.ToUnity(new RbxVector3(42f, 40f, 42f));

            Assert.IsTrue(Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 100f));
            Assert.IsTrue(Renderer.TryResolveHit(hit.collider, hit.point, hit.normal, out InstanceId id,
                out RbxMaterialId material, out RbxVector3 normal));

            Assert.AreEqual(_terrain.Id, id);
            Assert.AreEqual("Grass", material.Name);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), normal);
            Assert.AreEqual(16f, RbxSpace.FromUnity(hit.point).Y, 1e-3f);
        }

        [UnityTest]
        public IEnumerator P4_PlacingOneCell_RemeshesOnlyTheTwoChunksItTouches_WithinTwoFrames()
        {
            FillSlab();
            yield return DrainWithin(16, _ => { });
            long before = Renderer.RemeshCount;

            _terrain.FillBlock(RbxCFrame.FromPosition(RbxTerrain.CellCenterToWorld(16, 4, 5)),
                new RbxVector3(4f, 4f, 4f), Slot("Brick"));
            yield return DrainWithin(2, _ => { });

            Assert.AreEqual(2, Renderer.RemeshCount - before,
                "the touched chunk and the -X neighbour that shares the face; nothing else");

            before = Renderer.RemeshCount;
            _terrain.FillBlock(RbxCFrame.FromPosition(RbxTerrain.CellCenterToWorld(16, 4, 5)),
                new RbxVector3(4f, 4f, 4f), TerrainMaterials.Air);
            yield return DrainWithin(2, _ => { });
            Assert.AreEqual(2, Renderer.RemeshCount - before, "breaking it costs the same two chunks");
        }

        /// <summary>
        /// MS capture of the reference world (256 x 64 x 256 cells, ground plus caves and a sea): time to
        /// bring every chunk on screen under the default budget, then the frame time and the draw-call
        /// bound (chunk renderers times their submeshes) over 120 frames. Logged for the capture sheet.
        /// </summary>
        [UnityTest]
        [Category("P4Capture")]
        public IEnumerator P4_Capture_ReferenceWorld_FrameTimeAndDrawCallBound()
        {
            GameObject cameraObject = new("P4 capture camera");
            cameraObject.transform.SetParent(_host.transform, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = RbxSpace.ToUnity(new RbxVector3(128f, 300f, -200f));
            camera.transform.LookAt(RbxSpace.ToUnity(new RbxVector3(512f, 0f, 512f)));
            FillReferenceWorld(_terrain.Store);
            Stopwatch clock = Stopwatch.StartNew();
            int frames = 0;
            while (Renderer.PendingChunkCount > 0 && frames < 2000)
            {
                frames++;
                yield return null;
            }

            clock.Stop();
            Assert.AreEqual(0, Renderer.PendingChunkCount, "the whole world meshes within 2000 frames");
            int drawBound = 0;
            foreach (MeshRenderer chunkRenderer in Renderer.Root.GetComponentsInChildren<MeshRenderer>())
            {
                drawBound += chunkRenderer.sharedMaterials.Length;
            }

            Stopwatch frameClock = Stopwatch.StartNew();
            for (int frame = 0; frame < 120; frame++)
            {
                yield return null;
            }

            frameClock.Stop();
            Debug.Log("[P4 capture] reference world: " + _terrain.Store.SolidCellCount + " cells, "
                      + Renderer.ChunkObjectCount + " chunk objects; meshed in " + frames + " frames / "
                      + clock.Elapsed.TotalMilliseconds.ToString("0") + " ms; draw-call bound " + drawBound
                      + "; mean frame " + (frameClock.Elapsed.TotalMilliseconds / 120d).ToString("0.00") + " ms");
            Assert.LessOrEqual(drawBound, 6 * Renderer.ChunkObjectCount,
                "draw calls follow chunks and the few materials in each, never cells");
        }

        /// <summary>A bound-part world sharing the scene with the Terrain: a binder, its registry and a physics port.</summary>
        private sealed class BodyWorld : IDisposable
        {
            private readonly InstanceGameObjectBinder _binder;
            private readonly InstanceRegistry _registry;

            public BodyWorld(Transform parent, IRbxTerrainHitResolver terrain)
            {
                RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
                _binder = new InstanceGameObjectBinder(parent, null);
                _registry = new InstanceRegistry(null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
                DataModelBootstrap.CreateGame(_registry);
                Port = new UnityRbxPhysicsPort(_binder) { TerrainHits = terrain };
            }

            public UnityRbxPhysicsPort Port { get; }

            /// <remarks>
            /// WHY a block four studs tall: a chunk collider is a surface, and a one-stud plate falling a few metres
            /// moves further than its own height in one fixed step and passes it (no continuous collision on parts).
            /// </remarks>
            public RbxInstance CreatePart(RbxVector3 position, RbxVector3 size)
            {
                RbxInstance part = _registry.Create("Part");
                part.Parent = _registry.WorldRoot;
                _binder.SetSize(part.Id, size);
                _binder.SetPosition(part.Id, position);
                _binder.SetAnchored(part.Id, false);
                Physics.SyncTransforms();
                return part;
            }

            public RbxVector3 ReadPosition(RbxInstance part)
            {
                return _binder.GetLivePositionStuds(part.Id);
            }

            public void Simulate(float seconds)
            {
                int steps = Mathf.CeilToInt(seconds / FixedStep);
                for (int step = 0; step < steps; step++)
                {
                    Port.ApplyGravity();
                    Physics.Simulate(FixedStep);
                }
            }

            public void Dispose()
            {
                Port.Dispose();
            }
        }

        private static void FillReferenceWorld(TerrainChunkStore store)
        {
            TerrainCellBox box = new(0, 0, 0, 256, 64, 256);
            byte[] materials = new byte[box.CellCount];
            byte[] occupancy = new byte[box.CellCount];
            byte rock = Slot("Rock");
            byte ground = Slot("Ground");
            byte grass = Slot("Grass");
            byte sand = Slot("Sand");
            for (int x = 0; x < 256; x++)
            {
                for (int z = 0; z < 256; z++)
                {
                    double value = 30d + 8d * Math.Sin(x * 0.07d) + 6d * Math.Cos(z * 0.05d)
                                   + 4d * Math.Sin((x + z) * 0.11d);
                    int height = Math.Max(8, Math.Min(56, (int)Math.Floor(value)));
                    for (int y = 0; y < 64; y++)
                    {
                        byte material = TerrainMaterials.Air;
                        if (y < height - 4)
                        {
                            double cave = Math.Sin(x * 0.19d) * Math.Cos(y * 0.23d)
                                          + Math.Sin(z * 0.17d) * Math.Cos(x * 0.13d) + Math.Sin(y * 0.29d + z * 0.07d);
                            material = y >= 3 && cave > 1.4d ? TerrainMaterials.Air : rock;
                        }
                        else if (y < height - 1)
                        {
                            material = ground;
                        }
                        else if (y == height - 1)
                        {
                            material = height > 30 ? grass : sand;
                        }
                        else if (y < 30)
                        {
                            material = TerrainMaterials.Water;
                        }

                        int index = box.RegionIndexOf(x, y, z);
                        materials[index] = material;
                        occupancy[index] = material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full;
                    }
                }
            }

            store.WriteBox(box, materials, occupancy);
        }
    }
}

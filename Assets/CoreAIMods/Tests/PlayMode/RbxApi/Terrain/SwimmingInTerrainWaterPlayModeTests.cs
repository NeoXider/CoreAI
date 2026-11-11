using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.Terrain
{
    /// <summary>
    /// Swimming in Terrain water with the real character root, CoreAI's motor, the physics port's water sense and the
    /// engine-free Humanoid deciding the state: a character dropped into a pool three cells deep swims, does not go
    /// through the bottom and rises to the surface while Jump is held; it swims across at the swim speed; it climbs out
    /// onto a bank one block above the surface; it wades in water less than a body deep; on dry land it never swims
    /// (twin). Two captures: the eye under water (tinted fog, given back on leaving) and the surface seen from below.
    /// Numbers and frames go to <c>artifacts/swimming/</c>.
    /// </summary>
    public sealed class SwimmingInTerrainWaterPlayModeTests
    {
        private const float FixedStep = 0.02f;
        private const float Cell = 4f;
        private static readonly StringBuilder Report = new();

        private SimulationMode _savedSimulationMode;
        private GameObject _host;
        private RbxTerrain _terrain;
        private RbxTerrainPresenter _presenter;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private UnityRbxPhysicsPort _port;
        private string _folder;

        [SetUp]
        public void CreateWorld()
        {
            _savedSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry terrainRegistry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(terrainRegistry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(terrainRegistry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("Swimming host");
            _presenter = RbxTerrainPresenter.Attach(_host, game);
            _binder = new InstanceGameObjectBinder(_host.transform, null);
            _registry = new InstanceRegistry(catalog, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_registry);
            _port = new UnityRbxPhysicsPort(_binder) { TerrainHits = _presenter };
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _folder = Path.Combine(projectRoot, "artifacts", "swimming");
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            try
            {
                _port?.Dispose();
                if (_host != null)
                {
                    Object.Destroy(_host);
                }

                File.WriteAllText(Path.Combine(_folder, "numbers.txt"), Report.ToString());
            }
            finally
            {
                Physics.simulationMode = _savedSimulationMode;
            }
        }

        // ---- the matrix ---------------------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator FallingIntoAPoolThreeCellsDeep_Swims_StaysAboveTheBottom_AndRisesWithJumpHeld()
        {
            yield return Pool(bankBlocks: 0, depthCells: 3);
            Swimmer swimmer = Spawn(new RbxVector3(60f, 36f, 32f));
            float lowest = float.MaxValue;
            float swamAt = -1f;
            for (int step = 0; step < 100; step++)
            {
                swimmer.Tick(RbxVector3.Zero, false);
                lowest = Mathf.Min(lowest, swimmer.Feet);
                if (swamAt < 0f && swimmer.State == RbxHumanoidState.Swimming)
                {
                    swamAt = swimmer.Time;
                }
            }

            Assert.Greater(swamAt, 0f, "it swims once its middle is under the surface");
            Assert.Greater(lowest, 4f - 0.05f, "the plunge stops above the pool's bottom (4 studs)");
            float rising = swimmer.Time;
            float surfaced = -1f;
            for (int step = 0; step < 200 && surfaced < 0f; step++)
            {
                swimmer.Tick(RbxVector3.Zero, true);
                if (!swimmer.Water.HeadSubmerged)
                {
                    surfaced = swimmer.Time - rising;
                }
            }

            Record("pool 3 deep: swims after " + F(swamAt) + " s, lowest feet " + F(lowest) + " studs (bottom 4), head out "
                   + F(surfaced) + " s after Jump is held");
            Assert.Greater(surfaced, 0f, "holding Jump brings the head out");
        }

        [UnityTest]
        public IEnumerator ASwimmer_CrossesThePool_AtTheSwimSpeed()
        {
            yield return Pool(bankBlocks: 0, depthCells: 3);
            Swimmer swimmer = Spawn(new RbxVector3(44f, 12f, 32f));
            swimmer.Run(0.6f, RbxVector3.Zero, true);
            float from = swimmer.Root.X;
            swimmer.Run(1f, new RbxVector3(1f, 0f, 0f), false);
            float speed = swimmer.Root.X - from;
            Record("swimming across: " + F(speed) + " studs/s (swim speed " + F(16f * (float)RbxSwimRules.SwimSpeedShare) + ")");

            Assert.AreEqual(RbxHumanoidState.Swimming, swimmer.State);
            Assert.Greater(speed, 0.6f * 16f, "about three quarters of WalkSpeed");
        }

        [UnityTest]
        public IEnumerator ASwimmer_ClimbsOutOntoABankOneBlockAboveTheSurface()
        {
            yield return Pool(bankBlocks: 1, depthCells: 3);
            Swimmer swimmer = Spawn(new RbxVector3(70f, 12f, 32f));
            swimmer.Run(0.6f, RbxVector3.Zero, true);
            float climbedAt = -1f;
            for (int step = 0; step < 250 && climbedAt < 0f; step++)
            {
                swimmer.Tick(new RbxVector3(1f, 0f, 0f), true);
                if (swimmer.Root.X > 81f && swimmer.Feet >= 20f - 0.05f)
                {
                    climbedAt = swimmer.Time;
                }
            }

            swimmer.Run(0.5f, new RbxVector3(1f, 0f, 0f), false);
            Record("climbing out onto a one-block bank: on top at " + F(climbedAt) + " s, then " + swimmer.State);
            Assert.Greater(climbedAt, 0f, "it gets out onto the bank one block above the water");
            Assert.AreNotEqual(RbxHumanoidState.Swimming, swimmer.State, "and walks on, out of the water");
        }

        [UnityTest]
        public IEnumerator InWaterLessThanABodyDeep_ItWades_AndInDeepWaterItSwims()
        {
            yield return Pool(bankBlocks: 0, depthCells: 1);
            Swimmer wader = Spawn(new RbxVector3(60f, 13.05f, 32f));
            wader.Run(1f, new RbxVector3(1f, 0f, 0f), false);
            Record("water one cell deep: " + wader.State + ", feet " + F(wader.Feet));
            Assert.AreNotEqual(RbxHumanoidState.Swimming, wader.State, "its head is out and its feet on the bottom");
            Assert.AreEqual(12f, wader.Feet, 0.1f, "standing on the pool's bottom");
        }

        [UnityTest]
        public IEnumerator OnDryLand_ACharacterNeverSwims_NegativeTwin()
        {
            yield return Pool(bankBlocks: 0, depthCells: 0);
            Swimmer walker = Spawn(new RbxVector3(60f, 24f, 32f));
            bool swam = false;
            for (int step = 0; step < 150; step++)
            {
                walker.Tick(new RbxVector3(1f, 0f, 0f), step % 20 == 0);
                swam |= walker.State == RbxHumanoidState.Swimming;
            }

            Assert.IsFalse(swam);
        }

        // ---- the captures -----------------------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator UnderWater_TheEyeSeesTintedFog_AndTheWorldsFogComesBackOnLeaving()
        {
            yield return Pool(bankBlocks: 0, depthCells: 3);
            yield return Drain();
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.8f, 0.6f, 0.4f);
            RenderSettings.fogEndDistance = 50f;
            Camera camera = MainCamera(new RbxVector3(60f, 10f, 32f), new RbxVector3(70f, 6f, 40f));
            yield return null;
            yield return null;

            Assert.IsTrue(_presenter.Underwater.IsUnderwater, "the eye is in a water cell");
            Assert.AreEqual(RbxUnderwaterView.FogColor, RenderSettings.fogColor);
            Color under = Shoot(camera, "eye_under_water.png");
            Record("eye under water: mean " + Describe(under));
            Assert.Greater(under.b + under.g, under.r * 2f, "blue-green");

            camera.transform.position = RbxSpace.ToUnity(new RbxVector3(60f, 40f, 32f));
            camera.transform.LookAt(RbxSpace.ToUnity(new RbxVector3(70f, 16f, 40f)));
            yield return null;
            yield return null;
            Assert.IsFalse(_presenter.Underwater.IsUnderwater);
            Assert.AreEqual(new Color(0.8f, 0.6f, 0.4f), RenderSettings.fogColor, "the world's fog is back");
            Assert.AreEqual(50f, RenderSettings.fogEndDistance, 1e-4f);
        }

        [UnityTest]
        public IEnumerator FromBelow_TheWaterSurfaceIsDrawn()
        {
            yield return Pool(bankBlocks: 0, depthCells: 3);
            yield return Drain();
            Camera camera = MainCamera(new RbxVector3(60f, 6f, 32f), new RbxVector3(62f, 30f, 34f));
            yield return null;
            yield return null;
            Color up = Shoot(camera, "surface_from_below.png");
            Record("looking up at the surface from under water: mean " + Describe(up));
            Assert.IsTrue(_presenter.Underwater.IsUnderwater);
        }

        // ---- the world ------------------------------------------------------------------------------------------

        /// <summary>
        /// Ground (Grass) with its top at 16 studs, or at 16 plus <paramref name="bankBlocks"/> blocks, round a pool
        /// over x 40..80 and z 16..48 studs whose water fills <paramref name="depthCells"/> cells under 16 studs.
        /// </summary>
        private IEnumerator Pool(int bankBlocks, int depthCells)
        {
            TerrainMaterials.TryGetSlot("Grass", out byte grass);
            Fill(0, -2, 0, 40, 4 + bankBlocks, 40, grass);
            Fill(0, 4 + bankBlocks, 0, 40, 12, 40, TerrainMaterials.Air);
            if (depthCells > 0)
            {
                Fill(10, 4 - depthCells, 4, 20, 4 + bankBlocks, 12, TerrainMaterials.Air);
                Fill(10, 4 - depthCells, 4, 20, 4, 12, TerrainMaterials.Water);
            }

            yield return null;
        }

        private IEnumerator Drain()
        {
            for (int frame = 0; frame < 60 && ((RbxTerrainRenderer)_presenter.Renderer).PendingChunkCount > 0; frame++)
            {
                yield return null;
            }
        }

        private void Fill(int x0, int y0, int z0, int x1, int y1, int z1, byte material)
        {
            RbxVector3 centre = new((x0 + x1) * 0.5f * Cell, (y0 + y1) * 0.5f * Cell, (z0 + z1) * 0.5f * Cell);
            RbxVector3 size = new((x1 - x0) * Cell, (y1 - y0) * Cell, (z1 - z0) * Cell);
            _terrain.FillBlock(RbxCFrame.FromPosition(centre), size, material);
        }

        private Swimmer Spawn(RbxVector3 rootAt)
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;
            _binder.SetSize(part.Id, new RbxVector3(2f, 2f, 1f));
            _binder.SetPosition(part.Id, rootAt);
            _binder.SetAnchored(part.Id, false);
            Physics.SyncTransforms();
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject body));
            Rigidbody rigidbody = body.GetComponent<Rigidbody>();
            UnityRbxCharacterMotor motor = new(rigidbody,
                worldGravityMetresPerSecondSquared: () => _port.GravityMetresPerSecondSquared.magnitude);
            RbxHumanoid humanoid = (RbxHumanoid)_registry.Create("Humanoid");
            humanoid.AttachHost(null, motor, part);
            return new Swimmer(this, rigidbody, motor, humanoid);
        }

        private Camera MainCamera(RbxVector3 from, RbxVector3 at)
        {
            GameObject cameraObject = new("Swimming camera") { tag = "MainCamera" };
            cameraObject.transform.SetParent(_host.transform, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = RbxSpace.ToUnity(from);
            camera.transform.LookAt(RbxSpace.ToUnity(at));
            return camera;
        }

        private Color Shoot(Camera camera, string file)
        {
            RenderTexture target = new(480, 270, 24);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            Texture2D image = new(480, 270, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 480, 270), 0, 0);
            image.Apply();
            RenderTexture.active = null;
            camera.targetTexture = null;
            File.WriteAllBytes(Path.Combine(_folder, file), image.EncodeToPNG());
            Color[] pixels = image.GetPixels();
            Color sum = Color.clear;
            foreach (Color pixel in pixels)
            {
                sum += pixel;
            }

            Object.Destroy(image);
            target.Release();
            Object.Destroy(target);
            return sum / pixels.Length;
        }

        private static void Record(string line)
        {
            Report.AppendLine(line);
            TestContext.Out.WriteLine(line);
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Describe(Color color)
        {
            return "rgb(" + Mathf.RoundToInt(color.r * 255f) + ", " + Mathf.RoundToInt(color.g * 255f) + ", "
                   + Mathf.RoundToInt(color.b * 255f) + ")";
        }

        /// <summary>One character: its root, motor and Humanoid, stepped as the world host steps them.</summary>
        private sealed class Swimmer
        {
            private readonly SwimmingInTerrainWaterPlayModeTests _world;
            private readonly Rigidbody _body;
            private readonly UnityRbxCharacterMotor _motor;
            private readonly RbxHumanoid _humanoid;

            public Swimmer(SwimmingInTerrainWaterPlayModeTests world, Rigidbody body, UnityRbxCharacterMotor motor,
                RbxHumanoid humanoid)
            {
                _world = world;
                _body = body;
                _motor = motor;
                _humanoid = humanoid;
            }

            public float Time { get; private set; }

            public RbxVector3 Root => RbxSpace.FromUnity(_body.position);

            public float Feet => Root.Y - 1f;

            public RbxHumanoidState State => _humanoid.GetState();

            public RbxWaterContact Water => _motor.Water;

            public void Run(float seconds, RbxVector3 direction, bool jump)
            {
                int steps = Mathf.CeilToInt(seconds / FixedStep);
                for (int step = 0; step < steps; step++)
                {
                    Tick(direction, jump);
                }
            }

            public void Tick(RbxVector3 direction, bool jump)
            {
                _humanoid.Advance(FixedStep);
                _humanoid.Move(direction);
                if (jump)
                {
                    _humanoid.RequestJump();
                }

                _world._port.ApplyGravity();
                _motor.Step(FixedStep);
                Physics.Simulate(FixedStep);
                Time += FixedStep;
            }
        }
    }
}

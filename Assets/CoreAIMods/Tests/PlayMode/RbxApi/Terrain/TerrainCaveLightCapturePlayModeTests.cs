using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// A voxel hill at noon under the default look and the Lighting sink: a tunnel runs into a cliff face with a
    /// flooded stretch and a shaft from the hill top, a room stands under a one-cell roof with a doorway, a torch
    /// burns deep in the tunnel and one part stands in the tunnel, another in the open. Every measurement is a
    /// masked crop shot twice in the same run, with the sky exposure switched off (the look before it) and on,
    /// and the frames go to <c>artifacts/cavelight/</c>: the deep floor falls to the midnight ground, a torch
    /// lights its walls, cave water stops mirroring the sky, the mouth fades softly from both sides, a one-cell
    /// roof lets no light through, the hill top and the open ground do not change, breaking a roof block lights
    /// the room in the frame after, and the part in the tunnel is dimmed with it.
    /// </summary>
    public sealed class TerrainCaveLightCapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;
        private const float Cell = 4f;

        /// <summary>Lighting.Ambient the Block World demo sets by day, and the night value it keeps under rock.</summary>
        private static readonly RbxColor3 DayAmbient = new(110f / 255f, 110f / 255f, 116f / 255f);
        private static readonly RbxColor3 NightAmbient = new(14f / 255f, 16f / 255f, 30f / 255f);
        private static readonly RbxColor3 DayOutdoor = new(150f / 255f, 150f / 255f, 150f / 255f);
        private static readonly RbxColor3 NightOutdoor = new(34f / 255f, 40f / 255f, 74f / 255f);

        private readonly List<Object> _owned = new();
        private readonly StringBuilder _report = new();
        private RbxTerrain _terrain;
        private RbxTerrainPresenter _presenter;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _parts;
        private Camera _camera;
        private Light _sun;
        private Light _torch;
        private ICoreAiLookApplier _applier;
        private UnityRbxLightingSink _sink;
        private Light _savedSun;
        private string _outputFolder;
        private Renderer _caveBlock;
        private Renderer _openBlock;

        [SetUp]
        public void CreateWorld()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || GraphicsSettings.currentRenderPipeline == null
                || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR))
            {
                Assert.Ignore("These captures need a graphics device, a render pipeline and an HDR target.");
            }

            _savedSun = RenderSettings.sun;
            UsePackagedCatalogOnly(true);
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            GameObject host = new("Cave light host");
            _owned.Add(host);
            _presenter = RbxTerrainPresenter.Attach(host, game);
            _binder = new InstanceGameObjectBinder(host.transform, null);
            _parts = new InstanceRegistry(null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_parts);
            _presenter.PartRendererSource = _binder.CollectPartRenderers;

            GameObject cameraObject = new("Cave light camera");
            _owned.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 70f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 1000f;
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.allowHDR = true;
            _camera.aspect = (float)ShotWidth / ShotHeight;

            GameObject sunObject = new("Cave light sun");
            _owned.Add(sunObject);
            _sun = sunObject.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            RenderSettings.sun = _sun;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "cavelight");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            _sink?.Revert();
            _sink = null;
            _applier?.Revert();
            _applier = null;
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.Destroy(owned);
                }
            }

            _owned.Clear();
            RenderSettings.sun = _savedSun;
            UsePackagedCatalogOnly(false);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ACaveIsDarkAtNoon_WhileTheHillAboveItIsLit()
        {
            BuildWorld();
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);
            _sink = new UnityRbxLightingSink(_sun, LookAmbientOwnership.Shared);
            yield return Settle();

            // Reference: the open ground at midnight, with the demo's night values.
            SetClock(0f, NightAmbient, NightOutdoor);
            yield return SettleAfterTheClock();
            Aim(new RbxVector3(-60f, 10f, 60f), new RbxVector3(-60f, 0f, 30f));
            float midnightGround = Luminance(Shoot("midnight_ground.png"), Rect(new RbxVector3(-68f, 0f, 44f),
                new RbxVector3(-52f, 0f, 36f)));
            Record("midnight open ground " + F(midnightGround));

            foreach (bool darkAmbient in new[] { true, false })
            {
                string label = darkAmbient ? "night ambient" : "demo day ambient";
                SetClock(12f, darkAmbient ? NightAmbient : DayAmbient, DayOutdoor);
                yield return SettleAfterTheClock();
                CaveShots(darkAmbient ? "dark" : "day", label, darkAmbient, midnightGround);
            }

            yield return EditNextToTheRoom();
            File.WriteAllText(Path.Combine(_outputFolder, "cavelight_numbers.txt"), _report.ToString());
            TestContext.WriteLine(_report.ToString());
        }

        /// <summary>Every crop with the exposure off and on, for one value of Lighting.Ambient.</summary>
        private void CaveShots(string tag, string label, bool assertDark, float midnightGround)
        {
            // The deep tunnel floor, 22 to 25 cells in, 13 from the shaft, looking further in.
            Aim(new RbxVector3(76f, 5f, 2f), new RbxVector3(120f, 1f, 2f));
            RectInt deep = Rect(new RbxVector3(88f, 0f, -2f), new RbxVector3(100f, 0f, 6f));
            RectInt wall = Rect(new RbxVector3(96f, 1.5f, -3.9f), new RbxVector3(106f, 6f, -3.9f));
            float deepOff = Luminance(ShootPair(tag + "_deep", false), deep);
            float wallOff = Luminance(LastPair, wall);
            float deepOn = Luminance(ShootPair(tag + "_deep", true), deep);
            float wallOn = Luminance(LastPair, wall);
            _torch.enabled = true;
            float deepTorch = Luminance(Shoot(tag + "_deep_torch_on.png"), deep);
            float wallTorch = Luminance(LastShot, wall);
            float brightestTile = BrightestTile(LastShot, deep, out RectInt tile);
            _torch.enabled = false;
            Record(label + ": deep floor off " + F(deepOff) + " on " + F(deepOn) + " (midnight ground " + F(midnightGround)
                   + ") | with the torch " + F(deepTorch) + ", brightest tile " + F(brightestTile) + " at " + tile
                   + " | wall off " + F(wallOff) + " on " + F(wallOn) + " torch " + F(wallTorch));

            // The flooded stretch from above its near end.
            Aim(new RbxVector3(58f, 6f, 2f), new RbxVector3(84f, 0f, 2f));
            RectInt pool = Rect(new RbxVector3(68f, 0f, -2f), new RbxVector3(82f, 0f, 6f));
            float poolOff = Luminance(ShootPair(tag + "_cave_water", false), pool);
            float poolOn = Luminance(ShootPair(tag + "_cave_water", true), pool);
            Record(label + ": cave water off " + F(poolOff) + " on " + F(poolOn));

            // The mouth, from outside and from inside: one strip of floor per cell.
            Aim(new RbxVector3(-26f, 7f, 2f), new RbxVector3(16f, 2f, 2f));
            float[] outsideOff = Strips(ShootPair(tag + "_mouth_outside", false), -2, 7);
            float[] outsideOn = Strips(ShootPair(tag + "_mouth_outside", true), -2, 7);
            Aim(new RbxVector3(36f, 5f, 2f), new RbxVector3(-10f, 2f, 2f));
            float[] insideOff = Strips(ShootPair(tag + "_mouth_inside", false), -2, 7);
            float[] insideOn = Strips(ShootPair(tag + "_mouth_inside", true), -2, 7);
            Record(label + ": mouth from outside, floor strips x=-2..7 off " + Join(outsideOff) + " on " + Join(outsideOn));
            Record(label + ": mouth from inside, floor strips x=-2..7 off " + Join(insideOff) + " on " + Join(insideOn));

            // The room under a one-cell roof, from its door; and its roof from above.
            Aim(new RbxVector3(-80f, 5f, -82f), new RbxVector3(-130f, 1f, -82f));
            RectInt roomFloor = Rect(new RbxVector3(-126f, 0f, -86f), new RbxVector3(-114f, 0f, -78f));
            float roomOff = Luminance(ShootPair(tag + "_room", false), roomFloor);
            float roomOn = Luminance(ShootPair(tag + "_room", true), roomFloor);
            Aim(new RbxVector3(-120f, 40f, -60f), new RbxVector3(-120f, 12f, -84f));
            RectInt roof = Rect(new RbxVector3(-130f, 12f, -92f), new RbxVector3(-110f, 12f, -76f));
            float roofOff = Luminance(ShootPair(tag + "_roof", false), roof);
            float roofOn = Luminance(ShootPair(tag + "_roof", true), roof);
            Record(label + ": room floor off " + F(roomOff) + " on " + F(roomOn) + " | its roof off " + F(roofOff) + " on "
                   + F(roofOn));

            // The hill top straight above the deep tunnel, and the open ground.
            Aim(new RbxVector3(70f, 70f, 40f), new RbxVector3(96f, 40f, 0f));
            RectInt hillTop = Rect(new RbxVector3(84f, 40f, -12f), new RbxVector3(108f, 40f, 12f));
            float hillOff = Luminance(ShootPair(tag + "_hill_top", false), hillTop);
            float hillOn = Luminance(ShootPair(tag + "_hill_top", true), hillTop);
            Aim(new RbxVector3(-60f, 10f, 60f), new RbxVector3(-60f, 0f, 30f));
            RectInt ground = Rect(new RbxVector3(-68f, 0f, 44f), new RbxVector3(-52f, 0f, 36f));
            float groundOff = Luminance(ShootPair(tag + "_open_ground", false), ground);
            float groundOn = Luminance(ShootPair(tag + "_open_ground", true), ground);
            Record(label + ": hill top off " + F(hillOff) + " on " + F(hillOn) + " | open ground off " + F(groundOff)
                   + " on " + F(groundOn));

            // The two blocks: one 26 cells into the tunnel, one in the open.
            Aim(new RbxVector3(92f, 5f, 2f), new RbxVector3(112f, 2f, -2f));
            RectInt caveBlock = PartRect(_caveBlock);
            float blockOff = Luminance(ShootPair(tag + "_cave_block", false), caveBlock);
            float blockOn = Luminance(ShootPair(tag + "_cave_block", true), caveBlock);
            Aim(new RbxVector3(-40f, 5f, 2f), new RbxVector3(-20f, 2f, -2f));
            RectInt openBlock = PartRect(_openBlock);
            float openBlockOff = Luminance(ShootPair(tag + "_open_block", false), openBlock);
            float openBlockOn = Luminance(ShootPair(tag + "_open_block", true), openBlock);
            Record(label + ": block in the tunnel off " + F(blockOff) + " on " + F(blockOn) + " | block in the open off "
                   + F(openBlockOff) + " on " + F(openBlockOn));

            Assert.Less(Mathf.Abs(hillOn - hillOff), 0.004f, label + ": the hill top above the cave does not change");
            Assert.Less(Mathf.Abs(groundOn - groundOff), 0.004f, label + ": the open ground does not change");
            Assert.Less(Mathf.Abs(roofOn - roofOff), 0.004f, label + ": a roof's top does not change");
            Assert.Less(Mathf.Abs(openBlockOn - openBlockOff), 0.004f, label + ": a block in the open does not change");
            if (assertDark)
            {
                // WHY from the first cell in: the sunlit ground outside meets the hill's shadow at the cliff face, a
                // line the sun draws whatever the sky does; the fade under rock is the sky's. With the day Ambient
                // the floor fades only to that Ambient, so the fade is checked where the occluded light is dark.
                AssertSoftFade(Inside(outsideOn), label + ": the mouth seen from outside");
                AssertSoftFade(Inside(insideOn), label + ": the mouth seen from inside");
                Assert.Less(deepOn, deepOff * 0.15f, label + ": the deep floor is an order of magnitude darker");
                Assert.Less(deepOn, Mathf.Max(midnightGround * 3f, 0.02f), label + ": near the midnight ground");
                Assert.Less(roomOn, roomOff * 0.3f, label + ": a one-cell roof lets no sky through");
                Assert.Less(poolOn, poolOff * 0.5f, label + ": cave water stops mirroring the noon sky");
                Assert.Less(blockOn, blockOff * 0.6f, label + ": a block deep in the tunnel is not left glowing");
                Assert.Greater(deepTorch, deepOn * 2f, label + ": the torch lights the floor");
                Assert.Greater(wallTorch, wallOn * 2f, label + ": the torch lights the wall beside it");
            }
        }

        /// <summary>
        /// Breaks the roof block over the room's middle and puts it back. One presenter pump after each write,
        /// the floor under the hole carries the new exposure in its vertex colours.
        /// </summary>
        private IEnumerator EditNextToTheRoom()
        {
            SetClock(12f, NightAmbient, DayOutdoor);
            yield return SettleAfterTheClock();
            SetExposure(true);
            Aim(new RbxVector3(-80f, 5f, -82f), new RbxVector3(-130f, 1f, -82f));
            RectInt underHole = Rect(new RbxVector3(-122f, 0f, -86f), new RbxVector3(-118f, 0f, -82f));
            RbxTerrainRenderer renderer = (RbxTerrainRenderer)_presenter.Renderer;
            float closed = Luminance(Shoot("edit_roof_closed.png"), underHole);
            int alphaClosed = FloorAlpha(renderer, -30, -21);
            long relights = renderer.LightRefreshCount;
            _terrain.Store.SetCell(-30, 2, -21, TerrainMaterials.Air, 0);
            _presenter.Pump();
            int alphaOpened = FloorAlpha(renderer, -30, -21);
            float opened = Luminance(Shoot("edit_roof_opened.png"), underHole);
            int pending = renderer.PendingChunkCount;
            _terrain.Store.SetCell(-30, 2, -21, Slot("Rock"), TerrainOccupancy.Full);
            _presenter.Pump();
            int alphaClosedAgain = FloorAlpha(renderer, -30, -21);
            float closedAgain = Luminance(Shoot("edit_roof_closed_again.png"), underHole);
            Record("edit: floor under the roof block alpha " + alphaClosed + " -> broken, one pump later " + alphaOpened
                   + " -> put back " + alphaClosedAgain + "; floor crop " + F(closed) + " -> " + F(opened) + " -> "
                   + F(closedAgain) + "; queued after the pump " + pending + ", light refreshes "
                   + (renderer.LightRefreshCount - relights));
            Assert.AreEqual(0, alphaClosed, "dark under the closed roof");
            Assert.Greater(alphaOpened, 150, "the floor under the hole sees the sky after the pump that re-meshed the roof");
            Assert.AreEqual(0, alphaClosedAgain, "putting the block back darkens it again");
            Assert.Greater(opened, closed * 2f);
            Assert.AreEqual(closed, closedAgain, 0.003f);
            yield return null;
        }

        /// <summary>The largest exposure alpha on the top face of the floor cell (x, -1, z).</summary>
        private static int FloorAlpha(RbxTerrainRenderer renderer, int x, int z)
        {
            Assert.IsTrue(renderer.TryGetChunkObject(TerrainChunkCoord.FromCell(x, -1, z), out GameObject chunk));
            Mesh mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Color32[] colors = mesh.colors32;
            int best = -1;
            for (int index = 0; index < vertices.Length; index += 4)
            {
                if (normals[index].y < 0.5f)
                {
                    continue;
                }

                RbxVector3 centre = RbxSpace.FromUnity((vertices[index] + vertices[index + 2]) * 0.5f);
                if (Mathf.RoundToInt(centre.Y / Cell) != 0 || Mathf.FloorToInt(centre.X / Cell) != x
                    || Mathf.FloorToInt(centre.Z / Cell) != z)
                {
                    continue;
                }

                for (int corner = 0; corner < 4; corner++)
                {
                    best = Mathf.Max(best, colors[index + corner].a);
                }
            }

            Assert.GreaterOrEqual(best, 0, "the floor face under (" + x + ", " + z + ") is drawn");
            return best;
        }

        // ---- the world ---------------------------------------------------------------------------------------

        private void BuildWorld()
        {
            byte rock = Slot("Rock");
            Fill(-48, -4, -48, 96, 3, 96, Slot("Ground"));
            Fill(-48, -1, -48, 96, 1, 96, Slot("Grass"));
            Fill(0, -1, -16, 32, 10, 32, rock);
            Fill(0, 9, -16, 32, 1, 32, Slot("Grass"));
            Fill(0, 0, -1, 30, 2, 3, TerrainMaterials.Air);
            Fill(16, -1, -1, 6, 1, 3, TerrainMaterials.Water);
            Fill(12, 2, 0, 1, 8, 1, TerrainMaterials.Air);
            Fill(-40, 0, -30, 20, 3, 20, rock);
            Fill(-39, 0, -29, 18, 2, 18, TerrainMaterials.Air);
            Fill(-21, 0, -21, 1, 2, 2, TerrainMaterials.Air);
            while (_presenter.Renderer.PendingChunkCount > 0)
            {
                _presenter.Pump();
            }

            GameObject torchObject = new("Cave torch");
            _owned.Add(torchObject);
            _torch = torchObject.AddComponent<Light>();
            _torch.type = LightType.Point;
            _torch.intensity = 2.2f;
            _torch.range = RbxSpace.LengthToUnity(26f);
            _torch.color = new Color(1f, 196f / 255f, 120f / 255f);
            _torch.shadows = LightShadows.None;
            torchObject.transform.position = RbxSpace.ToUnity(new RbxVector3(102f, 5f, 2f));
            _torch.enabled = false;
            _caveBlock = Block(new RbxVector3(106f, 1.5f, -1.5f));
            _openBlock = Block(new RbxVector3(-30f, 1.5f, -1.5f));
            for (int pass = 0; pass < 4; pass++)
            {
                _presenter.PumpPartExposure();
            }
        }

        private Renderer Block(RbxVector3 position)
        {
            RbxInstance part = _parts.Create("Part");
            part.Parent = _parts.WorldRoot;
            _binder.SetSize(part.Id, new RbxVector3(3f, 3f, 3f));
            _binder.SetPosition(part.Id, position);
            _binder.SetAnchored(part.Id, true);
            _binder.SetColor(part.Id, new RbxColor3(0.64f, 0.64f, 0.66f));
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject gameObject));
            Renderer renderer = gameObject.GetComponentInChildren<Renderer>();
            Assert.IsNotNull(renderer);
            return renderer;
        }

        /// <summary>
        /// Frames and one discarded shot after the sun and the ambient changed: the first render after a switch
        /// from midnight to noon came out blown to white, before the sky probe and the shadows caught up.
        /// </summary>
        private IEnumerator SettleAfterTheClock()
        {
            yield return Settle();
            Shoot("_discarded.png");
            yield return null;
        }

        private IEnumerator Settle()
        {
            for (int frame = 0; frame < 20; frame++)
            {
                yield return null;
            }
        }

        private void SetClock(float hours, RbxColor3 ambient, RbxColor3 outdoor)
        {
            RbxVector3 sun = RbxSunModel.SunDirection(hours, RbxLighting.DefaultGeographicLatitude);
            _sink.Apply(new RbxLightingState(hours, RbxLighting.DefaultGeographicLatitude, 1f, ambient, outdoor, sun,
                1f, 1f));
        }

        /// <summary>Off draws everything as fully exposed, the look before the sky exposure existed.</summary>
        private void SetExposure(bool on)
        {
            foreach (Renderer chunk in _presenter.Renderer is RbxTerrainRenderer terrain
                         ? terrain.Root.GetComponentsInChildren<Renderer>(true)
                         : new Renderer[0])
            {
                foreach (Material material in chunk.sharedMaterials)
                {
                    if (material != null && material.HasProperty(RbxTerrainRenderer.SkyExposureSourceProperty))
                    {
                        material.SetFloat(RbxTerrainRenderer.SkyExposureSourceProperty, on ? 1f : 0f);
                    }
                }
            }

            MaterialPropertyBlock block = new();
            foreach (Renderer part in new[] { _caveBlock, _openBlock })
            {
                part.GetPropertyBlock(block);
                block.SetFloat(RbxPartSkyExposurePass.SkyExposureProperty, 1f);
                part.SetPropertyBlock(block);
            }

            if (on)
            {
                _presenter.PartExposure.Apply(_caveBlock, ((RbxTerrainRenderer)_presenter.Renderer).SkyExposure);
                _presenter.PartExposure.Apply(_openBlock, ((RbxTerrainRenderer)_presenter.Renderer).SkyExposure);
            }
        }

        private void Fill(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte material)
        {
            _terrain.Store.FillBox(new TerrainCellBox(x, y, z, sizeX, sizeY, sizeZ), material,
                material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full);
        }

        private static byte Slot(string name)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(name, out byte slot), name + " is a terrain material");
            return slot;
        }

        // ---- shots and crops ---------------------------------------------------------------------------------

        private Color[] LastShot { get; set; }

        private Color[] LastPair { get; set; }

        private Color[] ShootPair(string name, bool on)
        {
            SetExposure(on);
            LastPair = Shoot(name + (on ? "_on.png" : "_off.png"));
            return LastPair;
        }

        private void Aim(RbxVector3 positionStuds, RbxVector3 targetStuds)
        {
            Vector3 position = RbxSpace.ToUnity(positionStuds);
            Vector3 target = RbxSpace.ToUnity(targetStuds);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(target - position);
        }

        private RectInt Rect(RbxVector3 firstStuds, RbxVector3 secondStuds)
        {
            Vector3 first = _camera.WorldToViewportPoint(RbxSpace.ToUnity(firstStuds));
            Vector3 second = _camera.WorldToViewportPoint(RbxSpace.ToUnity(secondStuds));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.x, second.x) * ShotWidth), 0, ShotWidth - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.x, second.x) * ShotWidth), xMin + 1, ShotWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.y, second.y) * ShotHeight), 0, ShotHeight - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.y, second.y) * ShotHeight), yMin + 1, ShotHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>The middle 60 percent of a part's projected box, so the crop holds only the part.</summary>
        private RectInt PartRect(Renderer part)
        {
            Bounds bounds = part.bounds;
            Vector3 front = _camera.WorldToViewportPoint(bounds.center);
            Vector3 extent = _camera.WorldToViewportPoint(bounds.center + _camera.transform.right * bounds.extents.x * 0.6f
                                                          + _camera.transform.up * bounds.extents.y * 0.6f);
            float halfWidth = Mathf.Abs(extent.x - front.x);
            float halfHeight = Mathf.Abs(extent.y - front.y);
            int xMin = Mathf.Clamp(Mathf.RoundToInt((front.x - halfWidth) * ShotWidth), 0, ShotWidth - 1);
            int yMin = Mathf.Clamp(Mathf.RoundToInt((front.y - halfHeight) * ShotHeight), 0, ShotHeight - 1);
            int width = Mathf.Max(1, Mathf.RoundToInt(halfWidth * 2f * ShotWidth));
            int height = Mathf.Max(1, Mathf.RoundToInt(halfHeight * 2f * ShotHeight));
            return new RectInt(xMin, yMin, Mathf.Min(width, ShotWidth - xMin), Mathf.Min(height, ShotHeight - yMin));
        }

        /// <summary>Mean luminance of a strip of tunnel floor one cell long, for cells first..last along +X.</summary>
        private float[] Strips(Color[] shot, int first, int last)
        {
            float[] values = new float[last - first + 1];
            for (int cell = first; cell <= last; cell++)
            {
                values[cell - first] = Luminance(shot, Rect(new RbxVector3(cell * Cell + 0.6f, 0f, -2f),
                    new RbxVector3(cell * Cell + 3.4f, 0f, 6f)));
            }

            return values;
        }

        /// <summary>The strips of cells 0..5, under the cliff, of a run that starts at cell -2.</summary>
        private static float[] Inside(float[] fromMinusTwo)
        {
            float[] inside = new float[6];
            System.Array.Copy(fromMinusTwo, 2, inside, 0, 6);
            return inside;
        }

        private static void AssertSoftFade(float[] strips, string what)
        {
            for (int index = 1; index < strips.Length; index++)
            {
                Assert.LessOrEqual(strips[index], strips[index - 1] * 1.1f + 0.003f, what + ": darker going in, strip " + index);
            }

            Assert.Less(strips[strips.Length - 1], strips[0] * 0.5f, what + ": it fades");
            float largestStep = 0f;
            for (int index = 1; index < strips.Length; index++)
            {
                largestStep = Mathf.Max(largestStep, strips[index - 1] - strips[index]);
            }

            Assert.Less(largestStep, (strips[0] - strips[strips.Length - 1]) * 0.6f, what + ": no hard line");
        }

        private Color[] Shoot(string fileName)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(_camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                linear.ReadPixels(new UnityEngine.Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                linear.Apply();
                Color[] pixels = linear.GetPixels();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color pixel = pixels[index];
                    pixels[index] = new Color(Mathf.Clamp01(pixel.r), Mathf.Clamp01(pixel.g), Mathf.Clamp01(pixel.b), 1f).gamma;
                }

                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
                LastShot = pixels;
                return pixels;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(linear);
                Object.Destroy(png);
            }
        }

        private static float BrightestTile(Color[] shot, RectInt crop, out RectInt brightest)
        {
            int size = Mathf.Max(4, Mathf.Min(crop.width, crop.height) / 4);
            float best = -1f;
            brightest = default;
            for (int y = crop.yMin; y + size <= crop.yMax; y += size)
            {
                for (int x = crop.xMin; x + size <= crop.xMax; x += size)
                {
                    RectInt tile = new(x, y, size, size);
                    float value = Luminance(shot, tile);
                    if (value > best)
                    {
                        best = value;
                        brightest = tile;
                    }
                }
            }

            return best;
        }

        private static float Luminance(Color[] shot, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    sum += 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private void Record(string line)
        {
            _report.AppendLine(line);
            TestContext.WriteLine("[CaveLight] " + line);
        }

        private static string F(float value)
        {
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static string Join(float[] values)
        {
            string[] parts = new string[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                parts[index] = F(values[index]);
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        private static void UsePackagedCatalogOnly(bool enabled)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(RbxTextureMaterialProvider).GetProperty("IgnoreProjectOverrideForTests", flags)?.SetValue(null, enabled);
            typeof(RbxTextureMaterialProvider).GetMethod("ResetSharedCacheForTests", flags)?.Invoke(null, null);
        }
    }
}

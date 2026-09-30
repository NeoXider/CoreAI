using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// Photographs representative <c>Enum.Material</c> surfaces from the angles where projection
    /// defects show: a cube corner (three faces meet), a ball, a drum seen from above, and two flush
    /// blocks beside a wedge (part-to-part continuity). Also renders the diagnostics behind the
    /// material fixes: an orientation glyph on every face, normals on/off pairs, relief at close, mid
    /// and far range, the procedural plastic grain, and metals in both a bare and a sky-lit
    /// environment. Frames land in <c>artifacts/materials/latest/</c>.
    /// </summary>
    /// <remarks>
    /// WHY a sibling of the CoreAiUnity sheet rather than an edit of it: the flat front-on sheet is
    /// the right tool for judging a texture set, and useless for judging seams, which only appear
    /// where projections meet. The category lets CI exclude these GPU-bound shots.
    /// </remarks>
    [TestFixture]
    [Category("MaterialInspection")]
    public sealed class RbxMaterialSeamSheetPlayModeTests
    {
        private const int ShotWidth = 1200;
        private const int ShotHeight = 675;
        private const float GroupSpacingStuds = 140f;
        private const string OcclusionKeyword = "_RBX_OCCLUSION_MAP";

        private static readonly string[] Materials =
        {
            "Brick", "WoodPlanks", "Wood", "Concrete", "Metal", "DiamondPlate", "CorrodedMetal",
            "Cobblestone", "Grass", "Marble", "Plastic", "SmoothPlastic", "Slate", "Sand", "Rock",
            "Granite", "Salt", "Fabric"
        };

        private static readonly string[] ReliefMaterials =
        {
            "Brick", "Cobblestone", "Concrete", "Rock", "Slate", "Wood"
        };

        private static readonly string[] Metals = { "Metal", "DiamondPlate", "CorrodedMetal" };

        private static readonly string[] GrainMaterials =
        {
            "Plastic", "SmoothPlastic", "Salt", "Snow", "Rubber"
        };

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
        private static readonly int BumpStrengthId = Shader.PropertyToID("_BumpStrength");
        private static readonly int CavityStrengthId = Shader.PropertyToID("_CavityStrength");
        private static readonly int GrainStrengthId = Shader.PropertyToID("_GrainStrength");
        private static readonly int TextureScaleId = Shader.PropertyToID("_TextureScale");
        private static readonly int TextureAspectId = Shader.PropertyToID("_TextureAspect");

        private readonly List<Object> _ownedObjects = new();
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxEnum _materialEnum;
        private Camera _camera;
        private string _outputFolder;
        private Color32[] _lastShot;

        private AmbientMode _savedAmbientMode;
        private Color _savedAmbientSky;
        private Color _savedAmbientEquator;
        private Color _savedAmbientGround;
        private Light _savedSun;

        [SetUp]
        public void CreateWorld()
        {
            _savedAmbientMode = RenderSettings.ambientMode;
            _savedAmbientSky = RenderSettings.ambientSkyColor;
            _savedAmbientEquator = RenderSettings.ambientEquatorColor;
            _savedAmbientGround = RenderSettings.ambientGroundColor;
            _savedSun = RenderSettings.sun;

            // WHY: a machine with the local 2K override catalog would otherwise photograph those sets
            // instead of the packaged 1K ones every player gets.
            UsePackagedCatalogOnly(true);
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _root = new GameObject("CoreAI_MaterialSeamWorld");
            _binder = new InstanceGameObjectBinder(_root.transform, null);
            _registry = new InstanceRegistry(
                null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_registry);
            Assert.IsTrue(RbxEnumRegistry.CreateWithBuiltins().TryGet("Material", out _materialEnum));

            GameObject cameraObject = new("MaterialSeamCamera");
            _ownedObjects.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 2000f;
            _camera.aspect = (float)ShotWidth / ShotHeight;

            GameObject sunObject = new("MaterialSeamSun");
            _ownedObjects.Add(sunObject);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            // WHY: the camera looks from +X/+Z/above, so the sun comes from that side too but not
            // square-on: the three cube faces then receive three different amounts of light and the
            // corner stays readable instead of flattening into one tone.
            sunObject.transform.rotation = Quaternion.Euler(48f, 238f, 0f);
            RenderSettings.sun = sun;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "materials", "latest");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            try
            {
                foreach (Object owned in _ownedObjects)
                {
                    if (owned != null)
                    {
                        Object.Destroy(owned);
                    }
                }

                _ownedObjects.Clear();
                if (_root != null)
                {
                    Object.Destroy(_root);
                }
            }
            finally
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientSkyColor = _savedAmbientSky;
                RenderSettings.ambientEquatorColor = _savedAmbientEquator;
                RenderSettings.ambientGroundColor = _savedAmbientGround;
                RenderSettings.sun = _savedSun;
                UsePackagedCatalogOnly(false);
            }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator RepresentativeMaterials_ArePhotographedWhereProjectionsMeet()
        {
            UseSkyEnvironment();
            StringBuilder report = new();
            report.AppendLine(DescribeEnvironment("sky"));

            List<MaterialGroup> groups = new();
            for (int i = 0; i < Materials.Length; i++)
            {
                groups.Add(BuildGroup(Materials[i], i * GroupSpacingStuds, null));
            }

            MaterialGroup redPlastic = BuildGroup("Plastic", Materials.Length * GroupSpacingStuds,
                new RbxColor3(0.77f, 0.16f, 0.16f));
            yield return null;
            yield return null;

            Dictionary<string, Color32[]> withRelief = new();
            foreach (MaterialGroup group in groups)
            {
                yield return Shoot(group, "seams-" + group.Material + ".png", report, 215f);
                withRelief[group.Material] = _lastShot;
            }

            yield return Shoot(redPlastic, "seams-PlasticRed.png", report, 215f);

            Dictionary<string, float> reliefContribution = new();
            foreach (string name in new[] { "Concrete", "Brick", "Marble", "Plastic", "Metal" })
            {
                MaterialGroup group = groups.Find(candidate => candidate.Material == name);
                bool[] mask = CaptureGroupMask(group);
                List<Material> originals = OverrideGroupMaterial(group, material =>
                {
                    material.SetFloat(BumpScaleId, 0f);
                    material.SetFloat(BumpStrengthId, 0f);
                    material.SetFloat(CavityStrengthId, 0f);
                    material.DisableKeyword(OcclusionKeyword);
                });
                yield return Shoot(group, "bumpoff-" + name + ".png", report, 215f);
                RestoreGroupMaterial(group, originals);
                reliefContribution[name] = MaskedMeanAbsoluteDifference(withRelief[name], _lastShot,
                    mask);
                report.AppendLine("relief contribution " + name + " (object pixels only): " +
                                  Format(reliefContribution[name]) + " over " + CountMask(mask) +
                                  " px");
            }

            MaterialGroup orientation = groups.Find(candidate => candidate.Material == "Brick");
            Texture2D glyph = CreateOrientationGlyph();
            _ownedObjects.Add(glyph);
            List<Material> glyphOriginals = OverrideGroupMaterial(orientation, material =>
            {
                material.SetTexture(BaseMapId, glyph);
                material.SetFloat(BumpScaleId, 0f);
                material.SetFloat(CavityStrengthId, 0f);
                material.DisableKeyword(OcclusionKeyword);
                material.SetFloat(TextureAspectId, 1f);
                material.SetFloat(TextureScaleId, 1f / (4f * RbxSpace.MetersPerStud));
            });
            yield return Shoot(orientation, "orientation-glyph.png", report, 215f);
            yield return Shoot(orientation, "orientation-glyph-back.png", report, 35f);
            RestoreGroupMaterial(orientation, glyphOriginals);

            Dictionary<string, float> skyMetal = new();
            foreach (string name in Metals)
            {
                MaterialGroup metal = groups.Find(candidate => candidate.Material == name);
                skyMetal[name] = MeasureBallLuminance(metal, "env-sky-" + name + ".png", report);
            }

            yield return null;
            UseBareEnvironment();
            report.AppendLine(DescribeEnvironment("bare"));
            yield return null;
            Dictionary<string, float> bareMetal = new();
            foreach (string name in Metals)
            {
                MaterialGroup metal = groups.Find(candidate => candidate.Material == name);
                bareMetal[name] = MeasureBallLuminance(metal, "env-bare-" + name + ".png", report);
                report.AppendLine(name + " ball luminance: sky=" + Format(skyMetal[name]) +
                                  " bare=" + Format(bareMetal[name]));
            }

            File.WriteAllText(Path.Combine(_outputFolder, "report.txt"), report.ToString());
            TestContext.WriteLine(report.ToString());

            // WHY: measured only over the photographed objects, so the sky and floor that fill most
            // of the frame no longer dilute the number. The lower bound catches a relief path that
            // silently died (a tenth-strength tangent frame, a filtered-away stipple); the upper
            // bound catches over-bumping, which on these pieces shows as noise long before it
            // reads as depth.
            // Marble (strength 0.55, no cavity) and polished Metal are meant to be nearly smooth.
            foreach (KeyValuePair<string, float> relief in reliefContribution)
            {
                Assert.Greater(relief.Value, 0.05f,
                    relief.Key + " renders identically with its relief switched off");
                Assert.Less(relief.Value, 16f, relief.Key + " relief is strong enough to read as noise");
            }

            Assert.Greater(reliefContribution["Brick"], 4f, "Brick relief is too weak");
            Assert.Greater(reliefContribution["Concrete"], 1f, "Concrete relief is too weak");

            // WHY: the photographed metal colour maps made Metal a near-black mirror (0.23 mean on
            // this ball); a polished metal must read as a bright reflective surface in both
            // environments. Corroded metal is rust, dark by nature, and only has to stay visible.
            foreach (string name in new[] { "Metal", "DiamondPlate" })
            {
                Assert.Greater(skyMetal[name], 0.3f, name + " renders too dark under the default sky");
                Assert.Greater(bareMetal[name], 0.3f, name + " renders too dark in the bare environment");
            }

            Assert.Greater(skyMetal["CorrodedMetal"], 0.12f, "CorrodedMetal renders near-black");
            Assert.Greater(bareMetal["CorrodedMetal"], 0.12f, "CorrodedMetal renders near-black");
        }

        /// <summary>
        /// Relief of six grooved and rough packaged sets at close (about 2 m), mid (8 m) and far
        /// (20 m) range, each with its relief off, with normals only, and with normals plus the
        /// baked cavity, so the three can be compared side by side. Numbers are object pixels only.
        /// </summary>
        /// <remarks>
        /// WHY: a normal map keeps its relief in thin bevels that the mip chain averages away, which
        /// is why brick joints stopped reading as recessed beyond a few metres. The far shots must
        /// still change visibly with the relief switched on, and the close shots must not show more
        /// normal-map contrast than a sane surface would.
        /// </remarks>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Relief_IsVisibleAtCloseMidAndFarRange()
        {
            UseSkyEnvironment();
            StringBuilder report = new();
            report.AppendLine("view state-pair: mean abs difference (0-255) over object pixels; " +
                              "edge = mean luminance gradient over object pixels");
            List<ReliefTriad> triads = new();
            for (int i = 0; i < ReliefMaterials.Length; i++)
            {
                triads.Add(BuildReliefTriad(ReliefMaterials[i], 4000f + i * 400f));
            }

            yield return null;
            yield return null;
            Dictionary<string, Dictionary<string, ReliefNumbers>> results = new();
            foreach (ReliefTriad triad in triads)
            {
                ShowOnly(triads, triad);
                Dictionary<string, ReliefNumbers> perView = new();
                foreach ((string view, Vector3 position, Vector3 target) in ReliefViews(triad))
                {
                    _camera.transform.position = position;
                    _camera.transform.rotation = Quaternion.LookRotation(target - position);
                    yield return null;
                    bool[] mask = CaptureMask(triad.Floor);
                    Dictionary<string, Color32[]> shots = new();
                    foreach (string state in new[] { "off", "normals", "full" })
                    {
                        Dictionary<Renderer, Material> originals = ApplyReliefState(triad, state);
                        yield return null;
                        shots[state] = Capture("relief/" + triad.Material + "-" + view + "-" + state +
                                               ".png", report);
                        RestoreMaterials(originals);
                    }

                    ReliefNumbers numbers = new()
                    {
                        Pixels = CountMask(mask),
                        NormalsVsOff = MaskedMeanAbsoluteDifference(shots["normals"], shots["off"], mask),
                        FullVsOff = MaskedMeanAbsoluteDifference(shots["full"], shots["off"], mask),
                        FullVsNormals = MaskedMeanAbsoluteDifference(shots["full"], shots["normals"],
                            mask),
                        EdgeOff = MaskedEdgeContrast(shots["off"], mask),
                        EdgeNormals = MaskedEdgeContrast(shots["normals"], mask),
                        EdgeFull = MaskedEdgeContrast(shots["full"], mask)
                    };
                    perView[view] = numbers;
                    report.AppendLine(triad.Material + " " + view + " px=" + numbers.Pixels +
                                      " normals-off=" + Format(numbers.NormalsVsOff) +
                                      " full-off=" + Format(numbers.FullVsOff) +
                                      " full-normals=" + Format(numbers.FullVsNormals) +
                                      " edge off/normals/full=" + Format(numbers.EdgeOff) + "/" +
                                      Format(numbers.EdgeNormals) + "/" + Format(numbers.EdgeFull));
                }

                results[triad.Material] = perView;
            }

            File.WriteAllText(Path.Combine(_outputFolder, "relief-report.txt"), report.ToString());
            TestContext.WriteLine(report.ToString());

            StringBuilder failures = new();
            foreach (KeyValuePair<string, Dictionary<string, ReliefNumbers>> material in results)
            {
                foreach (KeyValuePair<string, ReliefNumbers> view in material.Value)
                {
                    ReliefNumbers numbers = view.Value;
                    if (numbers.Pixels < 2000)
                    {
                        failures.AppendLine(material.Key + " " + view.Key + ": only " +
                                            numbers.Pixels + " object pixels in the shot");
                        continue;
                    }

                    if (numbers.FullVsOff < MinimumRelief(view.Key))
                    {
                        failures.AppendLine(material.Key + " " + view.Key + ": relief changes the " +
                                            "object by only " + Format(numbers.FullVsOff) + " levels");
                    }

                    if (numbers.NormalsVsOff > MaximumNormalRelief)
                    {
                        failures.AppendLine(material.Key + " " + view.Key + ": normal map changes the " +
                                            "object by " + Format(numbers.NormalsVsOff) +
                                            " levels, which reads as noise");
                    }
                }
            }

            Assert.IsEmpty(failures.ToString(), failures.ToString());
        }

        /// <summary>
        /// The 4.5 cm grain reaches Plastic, SmoothPlastic and Salt and nothing else: Snow and Rubber
        /// share those materials' shader modes but must render pixel-identically with the grain
        /// switched off.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ProceduralGrain_ReachesOnlyPlasticSmoothPlasticAndSalt()
        {
            UseSkyEnvironment();
            StringBuilder report = new();
            RbxProceduralMaterialProvider procedural = new();
            List<ReliefTriad> triads = new();
            for (int i = 0; i < GrainMaterials.Length; i++)
            {
                ReliefTriad triad = BuildReliefTriad(GrainMaterials[i], 9000f + i * 400f);
                Assert.IsTrue(_materialEnum.TryGetItem(GrainMaterials[i], out RbxEnumItem item));
                RbxMaterialId id = new(item.Name, item.Value);
                Assert.IsTrue(procedural.TryGetMaterial(in id, out Material proceduralMaterial));
                foreach (Renderer renderer in triad.Renderers)
                {
                    renderer.sharedMaterial = proceduralMaterial;
                }

                triads.Add(triad);
            }

            yield return null;
            Dictionary<string, float> grainContribution = new();
            Dictionary<string, float> bumpContribution = new();
            foreach (ReliefTriad triad in triads)
            {
                ShowOnly(triads, triad);
                foreach ((string view, Vector3 position, Vector3 target) in ReliefViews(triad))
                {
                    if (view != "close-cube" && view != "mid")
                    {
                        continue;
                    }

                    _camera.transform.position = position;
                    _camera.transform.rotation = Quaternion.LookRotation(target - position);
                    yield return null;
                    bool[] mask = CaptureMask(triad.Floor);
                    Color32[] full = Capture("grain/" + triad.Material + "-" + view + "-full.png",
                        report);
                    Dictionary<Renderer, Material> originals = OverrideRenderers(triad.Renderers,
                        material => material.SetFloat(GrainStrengthId, 0f));
                    yield return null;
                    Color32[] noGrain = Capture("grain/" + triad.Material + "-" + view +
                                                "-nograin.png", report);
                    RestoreMaterials(originals);
                    originals = OverrideRenderers(triad.Renderers,
                        material => material.SetFloat(BumpStrengthId, 0f));
                    yield return null;
                    Color32[] flat = Capture("grain/" + triad.Material + "-" + view + "-flat.png",
                        report);
                    RestoreMaterials(originals);

                    float grain = MaskedMeanAbsoluteDifference(full, noGrain, mask);
                    float bump = MaskedMeanAbsoluteDifference(full, flat, mask);
                    grainContribution[triad.Material + " " + view] = grain;
                    bumpContribution[triad.Material + " " + view] = bump;
                    report.AppendLine(triad.Material + " " + view + " grain=" + Format(grain) +
                                      " bump=" + Format(bump) + " edge full/flat=" +
                                      Format(MaskedEdgeContrast(full, mask)) + "/" +
                                      Format(MaskedEdgeContrast(flat, mask)));
                }
            }

            File.WriteAllText(Path.Combine(_outputFolder, "grain-report.txt"), report.ToString());
            TestContext.WriteLine(report.ToString());

            foreach (string view in new[] { "close-cube", "mid" })
            {
                Assert.AreEqual(0f, grainContribution["Snow " + view], "Snow picked up the grain");
                Assert.AreEqual(0f, grainContribution["Rubber " + view], "Rubber picked up the grain");
                Assert.Greater(grainContribution["Plastic " + view], 0f, "Plastic lost its grain");
                Assert.Greater(grainContribution["Salt " + view], 0f, "Salt lost its grain");
                // WHY: without the old determinant floor a pixel-scale stipple reaches full strength
                // up close; this caps it before it reads as sandpaper.
                foreach (string name in new[] { "Plastic", "SmoothPlastic", "Salt" })
                {
                    Assert.Less(bumpContribution[name + " " + view], 6f, name + " bump is too strong");
                }
            }
        }

        private const float MaximumNormalRelief = 24f;

        private static float MinimumRelief(string view)
        {
            // WHY: at 20 m a joint spans one or two pixels, so the whole-object change is smaller
            // than close up, but it must stay well above JPEG-and-MSAA noise (under 0.3 levels).
            // Concrete has no cavity map and is the weakest set here (about 1.8 at 20 m).
            return view == "far" ? 1f : 1.5f;
        }

        /// <summary>Numbers for one view of one material.</summary>
        private sealed class ReliefNumbers
        {
            public int Pixels;
            public float NormalsVsOff;
            public float FullVsOff;
            public float FullVsNormals;
            public float EdgeOff;
            public float EdgeNormals;
            public float EdgeFull;
        }

        /// <summary>A cube, a ball and a wedge in one material on a neutral floor.</summary>
        private sealed class ReliefTriad
        {
            public string Material;
            public Vector3 Center;
            public Vector3 Cube;
            public Vector3 Ball;
            public Vector3 Wedge;
            public readonly List<Renderer> Renderers = new();
            public Renderer Floor;
        }

        private const float TriadPartStuds = 6f;

        private ReliefTriad BuildReliefTriad(string material, float originX)
        {
            Assert.IsTrue(_materialEnum.TryGetItem(material, out RbxEnumItem item),
                "unknown Enum.Material." + material);
            RbxMaterialId id = new(item.Name, item.Value);
            // WHY: laid out square to the camera's viewing direction, so no piece hides another.
            // Roblox Z runs opposite to Unity Z, hence +0.57 here for (0.82, 0, -0.57) on screen.
            Vector3 across = new(0.82f, 0f, 0.57f);
            float spacingStuds = 9f;
            float half = TriadPartStuds * 0.5f;
            Vector3 centerStuds = new(originX, half, 0f);
            ReliefTriad triad = new() { Material = material };
            RbxVector3 size = new(TriadPartStuds, TriadPartStuds, TriadPartStuds);
            Vector3 cubeStuds = centerStuds - across * spacingStuds;
            Vector3 wedgeStuds = centerStuds + across * spacingStuds;
            RbxInstance cube = CreatePart(material + "_ReliefCube", id, null, RbxPartShape.Block, size,
                RbxCFrame.FromPosition(cubeStuds.x, cubeStuds.y, cubeStuds.z));
            RbxInstance ball = CreatePart(material + "_ReliefBall", id, null, RbxPartShape.Ball, size,
                RbxCFrame.FromPosition(centerStuds.x, centerStuds.y, centerStuds.z));
            RbxInstance wedge = CreatePart(material + "_ReliefWedge", id, null, RbxPartShape.Wedge,
                size, RbxCFrame.FromPosition(wedgeStuds.x, wedgeStuds.y, wedgeStuds.z));
            RbxInstance floor = CreatePart(material + "_ReliefFloor",
                new RbxMaterialId("SmoothPlastic", 272), new RbxColor3(0.36f, 0.37f, 0.39f),
                RbxPartShape.Block, new RbxVector3(60f, 1f, 60f),
                RbxCFrame.FromPosition(originX, -0.5f, 0f));
            foreach (RbxInstance part in new[] { cube, ball, wedge })
            {
                Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject bound));
                triad.Renderers.AddRange(bound.GetComponentsInChildren<Renderer>());
            }

            Assert.IsTrue(_binder.TryGetBoundObject(floor.Id, out GameObject floorObject));
            triad.Floor = floorObject.GetComponentInChildren<Renderer>();
            triad.Cube = BoundsCenter(cube);
            triad.Ball = BoundsCenter(ball);
            triad.Wedge = BoundsCenter(wedge);
            triad.Center = triad.Ball;
            return triad;
        }

        private Vector3 BoundsCenter(RbxInstance part)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject bound));
            return bound.GetComponentInChildren<Renderer>().bounds.center;
        }

        private static IEnumerable<(string View, Vector3 Position, Vector3 Target)> ReliefViews(
            ReliefTriad triad)
        {
            Vector3 viewDirection = Quaternion.Euler(24f, 215f, 0f) * Vector3.forward;
            float halfSize = TriadPartStuds * 0.5f * RbxSpace.MetersPerStud;
            // WHY 2 m from the nearest surface, 8 m and 20 m from the middle piece: arm's length,
            // across a room, and across a small plaza.
            float close = 2f + halfSize;
            yield return ("close-cube", triad.Cube - viewDirection * close, triad.Cube);
            yield return ("close-ball", triad.Ball - viewDirection * close, triad.Ball);
            yield return ("close-wedge", triad.Wedge - viewDirection * close, triad.Wedge);
            // WHY: a grazing look along the cube's +X face exaggerates every normal-map tilt, which
            // is where an over-strong map shows first.
            Vector3 faceCenter = triad.Cube + Vector3.right * halfSize;
            yield return ("graze", faceCenter + Vector3.right * 0.6f + Vector3.forward * 2.6f +
                                   Vector3.up * 0.2f, faceCenter + Vector3.back * 0.2f);
            yield return ("mid", triad.Center - viewDirection * 8f, triad.Center);
            yield return ("far", triad.Center - viewDirection * 20f, triad.Center);
        }

        private static void ShowOnly(List<ReliefTriad> triads, ReliefTriad visible)
        {
            foreach (ReliefTriad triad in triads)
            {
                bool show = triad == visible;
                foreach (Renderer renderer in triad.Renderers)
                {
                    renderer.enabled = show;
                }

                triad.Floor.enabled = show;
            }
        }

        private Dictionary<Renderer, Material> ApplyReliefState(ReliefTriad triad, string state)
        {
            if (state == "full")
            {
                return new Dictionary<Renderer, Material>();
            }

            return OverrideRenderers(triad.Renderers, material =>
            {
                material.SetFloat(CavityStrengthId, 0f);
                material.DisableKeyword(OcclusionKeyword);
                if (state == "off")
                {
                    material.SetFloat(BumpScaleId, 0f);
                }
            });
        }

        private Dictionary<Renderer, Material> OverrideRenderers(List<Renderer> renderers,
            Action<Material> edit)
        {
            Dictionary<Renderer, Material> originals = new();
            Dictionary<Material, Material> clones = new();
            foreach (Renderer renderer in renderers)
            {
                Material original = renderer.sharedMaterial;
                if (!clones.TryGetValue(original, out Material clone))
                {
                    clone = new Material(original);
                    edit(clone);
                    _ownedObjects.Add(clone);
                    clones[original] = clone;
                }

                originals[renderer] = original;
                renderer.sharedMaterial = clone;
            }

            return originals;
        }

        private static void RestoreMaterials(Dictionary<Renderer, Material> originals)
        {
            foreach (KeyValuePair<Renderer, Material> pair in originals)
            {
                pair.Key.sharedMaterial = pair.Value;
            }
        }

        /// <summary>
        /// Object pixels of the current view: pixels that stay the same when only the background
        /// changes from black to white, with the floor hidden. Antialiased silhouette pixels mix
        /// with the background and drop out, which keeps the mask conservative.
        /// </summary>
        private bool[] CaptureMask(Renderer floor)
        {
            CameraClearFlags savedFlags = _camera.clearFlags;
            Color savedBackground = _camera.backgroundColor;
            bool floorWasEnabled = floor != null && floor.enabled;
            try
            {
                if (floor != null)
                {
                    floor.enabled = false;
                }

                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = Color.black;
                Color32[] onBlack = Capture(null, null);
                _camera.backgroundColor = Color.white;
                Color32[] onWhite = Capture(null, null);
                bool[] mask = new bool[onBlack.Length];
                for (int i = 0; i < mask.Length; i++)
                {
                    mask[i] = Math.Abs(onBlack[i].r - onWhite[i].r) <= 1 &&
                              Math.Abs(onBlack[i].g - onWhite[i].g) <= 1 &&
                              Math.Abs(onBlack[i].b - onWhite[i].b) <= 1;
                }

                return mask;
            }
            finally
            {
                _camera.clearFlags = savedFlags;
                _camera.backgroundColor = savedBackground;
                if (floor != null)
                {
                    floor.enabled = floorWasEnabled;
                }
            }
        }

        /// <summary>Object mask for a seam-sheet group, photographed from the sheet's own view.</summary>
        private bool[] CaptureGroupMask(MaterialGroup group)
        {
            PlaceSheetCamera(group, 215f);
            return CaptureMask(group.Floor);
        }

        private static int CountMask(bool[] mask)
        {
            int count = 0;
            foreach (bool inside in mask)
            {
                if (inside)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Mean absolute per-channel difference over masked pixels, in 0-255 levels.</summary>
        private static float MaskedMeanAbsoluteDifference(Color32[] first, Color32[] second,
            bool[] mask)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (!mask[i])
                {
                    continue;
                }

                sum += Math.Abs(first[i].r - second[i].r) + Math.Abs(first[i].g - second[i].g) +
                       Math.Abs(first[i].b - second[i].b);
                count++;
            }

            return count == 0 ? 0f : (float)(sum / (count * 3.0));
        }

        /// <summary>
        /// Mean luminance gradient (central differences, 0-255 levels) over masked pixels whose
        /// four neighbours are masked too, so silhouettes do not count as surface detail.
        /// </summary>
        private static float MaskedEdgeContrast(Color32[] pixels, bool[] mask)
        {
            double sum = 0;
            int count = 0;
            for (int y = 1; y < ShotHeight - 1; y++)
            {
                for (int x = 1; x < ShotWidth - 1; x++)
                {
                    int i = y * ShotWidth + x;
                    if (!mask[i] || !mask[i - 1] || !mask[i + 1] || !mask[i - ShotWidth] ||
                        !mask[i + ShotWidth])
                    {
                        continue;
                    }

                    double gx = Luma(pixels[i + 1]) - Luma(pixels[i - 1]);
                    double gy = Luma(pixels[i + ShotWidth]) - Luma(pixels[i - ShotWidth]);
                    sum += Math.Sqrt(gx * gx + gy * gy) * 0.5;
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private static double Luma(Color32 pixel)
        {
            return 0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b;
        }

        /// <summary>
        /// Every face of a block, photographed square-on, shows the map upright and unmirrored: its U
        /// grows to the viewer's right and its V upward (top and bottom as folded over the front).
        /// </summary>
        /// <remarks>
        /// WHY chromaticity: the probe map keeps blue constant while red follows U and green follows
        /// V, so red/blue and green/blue cancel whatever light falls on the face and only the mapping
        /// is measured. Before the fix every face failed one of the two checks — each side was either
        /// mirrored or upside down, which is what made corners look reflected.
        /// </remarks>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator BlockFaces_ShowTheMapUprightAndUnmirrored()
        {
            UseSkyEnvironment();
            Assert.IsTrue(_materialEnum.TryGetItem("Brick", out RbxEnumItem brick));
            RbxInstance part = CreatePart("OrientationProbe", new RbxMaterialId(brick.Name, brick.Value),
                null, RbxPartShape.Block, new RbxVector3(8f, 8f, 8f),
                RbxCFrame.FromPosition(0f, 40f, 0f));
            yield return null;
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject block));
            Renderer renderer = block.GetComponentInChildren<Renderer>();
            Material probe = new(renderer.sharedMaterial);
            _ownedObjects.Add(probe);
            Texture2D gradient = CreateChromaticityProbe();
            _ownedObjects.Add(gradient);
            probe.SetTexture(BaseMapId, gradient);
            probe.SetFloat(BumpScaleId, 0f);
            probe.SetFloat(CavityStrengthId, 0f);
            // WHY: the Brick occlusion map would still shade the ambient-lit bottom face in a brick
            // pattern and skew its chromaticity.
            probe.DisableKeyword(OcclusionKeyword);
            probe.SetFloat(TextureAspectId, 1f);
            // WHY 64 studs per tile: an 8-stud face then spans an eighth of a tile. Top and bottom
            // put U = 0 and V = 0 at the face centre and the sides put V = 0 at mid-height, so the
            // probe points sit right of and above the centre, where no pair straddles a wrap.
            probe.SetFloat(TextureScaleId, 1f / (64f * RbxSpace.MetersPerStud));
            renderer.sharedMaterial = probe;

            Transform frame = block.transform;
            (Vector3 Normal, Vector3 Up, string Name)[] faces =
            {
                (Vector3.right, Vector3.up, "+X"), (Vector3.left, Vector3.up, "-X"),
                (Vector3.forward, Vector3.up, "+Z"), (Vector3.back, Vector3.up, "-Z"),
                (Vector3.up, Vector3.back, "top"), (Vector3.down, Vector3.forward, "bottom")
            };
            StringBuilder failures = new();
            foreach ((Vector3 normal, Vector3 up, string name) in faces)
            {
                Vector3 worldNormal = frame.TransformDirection(normal);
                Vector3 worldUp = frame.TransformDirection(up);
                _camera.transform.position = renderer.bounds.center + worldNormal * 6f;
                _camera.transform.rotation = Quaternion.LookRotation(-worldNormal, worldUp);
                yield return null;
                Color32[] pixels = Capture("orientation-probe-" + name + ".png", new StringBuilder());
                float leftU = Chromaticity(pixels, 0.54f, 0.61f).x;
                float rightU = Chromaticity(pixels, 0.62f, 0.61f).x;
                float lowerV = Chromaticity(pixels, 0.58f, 0.56f).y;
                float upperV = Chromaticity(pixels, 0.58f, 0.66f).y;
                if (rightU <= leftU)
                {
                    failures.AppendLine(name + ": U does not grow to the right (" + Format(leftU) +
                                        " -> " + Format(rightU) + "), the map is mirrored");
                }

                if (upperV <= lowerV)
                {
                    failures.AppendLine(name + ": V does not grow upward (" + Format(lowerV) +
                                        " -> " + Format(upperV) + "), the map is upside down");
                }
            }

            Assert.IsEmpty(failures.ToString(), failures.ToString());
        }

        /// <summary>Red/blue and green/blue averaged over a small window centred on a viewport point.</summary>
        private static Vector2 Chromaticity(Color32[] pixels, float viewportX, float viewportY)
        {
            int cx = (int)(viewportX * ShotWidth);
            int cy = (int)(viewportY * ShotHeight);
            double red = 0;
            double green = 0;
            double blue = 0;
            for (int y = cy - 3; y <= cy + 3; y++)
            {
                for (int x = cx - 3; x <= cx + 3; x++)
                {
                    Color32 pixel = pixels[y * ShotWidth + x];
                    red += pixel.r;
                    green += pixel.g;
                    blue += pixel.b;
                }
            }

            blue = Math.Max(blue, 1.0);
            return new Vector2((float)(red / blue), (float)(green / blue));
        }

        /// <summary>A map whose red follows U and green follows V over constant blue.</summary>
        private static Texture2D CreateChromaticityProbe()
        {
            const int size = 256;
            Texture2D texture = new(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "ChromaticityProbe",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = new Color32((byte)(40 + x * 200 / size),
                        (byte)(40 + y * 200 / size), 120, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        /// <summary>One material's pieces; the ball is kept apart for luminance probes.</summary>
        private sealed class MaterialGroup
        {
            public string Material;
            public readonly List<RbxInstance> Parts = new();
            public RbxInstance Ball;
            public Renderer Floor;
        }

        private MaterialGroup BuildGroup(string material, float originX, RbxColor3? color)
        {
            Assert.IsTrue(_materialEnum.TryGetItem(material, out RbxEnumItem item),
                "unknown Enum.Material." + material);
            RbxMaterialId id = new(item.Name, item.Value);
            MaterialGroup group = new() { Material = material };
            // WHY this layout: the flush pair, the block stacked on it and the wedge share faces,
            // which is the only place part-to-part continuity can be judged; the cube corner shows
            // three faces meeting, and the ball and drum sweep the normal through every boundary.
            group.Parts.Add(CreatePart(material + "_PairA", id, color, RbxPartShape.Block,
                new RbxVector3(6f, 6f, 6f), RbxCFrame.FromPosition(originX - 30f, 3f, 0f)));
            group.Parts.Add(CreatePart(material + "_PairB", id, color, RbxPartShape.Block,
                new RbxVector3(6f, 6f, 6f), RbxCFrame.FromPosition(originX - 24f, 3f, 0f)));
            group.Parts.Add(CreatePart(material + "_PairTop", id, color, RbxPartShape.Block,
                new RbxVector3(6f, 4f, 6f), RbxCFrame.FromPosition(originX - 30f, 8f, 0f)));
            group.Parts.Add(CreatePart(material + "_Wedge", id, color, RbxPartShape.Wedge,
                new RbxVector3(6f, 6f, 6f), RbxCFrame.FromPosition(originX - 24f, 3f, -6f)));
            group.Parts.Add(CreatePart(material + "_Cube", id, color, RbxPartShape.Block,
                new RbxVector3(8f, 8f, 8f), RbxCFrame.FromPosition(originX - 12f, 4f, 0f)));
            group.Ball = CreatePart(material + "_Ball", id, color, RbxPartShape.Ball,
                new RbxVector3(8f, 8f, 8f), RbxCFrame.FromPosition(originX, 4f, 0f));
            group.Parts.Add(group.Ball);
            group.Parts.Add(CreatePart(material + "_Drum", id, color, RbxPartShape.Cylinder,
                new RbxVector3(8f, 7f, 7f),
                RbxCFrame.FromPosition(originX + 11f, 4f, 0f) *
                RbxCFrame.Angles(0f, 0f, Mathf.PI * 0.5f)));
            // WHY: a neutral floor gives the pieces contact shadows and a horizon to read against;
            // it stays out of Parts so it never widens the framing.
            RbxInstance floor = CreatePart(material + "_Floor", new RbxMaterialId("SmoothPlastic", 272),
                new RbxColor3(0.36f, 0.37f, 0.39f), RbxPartShape.Block,
                new RbxVector3(90f, 1f, 50f), RbxCFrame.FromPosition(originX - 9f, -0.5f, -3f));
            Assert.IsTrue(_binder.TryGetBoundObject(floor.Id, out GameObject floorObject));
            group.Floor = floorObject.GetComponentInChildren<Renderer>();
            return group;
        }

        private RbxInstance CreatePart(string name, in RbxMaterialId material, RbxColor3? color,
            RbxPartShape shape, RbxVector3 size, in RbxCFrame cframe)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = name;
            part.Parent = _registry.WorldRoot;
            _binder.SetAnchored(part.Id, true);
            _binder.SetShape(part.Id, shape);
            _binder.SetSize(part.Id, size);
            _binder.SetCFrame(part.Id, cframe);
            _binder.SetMaterial(part.Id, material);
            if (color.HasValue)
            {
                _binder.SetColor(part.Id, color.Value);
            }

            return part;
        }

        private IEnumerator Shoot(MaterialGroup group, string fileName, StringBuilder report,
            float yaw)
        {
            PlaceSheetCamera(group, yaw);
            yield return null;
            _lastShot = Capture(fileName, report);
        }

        private void PlaceSheetCamera(MaterialGroup group, float yaw)
        {
            Bounds bounds = GroupBounds(group);
            // WHY 24 degrees of pitch and a corner yaw: the cube then shows front, side and top at
            // once and the drum's cap is visible.
            Quaternion view = Quaternion.Euler(24f, yaw, 0f);
            float verticalHalf = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            // WHY 0.62: the row is long and low, so its bounding sphere is mostly sky; the factor
            // was tuned by eye until the row filled the frame without clipping the ends.
            float distance = bounds.extents.magnitude * 0.62f / Mathf.Sin(verticalHalf);
            _camera.transform.rotation = view;
            _camera.transform.position = bounds.center - view * Vector3.forward * distance;
        }

        private Bounds GroupBounds(MaterialGroup group)
        {
            Bounds bounds = new();
            bool started = false;
            foreach (Renderer renderer in GroupRenderers(group))
            {
                if (started)
                {
                    bounds.Encapsulate(renderer.bounds);
                }
                else
                {
                    bounds = renderer.bounds;
                    started = true;
                }
            }

            Assert.IsTrue(started, group.Material + " rendered nothing");
            return bounds;
        }

        private List<Material> OverrideGroupMaterial(MaterialGroup group, Action<Material> edit)
        {
            List<Material> originals = new();
            foreach (Renderer renderer in GroupRenderers(group))
            {
                originals.Add(renderer.sharedMaterial);
                Material clone = new(renderer.sharedMaterial);
                edit(clone);
                _ownedObjects.Add(clone);
                renderer.sharedMaterial = clone;
            }

            return originals;
        }

        private void RestoreGroupMaterial(MaterialGroup group, List<Material> originals)
        {
            List<Renderer> renderers = GroupRenderers(group);
            for (int i = 0; i < renderers.Count && i < originals.Count; i++)
            {
                renderers[i].sharedMaterial = originals[i];
            }
        }

        private List<Renderer> GroupRenderers(MaterialGroup group)
        {
            List<Renderer> renderers = new();
            foreach (RbxInstance part in group.Parts)
            {
                if (_binder.TryGetBoundObject(part.Id, out GameObject gameObject))
                {
                    renderers.AddRange(gameObject.GetComponentsInChildren<Renderer>());
                }
            }

            return renderers;
        }

        /// <summary>Mean luminance of the central disc of the group's ball, photographed alone.</summary>
        private float MeasureBallLuminance(MaterialGroup group, string fileName,
            StringBuilder report)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(group.Ball.Id, out GameObject ball));
            Bounds bounds = ball.GetComponentInChildren<Renderer>().bounds;
            Quaternion view = Quaternion.Euler(20f, 215f, 0f);
            _camera.transform.rotation = view;
            _camera.transform.position = bounds.center - view * Vector3.forward *
                (bounds.extents.magnitude * 2.2f);
            Color32[] pixels = Capture(fileName, report);
            // WHY: WorldToScreenPoint reports pixels of the camera's current target, which is the
            // screen here, not the shot; normalise through viewport space instead.
            Vector3 center = _camera.WorldToViewportPoint(bounds.center);
            Vector3 edge = _camera.WorldToViewportPoint(bounds.center +
                                                        _camera.transform.right * bounds.extents.x);
            float cx = center.x * ShotWidth;
            float cy = center.y * ShotHeight;
            float radius = Mathf.Abs(edge.x - center.x) * ShotWidth * 0.6f;
            double sum = 0;
            int count = 0;
            for (int y = Mathf.Max(0, (int)(cy - radius)); y < Mathf.Min(ShotHeight, cy + radius); y++)
            {
                for (int x = Mathf.Max(0, (int)(cx - radius)); x < Mathf.Min(ShotWidth, cx + radius); x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    if (dx * dx + dy * dy > radius * radius)
                    {
                        continue;
                    }

                    Color32 pixel = pixels[y * ShotWidth + x];
                    sum += (0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b) / 255.0;
                    count++;
                }
            }

            return count > 0 ? (float)(sum / count) : 0f;
        }

        private void UseSkyEnvironment()
        {
            // WHY: this is what a CoreAI demo scene ships with — the default procedural sky drives
            // ambient light and the default reflection — so it is the environment players see.
            _camera.clearFlags = CameraClearFlags.Skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
        }

        private void UseBareEnvironment()
        {
            // WHY: the flat trilight setup the CoreAiUnity contact sheet uses, where metals were
            // photographed nearly black; kept as the second environment so a fix is proven in both.
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.55f, 0.71f, 0.87f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.34f, 0.39f, 0.47f);
            RenderSettings.ambientEquatorColor = new Color(0.25f, 0.26f, 0.28f);
            RenderSettings.ambientGroundColor = new Color(0.15f, 0.14f, 0.13f);
        }

        private static string DescribeEnvironment(string label)
        {
            Texture reflection = ReflectionProbe.defaultTexture;
            return "[" + label + "] ambientMode=" + RenderSettings.ambientMode +
                   " skybox=" + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "null") +
                   " reflectionMode=" + RenderSettings.defaultReflectionMode +
                   " customReflection=" + (RenderSettings.customReflectionTexture != null
                       ? RenderSettings.customReflectionTexture.name
                       : "null") +
                   " defaultReflection=" + (reflection != null
                       ? reflection.name + " " + reflection.width + "px"
                       : "null") +
                   " reflectionIntensity=" + Format(RenderSettings.reflectionIntensity) +
                   " ambientProbeDC=" + Format(RenderSettings.ambientProbe[0, 0]);
        }

        /// <summary>
        /// Switches the texture provider between the packaged catalog and the project override
        /// through its internal test seam; the seam is internal to keep it out of the public API.
        /// </summary>
        private static void UsePackagedCatalogOnly(bool enabled)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            PropertyInfo ignoreOverride = typeof(RbxTextureMaterialProvider).GetProperty(
                "IgnoreProjectOverrideForTests", flags);
            MethodInfo resetCache = typeof(RbxTextureMaterialProvider).GetMethod(
                "ResetSharedCacheForTests", flags);
            Assert.IsNotNull(ignoreOverride, "RbxTextureMaterialProvider lost its override test seam");
            Assert.IsNotNull(resetCache, "RbxTextureMaterialProvider lost its cache reset test seam");
            ignoreOverride.SetValue(null, enabled);
            resetCache.Invoke(null, null);
        }

        /// <summary>A 256px map whose "F" reads correctly only when the face shows the map upright
        /// and unmirrored; red grows with U and green with V so a flipped axis is visible too.</summary>
        private static Texture2D CreateOrientationGlyph()
        {
            const int size = 256;
            Texture2D texture = new(size, size, TextureFormat.RGBA32, true)
            {
                name = "OrientationGlyph",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool border = x < 6 || y < 6 || x >= size - 6 || y >= size - 6;
                    bool glyph = (x >= 64 && x < 100 && y >= 40 && y < 216) ||
                                 (x >= 64 && x < 196 && y >= 180 && y < 216) ||
                                 (x >= 64 && x < 160 && y >= 112 && y < 144);
                    byte r = (byte)(60 + x * 150 / size);
                    byte g = (byte)(60 + y * 150 / size);
                    pixels[y * size + x] = glyph ? new Color32(250, 250, 250, 255)
                        : border ? new Color32(20, 20, 20, 255)
                        : new Color32(r, g, 70, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        /// <summary>Renders the camera; writes a PNG under the output folder when a name is given.</summary>
        private Color32[] Capture(string fileName, StringBuilder report)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4
            };
            Texture2D image = new(ShotWidth, ShotHeight, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = _camera.targetTexture;
            try
            {
                _camera.targetTexture = target;
                // WHY: Camera.Render() bypasses the URP light loop (ambient only, no sun); the SRP
                // render request is the supported path and also works under -batchmode.
                RenderPipeline.StandardRequest request = new() { destination = target };
                if (RenderPipeline.SupportsRenderRequest(_camera, request))
                {
                    RenderPipeline.SubmitRenderRequest(_camera, request);
                }
                else
                {
                    _camera.Render();
                }

                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                image.Apply();
                if (!string.IsNullOrEmpty(fileName))
                {
                    string path = Path.Combine(_outputFolder, fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _outputFolder);
                    File.WriteAllBytes(path, image.EncodeToPNG());
                    report?.AppendLine("shot " + fileName);
                }

                return image.GetPixels32();
            }
            finally
            {
                _camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live materials-and-shapes benchmark: one natural-language prompt makes the built-in Programmer
    /// build a medieval castle through the Rbx API (<c>Instance.new("Part")</c>, <c>Material</c>,
    /// <c>Shape</c>), and the scene is then measured through Lua — distinct <c>Enum.Material</c> values,
    /// all five <c>Enum.PartType</c> shapes, part count — and photographed to
    /// <c>artifacts/castle-showcase-&lt;model&gt;.png</c> for a human verdict on the materials.
    /// Self-skips when no live backend is configured (see RUNNING_LIVE_TESTS.md).
    /// </summary>
    public sealed class RbxCastleMaterialsShowcaseLivePlayModeTests
    {
        private const string CastlePrefix = "Castle";
        private const int MinParts = 40;
        private const int MinDistinctMaterials = 12;
        private const int RequiredShapes = 5;

        // WHY: one giant execute_lua call asks a small local model for ~10k tokens in a single
        // generation, which is where ling-3.0-tiny wedged for the whole 1800s transport budget. Section
        // by section is also how the G6 benchmark phrases it, so both graders exercise the same shape.
        private const string Prompt =
            "Build a medieval castle with the Roblox API, SECTION BY SECTION. Make one execute_lua call " +
            "per section and keep calling until the castle is finished; each call should be SHORT (about " +
            "8-14 parts) so it never runs long. Sections, in order: (1) grass ground plate and a sand path, " +
            "(2) the four outer walls in Cobblestone, (3) four round Cylinder towers in Limestone or " +
            "Granite, (4) their conical roofs from CornerWedge quadrants plus a Ball finial on each, " +
            "(5) the gatehouse with a Metal portcullis and a WoodPlanks drawbridge, (6) the keep with a " +
            "Wedge gable roof in Slate or ClayRoofTiles, (7) courtyard props on Marble - a well, crates, " +
            "barrels, benches, (8) Fabric banners, Neon torches, Glass windows and Wood beams.\n\n" +
            "Rules for every part: Instance.new('Part'), a distinct Name starting with 'Castle', explicit " +
            "Size (Vector3) and CFrame, Anchored = true, Parent = workspace. One unit is one stud; keep the " +
            "whole castle inside x/z of -64..64 and y of 0..96, footprint about 40 x 40 studs. A vertical " +
            "Cylinder needs CFrame.Angles(0, 0, math.rad(90)) because a Cylinder runs along X.\n\n" +
            "The scene is graded on at least 40 parts, at least 12 DIFFERENT Enum.Material values, and all " +
            "five Enum.PartType shapes (Block, Cylinder, Wedge, CornerWedge, Ball). Pick the material each " +
            "surface would really be made of and give it a natural Color3.fromRGB tint that keeps the " +
            "texture visible. Lua 5.2 syntax only: no '+=', no 'continue', no type annotations.\n\n" +
            "BEFORE YOU FINISH: run one more execute_lua that walks workspace:GetDescendants() and " +
            "returns the set of Enum.PartType and Enum.Material values you actually used. If any of " +
            "the five shapes is missing, add parts that use it - CornerWedge is the one most often " +
            "forgotten, so put it in conical roof quadrants or corner braces - and check again.";

        private const string MeasureLua =
            "local parts, mats, shapes = 0, {}, {}\n" +
            "for _, inst in ipairs(workspace:GetDescendants()) do\n" +
            "  if inst:IsA('Part') and string.sub(inst.Name, 1, 6) == 'Castle' then\n" +
            "    parts = parts + 1\n" +
            "    mats[(string.gsub(tostring(inst.Material), '^Enum%.%w+%.', ''))] = true\n" +
            "    shapes[(string.gsub(tostring(inst.Shape), '^Enum%.%w+%.', ''))] = true\n" +
            "  end\n" +
            "end\n" +
            "local m, s = {}, {}\n" +
            "for k in pairs(mats) do table.insert(m, k) end\n" +
            "for k in pairs(shapes) do table.insert(s, k) end\n" +
            "table.sort(m); table.sort(s)\n" +
            "return parts .. '|' .. table.concat(m, ',') .. '|' .. table.concat(s, ',')";

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            yield return null;
        }

        [UnityTest]
        [Timeout(3600000)]
        public IEnumerator Programmer_BuildsCastle_WithTwelveMaterialsAndAllFiveShapes()
        {
            // WHY: [UnitySetUp] runs in a different LogAssert scope than the [UnityTest] body in PlayMode.
            LogAssert.ignoreFailingMessages = true;
            TestContext.WriteLine("[CastleShowcase] === TEST START ===");

            // WHY: per-request, not per-run. A section takes a small local model a couple of minutes;
            // 600s aborts a wedged generation while the 3000s run budget still allows eight sections.
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            ProgrammerLiveHarness.Setup setup = null;
            GameObject rig = null;
            using CancellationTokenSource cts = new();
            try
            {
                TestContext.WriteLine($"[CastleShowcase] Backend: {handle.ResolvedBackend}");
                setup = ProgrammerLiveHarness.Build(handle, 3000);

                TestContext.WriteLine($"[CastleShowcase] Prompt: {Prompt}");
                Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Programmer,
                    Hint = Prompt
                }, cts.Token);
                yield return PlayModeTestAwait.WaitTask(task, 3000f, "Programmer castle showcase", cts);

                ProgrammerLiveHarness.LogToolCallTranscript("CastleShowcase");
                Assert.IsTrue(setup.Capturing.LastResult == null || setup.Capturing.LastResult.Ok,
                    $"Programmer run failed: {setup.Capturing.LastResult?.Error}");

                // Let the binder materialize every part and the texture provider bind its materials.
                yield return null;
                yield return null;

                Task<LuaTool.LuaResult> measure =
                    setup.Stack.ToolExecutor.ExecuteAsync(MeasureLua, setup.ActorContext, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(measure, 60f, "Castle measurement", cts);
                Assert.IsTrue(measure.Result.Success, $"Measurement Lua failed: {measure.Result.Error}");

                string[] fields = (measure.Result.Output ?? "").Split('|');
                Assert.AreEqual(3, fields.Length, $"Unexpected measurement output: {measure.Result.Output}");
                int parts = int.Parse(fields[0]);
                string[] materials = fields[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                string[] shapes = fields[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                TestContext.WriteLine($"[CastleShowcase] parts={parts} materials({materials.Length})=" +
                                      $"{string.Join(",", materials)} shapes({shapes.Length})={string.Join(",", shapes)}");

                // WHY: the model picks its own footprint and height, so a hard-coded camera either cuts
                // the castle off or photographs empty sky. Framing the built geometry keeps the hero shot
                // usable whatever the model builds.
                Bounds built = MeasureBuiltBounds(setup);
                rig = BuildCameraRig(built);
                Camera camera = rig.GetComponentInChildren<Camera>();
                string modelName = SafeModelName(handle);
                PlayModeCameraShot.Capture(camera,
                    PlayModeCameraShot.ArtifactPath("castle-showcase-" + modelName + ".png"));

                // WHY: the establishing shot fits the whole castle, and at that distance one texture tile
                // is a few pixels wide - it cannot show whether the materials carry real relief. The
                // second frame walks the camera up to the wall, which is where normals and tiling read.
                MoveToDetailView(camera, built);
                PlayModeCameraShot.Capture(camera,
                    PlayModeCameraShot.ArtifactPath("castle-showcase-" + modelName + "-detail.png"));

                Assert.GreaterOrEqual(parts, MinParts, "the castle must be built from at least " + MinParts + " Castle* parts");
                Assert.GreaterOrEqual(materials.Length, MinDistinctMaterials,
                    $"expected at least {MinDistinctMaterials} distinct Enum.Material values, got: {string.Join(",", materials)}");
                Assert.AreEqual(RequiredShapes, shapes.Length,
                    $"expected all five Enum.PartType shapes, got: {string.Join(",", shapes)}");
                TestContext.WriteLine("[CastleShowcase] TEST PASSED");
            }
            finally
            {
                cts.Cancel();
                if (rig != null)
                {
                    UnityEngine.Object.Destroy(rig);
                }

                setup?.Dispose();
                handle.Dispose();
            }
        }

        /// <summary>Model id reduced to a file-name-safe token for the screenshot name.</summary>
        private static string SafeModelName(PlayModeProductionLikeLlmHandle handle)
        {
            string model = handle.ResolvedConfig?.Model ?? "asset-model";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                model = model.Replace(c, '_');
            }

            return model.Replace('/', '_');
        }

        /// <summary>Walks the camera up to the south-west corner for a texel-level look at the surfaces.</summary>
        private static void MoveToDetailView(Camera camera, Bounds bounds)
        {
            // Aim at the middle of the south-west face at mid-height, and stand back by the castle's own
            // height: close enough for texels, far enough not to end up inside a tower.
            Vector3 target = new(
                bounds.center.x - bounds.extents.x * 0.45f,
                bounds.min.y + bounds.size.y * 0.45f,
                bounds.center.z - bounds.extents.z * 0.45f);
            Vector3 eye = target + new Vector3(-1f, 0.32f, -1f).normalized
                * Mathf.Max(bounds.size.y * 1.35f, 2.5f);
            camera.transform.position = eye;
            camera.transform.LookAt(target);
        }

        /// <summary>World-space bounds of everything the model materialized, or a small default.</summary>
        private static Bounds MeasureBuiltBounds(ProgrammerLiveHarness.Setup setup)
        {
            Renderer[] renderers = setup.WorldHostObject != null
                ? setup.WorldHostObject.GetComponentsInChildren<Renderer>()
                : Array.Empty<Renderer>();
            Bounds bounds = default;
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                    continue;
                }

                bounds.Encapsulate(renderer.bounds);
            }

            TestContext.WriteLine(any
                ? $"[CastleShowcase] built bounds center={bounds.center} size={bounds.size}"
                : "[CastleShowcase] nothing materialized; framing the origin");
            return any ? bounds : new Bounds(Vector3.zero, new Vector3(12f, 6f, 12f));
        }

        /// <summary>A camera and a key light framing <paramref name="bounds"/> from the south-west.</summary>
        private static GameObject BuildCameraRig(Bounds bounds)
        {
            GameObject rig = new("CastleShowcaseRig");
            GameObject cameraObject = new("CastleShowcaseCamera");
            cameraObject.transform.SetParent(rig.transform);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 42f;
            // WHY: a code-built PlayMode scene has no skybox, so CameraClearFlags.Skybox paints the flat
            // ambient grey the first hero shots came out on. An explicit sky colour reads as daylight.
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.71f, 0.87f);

            float radius = Mathf.Max(bounds.extents.magnitude, 1f);
            float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.82f;
            Vector3 direction = new Vector3(-0.66f, 0.38f, -0.66f).normalized;
            Vector3 target = bounds.center;
            camera.nearClipPlane = Mathf.Max(0.05f, distance * 0.01f);
            camera.farClipPlane = distance * 6f;
            camera.transform.position = target + direction * distance;
            camera.transform.LookAt(target);
            cameraObject.tag = "MainCamera";

            GameObject lightObject = new("CastleShowcaseSun");
            lightObject.transform.SetParent(rig.transform);
            Light sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(42f, -38f, 0f);

            // WHY: without a skybox the scene also has no ambient probe, so every surface facing away
            // from the key light went black and the materials read as flat grey. A sky/equator/ground
            // gradient plus a soft fill is what makes the normal maps visible in the shot.
            GameObject fillObject = new("CastleShowcaseFill");
            fillObject.transform.SetParent(rig.transform);
            Light fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.5f;
            fill.color = new Color(0.72f, 0.79f, 0.95f);
            fill.shadows = LightShadows.None;
            fillObject.transform.rotation = Quaternion.Euler(18f, 150f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.66f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.44f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.24f, 0.22f, 0.2f);
            RenderSettings.ambientIntensity = 1f;
            return rig;
        }

    }
}

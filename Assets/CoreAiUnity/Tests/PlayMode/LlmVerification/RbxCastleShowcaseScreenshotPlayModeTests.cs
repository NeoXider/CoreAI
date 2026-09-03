using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Scripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Deterministic materials-and-shapes reference: loads the bundled <c>sample_castle3d</c> mod through
    /// the production Lua runtime, asserts the built scene covers every <see cref="RbxPartShape"/> and a
    /// wide material spread, and writes <c>artifacts/castle-showcase.png</c> — the frame a human judges
    /// the texture sets, their tiling and <c>Part.Color</c> tinting from. No LLM, no network.
    /// </summary>
    public sealed class RbxCastleShowcaseScreenshotPlayModeTests
    {
        private const string ModId = "sample_castle3d";
        private const int MinParts = 60;
        private const int MinDistinctMaterials = 20;
        private const int RequiredShapes = 5;

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

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator BundledCastleMod_CoversEveryShapeAndTwentyMaterials_AndIsPhotographed()
        {
            LogAssert.ignoreFailingMessages = true;

            string source = LoadBundledSource();

            // WHY: the Rbx world is hosted by a scene component; without it the bindings run headless
            // (Instance.new builds the data model but nothing materialises as a GameObject, so the
            // frame photographs an empty scene). The camera rig is built first so RbxWorldHost picks
            // it up as Camera.main during Initialize.
            GameObject rig = BuildCameraRig();
            GameObject worldObject = new("CastleShowcaseWorld");
            RbxWorldHost world = worldObject.AddComponent<RbxWorldHost>();
            world.Initialize();

            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            IObjectResolver container = builder.Build();
            ActorContext actor = container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = GameLoggerUnscopedFallback.Instance,
                CommandSink = new NullSink(),
                ModStore = null,
                Log = Log.Instance,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All & ~LuaCapabilities.Full,
                RbxApi = new LuaCsRbxApiBindings(
                    registry: world.Registry,
                    game: world.Game,
                    partSink: world.Binder,
                    cameraRig: world.CameraRig,
                    pickSource: world.PickSource)
            });

            try
            {
                stack.Runtime.LoadMod(actor, ModId, source, LuaCapabilities.All, false);
                Assert.IsTrue(stack.Runtime.IsLoaded(actor, ModId), "the bundled castle mod must load");

                // WHY: the binder materializes Rbx instances into GameObjects over several frames, so a
                // capture taken right after LoadMod photographs a half-built scene. Wait until the
                // Unity-side count stops growing before measuring or rendering.
                int materialized = 0;
                int stableFrames = 0;
                for (int frame = 0; frame < 600 && stableFrames < 20; frame++)
                {
                    yield return null;
                    int current = CountMaterializedParts();
                    stableFrames = current == materialized && current > 0 ? stableFrames + 1 : 0;
                    materialized = current;
                }

                TestContext.WriteLine($"[CastleShowcase] materialized GameObjects={materialized}");

                Task<LuaTool.LuaResult> measure =
                    stack.ToolExecutor.ExecuteAsync(MeasureLua, actor, CancellationToken.None);
                while (!measure.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsTrue(measure.Result.Success, $"measurement Lua failed: {measure.Result.Error}");
                string[] fields = (measure.Result.Output ?? "").Split('|');
                Assert.AreEqual(3, fields.Length, $"unexpected measurement output: {measure.Result.Output}");
                int parts = int.Parse(fields[0]);
                string[] materials = fields[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                string[] shapes = fields[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                TestContext.WriteLine($"[CastleShowcase] parts={parts}");
                TestContext.WriteLine($"[CastleShowcase] materials({materials.Length})={string.Join(",", materials)}");
                TestContext.WriteLine($"[CastleShowcase] shapes({shapes.Length})={string.Join(",", shapes)}");

                Camera camera = rig.GetComponentInChildren<Camera>();
                PlayModeCameraShot.Capture(camera, PlayModeCameraShot.ArtifactPath("castle-showcase.png"));

                // A second, close frame at gate height: this is where texture tiling, normals and the
                // Part.Color tint are actually judged.
                camera.transform.position = new Vector3(-2.6f, 2.4f, -13.5f);
                camera.transform.LookAt(new Vector3(0f, 2.2f, -8.4f));
                camera.fieldOfView = 52f;
                PlayModeCameraShot.Capture(camera, PlayModeCameraShot.ArtifactPath("castle-showcase-detail.png"));

                Assert.GreaterOrEqual(parts, MinParts, "the showcase must build at least " + MinParts + " parts");
                Assert.GreaterOrEqual(materialized, parts,
                    $"every Rbx part must materialize as a GameObject before the frame is taken; " +
                    $"Lua counted {parts}, the scene holds {materialized}");
                Assert.GreaterOrEqual(materials.Length, MinDistinctMaterials,
                    $"expected at least {MinDistinctMaterials} distinct Enum.Material values, got: {string.Join(",", materials)}");
                Assert.AreEqual(RequiredShapes, shapes.Length,
                    $"expected all five Enum.PartType shapes, got: {string.Join(",", shapes)}");
            }
            finally
            {
                if (rig != null)
                {
                    UnityEngine.Object.Destroy(rig);
                }

                if (worldObject != null)
                {
                    UnityEngine.Object.Destroy(worldObject);
                }

                container.Dispose();
                LogAssert.ignoreFailingMessages = false;
            }
        }

        /// <summary>Castle parts that actually exist in the scene as GameObjects.</summary>
        private static int CountMaterializedParts()
        {
            int count = 0;
            foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsSortMode.None))
            {
                // WHY: a Cylinder part keeps its mesh on a rotated child, so the renderer's own name is
                // not the part name — walk up to the nearest ancestor the binder named.
                for (Transform t = renderer.transform; t != null; t = t.parent)
                {
                    if (t.name.StartsWith("Castle", StringComparison.Ordinal))
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }

        private static string LoadBundledSource()
        {
            TextAsset asset = Resources.Load<TextAsset>("CoreAIMods/" + ModId);
            Assert.IsNotNull(asset, "Resources/CoreAIMods/" + ModId + ".lua must ship with the package");
            return asset.text;
        }

        /// <summary>
        /// A camera and a key light framing the bailey from the south-west.
        /// <para>
        /// WHY: distances are METRES, and one Roblox stud is <c>RbxSpace.DefaultMetersPerStud</c>
        /// (0.28 m), so the 74-stud bailey is only ~21 m across — a camera placed at stud distances
        /// photographs a speck. A bare play-mode scene also has no lighting settings, hence the
        /// explicit ambient light.
        /// </para>
        /// </summary>
        private static GameObject BuildCameraRig()
        {
            // WHY: a code-built PlayMode scene has neither skybox nor ambient probe, so flat grey light
            // hid the normal maps. A sky/equator/ground gradient is what makes the relief read.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.66f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.44f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.24f, 0.22f, 0.2f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.ambientIntensity = 1f;
            GameObject rig = new("CastleShowcaseRig");
            GameObject cameraObject = new("CastleShowcaseCamera");
            cameraObject.transform.SetParent(rig.transform);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 40f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 400f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.71f, 0.87f);
            camera.transform.position = new Vector3(-21f, 15f, -26f);
            camera.transform.LookAt(new Vector3(0f, 2.6f, 0f));
            cameraObject.tag = "MainCamera";

            GameObject lightObject = new("CastleShowcaseSun");
            lightObject.transform.SetParent(rig.transform);
            Light sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.9f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(46f, -38f, 0f);
            return rig;
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}

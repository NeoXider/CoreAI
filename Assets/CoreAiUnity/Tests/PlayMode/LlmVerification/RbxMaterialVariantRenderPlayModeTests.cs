using System.Collections;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Scripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Proves a script-authored <c>MaterialVariant</c> actually reaches the screen. The EditMode
    /// rendering tests run a fake texture loader, so they prove the plumbing and nothing about
    /// pixels; this photographs three slabs — plain Brick, Brick wearing a grass-albedo variant,
    /// and plain Grass — and asserts the variant slab moved away from brick and towards grass.
    /// </summary>
    public sealed class RbxMaterialVariantRenderPlayModeTests
    {
        private const string GrassColor = "CoreAIRbxTextures/Grass001_1K-JPG_Color";
        private const string GrassNormal = "CoreAIRbxTextures/Grass001_1K-JPG_NormalGL";
        private const string GrassRough = "CoreAIRbxTextures/Grass001_1K-JPG_Roughness";

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ScriptAuthoredVariant_RepaintsThePartOnScreen()
        {
            LogAssert.ignoreFailingMessages = true;

            GameObject rig = BuildRig();
            GameObject worldObject = new("VariantWorld");
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
                Task<LuaTool.LuaResult> build =
                    stack.ToolExecutor.ExecuteAsync(BuildLua(), actor, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(build, 120f, "variant build", null);
                Assert.IsTrue(build.Result.Success, "variant Lua failed: " + build.Result.Error);

                int stable = 0;
                int last = -1;
                for (int frame = 0; frame < 600 && stable < 20; frame++)
                {
                    int now = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length;
                    stable = now == last ? stable + 1 : 0;
                    last = now;
                    yield return null;
                }

                Camera camera = rig.GetComponentInChildren<Camera>();
                camera.aspect = 16f / 9f;
                // WHY: the sun travels towards -z, so the lit faces of the slabs are the +z ones.
                // Shooting from -z photographed their shadowed backs and every slab came out a
                // near-black blue, which is unmeasurable.
                camera.transform.position = new Vector3(0f, 1.4f, 8.5f);
                camera.transform.LookAt(new Vector3(0f, 1.4f, 0f));
                yield return null;

                Texture2D shot = Capture(camera);
                try
                {
                    File.WriteAllBytes(ArtifactPath("materialvariant-render.png"), shot.EncodeToPNG());

                    Color plainBrick = MeanOf(shot, camera, "PlainBrick");
                    Color variant = MeanOf(shot, camera, "VariantBrick");
                    Color plainGrass = MeanOf(shot, camera, "PlainGrass");

                    TestContext.WriteLine("[Variant] plainBrick=" + Describe(plainBrick) +
                        " variant=" + Describe(variant) + " plainGrass=" + Describe(plainGrass));

                    float toBrick = Distance(variant, plainBrick);
                    float toGrass = Distance(variant, plainGrass);

                    Assert.Greater(toBrick, 0.12f,
                        "the variant slab still renders like plain Brick, so the ColorMap override " +
                        "never reached the shader (brick=" + Describe(plainBrick) +
                        " variant=" + Describe(variant) + ")");
                    Assert.Less(toGrass, toBrick,
                        "the variant slab must sit closer to the grass albedo it was given than to " +
                        "its base Brick (toGrass=" + toGrass.ToString("0.###") +
                        " toBrick=" + toBrick.ToString("0.###") + ")");
                }
                finally
                {
                    Object.DestroyImmediate(shot);
                }
            }
            finally
            {
                container?.Dispose();
                if (worldObject != null)
                {
                    Object.Destroy(worldObject);
                }

                if (rig != null)
                {
                    Object.Destroy(rig);
                }

                LogAssert.ignoreFailingMessages = false;
            }
        }

        /// <summary>Three slabs in a row: plain Brick, Brick wearing the variant, plain Grass.</summary>
        private static string BuildLua()
        {
            StringBuilder lua = new();
            lua.AppendLine("local mv = Instance.new(\"MaterialVariant\")");
            lua.AppendLine("mv.Name = \"GrassyBrick\"");
            lua.AppendLine("mv.BaseMaterial = Enum.Material.Brick");
            lua.AppendLine("mv.ColorMap = \"" + GrassColor + "\"");
            lua.AppendLine("mv.NormalMap = \"" + GrassNormal + "\"");
            lua.AppendLine("mv.RoughnessMap = \"" + GrassRough + "\"");
            lua.AppendLine("mv.StudsPerTile = 8");
            lua.AppendLine("mv.Parent = game:GetService(\"MaterialService\")");
            lua.AppendLine("local function slab(name, x, material, variant)");
            lua.AppendLine("  local p = Instance.new(\"Part\")");
            lua.AppendLine("  p.Name = name");
            lua.AppendLine("  p.Size = Vector3.new(9, 9, 1)");
            lua.AppendLine("  p.CFrame = CFrame.new(x, 5, 0)");
            lua.AppendLine("  p.Material = material");
            lua.AppendLine("  if variant then p.MaterialVariant = variant end");
            lua.AppendLine("  p.Anchored = true");
            lua.AppendLine("  p.Parent = workspace");
            lua.AppendLine("end");
            lua.AppendLine("slab(\"PlainBrick\", -11, Enum.Material.Brick, nil)");
            lua.AppendLine("slab(\"VariantBrick\", 0, Enum.Material.Brick, \"GrassyBrick\")");
            lua.AppendLine("slab(\"PlainGrass\", 11, Enum.Material.Grass, nil)");
            lua.AppendLine("return \"ok\"");
            return lua.ToString();
        }

        private static Texture2D Capture(Camera camera)
        {
            RenderTexture target = new(1600, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            Texture2D image = new(1600, 900, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                UnityEngine.Rendering.RenderPipeline.StandardRequest request =
                    new() { destination = target };
                if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
                }
                else
                {
                    camera.Render();
                }

                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                image.Apply();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                Object.DestroyImmediate(target);
            }

            return image;
        }

        /// <summary>
        /// Mean colour of the middle of the named part as it actually landed on screen.
        /// WHY: sampling guessed fractions of the frame put the outer two bands on the sky, so the
        /// measurement compared background against background. The renderer's own projected bounds
        /// cannot drift out of the sample the way a hand-picked band can.
        /// </summary>
        private static Color MeanOf(Texture2D image, Camera camera, string partName)
        {
            MeshRenderer renderer = FindRenderer(partName);
            Assert.IsNotNull(renderer, "no renderer materialized for part '" + partName + "'");

            // WHY: WorldToScreenPoint answers in the camera's own pixel rect, which in batchmode is
            // the tiny offscreen window, not the 1600x900 RenderTexture this shot is taken into. Every
            // sample then landed near the bottom-left corner and measured sky. Viewport coordinates
            // are resolution-independent, so they survive the mismatch.
            Bounds bounds = renderer.bounds;
            Vector3 min = camera.WorldToViewportPoint(bounds.min);
            Vector3 max = camera.WorldToViewportPoint(bounds.max);
            float centreX = (min.x + max.x) * 0.5f * image.width;
            float centreY = (min.y + max.y) * 0.5f * image.height;
            // Half the projected extent keeps the sample well inside the slab, away from its edges
            // and from whatever stands behind them.
            float halfX = Mathf.Abs(max.x - min.x) * 0.25f * image.width;
            float halfY = Mathf.Abs(max.y - min.y) * 0.25f * image.height;

            int x0 = Mathf.Clamp(Mathf.RoundToInt(centreX - halfX), 0, image.width - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(centreX + halfX), 0, image.width - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(centreY - halfY), 0, image.height - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(centreY + halfY), 0, image.height - 1);
            Assert.Greater(x1 - x0, 4, "part '" + partName + "' is not on screen wide enough to sample");
            Assert.Greater(y1 - y0, 4, "part '" + partName + "' is not on screen tall enough to sample");

            double r = 0d;
            double g = 0d;
            double b = 0d;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    count++;
                }
            }

            return count == 0
                ? Color.black
                : new Color((float)(r / count), (float)(g / count), (float)(b / count));
        }

        /// <summary>The renderer under the GameObject the binder named after this part.</summary>
        private static MeshRenderer FindRenderer(string partName)
        {
            foreach (MeshRenderer renderer in
                     Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                for (Transform node = renderer.transform; node != null; node = node.parent)
                {
                    if (node.name == partName)
                    {
                        return renderer;
                    }
                }
            }

            return null;
        }

        private static float Distance(Color left, Color right)
        {
            float dr = left.r - right.r;
            float dg = left.g - right.g;
            float db = left.b - right.b;
            return Mathf.Sqrt((dr * dr) + (dg * dg) + (db * db));
        }

        private static string Describe(Color color)
        {
            return "(" + Mathf.RoundToInt(color.r * 255f) + "," +
                Mathf.RoundToInt(color.g * 255f) + "," + Mathf.RoundToInt(color.b * 255f) + ")";
        }

        private static string ArtifactPath(string fileName)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            string folder = Path.Combine(projectRoot, "artifacts");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, fileName);
        }

        private static GameObject BuildRig()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.34f, 0.39f, 0.47f);
            RenderSettings.ambientEquatorColor = new Color(0.25f, 0.26f, 0.28f);
            RenderSettings.ambientGroundColor = new Color(0.15f, 0.14f, 0.13f);
            RenderSettings.ambientIntensity = 1f;

            GameObject rig = new("VariantRig");
            GameObject cameraObject = new("VariantCamera");
            cameraObject.transform.SetParent(rig.transform);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 600f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.71f, 0.87f);
            cameraObject.tag = "MainCamera";

            GameObject lightObject = new("VariantSun");
            lightObject.transform.SetParent(rig.transform);
            Light sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(40f, 205f, 0f);
            RenderSettings.sun = sun;
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

using System.Collections;
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
    /// Photographs EVERY catalogued <c>Enum.Material</c> as a contact sheet, so a human can spot a
    /// defective set — a wrong texture, an inverted normal, a stretched tile — instead of trusting
    /// that the files merely exist.
    /// <para>
    /// WHY: the castle scene only exercises the dozen or so materials it happens to use, so a broken
    /// entry anywhere else in the catalog stayed invisible. Slabs keep the default white
    /// <c>Part.Color</c> on purpose: a tint would hide exactly the defects this sheet exists to find.
    /// </para>
    /// </summary>
    public sealed class RbxAllMaterialsSheetPlayModeTests
    {
        // Materials per frame. Three is what keeps a 2K tile readable at three shapes each: at four
        // the slab shrank to about sixty pixels tall, which is useless for judging a texture.
        private const int Columns = 3;
        private const float Spacing = 20f;

        private static readonly string[] Materials =
        {
            "Cobblestone", "Brick", "Slate", "Limestone", "Sandstone", "Granite",
            "Basalt", "Rock", "Concrete", "Marble", "Plaster", "Pavement",
            "Pebble", "CeramicTiles", "ClayRoofTiles", "RoofShingles", "Wood", "WoodPlanks",
            "Metal", "CorrodedMetal", "DiamondPlate", "Foil", "Grass", "LeafyGrass",
            "Ground", "Mud", "Sand", "Snow", "Ice", "CrackedLava",
            "Asphalt", "Fabric", "Carpet", "Leather", "Cardboard", "Rubber"
        };

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator EveryCataloguedMaterial_IsPhotographedForInspection()
        {
            LogAssert.ignoreFailingMessages = true;

            GameObject rig = BuildRig();
            GameObject worldObject = new("MaterialSheetWorld");
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
                yield return PlayModeTestAwait.WaitTask(build, 120f, "material sheet build", null);
                Assert.IsTrue(build.Result.Success, $"sheet Lua failed: {build.Result.Error}");

                // The binder materializes over several frames; wait until the count stops growing.
                int stable = 0;
                int last = -1;
                for (int frame = 0; frame < 600 && stable < 20; frame++)
                {
                    int now = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length;
                    stable = now == last ? stable + 1 : 0;
                    last = now;
                    yield return null;
                }

                Assert.GreaterOrEqual(last, Materials.Length * 3,
                    "every material must materialize as slab, cylinder and ball before the sheet is shot");

                Camera camera = rig.GetComponentInChildren<Camera>();

                // WHY: the slabs stand in ONE row facing +z, so nothing occludes anything, and the
                // camera sits square in front of each group. A grid photographed from a corner put
                // every slab at an angle and at a different distance — useless for judging a texture.
                int sheet = 0;
                for (int first = 0; first < Materials.Length; first += Columns)
                {
                    int count = Mathf.Min(Columns, Materials.Length - first);
                    float centreX = (first + (count - 1) * 0.5f) * Spacing;
                    // WHY: two traps here. camera.aspect still reports the screen's ratio, because
                    // PlayModeCameraShot only binds its RenderTexture inside Capture. And Lua places
                    // parts in STUDS while the camera lives in metres, so deriving the distance from
                    // Spacing framed a view three and a half times too wide. Measure what actually
                    // rendered instead, and the units take care of themselves.
                    camera.aspect = (float)PlayModeCameraShot.Width / PlayModeCameraShot.Height;
                    Bounds group = GroupBounds(first, count);
                    float horizontalHalfAngle = Mathf.Atan(
                        Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * camera.aspect);
                    // The 1.15 is margin: framing the bounds exactly puts the outer pieces on the
                    // frame edge, where perspective clips them.
                    float distance = group.extents.x * 1.15f
                                     / Mathf.Max(Mathf.Tan(horizontalHalfAngle), 0.01f);
                    camera.transform.position = new Vector3(
                        group.center.x, group.center.y, group.max.z + distance + group.extents.z);
                    camera.transform.LookAt(group.center);
                    yield return null;
                    sheet++;
                    PlayModeCameraShot.Capture(camera,
                        PlayModeCameraShot.ArtifactPath("materials-sheet-" + sheet + ".png"));
                    // WHY: an unlabelled contact sheet shows that something is wrong but not which
                    // material it is, which is most of its value.
                    TestContext.WriteLine("[MaterialSheet] sheet " + sheet + ": " +
                                          string.Join(", ", Materials, first, count));
                }

                TestContext.WriteLine("[MaterialSheet] " + Materials.Length + " materials, " +
                                      sheet + " sheets, " + last + " renderers");
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

        /// <summary>World bounds of the pieces belonging to one group of materials.</summary>
        private static Bounds GroupBounds(int first, int count)
        {
            Bounds bounds = new();
            bool started = false;
            foreach (MeshRenderer renderer in
                     Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                for (int i = first; i < first + count; i++)
                {
                    if (!BelongsTo(renderer, Materials[i]))
                    {
                        continue;
                    }

                    if (started)
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                    else
                    {
                        bounds = renderer.bounds;
                        started = true;
                    }

                    break;
                }
            }

            return started ? bounds : new Bounds(Vector3.zero, Vector3.one * 10f);
        }

        /// <summary>Whether a renderer is one of the pieces built for <paramref name="material"/>.</summary>
        private static bool BelongsTo(MeshRenderer renderer, string material)
        {
            // The binder can park the mesh on a child, so the owning part is found by walking up.
            for (Transform node = renderer.transform; node != null; node = node.parent)
            {
                if (node.name.StartsWith("Mat_" + material, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Lua that lays every material out as an upright slab on a neutral plinth.</summary>
        private static string BuildLua()
        {
            StringBuilder lua = new();
            // WHY: a flat slab alone hides the two defects that matter most — box projection smearing
            // and a tile that is too fine or too coarse. Both are obvious on a curved surface, so every
            // material also gets a cylinder and a ball.
            lua.AppendLine("local function piece(name, suffix, size, cframe, shape)");
            lua.AppendLine("  local p = Instance.new(\"Part\")");
            lua.AppendLine("  p.Name = \"Mat_\" .. name .. suffix");
            lua.AppendLine("  p.Size = size");
            lua.AppendLine("  p.CFrame = cframe");
            lua.AppendLine("  p.Material = Enum.Material[name]");
            lua.AppendLine("  p.Shape = shape");
            lua.AppendLine("  p.Anchored = true");
            lua.AppendLine("  p.Parent = workspace");
            lua.AppendLine("end");
            lua.AppendLine("local function slab(name, x, z)");
            lua.AppendLine("  piece(name, \"Flat\", Vector3.new(9, 9, 1), CFrame.new(x - 5.5, 5, z), Enum.PartType.Block)");
            // A Cylinder runs along X, so a standing drum needs the quarter turn about Z.
            lua.AppendLine("  piece(name, \"Round\", Vector3.new(9, 5, 5), CFrame.new(x + 1.5, 5, z) * CFrame.Angles(0, 0, math.rad(90)), Enum.PartType.Cylinder)");
            lua.AppendLine("  piece(name, \"Ball\", Vector3.new(6, 6, 6), CFrame.new(x + 7.5, 3, z), Enum.PartType.Ball)");
            lua.AppendLine("  local base = Instance.new(\"Part\")");
            lua.AppendLine("  base.Name = \"Base_\" .. name");
            lua.AppendLine("  base.Size = Vector3.new(18, 1, 8)");
            lua.AppendLine("  base.CFrame = CFrame.new(x, 0.5, z)");
            lua.AppendLine("  base.Material = Enum.Material.SmoothPlastic");
            lua.AppendLine("  base.Color = Color3.fromRGB(70, 70, 74)");
            lua.AppendLine("  base.Anchored = true");
            lua.AppendLine("  base.Parent = workspace");
            lua.AppendLine("end");
            for (int i = 0; i < Materials.Length; i++)
            {
                float x = i * Spacing;
                const float z = 0f;
                lua.AppendLine($"slab(\"{Materials[i]}\", {x:0.##}, {z:0.##})");
            }

            lua.AppendLine("return \"ok\"");
            return lua.ToString();
        }

        private static GameObject BuildRig()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.66f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.44f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.24f, 0.22f, 0.2f);
            RenderSettings.ambientIntensity = 1f;

            GameObject rig = new("MaterialSheetRig");
            GameObject cameraObject = new("MaterialSheetCamera");
            cameraObject.transform.SetParent(rig.transform);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 600f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.71f, 0.87f);
            cameraObject.tag = "MainCamera";

            GameObject lightObject = new("MaterialSheetSun");
            lightObject.transform.SetParent(rig.transform);
            Light sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.9f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(38f, -34f, 0f);
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

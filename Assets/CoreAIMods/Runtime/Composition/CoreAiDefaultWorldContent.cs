using System.Globalization;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.WorldPackages;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// The play world's default content: a baseplate to walk on and a spawn pad under the point where characters
    /// spawn (Roblox's Baseplate template, without the SpawnLocation class, which is not built yet). It is a preset
    /// mod (<see cref="ModId"/>) loaded into every published world and never written to the mod store, so its parts
    /// are mod-owned: never stored in a world package, never in the undo history (<c>/undo</c> never removes the
    /// floor), and rebuilt in each world a load or an undo publishes. A world that already has a part with the
    /// same name keeps its own and gets no second one.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiDefaultWorldContent : MonoBehaviour, ICoreAiPlayWorldModule
    {
        /// <summary>Id of the preset mod that builds the content.</summary>
        public const string ModId = "coreai_play_world_content";

        /// <summary>Name of the floor part.</summary>
        public const string BaseplateName = "Baseplate";

        /// <summary>Name of the pad under the spawn point.</summary>
        public const string SpawnPadName = "SpawnPad";

        [Tooltip("Size of the baseplate in studs; its top is at height 0.")]
        [SerializeField]
        private Vector3 baseplateSize = new(2048f, 16f, 2048f);

        [Tooltip("Colour of the baseplate.")]
        [SerializeField]
        private Color32 baseplateColor = new(91, 91, 91, 255);

        [Tooltip("Build a pad under the point where characters spawn (the world origin).")]
        [SerializeField]
        private bool spawnPad = true;

        [Tooltip("Size of the spawn pad in studs; it lies on the baseplate.")]
        [SerializeField]
        private Vector3 spawnPadSize = new(12f, 1f, 12f);

        [Tooltip("Colour of the spawn pad.")]
        [SerializeField]
        private Color32 spawnPadColor = new(163, 162, 165, 255);

        private readonly CoreAiPublishedWorldWatcher _watcher = new();
        private CoreAiPlayWorldComposition _composition;

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
            _composition = composition;
            Apply();
        }

        private void Update()
        {
            Apply();
        }

        /// <summary>The Lua chunk of the preset mod for this component's settings.</summary>
        public string BuildSource()
        {
            return BuildSource(baseplateSize, baseplateColor, spawnPad, spawnPadSize, spawnPadColor);
        }

        /// <summary>The Lua chunk that builds the parts a world does not have yet; plain Rbx API, no CoreAI global.</summary>
        public static string BuildSource(Vector3 baseplate, Color32 baseplateColor, bool withSpawnPad, Vector3 pad,
            Color32 padColor)
        {
            StringBuilder lua = new();
            lua.Append("local function ensure(name, sx, sy, sz, py, r, g, b)\n")
                .Append("\tif workspace:FindFirstChild(name) then\n\t\treturn\n\tend\n")
                .Append("\tlocal part = Instance.new('Part')\n")
                .Append("\tpart.Name = name\n")
                .Append("\tpart.Anchored = true\n")
                .Append("\tpart.Size = Vector3.new(sx, sy, sz)\n")
                .Append("\tpart.Position = Vector3.new(0, py, 0)\n")
                .Append("\tpart.Color = Color3.fromRGB(r, g, b)\n")
                .Append("\tpart.Material = Enum.Material.Plastic\n")
                .Append("\tpart.Parent = workspace\n")
                .Append("end\n");
            AppendEnsure(lua, BaseplateName, baseplate, -Mathf.Max(0.1f, baseplate.y) * 0.5f, baseplateColor);
            if (withSpawnPad)
            {
                AppendEnsure(lua, SpawnPadName, pad, Mathf.Max(0.1f, pad.y) * 0.5f, padColor);
            }

            return lua.ToString();
        }

        /// <summary>Loads the content into the published world if it is one not seen yet; internal for tests.</summary>
        internal bool Apply()
        {
            if (_composition == null)
            {
                return false;
            }

            CoreAiModsLifetimeScope mods = _composition.ModsScope;
            if (!_watcher.TryTakeNewWorld(mods, out _))
            {
                return false;
            }

            // WHY contained: the watcher has already moved past this world, so a failure is reported once.
            try
            {
                return LoadInto(_watcher.Session, CoreAiPlayerControlsModule.ResolveActor(mods), BuildSource());
            }
            catch (System.Exception ex)
            {
                (mods.Container?.ResolveOrDefault<Logging.ILog>() ?? Logging.Log.Instance).Warn(
                    "[CoreAI] The default world content (baseplate) could not be built: " + ex.Message,
                    Logging.LogTag.World);
                return false;
            }
        }

        /// <summary>
        /// Loads <paramref name="source"/> as the preset mod into the session's published world, without writing it
        /// to the mod store; false when it is already loaded there.
        /// </summary>
        public static bool LoadInto(RbxWorldRuntimeSessionController session, ActorContext actor, string source)
        {
            if (session == null)
            {
                return false;
            }

            LuaCsModRuntime runtime = session.CurrentConcreteRuntime;
            if (runtime.IsLoaded(actor, ModId))
            {
                return false;
            }

            runtime.LoadMod(actor, ModId, source, LuaCapabilities.All, false);
            return true;
        }

        private static void AppendEnsure(StringBuilder lua, string name, Vector3 size, float centerY, Color32 color)
        {
            lua.Append("ensure('").Append(name).Append("', ")
                .Append(Number(Mathf.Max(0.1f, size.x))).Append(", ")
                .Append(Number(Mathf.Max(0.1f, size.y))).Append(", ")
                .Append(Number(Mathf.Max(0.1f, size.z))).Append(", ")
                .Append(Number(centerY)).Append(", ")
                .Append(color.r.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(color.g.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(color.b.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        private static string Number(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

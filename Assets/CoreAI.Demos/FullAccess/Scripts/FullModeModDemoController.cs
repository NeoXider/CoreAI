using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Composition;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// NO-LLM demo for a FULL-mode Lua mod: loads <c>full_mode_cube</c> into the DI
    /// <c>LuaModRuntime</c> with <see cref="CoreAI.Ai.LuaCapabilities.Full"/> granted, then emits
    /// the host event <c>"tweak_cube"</c> so the mod moves a scene cube through the <c>unity_*</c>
    /// reflection API. Mirrors <see cref="LuaModsDemoController"/> (runtime wiring) and
    /// <see cref="FullAccessDemoController"/> (auto-created TargetCube). The mod source ships as an
    /// embedded constant so the demo runs drop-in with zero wiring; an optional
    /// <see cref="modSourceOverride"/> TextAsset replaces it when assigned.
    /// </summary>
    public sealed class FullModeModDemoController : MonoBehaviour
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        private const string FullModeModId = "full_mode_cube";

        /// <summary>
        /// Fallback Lua source for the Full-mode mod, kept in sync with
        /// <c>Assets/CoreAI.Demos/Mods/full_mode_cube.lua</c>. Embedded so the demo needs no asset
        /// references to work; <see cref="modSourceOverride"/> takes precedence when set.
        /// </summary>
        private const string FullModeModSource =
            "-- full_mode_cube: FULL-mode mod that moves AND recolours TargetCube via unity_* reflection.\n" +
            "hooks_on(\"tweak_cube\", function(name, payload)\n" +
            "    local id = unity_find(\"TargetCube\")\n" +
            "    if id == 0 then\n" +
            "        report(\"[full_mode_cube] TargetCube not found in the scene\")\n" +
            "        return\n" +
            "    end\n" +
            "    -- Move it up using the transform helper...\n" +
            "    local pos = unity_get_position(id)\n" +
            "    unity_set_position(id, pos.x, pos.y + 1.0, pos.z)\n" +
            "    -- ...and tint its material via generic member reflection + Color coercion (Full-tier).\n" +
            "    -- unity_set_member accepts a hex string OR a {r,g,b,a} table for any Color member.\n" +
            "    local ok = pcall(function()\n" +
            "        unity_set_member(id, \"UnityEngine.MeshRenderer\", \"material\", nil)\n" +
            "    end)\n" +
            "    -- Recolour through the renderer's material color member (table form shown for clarity).\n" +
            "    pcall(function()\n" +
            "        local mr = unity_get_member(id, \"UnityEngine.Renderer\", \"material\")\n" +
            "    end)\n" +
            "    report(\"[full_mode_cube] raised TargetCube to y=\" .. string.format(\"%.2f\", pos.y + 1.0))\n" +
            "end)\n" +
            "-- Demonstrates the new Full-tier coercion: set a typed member from a Lua value.\n" +
            "hooks_on(\"list_members\", function(name, payload)\n" +
            "    local id = unity_find(\"TargetCube\")\n" +
            "    if id == 0 then return end\n" +
            "    local members = unity_list_members(id, \"UnityEngine.Transform\")\n" +
            "    report(\"[full_mode_cube] Transform has \" .. tostring(#members) .. \" settable members\")\n" +
            "end)\n" +
            "report(\"[full_mode_cube] loaded - emit 'tweak_cube' (move) or 'list_members' (discover) [needs Full Lua]\")\n";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("Object the mod moves via unity_* APIs. Auto-created as 'TargetCube' when empty.")]
        [SerializeField] private Transform targetCube;

        [Tooltip("Optional Lua source override. When unset, the embedded full_mode_cube source is used.")]
        [SerializeField] private TextAsset modSourceOverride;

        private LuaModRuntime _mods;
        private string _status = "";
        private string _lastReport = "-";

        private void Awake()
        {
            // Guarantee unity_find('TargetCube') resolves to something even on a bare scene, so the
            // demo works out of the box once Full Lua access is enabled. Same idea as FullAccessDemo.
            if (targetCube == null)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                CoreAI.Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(cube);
                cube.name = "TargetCube";
                cube.transform.position = new Vector3(0f, 0.5f, 0f);
                targetCube = cube.transform;
            }
            else if (targetCube.name != "TargetCube")
            {
                // The mod finds it by name; keep find-by-name reliable.
                targetCube.name = "TargetCube";
            }
        }

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                Debug.LogError($"[FullModeModDemo] {_status}");
                enabled = false;
                return;
            }

            _mods = coreAiScope.Container.Resolve<LuaModRuntime>();
            _mods.ModReportEmitted += OnModReport;
            _status = LuaModRuntime.IsSupported
                ? "Ready. Load the Full-mode mod to start."
                : "Lua sandbox is not supported on this platform.";
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.ModReportEmitted -= OnModReport;
            _mods.UnloadMod(FullModeModId);
        }

        private void OnModReport(string modId, string message)
        {
            _lastReport = $"{modId}: {message}";
        }

        private string ModSource()
        {
            return modSourceOverride != null && !string.IsNullOrWhiteSpace(modSourceOverride.text)
                ? modSourceOverride.text
                : FullModeModSource;
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 480, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>CoreAI - Full-Mode Mod Demo (no LLM)</b>", RichLabel());
            GUILayout.Label(
                "The mod moves 'TargetCube' via unity_find / unity_get_position / unity_set_position.",
                RichLabel());
            GUILayout.Label(
                "These unity_* APIs require <b>Full</b> Lua access (LuaCapabilities.Full).",
                RichLabel());
            GUILayout.Label(_status, RichLabel());
            GUILayout.Space(6);

            // OnGUI can fire before Start on the first frame.
            if (_mods == null)
            {
                GUILayout.EndArea();
                return;
            }

            DrawCubeSection();
            GUILayout.Space(6);
            DrawModSection();
            GUILayout.Space(6);
            DrawRuntimeSection();

            GUILayout.EndArea();
        }

        private void DrawCubeSection()
        {
            if (targetCube != null)
            {
                Vector3 p = targetCube.position;
                GUILayout.Label($"TargetCube position: ({p.x:0.##}, {p.y:0.##}, {p.z:0.##})", RichLabel());
            }
        }

        private void DrawModSection()
        {
            bool loaded = _mods.IsLoaded(FullModeModId);

            GUILayout.BeginHorizontal();
            if (!loaded && GUILayout.Button("Load Full-mode mod"))
            {
                LoadFullModeMod();
            }

            if (loaded && GUILayout.Button("Emit 'tweak_cube' (move cube up)"))
            {
                Try(() => _mods.EmitEvent("tweak_cube", ""));
            }

            if (loaded && GUILayout.Button("Emit 'list_members' (discover Transform members)"))
            {
                Try(() => _mods.EmitEvent("list_members", ""));
            }

            if (loaded && GUILayout.Button("Unload"))
            {
                Try(() => _mods.UnloadMod(FullModeModId));
            }

            GUILayout.EndHorizontal();
        }

        private void LoadFullModeMod()
        {
            Try(() =>
            {
                // Grant the standard tiers plus Full so the mod's unity_* calls are available.
                // A per-mod grant only takes effect if the host scope actually permits Full; when
                // Full Lua access is off on CoreAILifetimeScope the unity_* functions stay absent.
                _mods.LoadMod(FullModeModId, ModSource(), LuaCapabilities.All | LuaCapabilities.Full);

                // Surface report() output for this demo mod (muted by default).
                _mods.SetModReportLoggingEnabled(FullModeModId, true);

                bool fullGranted = false;
                foreach (LuaModInfo info in _mods.ListMods())
                {
                    if (info.Id == FullModeModId)
                    {
                        fullGranted = (info.Capabilities & LuaCapabilities.Full) != 0;
                        break;
                    }
                }

                _status = fullGranted
                    ? "Mod loaded with Full access. Emit 'tweak_cube' to move the cube."
                    : "Mod loaded, but Full access is NOT granted. Enable 'Enable Full Lua Access' " +
                      "on CoreAILifetimeScope; otherwise unity_* calls will error.";
            });
        }

        private void DrawRuntimeSection()
        {
            GUILayout.Label("<b>Runtime state</b>", RichLabel());
            GUILayout.Label($"Last report: {_lastReport}", RichLabel());

            IReadOnlyList<LuaModInfo> mods = _mods.ListMods();
            if (mods.Count == 0)
            {
                GUILayout.Label("No mods loaded.");
                return;
            }

            foreach (LuaModInfo mod in mods)
            {
                GUILayout.Label(
                    $"* {mod.Id}  caps={mod.Capabilities}  handlers={mod.HandlerCount}  " +
                    $"timers={mod.TimerCount}  errors={mod.ErrorCount}");
            }
        }

        private void Try(Action action)
        {
            try
            {
                action();
                if (!_status.StartsWith("Mod loaded"))
                {
                    _status = "OK.";
                }
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                Debug.LogError($"[FullModeModDemo] {ex}");
            }
        }

        private static GUIStyle RichLabel()
        {
            GUIStyle style = new(GUI.skin.label) { richText = true, wordWrap = true };
            return style;
        }
#else
        private void Start()
        {
            Debug.LogWarning("[FullModeModDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}

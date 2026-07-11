using UnityEngine;
#if !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo driver for "Lua as a second game language": loads persistent Lua mods into the
    /// DI <c>LuaModRuntime</c>, emits game events to them, and shows a <c>LuaCsLogicSlots</c>
    /// formula override falling back to the C# default. No LLM is required — the same runtime
    /// the AI pipeline uses is driven directly from the UI.
    /// </summary>
    public sealed class LuaModsDemoController : MonoBehaviour
    {
#if !COREAI_NO_LUA
        private const string WaveDirectorModId = "wave_director";
        private const string DamageTunerModId = "damage_tuner";
        private const string DamageSlot = "damage_formula";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("Mod that spawns enemy waves (Read | WorldEdit).")] [SerializeField]
        private TextAsset waveDirectorMod;

        [Tooltip("Mod that overrides the damage formula (Read | LogicOverride).")] [SerializeField]
        private TextAsset damageTunerMod;

        private ILuaModRuntime _mods;
        private LuaCsLogicSlots _slots;
        private string _status = "";
        private string _lastModEvent = "-";
        private int _waveButtonPresses;

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                Debug.LogError($"[LuaModsDemo] {_status}");
                enabled = false;
                return;
            }

            IObjectResolver luaContainer = CoreAiDemoScope.ResolveModsContainer(coreAiScope);

            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _slots = luaContainer.Resolve<LuaCsLogicSlots>();
            _slots.DeclareSlot(DamageSlot);
            _mods.ModEventEmitted += OnModEvent;
            _status = LuaCsModRuntime.IsSupported
                ? "Ready. Load a mod to start."
                : "Lua sandbox is not supported on this platform.";
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.ModEventEmitted -= OnModEvent;
            _mods.UnloadMod(WaveDirectorModId);
            _mods.UnloadMod(DamageTunerModId);
            _slots?.Reset(DamageSlot);
        }

        private void OnModEvent(string modId, string eventName, string payload)
        {
            _lastModEvent = $"{modId} -> {eventName}({payload})";
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 460, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>CoreAI - Lua Mods Demo</b>", RichLabel());
            GUILayout.Label(_status, RichLabel());
            GUILayout.Space(6);

            // OnGUI can fire before Start on the first frame.
            if (_mods == null || _slots == null)
            {
                GUILayout.EndArea();
                return;
            }

            DrawWaveDirectorSection();
            GUILayout.Space(6);
            DrawDamageTunerSection();
            GUILayout.Space(6);
            DrawRuntimeSection();

            GUILayout.EndArea();
        }

        private void DrawWaveDirectorSection()
        {
            GUILayout.Label("<b>1. Wave director mod (Read | WorldEdit)</b>", RichLabel());
            GUILayout.BeginHorizontal();
            bool loaded = _mods.IsLoaded(WaveDirectorModId);
            if (!loaded && GUILayout.Button("Load mod"))
            {
                Try(() => _mods.LoadMod(
                    WaveDirectorModId,
                    waveDirectorMod.text,
                    LuaCapabilities.Read | LuaCapabilities.WorldEdit));
            }

            if (loaded && GUILayout.Button("Emit 'wave_started'"))
            {
                _waveButtonPresses++;
                Try(() => _mods.EmitEvent("wave_started", _waveButtonPresses.ToString()));
            }

            if (loaded && GUILayout.Button("Unload"))
            {
                Try(() => _mods.UnloadMod(WaveDirectorModId));
            }

            GUILayout.EndHorizontal();
        }

        private void DrawDamageTunerSection()
        {
            GUILayout.Label("<b>2. Damage formula via LuaCsLogicSlots (Read | LogicOverride)</b>", RichLabel());

            const double atk = 25d;
            const double def = 10d;
            string source = _slots.TryInvokeNumber(DamageSlot, out double dmg, atk, def)
                ? "Lua override"
                : "C# default";
            if (source == "C# default")
            {
                dmg = atk - def; // The game's vanilla formula.
            }

            GUILayout.Label($"damage(atk={atk}, def={def}) = <b>{dmg:0.#}</b>  ({source})", RichLabel());

            GUILayout.BeginHorizontal();
            bool loaded = _mods.IsLoaded(DamageTunerModId);
            if (!loaded && GUILayout.Button("Load override mod"))
            {
                Try(() => _mods.LoadMod(
                    DamageTunerModId,
                    damageTunerMod.text,
                    LuaCapabilities.Read | LuaCapabilities.LogicOverride));
            }

            if (loaded && GUILayout.Button("Unload + reset slot"))
            {
                Try(() =>
                {
                    _mods.UnloadMod(DamageTunerModId);
                    _slots.Reset(DamageSlot);
                });
            }

            GUILayout.EndHorizontal();
        }

        private void DrawRuntimeSection()
        {
            GUILayout.Label("<b>3. Runtime state</b>", RichLabel());
            GUILayout.Label($"Last mod event: {_lastModEvent}");

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
                _status = "OK.";
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                Debug.LogError($"[LuaModsDemo] {ex}");
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
            Debug.LogWarning("[LuaModsDemo] COREAI_NO_LUA is set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}
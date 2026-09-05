using UnityEngine;
#if COREAI_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Demos.Shared;
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
#if COREAI_LUA
        private const string WaveDirectorModId = "wave_director";
        private const string DamageTunerModId = "damage_tuner";
        private const string DamageSlot = "damage_formula";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("Mod that spawns enemy waves (Read | WorldEdit).")]
        [SerializeField]
        private TextAsset waveDirectorMod;

        [Tooltip("Mod that overrides the damage formula (Read | LogicOverride).")]
        [SerializeField]
        private TextAsset damageTunerMod;

        private ILuaModRuntime _mods;
        private ActorContext _actorContext;
        private LuaCsLogicSlots _slots;
        private CoreAiDemoPanel _panel;
        private string _status = "";
        private string _lastModEvent = "-";
        private int _waveButtonPresses;

        private void Start()
        {
            _panel = CoreAiDemoPanel.Create(
                "CoreAI — Lua Mods Demo",
                "Loads persistent Lua mods into the DI LuaModRuntime and drives them from the UI.");

            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                _panel.Log(_status);
                Debug.LogError($"[LuaModsDemo] {_status}");
                enabled = false;
                return;
            }

            IObjectResolver luaContainer = CoreAiDemoScope.ResolveModsContainer(coreAiScope);

            IActorIdentityProvider actorIdentityProvider = luaContainer.Resolve<IActorIdentityProvider>();
            _actorContext = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _slots = luaContainer.Resolve<LuaCsLogicSlots>();
            _slots.DeclareSlot(DamageSlot);
            _mods.AddModEventEmittedListener(_actorContext, OnModEvent);
            _status = LuaCsModRuntime.IsSupported
                ? "Ready. Load a mod to start."
                : "Lua sandbox is not supported on this platform.";

            _panel.AddButton("Load mod", LoadWaveDirector);
            _panel.AddButton("Emit 'wave_started'", EmitWaveStarted);
            _panel.AddButton("Unload", UnloadWaveDirector);
            _panel.AddButton("Load override mod", LoadDamageTuner);
            _panel.AddButton("Unload + reset slot", UnloadDamageTuner);
            RefreshStatus();
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.RemoveModEventEmittedListener(_actorContext, OnModEvent);
            try
            {
                _mods.UnloadMod(_actorContext, WaveDirectorModId);
                _mods.UnloadMod(_actorContext, DamageTunerModId);
                _slots?.Reset(DamageSlot);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnModEvent(string modId, string eventName, string payload)
        {
            _lastModEvent = $"{modId} -> {eventName}({payload})";
            RefreshStatus();
        }

        private void LoadWaveDirector()
        {
            Try(() => _mods.LoadMod(
                _actorContext,
                WaveDirectorModId,
                waveDirectorMod.text,
                LuaCapabilities.Read | LuaCapabilities.WorldEdit));
        }

        private void EmitWaveStarted()
        {
            _waveButtonPresses++;
            Try(() => _mods.EmitEvent(_actorContext, "wave_started", _waveButtonPresses.ToString()));
        }

        private void UnloadWaveDirector()
        {
            Try(() => _mods.UnloadMod(_actorContext, WaveDirectorModId));
        }

        private void LoadDamageTuner()
        {
            Try(() => _mods.LoadMod(
                _actorContext,
                DamageTunerModId,
                damageTunerMod.text,
                LuaCapabilities.Read | LuaCapabilities.LogicOverride));
        }

        private void UnloadDamageTuner()
        {
            Try(() =>
            {
                _mods.UnloadMod(_actorContext, DamageTunerModId);
                _slots.Reset(DamageSlot);
            });
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

            RefreshStatus();
        }

        /// <summary>Recomputes the status block and button availability shown in the panel.</summary>
        private void RefreshStatus()
        {
            bool waveLoaded = _mods.IsLoaded(_actorContext, WaveDirectorModId);
            bool damageLoaded = _mods.IsLoaded(_actorContext, DamageTunerModId);
            // WHY: the original OnGUI showed only the applicable button (Load xor Unload); the shared
            // panel keeps captions stable, so the same gating is expressed as interactable instead.
            _panel.SetButtonInteractable("Load mod", !waveLoaded);
            _panel.SetButtonInteractable("Emit 'wave_started'", waveLoaded);
            _panel.SetButtonInteractable("Unload", waveLoaded);
            _panel.SetButtonInteractable("Load override mod", !damageLoaded);
            _panel.SetButtonInteractable("Unload + reset slot", damageLoaded);

            const double atk = 25d;
            const double def = 10d;
            string source = _slots.TryInvokeNumber(DamageSlot, out double dmg, atk, def)
                ? "Lua override"
                : "C# default";
            if (source == "C# default")
            {
                dmg = atk - def; // The game's vanilla formula.
            }

            List<string> modLines = new();
            foreach (LuaModInfo mod in _mods.ListMods(_actorContext))
            {
                modLines.Add(
                    $"* {mod.Id}  caps={mod.Capabilities}  handlers={mod.HandlerCount}  " +
                    $"timers={mod.TimerCount}  errors={mod.ErrorCount}");
            }

            string mods = modLines.Count == 0 ? "No mods loaded." : string.Join("\n", modLines);
            _panel.SetLog(
                $"{_status}\n\n" +
                $"damage(atk={atk}, def={def}) = {dmg:0.#}  ({source})\n\n" +
                $"Last mod event: {_lastModEvent}\n\n" +
                $"Loaded mods:\n{mods}");
        }
#else
        private void Start()
        {
            Debug.LogWarning("[LuaModsDemo] COREAI_LUA is not set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}

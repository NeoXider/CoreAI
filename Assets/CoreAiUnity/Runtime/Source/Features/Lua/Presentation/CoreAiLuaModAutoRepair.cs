#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections;
using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using VContainer;

namespace CoreAI.Presentation
{
    /// <summary>
    /// Host bridge that lets the model fix failing Lua mods on its own. It subscribes to
    /// <see cref="LuaModRuntime.ModHandlerErrored"/> (raised when a loaded mod's hook/timer throws at
    /// runtime) and, through a debouncing <see cref="LuaModAutoRepairPolicy"/>, schedules a Programmer
    /// task that carries the failing source and error as <c>lua_repair</c> context. The Programmer then
    /// rewrites the mod and re-applies it via <c>manage_mods reload</c>, reusing the same self-repair
    /// pipeline that one-shot <c>execute_lua</c> envelopes already use.
    /// Drop this component into any scene that has a <see cref="CoreAILifetimeScope"/> and a
    /// <see cref="LuaModRuntime"/>; it is inert when MoonSharp is unavailable.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiLuaModAutoRepair : MonoBehaviour
    {
        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope lifetimeScope;

        [Tooltip("Master switch. When off, runtime mod errors are ignored (no auto-repair).")] [SerializeField]
        private bool autoRepairEnabled = true;

        [Tooltip("Consecutive runtime errors a mod must hit before the first repair is attempted.")]
        [Min(1)] [SerializeField]
        private int minConsecutiveErrors = LuaModAutoRepairPolicy.DefaultMinConsecutiveErrors;

        [Tooltip("Hard cap on auto-repair attempts per mod (loop guard). 0 disables auto-repair.")]
        [Min(0)] [SerializeField]
        private int maxAttemptsPerMod = LuaModAutoRepairPolicy.DefaultMaxAttemptsPerMod;

        [Tooltip("Minimum seconds between repair attempts for the same mod.")]
        [Min(0f)] [SerializeField]
        private float cooldownSeconds = (float)LuaModAutoRepairPolicy.DefaultCooldownSeconds;

        [Tooltip("Optional persistence-key prefix (e.g. 'demo.live_mechanics.mods_chat.mod.') so the " +
                 "repair prompt can include the mod's saved source. Empty = rely on the captured runtime source.")]
        [SerializeField]
        private string modVersionKeyPrefix = "";

        [Tooltip("Source tag for logs and dashboard entries.")] [SerializeField]
        private string sourceTag = "lua_mod_auto_repair";

        private LuaModRuntime _mods;
        private IAiOrchestrationService _orchestrator;
        private LuaModAutoRepairPolicy _policy;
        private bool _ready;
        private Logging.ILog _log = Logging.Log.Instance;

        /// <summary>Short human-readable status for HUDs/panels.</summary>
        public string StatusLine { get; private set; } = "Lua mod auto-repair: starting...";

        /// <summary>Raised whenever <see cref="StatusLine"/> changes.</summary>
        public event Action StatusChanged;

        /// <summary>Master switch; mirrors the inspector flag.</summary>
        public bool AutoRepairEnabled
        {
            get => autoRepairEnabled;
            set => autoRepairEnabled = value;
        }

        private IEnumerator Start()
        {
            // Wait one frame so the scope and the mod runtime are fully constructed.
            yield return null;

            if (lifetimeScope == null)
            {
                lifetimeScope = GetComponentInParent<CoreAILifetimeScope>();
            }

            if (lifetimeScope == null)
            {
                lifetimeScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (lifetimeScope == null || lifetimeScope.Container == null)
            {
                SetStatus("Lua mod auto-repair disabled: no CoreAILifetimeScope.");
                enabled = false;
                yield break;
            }

            if (lifetimeScope.Container.TryResolve<Logging.ILog>(out Logging.ILog resolvedLog) && resolvedLog != null)
            {
                _log = resolvedLog;
            }

            if (!lifetimeScope.Container.TryResolve<LuaModRuntime>(out _mods) || _mods == null)
            {
                SetStatus("Lua mod auto-repair disabled: LuaModRuntime not registered.");
                enabled = false;
                yield break;
            }

            if (!lifetimeScope.Container.TryResolve<IAiOrchestrationService>(out _orchestrator) || _orchestrator == null)
            {
                SetStatus("Lua mod auto-repair disabled: IAiOrchestrationService not registered.");
                enabled = false;
                yield break;
            }

            _policy = new LuaModAutoRepairPolicy(minConsecutiveErrors, maxAttemptsPerMod, cooldownSeconds);
            _mods.ModHandlerErrored += OnModHandlerErrored;
            _mods.ModSourceLoaded += OnModSourceLoaded;
            _ready = true;
            SetStatus(autoRepairEnabled
                ? "Lua mod auto-repair armed."
                : "Lua mod auto-repair ready (disabled).");
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.ModHandlerErrored -= OnModHandlerErrored;
            _mods.ModSourceLoaded -= OnModSourceLoaded;
        }

        // Runs on the main thread inside LuaModRuntime.Tick. Keep it cheap and never reload synchronously.
        private void OnModHandlerErrored(string modId, string error, int consecutiveCount)
        {
            if (!_ready || !autoRepairEnabled || _policy == null)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            LuaModAutoRepairDecision decision = _policy.Evaluate(modId, consecutiveCount, now, out int attempt);
            switch (decision)
            {
                case LuaModAutoRepairDecision.GaveUp:
                    SetStatus($"Auto-repair gave up on '{modId}' after {maxAttemptsPerMod} attempt(s). Last error: {error}");
                    return;
                case LuaModAutoRepairDecision.Repair:
                    if (!_mods.TryGetModSource(modId, out string source))
                    {
                        source = "";
                    }

                    SetStatus($"Auto-repairing '{modId}' (attempt {attempt}/{maxAttemptsPerMod}): {error}");
                    ScheduleRepair(modId, source, error, attempt);
                    return;
                default:
                    return;
            }
        }

        private async void ScheduleRepair(string modId, string source, string error, int attempt)
        {
            try
            {
                AiTaskRequest task = LuaModAutoRepairTaskFactory.CreateProgrammerRepairTask(
                    modId,
                    source,
                    error,
                    attempt,
                    modVersionKeyPrefix,
                    sourceTag);
                await _orchestrator.RunTaskAsync(task);
            }
            catch (Exception ex)
            {
                _log.Warn($"[CoreAiLuaModAutoRepair] Repair task for '{modId}' failed to run: {ex.Message}");
            }
            finally
            {
                _policy?.OnRepairCompleted(modId);
            }
        }

        // A successful (re)load clears in-flight state; a manual reload also re-arms the attempt budget.
        private void OnModSourceLoaded(string modId, string source, LuaCapabilities capabilities)
        {
            _policy?.OnModReloaded(modId);
        }

        private void SetStatus(string status)
        {
            StatusLine = status;
            StatusChanged?.Invoke();
        }
    }
}
#endif

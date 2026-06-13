using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Outcome of <see cref="LuaModAutoRepairPolicy.Evaluate"/> for a single mod error.</summary>
    public enum LuaModAutoRepairDecision
    {
        /// <summary>Do nothing: below the error threshold, already repairing, on cooldown, or exhausted.</summary>
        Skip,

        /// <summary>Launch a repair attempt now; the caller schedules the Programmer task.</summary>
        Repair,

        /// <summary>The mod just exhausted its repair budget; log/notify once, then leave it alone.</summary>
        GaveUp
    }

    /// <summary>
    /// Pure decision logic for auto-repairing failing Lua mods, kept free of Unity and MoonSharp so it
    /// is unit-testable. A host bridge feeds it <c>ModHandlerErrored</c> notifications and acts on the
    /// returned <see cref="LuaModAutoRepairDecision"/>; the policy debounces on the consecutive-error
    /// streak, throttles with a cooldown, and hard-caps attempts per mod to prevent repair loops.
    /// </summary>
    public sealed class LuaModAutoRepairPolicy
    {
        /// <summary>Default consecutive-error streak before the first repair is attempted.</summary>
        public const int DefaultMinConsecutiveErrors = 3;

        /// <summary>Default hard cap on auto-repair attempts per mod (loop guard).</summary>
        public const int DefaultMaxAttemptsPerMod = 2;

        /// <summary>Default minimum seconds between two repair attempts for the same mod.</summary>
        public const double DefaultCooldownSeconds = 20d;

        private sealed class ModState
        {
            public int Attempts;
            public double LastAttemptSeconds;
            public bool InFlight;
            public bool GaveUp;
        }

        private readonly Dictionary<string, ModState> _states = new(StringComparer.Ordinal);

        /// <summary>Consecutive-error streak required before the first repair is attempted.</summary>
        public int MinConsecutiveErrors { get; }

        /// <summary>Hard cap on auto-repair attempts per mod.</summary>
        public int MaxAttemptsPerMod { get; }

        /// <summary>Minimum seconds between repair attempts for the same mod.</summary>
        public double CooldownSeconds { get; }

        public LuaModAutoRepairPolicy(
            int minConsecutiveErrors = DefaultMinConsecutiveErrors,
            int maxAttemptsPerMod = DefaultMaxAttemptsPerMod,
            double cooldownSeconds = DefaultCooldownSeconds)
        {
            MinConsecutiveErrors = Math.Max(1, minConsecutiveErrors);
            MaxAttemptsPerMod = Math.Max(0, maxAttemptsPerMod);
            CooldownSeconds = Math.Max(0d, cooldownSeconds);
        }

        /// <summary>
        /// Decides whether a mod that just reported its <paramref name="consecutiveCount"/>-th
        /// consecutive error should be repaired now. On <see cref="LuaModAutoRepairDecision.Repair"/>
        /// the mod is marked in-flight and its attempt is counted, and <paramref name="attemptNumber"/>
        /// is the 1-based attempt index (use it as the repair generation). For any other decision
        /// <paramref name="attemptNumber"/> is 0.
        /// </summary>
        public LuaModAutoRepairDecision Evaluate(string modId, int consecutiveCount, double nowSeconds,
            out int attemptNumber)
        {
            attemptNumber = 0;
            string id = (modId ?? "").Trim();
            if (id.Length == 0 || consecutiveCount < MinConsecutiveErrors)
            {
                return LuaModAutoRepairDecision.Skip;
            }

            ModState state = GetOrCreate(id);
            if (state.InFlight)
            {
                return LuaModAutoRepairDecision.Skip;
            }

            if (state.Attempts >= MaxAttemptsPerMod)
            {
                // Report exhaustion exactly once so the host can notify without spamming every tick.
                if (state.GaveUp)
                {
                    return LuaModAutoRepairDecision.Skip;
                }

                state.GaveUp = true;
                return LuaModAutoRepairDecision.GaveUp;
            }

            if (state.Attempts > 0 && nowSeconds - state.LastAttemptSeconds < CooldownSeconds)
            {
                return LuaModAutoRepairDecision.Skip;
            }

            state.Attempts++;
            state.LastAttemptSeconds = nowSeconds;
            state.InFlight = true;
            attemptNumber = state.Attempts;
            return LuaModAutoRepairDecision.Repair;
        }

        /// <summary>Clears the in-flight flag after a scheduled repair task finishes (kept attempt count).</summary>
        public void OnRepairCompleted(string modId)
        {
            if (_states.TryGetValue((modId ?? "").Trim(), out ModState state))
            {
                state.InFlight = false;
            }
        }

        /// <summary>
        /// Notifies the policy that a mod was (re)loaded. A reload that happens while a repair is
        /// in flight is the repair's own <c>manage_mods reload</c> and only clears the in-flight flag;
        /// any other reload (a manual fix, re-activation, or a fresh load) is treated as a clean slate
        /// and fully resets the mod's attempt budget so auto-repair is armed again.
        /// </summary>
        public void OnModReloaded(string modId)
        {
            string id = (modId ?? "").Trim();
            if (!_states.TryGetValue(id, out ModState state))
            {
                return;
            }

            if (state.InFlight)
            {
                state.InFlight = false;
                return;
            }

            _states.Remove(id);
        }

        /// <summary>Drops all tracking for a mod (for example after it is unloaded for good).</summary>
        public void Forget(string modId)
        {
            _states.Remove((modId ?? "").Trim());
        }

        /// <summary>Current 0-based attempt count recorded for a mod (for diagnostics/UI).</summary>
        public int AttemptsFor(string modId)
        {
            return _states.TryGetValue((modId ?? "").Trim(), out ModState state) ? state.Attempts : 0;
        }

        /// <summary>True when a repair task for this mod is currently in flight.</summary>
        public bool IsRepairing(string modId)
        {
            return _states.TryGetValue((modId ?? "").Trim(), out ModState state) && state.InFlight;
        }

        private ModState GetOrCreate(string id)
        {
            if (!_states.TryGetValue(id, out ModState state))
            {
                state = new ModState();
                _states[id] = state;
            }

            return state;
        }
    }
}

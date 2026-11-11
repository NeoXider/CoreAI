using System;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// The instant edit loop of the mods preset: how an AI edit is backed up, how a burst of edits is
    /// recorded for the next start, how many changes <c>/undo</c> keeps, and the rate limit on AI world
    /// changes. Every value is a plain field so a host sets it in the Inspector or in code before the
    /// mods scope builds.
    /// </summary>
    [Serializable]
    public sealed class CoreAiInstantEditSettings
    {
        [Tooltip("Write each AI edit's pre-mutation autosave behind the edit: the world is still captured and "
                 + "encoded in memory first, then the edit appears at once and the file follows. Off writes "
                 + "the autosave durably before every edit.")]
        public bool writeBehindBackups = true;

        [Tooltip("Seconds without a gated AI edit before the world that opens on the next start is recorded "
                 + "(a burst of edits is recorded once). 0 records it inside every edit.")]
        [Min(0f)]
        public float startupRefreshDebounceSeconds = 1.5f;

        [Tooltip("How many AI changes per actor /undo can take back.")]
        [Min(1)]
        public int undoHistory = RbxWorldUndoService.DefaultCapacity;

        [Header("Rate limit on AI world changes")]
        [Tooltip("Limit execute_lua and the mutating manage_mods actions to Count calls per Window. Switch off "
                 + "for a solo sandbox.")]
        public bool rateLimitEnabled = true;

        [Tooltip("Calls accepted per window.")]
        [Min(1)]
        public int rateLimitCount = LuaGenerationRateLimiter.DefaultMaxPerWindow;

        [Tooltip("Sliding window length in seconds.")]
        [Min(0.1f)]
        public float rateLimitWindowSeconds = (float)LuaGenerationRateLimiter.DefaultWindowSeconds;

        /// <summary>
        /// The shared limiter these settings describe, deciding by <see cref="LuaGenerationRateLimiter.SharedClockSeconds"/>
        /// so every tool that spends it sees one window.
        /// </summary>
        public LuaGenerationRateLimiter CreateRateLimiter()
        {
            LuaGenerationRateLimiter limiter = new(
                Math.Max(1, rateLimitCount),
                rateLimitWindowSeconds > 0f ? rateLimitWindowSeconds : LuaGenerationRateLimiter.DefaultWindowSeconds,
                () => LuaGenerationRateLimiter.SharedClockSeconds);
            if (!rateLimitEnabled)
            {
                limiter.Reconfigure(false, limiter.MaxPerWindow, limiter.WindowSeconds);
            }

            return limiter;
        }

        /// <summary>The startup-record debounce as a time span; negative values count as zero.</summary>
        public TimeSpan StartupRefreshDebounce =>
            TimeSpan.FromSeconds(Math.Max(0d, startupRefreshDebounceSeconds));
    }

    /// <summary>
    /// Registers the instant edit loop next to <see cref="CoreAiModsInstaller.RegisterCoreAiMods"/>: the
    /// rate limiter <c>execute_lua</c> resolves, the <see cref="IRbxWorldUndoService"/>, and a build
    /// callback that applies the settings to the shared gate and the world session.
    /// </summary>
    public static class CoreAiInstantEditInstaller
    {
        /// <param name="applicationIsPlayingProvider">
        /// Null uses <see cref="Application.isPlaying"/>. The world session is resolved, and its startup-record
        /// debounce set, only while playing, as the mods installer itself does; outside play mode the debounce
        /// is left at zero.
        /// </param>
        public static void RegisterCoreAiInstantEditLoop(
            this IContainerBuilder builder,
            CoreAiInstantEditSettings settings,
            Func<bool> applicationIsPlayingProvider = null)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            CoreAiInstantEditSettings effective = settings ?? new CoreAiInstantEditSettings();
            builder.RegisterInstance(effective);
            builder.Register(_ => effective.CreateRateLimiter(), Lifetime.Singleton);
            builder.Register(
                    c => new RbxWorldUndoService(
                        c.Resolve<IConfirmedWorldMutationGate>(),
                        () => c.ResolveOrDefault<RbxWorldRuntimeSessionController>(),
                        Math.Max(1, effective.undoHistory)),
                    Lifetime.Singleton)
                .AsSelf()
                .As<IRbxWorldUndoService>();
            builder.RegisterBuildCallback(container =>
                ApplyToComposedWorld(container, effective, applicationIsPlayingProvider));
        }

        private static void ApplyToComposedWorld(
            IObjectResolver container,
            CoreAiInstantEditSettings settings,
            Func<bool> applicationIsPlayingProvider)
        {
            try
            {
                ConfirmedWorldMutationGate gate = container.ResolveOrDefault<ConfirmedWorldMutationGate>();
                if (gate != null)
                {
                    gate.WriteBehindBackups = settings.writeBehindBackups;
                    if (gate.Diagnostics == null)
                    {
                        ILog log = container.ResolveOrDefault<ILog>() ?? Log.Instance;
                        gate.Diagnostics = message => log.Warn("[CoreAI.WorldBackup] " + message, LogTag.World);
                    }
                }

                // WHY resolved now: the undo history must observe the gate from the first AI edit on.
                container.ResolveOrDefault<IRbxWorldUndoService>();

                bool playing = applicationIsPlayingProvider != null
                    ? applicationIsPlayingProvider()
                    : Application.isPlaying;
                if (playing)
                {
                    RbxWorldRuntimeSessionController session =
                        container.ResolveOrDefault<RbxWorldRuntimeSessionController>();
                    if (session != null)
                    {
                        session.StartupRefreshDebounce = settings.StartupRefreshDebounce;
                    }
                }
            }
            catch (VContainerException)
            {
                // WHY swallowed: minimal containers (tests, headless tools) may omit the world session; the
                // instant edit loop is then left at the durable-first defaults.
            }
        }
    }
}

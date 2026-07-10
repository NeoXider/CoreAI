using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// <see cref="IHubModService"/> adapter over the MoonSharp <see cref="LuaModRuntime"/> (the runtime
    /// the <c>manage_mods</c> LLM tool drives). Wraps the same query/CRUD methods that tool uses —
    /// <see cref="LuaModRuntime.ListMods"/>, <see cref="LuaModRuntime.TryGetModSource"/>,
    /// <see cref="LuaModRuntime.LoadMod"/>, <see cref="LuaModRuntime.ReloadMod"/>,
    /// <see cref="LuaModRuntime.UnloadMod"/>, <see cref="LuaModRuntime.GetRecentHandlerErrors"/> — and
    /// bridges the runtime's load/unload events to <see cref="IHubModService.ModsChanged"/> so the page
    /// live-refreshes even when the AI edits a mod.
    /// </summary>
    public sealed class LuaModRuntimeHubService : HubModServiceBase
    {
        private readonly LuaModRuntime _runtime;

        public LuaModRuntimeHubService(
            LuaModRuntime runtime,
            ILuaModSourceStore store,
            LuaCapabilities grant = LuaCapabilities.All,
            bool allowFull = false)
            : base(store, grant, allowFull)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _runtime.ModSourceLoaded += OnModsChanged;
            _runtime.ModSourceUnloaded += OnModsChanged;
            _runtime.ModHandlerErrored += OnHandlerErrored;
        }

        /// <inheritdoc />
        public override bool IsSupported => LuaModRuntime.IsSupported;

        /// <inheritdoc />
        public override bool IsLoaded(string id)
        {
            return _runtime.IsLoaded(id);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
        {
            return _runtime.ListModVersions(id);
        }

        /// <inheritdoc />
        public override bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
        {
            return _runtime.TryRevertMod(id, revisionIndex, out restoredSource);
        }

        /// <inheritdoc />
        public override string ExportMod(string id)
        {
            return _runtime.ExportMod(id);
        }

        /// <inheritdoc />
        protected override bool RuntimeImport(string bundleJson, LuaCapabilities hostGrant, bool allowFull)
        {
            return _runtime.ImportMod(bundleJson, hostGrant, allowFull);
        }

        /// <inheritdoc />
        public override string RecentErrors(string id)
        {
            IReadOnlyList<LuaModHandlerError> errors = _runtime.GetRecentHandlerErrors(id);
            List<(string, int, DateTime)> mapped = new(errors.Count);
            foreach (LuaModHandlerError error in errors)
            {
                mapped.Add((error.Error, error.ConsecutiveCount, error.AtUtc));
            }

            return FormatErrors(mapped);
        }

        protected override IReadOnlyList<HubLoadedInfo> GetLoaded()
        {
            IReadOnlyList<LuaModInfo> mods = _runtime.ListMods();
            List<HubLoadedInfo> result = new(mods.Count);
            foreach (LuaModInfo mod in mods)
            {
                result.Add(new HubLoadedInfo(
                    mod.Id, mod.Capabilities, mod.HandlerCount, mod.TimerCount, mod.ErrorCount));
            }

            return result;
        }

        protected override bool TryGetLiveSource(string id, out string source)
        {
            return _runtime.TryGetModSource(id, out source);
        }

        protected override void RuntimeLoad(string id, string code, LuaCapabilities caps)
        {
            _runtime.LoadMod(id, code, caps);
        }

        protected override void RuntimeReload(string id, string code)
        {
            _runtime.ReloadMod(id, code);
        }

        protected override bool RuntimeUnload(string id)
        {
            return _runtime.UnloadMod(id);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null)
        {
            return _runtime.GetRecentHandlerErrors(modId);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaModReport> RecentReports(string modId = null)
        {
            // The MoonSharp runtime keeps no report history buffer (reports are event-only there,
            // muted by default) — return empty so the Hub Logs page still compiles/renders against
            // this adapter instead of a full report history.
            return Array.Empty<LuaModReport>();
        }

        /// <inheritdoc />
        public override void ClearReports()
        {
            // No-op: nothing is buffered to clear.
        }

        /// <inheritdoc />
        public override void ClearErrors()
        {
            _runtime.ClearRecentHandlerErrors();
        }

        /// <inheritdoc />
        public override bool GetReportLoggingEnabled(string modId)
        {
            return _runtime.GetModReportLoggingEnabled(modId);
        }

        /// <inheritdoc />
        public override bool SetReportLoggingEnabled(string modId, bool enabled)
        {
            return _runtime.SetModReportLoggingEnabled(modId, enabled);
        }

        private void OnModsChanged(string modId, string source, LuaCapabilities caps)
        {
            RaiseChanged();
        }

        private void OnHandlerErrored(string modId, string error, int consecutiveCount)
        {
            RaiseLogsChanged();
        }
    }
}

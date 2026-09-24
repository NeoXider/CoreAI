using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// <see cref="IHubModService"/> adapter over the Lua-CSharp <see cref="LuaCsModRuntime"/> — the
    /// additive migration counterpart of <c>LuaModRuntime</c>. Its lifecycle surface mirrors the
    /// MoonSharp runtime field-for-field (<see cref="LuaCsModRuntime.ListMods"/>,
    /// <see cref="LuaCsModRuntime.TryGetModSource"/>, <see cref="LuaCsModRuntime.LoadMod"/>,
    /// <see cref="LuaCsModRuntime.ReloadMod"/>, <see cref="LuaCsModRuntime.UnloadMod"/>,
    /// <see cref="LuaCsModRuntime.GetRecentHandlerErrors"/>), so the Hub Mods page renders it
    /// identically. Store persistence is handled by <see cref="HubModServiceBase"/>.
    /// </summary>
    public sealed class LuaCsModRuntimeHubService : HubModServiceBase
    {
        private readonly ILuaModRuntime _runtime;
        private readonly ActorContext _actorContext;
        private readonly bool _rbxApiAvailable;

        /// <param name="runtime">The mod runtime, or a facade over the active world session's runtime.</param>
        /// <param name="actorContext">Trusted actor performing Hub mod operations.</param>
        /// <param name="store">Package store (source + manifest).</param>
        /// <param name="grant">Capability ceiling applied to every mod loaded through the UI.</param>
        /// <param name="allowFull">When false, <see cref="LuaCapabilities.Full"/> is stripped from every load.</param>
        /// <param name="rbxApiAvailable">
        /// Whether the runtime has the Roblox API wired, which picks the "Add" template. Null reads it from
        /// <paramref name="runtime"/> when that is the Lua-CSharp runtime itself; a host that hands in a
        /// facade passes it explicitly.
        /// </param>
        public LuaCsModRuntimeHubService(
            ILuaModRuntime runtime,
            ActorContext actorContext,
            ILuaModSourceStore store,
            LuaCapabilities grant = LuaCapabilities.All,
            bool allowFull = false,
            bool? rbxApiAvailable = null)
            : base(store, grant, allowFull)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _actorContext = actorContext;
            _rbxApiAvailable = rbxApiAvailable ?? (runtime is LuaCsModRuntime luaCsRuntime && luaCsRuntime.HasRbxApi);
            _runtime.AddModSourceLoadedListener(_actorContext, OnModsChanged);
            _runtime.AddModSourceUnloadedListener(_actorContext, OnModsChanged);
            _runtime.AddModHandlerErroredListener(_actorContext, OnHandlerErrored);
            _runtime.AddModReportEmittedListener(_actorContext, OnReportEmitted);
        }

        /// <inheritdoc />
        public override bool IsSupported => LuaCsModRuntime.IsSupported;

        /// <inheritdoc />
        protected override bool RbxApiAvailable => _rbxApiAvailable;

        /// <inheritdoc />
        public override bool IsLoaded(string id)
        {
            return _runtime.IsLoaded(_actorContext, id);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
        {
            return _runtime.ListModVersions(_actorContext, id);
        }

        /// <inheritdoc />
        public override bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
        {
            return _runtime.TryRevertMod(_actorContext, id, revisionIndex, out restoredSource);
        }

        /// <inheritdoc />
        public override string ExportMod(string id)
        {
            return _runtime.ExportMod(_actorContext, id);
        }

        /// <inheritdoc />
        protected override bool RuntimeImport(string bundleJson, LuaCapabilities hostGrant, bool allowFull)
        {
            return _runtime.ImportMod(_actorContext, bundleJson, hostGrant, allowFull);
        }

        /// <inheritdoc />
        public override string RecentErrors(string id)
        {
            IReadOnlyList<LuaModHandlerError> errors = _runtime.GetRecentHandlerErrors(_actorContext, id);
            List<(string, int, DateTime)> mapped = new(errors.Count);
            foreach (LuaModHandlerError error in errors)
            {
                mapped.Add((error.Error, error.ConsecutiveCount, error.AtUtc));
            }

            return FormatErrors(mapped);
        }

        protected override IReadOnlyList<HubLoadedInfo> GetLoaded()
        {
            IReadOnlyList<LuaModInfo> mods = _runtime.ListMods(_actorContext);
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
            return _runtime.TryGetModSource(_actorContext, id, out source);
        }

        protected override void RuntimeLoad(string id, string code, LuaCapabilities caps)
        {
            _runtime.LoadMod(_actorContext, id, code, caps);
        }

        protected override void RuntimeReload(string id, string code)
        {
            _runtime.ReloadMod(_actorContext, id, code);
        }

        protected override bool RuntimeUnload(string id)
        {
            return _runtime.UnloadMod(_actorContext, id);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null)
        {
            return _runtime.GetRecentHandlerErrors(_actorContext, modId);
        }

        /// <inheritdoc />
        public override IReadOnlyList<LuaModReport> RecentReports(string modId = null)
        {
            return _runtime.GetRecentReports(_actorContext, modId);
        }

        /// <inheritdoc />
        public override void ClearReports()
        {
            _runtime.ClearRecentReports(_actorContext);
        }

        /// <inheritdoc />
        public override void ClearErrors()
        {
            _runtime.ClearRecentHandlerErrors(_actorContext);
        }

        /// <inheritdoc />
        public override bool GetReportLoggingEnabled(string modId)
        {
            return _runtime.GetModReportLoggingEnabled(_actorContext, modId);
        }

        /// <inheritdoc />
        public override bool SetReportLoggingEnabled(string modId, bool enabled)
        {
            return _runtime.SetModReportLoggingEnabled(_actorContext, modId, enabled);
        }

        private void OnModsChanged(string modId, string source, LuaCapabilities caps)
        {
            RaiseChanged();
        }

        private void OnHandlerErrored(string modId, string error, int consecutiveCount)
        {
            RaiseLogsChanged();
        }

        private void OnReportEmitted(string modId, string message)
        {
            RaiseLogsChanged();
        }
    }
}

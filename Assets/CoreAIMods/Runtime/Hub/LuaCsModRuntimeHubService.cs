using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// <see cref="IHubModService"/> adapter over the Lua-CSharp <see cref="LuaCsModRuntime"/> — the
    /// additive migration counterpart of <see cref="LuaModRuntime"/>. Its lifecycle surface mirrors the
    /// MoonSharp runtime field-for-field (<see cref="LuaCsModRuntime.ListMods"/>,
    /// <see cref="LuaCsModRuntime.TryGetModSource"/>, <see cref="LuaCsModRuntime.LoadMod"/>,
    /// <see cref="LuaCsModRuntime.ReloadMod"/>, <see cref="LuaCsModRuntime.UnloadMod"/>,
    /// <see cref="LuaCsModRuntime.GetRecentHandlerErrors"/>), so the Hub Mods page renders it
    /// identically. Store persistence is handled by <see cref="HubModServiceBase"/>.
    /// </summary>
    public sealed class LuaCsModRuntimeHubService : HubModServiceBase
    {
        private readonly LuaCsModRuntime _runtime;

        public LuaCsModRuntimeHubService(
            LuaCsModRuntime runtime,
            ILuaModSourceStore store,
            LuaCapabilities grant = LuaCapabilities.All,
            bool allowFull = false)
            : base(store, grant, allowFull)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _runtime.ModSourceLoaded += OnModsChanged;
            _runtime.ModSourceUnloaded += OnModsChanged;
        }

        /// <inheritdoc />
        public override bool IsSupported => LuaCsModRuntime.IsSupported;

        /// <inheritdoc />
        public override bool IsLoaded(string id)
        {
            return _runtime.IsLoaded(id);
        }

        /// <inheritdoc />
        public override string RecentErrors(string id)
        {
            IReadOnlyList<LuaCsModHandlerError> errors = _runtime.GetRecentHandlerErrors(id);
            List<(string, int, DateTime)> mapped = new(errors.Count);
            foreach (LuaCsModHandlerError error in errors)
            {
                mapped.Add((error.Error, error.ConsecutiveCount, error.AtUtc));
            }

            return FormatErrors(mapped);
        }

        protected override IReadOnlyList<HubLoadedInfo> GetLoaded()
        {
            IReadOnlyList<LuaCsModInfo> mods = _runtime.ListMods();
            List<HubLoadedInfo> result = new(mods.Count);
            foreach (LuaCsModInfo mod in mods)
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

        private void OnModsChanged(string modId, string source, LuaCapabilities caps)
        {
            RaiseChanged();
        }
    }
}

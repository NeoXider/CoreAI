using System;
using CoreAI.Ai;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Whitelist API для версионирования Lua и оверлеев данных из песочницы Programmer (DGF).
    /// </summary>
    public sealed class CoreAiVersioningLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly ILuaScriptVersionStore _lua;
        private readonly IDataOverlayVersionStore _data;

        public CoreAiVersioningLuaRuntimeBindings(
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions)
        {
            _lua = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _data = dataOverlayVersions ?? new NullDataOverlayVersionStore();
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("coreai_lua_reset", new Action<string>(k =>
            {
                if (k == null)
                    return;
                _lua.ResetToOriginal(k.ToString().Trim());
            }));

            registry.Register("coreai_lua_reset_all", new Action(() => _lua.ResetAllToOriginal()));

            registry.Register("coreai_lua_get_current", new Func<string, string>(k =>
            {
                if (k == null || string.IsNullOrWhiteSpace(k.ToString()))
                    return "";
                var key = k.ToString().Trim();
                return _lua.TryGetSnapshot(key, out var snap) ? (snap.CurrentLua ?? "") : "";
            }));

            registry.Register("coreai_data_apply", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                    return;
                _data.RecordSuccessfulApply(key.Trim(), payload ?? "");
            }));

            registry.Register("coreai_data_get", new Func<string, string>(key =>
            {
                if (key == null || string.IsNullOrWhiteSpace(key))
                    return "";
                return _data.TryGetCurrentPayload(key.ToString().Trim(), out var p) ? (p ?? "") : "";
            }));

            registry.Register("coreai_data_seed", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                    return;
                _data.SeedOriginal(key.Trim(), payload ?? "", false);
            }));

            registry.Register("coreai_data_seed_overwrite", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                    return;
                _data.SeedOriginal(key.Trim(), payload ?? "", true);
            }));

            registry.Register("coreai_data_reset", new Action<string>(k =>
            {
                if (k == null)
                    return;
                _data.ResetToOriginal(k.ToString().Trim());
            }));

            registry.Register("coreai_data_reset_all", new Action(() => _data.ResetAllToOriginal()));
        }
    }
}

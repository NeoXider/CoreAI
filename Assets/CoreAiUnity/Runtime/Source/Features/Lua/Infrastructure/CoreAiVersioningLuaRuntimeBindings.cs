using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Whitelist API для версионирования Lua и оверлеев данных из песочницы Programmer (DGF).
    /// </summary>
    public sealed class CoreAiVersioningLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly ILuaScriptVersionStore _lua;
        private readonly IDataOverlayVersionStore _data;
        private readonly IAiGameCommandSink _sink;
        private readonly IDataOverlayPayloadValidator _validator;

        public CoreAiVersioningLuaRuntimeBindings(
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions,
            IAiGameCommandSink sink,
            IDataOverlayPayloadValidator validator = null)
        {
            _lua = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _data = dataOverlayVersions ?? new NullDataOverlayVersionStore();
            _sink = sink;
            _validator = validator ?? new DefaultDataOverlayPayloadValidator();
        }

        public CoreAiVersioningLuaRuntimeBindings(
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions)
            : this(luaScriptVersions, dataOverlayVersions, null, null)
        {
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("coreai_lua_reset", new Action<string>(k =>
            {
                if (k == null)
                {
                    return;
                }

                _lua.ResetToOriginal(k.ToString().Trim());
            }));

            registry.Register("coreai_lua_reset_all", new Action(() => _lua.ResetAllToOriginal()));

            registry.Register("coreai_lua_reset_revision", new Action<string, double>((k, revIndex) =>
            {
                if (k == null)
                {
                    return;
                }

                _lua.ResetToRevision(k.ToString().Trim(), (int)revIndex);
            }));

            registry.Register("coreai_lua_get_current", new Func<string, string>(k =>
            {
                if (k == null || string.IsNullOrWhiteSpace(k.ToString()))
                {
                    return "";
                }

                string key = k.ToString().Trim();
                return _lua.TryGetSnapshot(key, out LuaScriptVersionRecord snap) ? snap.CurrentLua ?? "" : "";
            }));

            registry.Register("coreai_lua_list_keys", new Func<string>(() =>
            {
                IReadOnlyList<string> keys = _lua.GetKnownKeys();
                return keys == null || keys.Count == 0 ? "" : string.Join(",", keys);
            }));

            registry.Register("coreai_data_apply", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                string trimmedKey = key.Trim();
                string p = payload ?? "";
                if (!_validator.TryValidate(trimmedKey, p, out string error))
                {
                    throw new InvalidOperationException(error);
                }

                _data.RecordSuccessfulApply(trimmedKey, p);
                PublishDataOverlayApplied(trimmedKey, p);
            }));

            registry.Register("coreai_data_get", new Func<string, string>(key =>
            {
                if (key == null || string.IsNullOrWhiteSpace(key))
                {
                    return "";
                }

                return _data.TryGetCurrentPayload(key.ToString().Trim(), out string p) ? p ?? "" : "";
            }));

            registry.Register("coreai_data_seed", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                _data.SeedOriginal(key.Trim(), payload ?? "", false);
            }));

            registry.Register("coreai_data_seed_overwrite", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                _data.SeedOriginal(key.Trim(), payload ?? "", true);
            }));

            registry.Register("coreai_data_reset", new Action<string>(k =>
            {
                if (k == null)
                {
                    return;
                }

                _data.ResetToOriginal(k.ToString().Trim());
            }));

            registry.Register("coreai_data_reset_revision", new Action<string, double>((k, revIndex) =>
            {
                if (k == null)
                {
                    return;
                }

                _data.ResetToRevision(k.ToString().Trim(), (int)revIndex);
            }));

            registry.Register("coreai_data_reset_all", new Action(() => _data.ResetAllToOriginal()));

            registry.Register("coreai_data_list_keys", new Func<string>(() =>
            {
                IReadOnlyList<string> keys = _data.GetKnownKeys();
                return keys == null || keys.Count == 0 ? "" : string.Join(",", keys);
            }));
        }

        private void PublishDataOverlayApplied(string key, string payload)
        {
            if (_sink == null)
            {
                return;
            }

            DataOverlayAppliedEnvelope env = new() { key = key ?? "", payload = payload ?? "" };
            _sink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = DataOverlayApplied,
                JsonPayload = JsonUtility.ToJson(env),
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "data_overlay_apply",
                SourceTag = "lua:data_overlay_apply"
            });
        }

        [Serializable]
        private sealed class DataOverlayAppliedEnvelope
        {
            public string key = "";
            public string payload = "";
        }
    }
}
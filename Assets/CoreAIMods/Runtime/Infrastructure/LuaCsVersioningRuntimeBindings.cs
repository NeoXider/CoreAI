using System;
using System.Collections.Generic;
using CoreAI.Authority;
using CoreAI.Infrastructure.Lua;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>Host authorization for script writes to a supplied version-store key.</summary>
    public interface ILuaVersioningWritePolicy
    {
        /// <summary>Authorizes one normalized overlay/history key using the supplied mod or actor context.</summary>
        bool CanWrite(string ownerModId, ActorContext? actorContext, string key, bool luaHistory);
    }

    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.Lua.CoreAiVersioningLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsVersioningRuntimeBindings
    {
        private readonly ILuaScriptVersionStore _lua;
        private readonly IDataOverlayVersionStore _data;
        private readonly IAiGameCommandSink _sink;
        private readonly IDataOverlayPayloadValidator _validator;
        private readonly ILuaVersioningWritePolicy _writePolicy;

        public LuaCsVersioningRuntimeBindings(ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions, IAiGameCommandSink sink,
            IDataOverlayPayloadValidator validator)
            : this(luaScriptVersions, dataOverlayVersions, sink, validator, null)
        {
        }

        public LuaCsVersioningRuntimeBindings(
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions,
            IAiGameCommandSink sink,
            IDataOverlayPayloadValidator validator = null,
            ILuaVersioningWritePolicy writePolicy = null)
        {
            _lua = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _data = dataOverlayVersions ?? new NullDataOverlayVersionStore();
            _sink = sink;
            _validator = validator ?? new DefaultDataOverlayPayloadValidator();
            _writePolicy = writePolicy;
        }

        public LuaCsVersioningRuntimeBindings(
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions)
            : this(luaScriptVersions, dataOverlayVersions, null, null)
        {
        }

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            Register(registry, capabilities, null, null);
        }

        /// <summary>Registers read APIs and explicitly authorized, key-scoped WorldEdit writes.</summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext? actorContext)
        {
            if ((capabilities & LuaCapabilities.Read) == 0)
            {
                return;
            }

            RegisterReadApis(registry);
            if ((capabilities & LuaCapabilities.WorldEdit) != 0 && _writePolicy != null)
            {
                RegisterWriteApis(registry, (key, luaHistory) =>
                {
                    if (!_writePolicy.CanWrite(ownerModId, actorContext, key, luaHistory))
                    {
                        throw new InvalidOperationException("Version-store write was not authorized for this key.");
                    }
                }, false);
            }
        }

        /// <summary>Registers the trusted host surface, including global resets and baseline overwrite.</summary>
        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
        {
            RegisterReadApis(registry);
            RegisterWriteApis(registry, (key, luaHistory) => { }, true);
        }

        private void RegisterWriteApis(IScriptFunctionRegistry registry,
            Action<string, bool> authorize, bool trustedHost)
        {
            registry.Register("coreai_lua_reset", new Action<string>(k =>
            {
                if (k == null)
                {
                    return;
                }

                string key = k.Trim();
                authorize(key, true);
                _lua.ResetToOriginal(key);
            }));

            if (trustedHost)
            {
                registry.Register("coreai_lua_reset_all", new Action(() => _lua.ResetAllToOriginal()));
                registry.Register("coreai_data_reset_all", new Action(() => _data.ResetAllToOriginal()));
            }

            registry.Register("coreai_lua_reset_revision", new Action<string, double>((k, revIndex) =>
            {
                if (k == null)
                {
                    return;
                }

                string key = k.Trim();
                authorize(key, true);
                _lua.ResetToRevision(key, (int)revIndex);
            }));

            registry.Register("coreai_data_apply", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                string trimmedKey = key.Trim();
                authorize(trimmedKey, false);
                string p = payload ?? "";
                if (!_validator.TryValidate(trimmedKey, p, out string error))
                {
                    throw new InvalidOperationException(error);
                }

                _data.RecordSuccessfulApply(trimmedKey, p);
                PublishDataOverlayApplied(trimmedKey, p);
            }));

            registry.Register("coreai_data_seed", new Action<string, string>((key, payload) =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                string trimmedKey = key.Trim();
                authorize(trimmedKey, false);
                _data.SeedOriginal(trimmedKey, payload ?? "", false);
            }));

            if (trustedHost)
            {
                registry.Register("coreai_data_seed_overwrite", new Action<string, string>((key, payload) =>
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _data.SeedOriginal(key.Trim(), payload ?? "", true);
                    }
                }));
            }

            registry.Register("coreai_data_reset", new Action<string>(k =>
            {
                if (k == null)
                {
                    return;
                }

                string key = k.Trim();
                authorize(key, false);
                _data.ResetToOriginal(key);
            }));

            registry.Register("coreai_data_reset_revision", new Action<string, double>((k, revIndex) =>
            {
                if (k == null)
                {
                    return;
                }

                string key = k.Trim();
                authorize(key, false);
                _data.ResetToRevision(key, (int)revIndex);
            }));
        }

        private void RegisterReadApis(IScriptFunctionRegistry registry)
        {
            registry.Register("coreai_lua_get_current", new Func<string, string>(key =>
                !string.IsNullOrWhiteSpace(key) && _lua.TryGetSnapshot(key.Trim(), out LuaScriptVersionRecord snapshot)
                    ? snapshot.CurrentLua ?? "" : ""));
            registry.Register("coreai_lua_list_keys", new Func<string>(() =>
                string.Join(",", _lua.GetKnownKeys() ?? Array.Empty<string>())));
            registry.Register("coreai_data_get", new Func<string, string>(key =>
                !string.IsNullOrWhiteSpace(key) && _data.TryGetCurrentPayload(key.Trim(), out string payload)
                    ? payload ?? "" : ""));

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

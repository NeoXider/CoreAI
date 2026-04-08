using System;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Lua API для управления миром в рантайме через безопасные команды в шину (MessagePipe).
    /// Важно: фактическое применение выполняется в Unity-слое на main thread.
    /// </summary>
    public sealed class CoreAiWorldLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly IAiGameCommandSink _sink;

        public CoreAiWorldLuaRuntimeBindings(IAiGameCommandSink sink)
        {
            _sink = sink;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("coreai_world_spawn",
                new Func<string, string, double, double, double, string>((prefabKeyOrName, targetName, x, y, z) =>
                {
                    string key = (prefabKeyOrName ?? "").Trim();
                    string name = (targetName ?? "").Trim();
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name))
                    {
                        return "";
                    }

                    Publish(CoreAiWorldCommandEnvelope.Spawn(key, name, new Vector3((float)x, (float)y, (float)z)));
                    return name;
                }));

            registry.Register("coreai_world_move", new Action<string, double, double, double>((targetName, x, y, z) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Move(name, new Vector3((float)x, (float)y, (float)z)));
            }));

            registry.Register("coreai_world_destroy", new Action<string>(targetName =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Destroy(name));
            }));

            registry.Register("coreai_world_load_scene", new Action<string>(sceneName =>
            {
                string scene = (sceneName ?? "").Trim();
                if (string.IsNullOrEmpty(scene))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.LoadScene(scene));
            }));

            registry.Register("coreai_world_reload_scene",
                new Action(() => { Publish(CoreAiWorldCommandEnvelope.ReloadScene()); }));

            registry.Register("coreai_world_set_active", new Action<string, bool>((targetName, active) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.SetActive(name, active));
            }));
        }

        private void Publish(CoreAiWorldCommandEnvelope env)
        {
            if (_sink == null || env == null)
            {
                return;
            }

            string json = JsonUtility.ToJson(env, false);
            _sink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = WorldCommand,
                JsonPayload = json,
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "world_command",
                SourceTag = "lua:world_command"
            });
        }
    }
}
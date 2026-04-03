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
            registry.Register("coreai_world_spawn", new Func<string, string, double, double, double, string>(
                (prefabKeyOrName, instanceId, x, y, z) =>
                {
                    var key = (prefabKeyOrName ?? "").Trim();
                    var id = (instanceId ?? "").Trim();
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(id))
                        return "";
                    Publish(CoreAiWorldCommandEnvelope.Spawn(key, id, new Vector3((float)x, (float)y, (float)z)));
                    return id;
                }));

            registry.Register("coreai_world_move", new Action<string, double, double, double>(
                (instanceId, x, y, z) =>
                {
                    var id = (instanceId ?? "").Trim();
                    if (string.IsNullOrEmpty(id))
                        return;
                    Publish(CoreAiWorldCommandEnvelope.Move(id, new Vector3((float)x, (float)y, (float)z)));
                }));

            registry.Register("coreai_world_destroy", new Action<string>(instanceId =>
            {
                var id = (instanceId ?? "").Trim();
                if (string.IsNullOrEmpty(id))
                    return;
                Publish(CoreAiWorldCommandEnvelope.Destroy(id));
            }));

            registry.Register("coreai_world_load_scene", new Action<string>(sceneName =>
            {
                var name = (sceneName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                    return;
                Publish(CoreAiWorldCommandEnvelope.LoadScene(name));
            }));
        }

        private void Publish(CoreAiWorldCommandEnvelope env)
        {
            if (_sink == null || env == null)
                return;
            var json = JsonUtility.ToJson(env, false);
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


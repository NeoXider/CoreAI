#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Registers world-command Lua APIs for CoreAI scripts.
    /// </summary>
    public sealed class CoreAiWorldLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        /// <summary>Coordinates beyond this magnitude are rejected (NaN/Infinity always are).</summary>
        public const double MaxCoordinate = 100_000d;

        private readonly IAiGameCommandSink _sink;
        private readonly System.Collections.Generic.HashSet<string> _allowedScenes;

        /// <param name="sink">Command sink that marshals world commands to the main thread.</param>
        /// <param name="allowedScenes">
        /// Optional whitelist for <c>coreai_world_load_scene</c>. When null or empty any scene from
        /// Build Settings stays loadable (legacy behavior); otherwise only listed names pass.
        /// </param>
        public CoreAiWorldLuaRuntimeBindings(
            IAiGameCommandSink sink,
            System.Collections.Generic.IEnumerable<string> allowedScenes = null)
        {
            _sink = sink;
            if (allowedScenes != null)
            {
                var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string scene in allowedScenes)
                {
                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        set.Add(scene.Trim());
                    }
                }

                _allowedScenes = set.Count > 0 ? set : null;
            }
        }

        private static bool TryValidateCoordinate(double v, out float f)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < -MaxCoordinate || v > MaxCoordinate)
            {
                f = 0f;
                return false;
            }

            f = (float)v;
            return true;
        }

        private static Vector3 ValidatePosition(double x, double y, double z)
        {
            if (!TryValidateCoordinate(x, out float fx) ||
                !TryValidateCoordinate(y, out float fy) ||
                !TryValidateCoordinate(z, out float fz))
            {
                throw new ArgumentException(
                    $"world position must be finite and within ±{MaxCoordinate:0} per axis.");
            }

            return new Vector3(fx, fy, fz);
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

                    Publish(CoreAiWorldCommandEnvelope.Spawn(key, name, ValidatePosition(x, y, z)));
                    return name;
                }));

            registry.Register("coreai_world_move", new Action<string, double, double, double>((targetName, x, y, z) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Move(name, ValidatePosition(x, y, z)));
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

                if (_allowedScenes != null && !_allowedScenes.Contains(scene))
                {
                    throw new ArgumentException($"scene '{scene}' is not in the allowed scene list.");
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

            registry.Register("coreai_world_play_animation", new Action<string, string>((targetName, animationName) =>
            {
                string name = (targetName ?? "").Trim();
                string anim = (animationName ?? "").Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(anim))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.PlayAnimation(name, anim));
            }));

            registry.Register("coreai_world_play_sound",
                new Action<string, string, double>((targetName, clipName, volume) =>
                {
                    string name = (targetName ?? "").Trim();
                    string clip = (clipName ?? "").Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        return;
                    }

                    float vol = double.IsNaN(volume) ? 1f : Mathf.Clamp01((float)volume);
                    Publish(CoreAiWorldCommandEnvelope.PlaySound(name, clip, vol));
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
#endif
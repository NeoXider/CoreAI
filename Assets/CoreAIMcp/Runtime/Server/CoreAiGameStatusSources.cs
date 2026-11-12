using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Unity and mod-runtime parts of the MCP <c>game_status</c> tool. Each method is cheap and runs on the game
    /// thread when the tool is called.
    /// </summary>
    internal static class CoreAiGameStatusSources
    {
        /// <summary>Most mods listed one by one; the rest are counted.</summary>
        public const int MaxListedMods = 100;

        /// <summary>Platform, build type, versions, scenes and frame settings.</summary>
        public static JObject Engine()
        {
            JArray scenes = new();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                scenes.Add(scene.isLoaded ? scene.name : scene.name + " (loading)");
            }

            return new JObject
            {
                ["platform"] = Application.platform.ToString(),
                ["editor"] = Application.isEditor,
                ["development_build"] = Debug.isDebugBuild,
                ["unity_version"] = Application.unityVersion,
                ["product"] = Application.productName,
                ["app_version"] = Application.version,
                ["active_scene"] = SceneManager.GetActiveScene().name,
                ["scenes"] = scenes,
                ["uptime_s"] = Math.Round(Time.realtimeSinceStartupAsDouble, 1),
                ["frame"] = Time.frameCount,
                ["time_scale"] = Time.timeScale,
                ["focused"] = Application.isFocused,
                ["target_frame_rate"] = Application.targetFrameRate,
                ["vsync"] = QualitySettings.vSyncCount,
                ["screen"] = Screen.width + "x" + Screen.height,
                ["graphics_api"] = SystemInfo.graphicsDeviceType.ToString()
            };
        }

        /// <summary>The mods <paramref name="identity"/> may list, with their state and error counts.</summary>
        public static JObject Mods(ILuaModRuntime runtime, IActorIdentityProvider identity)
        {
            ActorContext caller = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            IReadOnlyList<LuaModInfo> mods = runtime.ListMods(caller) ?? Array.Empty<LuaModInfo>();
            JArray list = new();
            int quarantined = 0;
            int withErrors = 0;
            foreach (LuaModInfo mod in mods)
            {
                if (mod == null) continue;
                if (mod.Quarantined) quarantined++;
                if (mod.ErrorCount > 0) withErrors++;
                if (list.Count >= MaxListedMods) continue;
                list.Add(new JObject
                {
                    ["id"] = mod.Id,
                    ["state"] = mod.Quarantined ? "quarantined" : "running",
                    ["errors"] = mod.ErrorCount,
                    ["handlers"] = mod.HandlerCount,
                    ["timers"] = mod.TimerCount
                });
            }

            JObject result = new()
            {
                ["count"] = mods.Count,
                ["quarantined"] = quarantined,
                ["with_errors"] = withErrors,
                ["list"] = list
            };
            if (mods.Count > list.Count) result["not_listed"] = mods.Count - list.Count;
            return result;
        }

        /// <summary>Instance and bound-object counts of the published world, or null without a world host.</summary>
        public static JObject World(RbxWorldHost host)
        {
            if (host == null)
            {
                return null;
            }

            return new JObject
            {
                ["initialized"] = host.IsInitialized,
                ["instances"] = host.Registry != null ? host.Registry.Count : 0,
                ["bound_objects"] = host.Binder != null ? host.Binder.BoundCount : 0
            };
        }
    }
}

using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Предоставляет функции для работы со временем Unity (Time API) в Lua скриптах.
    /// Полезно для долгоживущих корутин, которым нужно реагировать на TimeScale и frame delta.
    /// </summary>
    public sealed class LuaTimeBindings
    {
        public void RegisterTimeApis(LuaApiRegistry registry)
        {
            registry.Register("time_delta", new System.Func<float>(() => Time.deltaTime));
            registry.Register("time_unscaled_delta", new System.Func<float>(() => Time.unscaledDeltaTime));
            registry.Register("time_now", new System.Func<float>(() => Time.time));
            registry.Register("time_realtime", new System.Func<float>(() => Time.realtimeSinceStartup));
            registry.Register("time_scale", new System.Func<float>(() => Time.timeScale));
            registry.Register("time_set_scale", new System.Action<float>(v => Time.timeScale = v));
            registry.Register("time_frame_count", new System.Func<int>(() => Time.frameCount));
            registry.Register("time_fixed_delta", new System.Func<float>(() => Time.fixedDeltaTime));
        }
    }
}

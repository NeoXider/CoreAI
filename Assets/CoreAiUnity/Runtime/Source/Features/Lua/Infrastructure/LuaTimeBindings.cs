#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Registers time-related Lua APIs.
    /// </summary>
    public sealed class LuaTimeBindings
    {
        /// <summary>Upper bound accepted by <c>time_set_scale</c>; values are clamped into [0, max].</summary>
        public const float MaxTimeScale = 10f;

        public void RegisterTimeApis(LuaApiRegistry registry)
        {
            registry.Register("time_delta", new System.Func<float>(() => Time.deltaTime));
            registry.Register("time_unscaled_delta", new System.Func<float>(() => Time.unscaledDeltaTime));
            registry.Register("time_now", new System.Func<float>(() => Time.time));
            registry.Register("time_realtime", new System.Func<float>(() => Time.realtimeSinceStartup));
            registry.Register("time_scale", new System.Func<float>(() => Time.timeScale));
            registry.Register("time_set_scale", new System.Action<double>(v =>
            {
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    throw new System.ArgumentException("time_set_scale: value must be finite.");
                }

                Time.timeScale = Mathf.Clamp((float)v, 0f, MaxTimeScale);
            }));
            registry.Register("time_frame_count", new System.Func<int>(() => Time.frameCount));
            registry.Register("time_fixed_delta", new System.Func<float>(() => Time.fixedDeltaTime));
        }
    }
}
#endif
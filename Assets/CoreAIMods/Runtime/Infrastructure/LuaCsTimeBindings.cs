using System;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using UnityEngine;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.Lua.LuaTimeBindings"/>.
    /// </summary>
    public sealed class LuaCsTimeBindings
    {
        public const float MaxTimeScale = 10f;

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Gameplay) == 0)
            {
                return;
            }

            RegisterTimeApis(registry);
        }

        public void RegisterTimeApis(IScriptFunctionRegistry registry)
        {
            registry.Register("time_delta", new Func<float>(() => Time.deltaTime));
            registry.Register("time_unscaled_delta", new Func<float>(() => Time.unscaledDeltaTime));
            registry.Register("time_now", new Func<float>(() => Time.time));
            registry.Register("time_realtime", new Func<float>(() => Time.realtimeSinceStartup));
            registry.Register("time_scale", new Func<float>(() => Time.timeScale));
            registry.Register("time_set_scale", new Action<double>(v =>
            {
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    throw new ArgumentException("time_set_scale: value must be finite.");
                }

                Time.timeScale = Mathf.Clamp((float)v, 0f, MaxTimeScale);
            }));
            registry.Register("time_frame_count", new Func<int>(() => Time.frameCount));
            registry.Register("time_fixed_delta", new Func<float>(() => Time.fixedDeltaTime));
        }
    }
}

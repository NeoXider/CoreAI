#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using UnityEngine;
using VContainer.Unity;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Drives <see cref="LuaModRuntime.Tick"/> from the Unity player loop so mod timers and
    /// queued events run on the main thread once per frame.
    /// </summary>
    public sealed class LuaModRuntimeTicker : ITickable
    {
        private readonly LuaModRuntime _runtime;

        public LuaModRuntimeTicker(LuaModRuntime runtime)
        {
            _runtime = runtime;
        }

        public void Tick()
        {
            _runtime?.Tick(Time.deltaTime);
        }
    }
}
#endif

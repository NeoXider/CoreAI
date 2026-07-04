#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Drives <see cref="LuaModRuntime.Tick"/> from a plain <c>Update()</c>. The previous
    /// VContainer <c>RegisterEntryPoint&lt;ITickable&gt;(factory)</c> registration never produced a
    /// dispatched tickable (verified live: ITickable unresolved, mods' hooks_every timers frozen in
    /// both the editor and WebGL), so persistent mods only advanced when something ticked the
    /// runtime manually. A MonoBehaviour has no such failure mode.
    /// </summary>
    public sealed class LuaModRuntimeTickDriver : MonoBehaviour
    {
        private LuaModRuntime _runtime;

        /// <summary>Attaches the runtime this driver ticks every frame.</summary>
        public void Initialize(LuaModRuntime runtime)
        {
            _runtime = runtime;
        }

        private void Update()
        {
            _runtime?.Tick(Time.deltaTime);
        }
    }
}
#endif

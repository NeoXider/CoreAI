using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Drives <see cref="ILuaModRuntime.Tick"/> from a plain <c>Update()</c>. VM-agnostic: it ticks
    /// whichever mod runtime (MoonSharp <c>LuaModRuntime</c> or Lua-CSharp <c>LuaCsModRuntime</c>) the
    /// composition wired. The previous VContainer <c>RegisterEntryPoint&lt;ITickable&gt;(factory)</c>
    /// registration never produced a dispatched tickable (verified live: ITickable unresolved, mods'
    /// hooks_every timers frozen in both the editor and WebGL), so persistent mods only advanced when
    /// something ticked the runtime manually. A MonoBehaviour has no such failure mode.
    /// </summary>
    public sealed class LuaModRuntimeTickDriver : MonoBehaviour
    {
        private ILuaModRuntime _runtime;

        /// <summary>Attaches the runtime this driver ticks every frame.</summary>
        public void Initialize(ILuaModRuntime runtime)
        {
            _runtime = runtime;
        }

        private void Update()
        {
            _runtime?.Tick(Time.deltaTime);
        }
    }
}
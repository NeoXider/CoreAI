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
        private System.Action _preTick;

        /// <summary>Attaches the runtime this driver ticks every frame, plus an optional
        /// pre-tick pump (the Roblox input pump) that must observe device state before mod
        /// dispatch so handlers see this frame's events.</summary>
        public void Initialize(ILuaModRuntime runtime, System.Action preTick = null)
        {
            _runtime = runtime;
            _preTick = preTick;
        }

        private void Update()
        {
            _preTick?.Invoke();
            _runtime?.Tick(Time.deltaTime);
        }
    }
}

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
        private System.Action<float> _preTick;

        /// <summary>Attaches the runtime this driver ticks every frame, plus an optional
        /// pre-tick pump (the Roblox input + RunService pump) that must observe device state and
        /// fire the per-frame signals before mod dispatch, carrying this frame's delta so
        /// RunService.Heartbeat handlers receive it.</summary>
        public void Initialize(ILuaModRuntime runtime, System.Action<float> preTick = null)
        {
            _runtime = runtime;
            _preTick = preTick;
        }

        private void Update()
        {
            // WHY: one delta for both the pre-tick pump (RunService.Step wants the frame delta) and
            // the runtime tick, so the game loop and mod timers advance on the same clock.
            float dt = Time.deltaTime;
            _preTick?.Invoke(dt);
            _runtime?.Tick(dt);
        }
    }
}

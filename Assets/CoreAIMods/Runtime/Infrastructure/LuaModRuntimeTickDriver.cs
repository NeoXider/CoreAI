using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Scheduling;
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
        private ActorContext _actorContext;
        private System.Action<float> _preSimulation;
        private System.Action<float> _heartbeat;
        private System.Action<float> _preRender;
        private ModScheduler _scheduler;

        /// <summary>Attaches the runtime and phase-specific host pumps to the scheduler.</summary>
        public void Initialize(ILuaModRuntime runtime, ActorContext actorContext, ModScheduler scheduler = null,
            System.Action<float> preSimulation = null,
            System.Action<float> heartbeat = null,
            System.Action<float> preRender = null)
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnSchedulerPhaseReached;
            }

            _runtime = runtime;
            _actorContext = actorContext;
            _scheduler = scheduler;
            _preSimulation = preSimulation;
            _heartbeat = heartbeat;
            _preRender = preRender;
            if (_scheduler != null)
            {
                _scheduler.PhaseReached += OnSchedulerPhaseReached;
            }
        }

        private void Update()
        {
            PumpFrame(Time.deltaTime);
        }

        private void OnDestroy()
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnSchedulerPhaseReached;
            }
        }

        /// <summary>Advances one scaled host frame in scheduler, signal, then runtime order.</summary>
        public void PumpFrame(float deltaSeconds)
        {
            if (_scheduler != null)
            {
                _scheduler.Advance(deltaSeconds);
            }
            else
            {
                _preSimulation?.Invoke(deltaSeconds);
                _heartbeat?.Invoke(deltaSeconds);
                _preRender?.Invoke(deltaSeconds);
            }

            _runtime?.Tick(_actorContext, deltaSeconds);
        }

        private void OnSchedulerPhaseReached(SchedulerPhase phase, double deltaSeconds)
        {
            float frameDelta = (float)deltaSeconds;
            switch (phase)
            {
                case SchedulerPhase.PreSimulation:
                    _preSimulation?.Invoke(frameDelta);
                    return;
                case SchedulerPhase.Heartbeat:
                    _heartbeat?.Invoke(frameDelta);
                    return;
                case SchedulerPhase.PreRender:
                    _preRender?.Invoke(frameDelta);
                    return;
            }
        }
    }
}

using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
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
        private RbxWorldRuntimeSessionController _sessionController;
        private System.Action _beginPhysicsStep;
        private System.Action _applyGravity;

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
            _sessionController = null;
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

        /// <summary>
        /// Attaches the two fixed-step pumps: opening a physics step and applying world gravity.
        /// Either may be null; a world with no physics adapter simply has nothing to pump.
        /// </summary>
        public void AttachPhysicsPumps(System.Action beginPhysicsStep, System.Action applyGravity)
        {
            _beginPhysicsStep = beginPhysicsStep;
            _applyGravity = applyGravity;
        }

        /// <summary>Attaches the production session controller so every frame targets the active world.</summary>
        public void Initialize(
            RbxWorldRuntimeSessionController sessionController,
            ActorContext actorContext)
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnSchedulerPhaseReached;
            }

            _runtime = null;
            _scheduler = null;
            _preSimulation = null;
            _heartbeat = null;
            _preRender = null;
            _sessionController = sessionController
                ?? throw new System.ArgumentNullException(nameof(sessionController));
            _actorContext = actorContext;
        }

        private void Update()
        {
            PumpFrame(Time.deltaTime);
        }

        /// <summary>
        /// Opens the physics step and applies world gravity, once per fixed step.
        /// </summary>
        /// <remarks>
        /// WHY a second pump and not more work in Update: gravity is a force, and a force applied on
        /// the render frame is applied a variable number of times per simulated step — parts would
        /// fall at a rate that depends on the frame rate. Unity's contract is that forces belong in
        /// FixedUpdate, and CoreAI's teleport rule needs the same boundary: this runs before the
        /// simulation, so a script's assignment during the step is known when its contacts arrive.
        /// </remarks>
        private void FixedUpdate()
        {
            _beginPhysicsStep?.Invoke();
            _applyGravity?.Invoke();
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
            if (_sessionController != null)
            {
                _sessionController.PumpFrame(_actorContext, deltaSeconds);
                return;
            }

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

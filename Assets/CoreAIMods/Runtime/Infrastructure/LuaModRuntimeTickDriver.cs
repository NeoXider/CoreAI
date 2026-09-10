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
        private ModScheduler _scheduler;
        private RbxWorldRuntimeSessionController _sessionController;
        private System.Action _beginPhysicsStep;
        private System.Action _applyGravity;
        private System.Action<float> _stepCharacterMotors;
        private System.Func<System.Action> _liveApplyGravity;

        /// <summary>Attaches the runtime and, for a host that owns one directly, its scheduler.</summary>
        /// <remarks>
        /// WHY this driver takes no per-phase pumps: the scheduler is the ONLY frame authority.
        /// <c>ModScheduler.Advance</c> raises <c>PhaseReached</c> for each phase in pipeline order, and
        /// <c>LuaCsRbxApiBindings</c> — which owns both the scheduler and the RunService instance whose
        /// signals those phases fire — subscribes to it once and pumps every phase itself. A driver that
        /// ALSO subscribed and re-invoked the very same public <c>Pump*</c> methods made Stepped,
        /// Heartbeat and RenderStepped fire TWICE per frame: mods counted double, budget kills were
        /// charged twice, and a handler cut for a runaway loop was cut again in the same frame. Do not
        /// re-add a second phase route here, and do not call the bindings' <c>Pump*</c> methods next to
        /// an <c>Advance</c> — one frame is one <c>Advance</c>.
        /// </remarks>
        public void Initialize(ILuaModRuntime runtime, ActorContext actorContext,
            ModScheduler scheduler = null)
        {
            _runtime = runtime;
            _sessionController = null;
            _actorContext = actorContext;
            _scheduler = scheduler;
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

        /// <summary>
        /// Attaches the resolver for the LIVE physics port, so the fixed step follows a world
        /// replacement instead of pumping the world that was just disposed.
        /// </summary>
        /// <remarks>
        /// WHY a resolver and not the port itself: loading a world builds a new Rbx API and a new
        /// physics port and disposes the old ones. A delegate captured at composition time keeps
        /// pumping the dead pair — gravity iterates bodies of a torn-down binder, raycasts miss
        /// everything, and every Humanoid runs on the null motor — all silently, because the
        /// render-frame pumps DO follow the session and only the fixed step is left behind.
        /// </remarks>
        public void AttachLiveGravityPump(System.Func<System.Action> liveApplyGravity)
        {
            _liveApplyGravity = liveApplyGravity;
        }

        /// <summary>
        /// Attaches the character-motor fixed-step pump (F10): <c>LuaCsRbxApiBindings.
        /// StepCharacterMotors</c>, called from <see cref="FixedUpdate"/> alongside the physics-step
        /// opening and gravity, never from the render-frame pump. Null leaves character motors
        /// unstepped, same as an unattached physics pump.
        /// </summary>
        public void AttachCharacterMotorStep(System.Action<float> stepCharacterMotors)
        {
            _stepCharacterMotors = stepCharacterMotors;
        }

        /// <summary>Attaches the production session controller so every frame targets the active world.</summary>
        public void Initialize(
            RbxWorldRuntimeSessionController sessionController,
            ActorContext actorContext)
        {
            _runtime = null;
            _scheduler = null;
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
            // WHY the session is asked every step instead of using the delegates captured at
            // composition: loading a world replaces the Rbx API and the physics port and disposes
            // the old ones, and only the render-frame pumps follow that swap. Captured fixed-step
            // delegates kept driving the dead world — gravity on a torn-down binder, raycasts that
            // always miss, every Humanoid back on the null motor — with nothing in the log. The
            // captured delegates remain the fallback for a host that runs no session controller.
            global::CoreAI.Ai.LuaCs.LuaCsRbxApiBindings live = _sessionController?.CurrentRbxApi;
            if (live != null)
            {
                live.WorldPhysics?.BeginPhysicsStep();
                live.StepCharacterMotors(Time.fixedDeltaTime);
            }
            else
            {
                _beginPhysicsStep?.Invoke();
                _stepCharacterMotors?.Invoke(Time.fixedDeltaTime);
            }

            System.Action gravity = _liveApplyGravity?.Invoke() ?? _applyGravity;
            gravity?.Invoke();
        }

        /// <summary>Advances one scaled host frame in scheduler, signal, then runtime order.</summary>
        public void PumpFrame(float deltaSeconds)
        {
            if (_sessionController != null)
            {
                _sessionController.PumpFrame(_actorContext, deltaSeconds);
                return;
            }

            // WHY nothing but Advance: the scheduler walks the phase pipeline and the Rbx bindings fire
            // each phase's signals from PhaseReached. Adding a second pump here fires them twice.
            _scheduler?.Advance(deltaSeconds);
            _runtime?.Tick(_actorContext, deltaSeconds);
        }
    }
}

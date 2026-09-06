namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox RunService: the per-frame game loop signals Stepped/Heartbeat/RenderStepped. State
    /// is driven by the host composition, which calls <see cref="Step"/> once per frame with the
    /// frame delta (mirroring how <see cref="RbxUserInputService.Step"/> is pumped); each signal
    /// queues its handlers for the scheduler's deferred signal drain.
    /// </summary>
    public sealed class RbxRunService : RbxInstance
    {
        // WHY: Roblox fires Stepped with (runTime, dt) where runTime is the accumulated frame time
        // since the loop started; keep the running total so the first argument matches Roblox.
        private float _runTime;

        internal RbxRunService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "RunService";
        }

        /// <summary>Fires (deltaTime) each frame after physics, before rendering — the idiomatic
        /// per-frame game-loop hook.</summary>
        public RbxScriptSignal Heartbeat { get; } =
            new("RunService.Heartbeat");

        /// <summary>Fires (accumulatedTime, deltaTime) each frame before physics.</summary>
        public RbxScriptSignal Stepped { get; } =
            new("RunService.Stepped");

        /// <summary>Fires (deltaTime) each frame before the screen renders. Superseded by
        /// <see cref="PreRender"/>; both fire, and only on a process that draws frames.</summary>
        public RbxScriptSignal RenderStepped { get; } =
            new("RunService.RenderStepped");

        /// <summary>Fires (deltaTimeSim) each frame before the physics simulation, after
        /// rendering — the phase for touching animation state.</summary>
        public RbxScriptSignal PreAnimation { get; } =
            new("RunService.PreAnimation");

        /// <summary>Fires (deltaTimeSim) each frame before the physics simulation. The modern
        /// replacement for <see cref="Stepped"/>, which keeps its legacy (runTime, dt) signature.</summary>
        public RbxScriptSignal PreSimulation { get; } =
            new("RunService.PreSimulation");

        /// <summary>Fires (deltaTimeSim) each frame after the physics simulation, before
        /// <see cref="Heartbeat"/>.</summary>
        public RbxScriptSignal PostSimulation { get; } =
            new("RunService.PostSimulation");

        /// <summary>Fires (deltaTimeRender) each frame before the frame is drawn. The modern
        /// replacement for <see cref="RenderStepped"/>; neither fires on a dedicated server.</summary>
        public RbxScriptSignal PreRender { get; } =
            new("RunService.PreRender");

        /// <summary>Topology source behind IsServer/IsClient/IsStudio/IsRunning. Defaults to the
        /// solo/loopback answer; the host/client slice replaces it without touching the Lua binding.</summary>
        public IRbxRuntimeTopology Topology { get; set; } = RbxSoloRuntimeTopology.Shared;

        /// <summary>
        /// Per-frame pump in the mirror's frame order: PreAnimation, PreSimulation (with legacy
        /// Stepped), PostSimulation, Heartbeat, then the render pair. The host calls this once per
        /// frame (before mod dispatch, like the input pump) so handlers advance every frame. Each
        /// signal is gated on <see cref="RbxScriptSignal.HasConnections"/> so an unlistened signal
        /// boxes nothing.
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsDestroyed)
            {
                return;
            }

            _runTime += deltaSeconds;

            // WHY: the delta is passed as a boxed number so MarshalSignalArg wraps it into a Lua
            // number, exactly as the input pump boxes its InputObject/bool payloads for dispatch.
            object delta = deltaSeconds;
            FireIfListened(PreAnimation, delta);
            FireIfListened(PreSimulation, delta);
            if (Stepped.HasConnections)
            {
                Stepped.Fire(_runTime, delta);
            }

            FireIfListened(PostSimulation, delta);
            FireIfListened(Heartbeat, delta);
            FireRenderPhase(delta);
        }

        /// <summary>
        /// Fires PreRender and its legacy alias RenderStepped, unless this process draws no frames.
        /// </summary>
        /// <remarks>
        /// WHY the gate: the mirror makes PreRender client-side, and a dedicated server that ran
        /// render-frame handlers would burn its budget on work whose output nobody sees — and would
        /// let a mod's camera/effect code run where there is no camera. Solo and host both draw, so
        /// both keep firing.
        /// </remarks>
        internal void FireRenderPhase(object delta)
        {
            if (Topology is { RendersFrames: false })
            {
                return;
            }

            FireIfListened(PreRender, delta);
            FireIfListened(RenderStepped, delta);
        }

        private static void FireIfListened(RbxScriptSignal signal, object delta)
        {
            if (signal.HasConnections)
            {
                signal.Fire(delta);
            }
        }
    }
}

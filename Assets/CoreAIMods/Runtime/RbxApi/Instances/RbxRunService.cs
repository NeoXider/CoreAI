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

        /// <summary>Fires (deltaTime) each frame before the screen renders.</summary>
        public RbxScriptSignal RenderStepped { get; } =
            new("RunService.RenderStepped");

        /// <summary>Topology source behind IsServer/IsClient/IsStudio/IsRunning. Defaults to the
        /// solo/loopback answer; the host/client slice replaces it without touching the Lua binding.</summary>
        public IRbxRuntimeTopology Topology { get; set; } = RbxSoloRuntimeTopology.Shared;

        /// <summary>
        /// Per-frame pump: fires Stepped, then Heartbeat, then RenderStepped with the frame delta.
        /// The host calls this once per frame (before mod dispatch, like the input pump) so handlers
        /// advance every frame. Each signal is gated on <see cref="RbxScriptSignal.HasConnections"/>
        /// so an unlistened signal boxes nothing.
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
            if (Stepped.HasConnections)
            {
                Stepped.Fire(_runTime, delta);
            }

            if (Heartbeat.HasConnections)
            {
                Heartbeat.Fire(delta);
            }

            if (RenderStepped.HasConnections)
            {
                RenderStepped.Fire(delta);
            }
        }
    }
}

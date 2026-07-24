namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox RunService: the per-frame game loop signals Stepped/Heartbeat/RenderStepped. State
    /// is driven by the host composition, which calls <see cref="Step"/> once per frame with the
    /// frame delta (mirroring how <see cref="RbxUserInputService.Step"/> is pumped); each signal
    /// fires synchronously so <c>Heartbeat:Connect(function(dt) ... end)</c> runs that frame.
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
            new RbxScriptSignal("RunService.Heartbeat", supportsDispatch: true);

        /// <summary>Fires (accumulatedTime, deltaTime) each frame before physics.</summary>
        public RbxScriptSignal Stepped { get; } =
            new RbxScriptSignal("RunService.Stepped", supportsDispatch: true);

        /// <summary>Fires (deltaTime) each frame before the screen renders.</summary>
        public RbxScriptSignal RenderStepped { get; } =
            new RbxScriptSignal("RunService.RenderStepped", supportsDispatch: true);

        /// <summary>
        /// Per-frame pump: fires Stepped, then Heartbeat, then RenderStepped with the frame delta.
        /// The host calls this once per frame (before mod dispatch, like the input pump) so handlers
        /// advance every frame. Each signal is gated on <see cref="RbxScriptSignal.HasConnections"/>
        /// so an unlistened signal boxes nothing.
        /// </summary>
        // TODO: MVP2 — the general signal scheduler replaces this pump with deferred dispatch.
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

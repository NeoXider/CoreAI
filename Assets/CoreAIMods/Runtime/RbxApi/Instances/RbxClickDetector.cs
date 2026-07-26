namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox ClickDetector: parented under a clickable BasePart, it fires MouseClick when the user
    /// clicks that part with the mouse (the host picks the part under the cursor each frame and fires
    /// the hit part's detector — see the pick pump in the bindings layer). MouseHoverEnter/
    /// MouseHoverLeave exist for parity; the pick pump leaves them unfired in this slice.
    /// State is driven by the host: <see cref="MouseClick"/> is a dispatch-enabled signal fired by
    /// C# (mirroring <see cref="RbxRunService"/>'s Heartbeat), so a mod does
    /// <c>cd.MouseClick:Connect(function() ... end)</c> and runs that handler on the click frame.
    /// </summary>
    public sealed class RbxClickDetector : RbxInstance
    {
        internal RbxClickDetector(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "ClickDetector";
        }

        // WHY: Roblox fires MouseClick with (playerWhoClicked). CoreAI has no Players service yet,
        // so MouseClick fires with NO arguments — a mod connects `MouseClick:Connect(function() end)`.
        // TODO: pass the clicking player once a Players service lands.
        /// <summary>Fires (no args) when the owning part is clicked within MaxActivationDistance.</summary>
        public RbxScriptSignal MouseClick { get; } =
            new("ClickDetector.MouseClick", true);

        /// <summary>Fires when the cursor enters the owning part's hover range. Parity hook; the
        /// MVP pick pump does not fire it yet.</summary>
        public RbxScriptSignal MouseHoverEnter { get; } =
            new("ClickDetector.MouseHoverEnter", true);

        /// <summary>Fires when the cursor leaves the owning part's hover range. Parity hook; the
        /// MVP pick pump does not fire it yet.</summary>
        public RbxScriptSignal MouseHoverLeave { get; } =
            new("ClickDetector.MouseHoverLeave", true);

        /// <summary>Roblox ClickDetector.MaxActivationDistance (studs, default 32): a click farther
        /// than this from the camera does not fire MouseClick.</summary>
        public double MaxActivationDistance { get; set; } = 32d;
    }
}

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox ClickDetector: parented under a BasePart, a Model or a Folder, it fires MouseClick when
    /// the user clicks a part of that ancestor with the mouse (the host picks the part under the
    /// cursor each frame and fires the deepest detector above it — see the pick pump in the bindings
    /// layer). MouseHoverEnter/MouseHoverLeave exist for parity; the pick pump leaves them unfired in
    /// this slice. State is driven by the host: <see cref="MouseClick"/> is fired by C# and uses the
    /// same deferred scheduler dispatch as every other signal.
    /// </summary>
    public sealed class RbxClickDetector : RbxInstance
    {
        private double _maxActivationDistance = 32d;

        internal RbxClickDetector(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "ClickDetector";
        }

        /// <summary>
        /// Fires with <c>(playerWhoClicked)</c> when a part under the detector's parent is clicked
        /// within <see cref="MaxActivationDistance"/>; the player is nil only when the host could
        /// resolve no local player for the click.
        /// </summary>
        /// <remarks>
        /// WHY registered signals and not field-initialised ones: Destroy disconnects exactly the
        /// signals held in the instance's signal table, so a field kept delivering to the handlers
        /// of a destroyed detector.
        /// </remarks>
        public RbxScriptSignal MouseClick => GetOrCreateSignal("MouseClick");

        /// <summary>Fires when the cursor enters the owning part's hover range. Parity hook; the
        /// MVP pick pump does not fire it yet.</summary>
        public RbxScriptSignal MouseHoverEnter => GetOrCreateSignal("MouseHoverEnter");

        /// <summary>Fires when the cursor leaves the owning part's hover range. Parity hook; the
        /// MVP pick pump does not fire it yet.</summary>
        public RbxScriptSignal MouseHoverLeave => GetOrCreateSignal("MouseHoverLeave");

        /// <summary>Roblox ClickDetector.MaxActivationDistance (studs, default 32): a click by a
        /// player whose character is farther than this from the clicked part does not fire
        /// MouseClick. A change fires <c>Changed("MaxActivationDistance")</c> and its property
        /// signal; an equal write fires nothing.</summary>
        public double MaxActivationDistance
        {
            get => _maxActivationDistance;
            set
            {
                if (_maxActivationDistance.Equals(value))
                {
                    return;
                }

                _maxActivationDistance = value;
                NotifyPropertyChanged(nameof(MaxActivationDistance));
            }
        }
    }
}

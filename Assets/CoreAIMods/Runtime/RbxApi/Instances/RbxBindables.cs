namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Mirror <c>BindableEvent</c>: one-way communication between scripts on the same side. <c>Fire</c> never
    /// yields; every handler connected to <c>Event</c> runs deferred, like every other CoreAI signal, and gets
    /// its own copy of the arguments.
    /// </summary>
    /// <remarks>
    /// The Lua binding copies the arguments before they reach <see cref="Fire"/> (the remote copy rules:
    /// tables copied without metatables, non-string dictionary keys turned into strings, functions dropped),
    /// so this class only carries already-copied values.
    /// </remarks>
    public sealed class RbxBindableEvent : RbxInstance
    {
        internal RbxBindableEvent(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Event = GetOrCreateSignal("Event");
        }

        /// <summary>Mirror <c>BindableEvent.Event(...)</c>.</summary>
        public RbxScriptSignal Event { get; }

        /// <summary>Mirror <c>BindableEvent:Fire(...)</c>: queues <c>Event</c> with the copied arguments.</summary>
        public void Fire(object[] copiedArguments)
        {
            ThrowIfDestroyed("Fire");
            FireSignal("Event", copiedArguments ?? System.Array.Empty<object>());
        }
    }

    /// <summary>
    /// Mirror <c>BindableFunction</c>: two-way communication between scripts on the same side. The
    /// <c>OnInvoke</c> callback and the invocations waiting for one are kept by the Lua binding, which runs the
    /// callback on its own mod's thread and budget and resumes the yielding <c>Invoke</c> with copied results.
    /// </summary>
    public sealed class RbxBindableFunction : RbxInstance
    {
        internal RbxBindableFunction(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }
    }
}

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// <c>game:GetService("UxmlService")</c>: the CoreAI-only service through which a mod builds its
    /// interface from UXML and USS text while the game runs. Roblox has no markup, so this is a
    /// conscious deviation (a numbered DEV entry) that the export lint flags; imported Roblox scripts
    /// use the Roblox GUI classes instead. The instance is the Lua-facing face of an
    /// <see cref="RbxUiWorld"/>; without one attached, every member answers <c>UI_UNAVAILABLE</c>.
    /// </summary>
    public sealed class RbxUxmlService : RbxInstance
    {
        /// <summary>The service class name.</summary>
        public const string ServiceClassName = "UxmlService";

        /// <summary>Creates the service; it is a registered service, not a member of the instance tree.</summary>
        public RbxUxmlService()
            : base(new ClassDescriptor(ServiceClassName, "Instance", false, false, true))
        {
        }

        /// <summary>The runtime UI state of the world this service fronts, or null when none is attached.</summary>
        public RbxUiWorld World { get; private set; }

        /// <summary>Attaches (or replaces) the world state behind the service.</summary>
        public void Attach(RbxUiWorld world)
        {
            World = world;
        }
    }
}

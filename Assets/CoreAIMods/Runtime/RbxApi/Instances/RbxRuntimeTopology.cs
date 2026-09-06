namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Engine-free source behind the RunService topology queries
    /// (<c>IsServer</c>/<c>IsClient</c>/<c>IsStudio</c>/<c>IsRunning</c>), so the solo/loopback
    /// answer can be swapped for the real host/client answer without touching the Lua binding.</summary>
    public interface IRbxRuntimeTopology
    {
        /// <summary>Whether mod Lua runs in a server execution context.</summary>
        bool IsServer { get; }

        /// <summary>Whether mod Lua runs in a client execution context.</summary>
        bool IsClient { get; }

        /// <summary>Whether the runtime is Roblox Studio (always false for CoreAI).</summary>
        bool IsStudio { get; }

        /// <summary>Whether the experience simulation is currently running.</summary>
        bool IsRunning { get; }

        /// <summary>
        /// Whether this process draws frames, and therefore whether the render-phase signals
        /// (<c>PreRender</c> and its legacy alias <c>RenderStepped</c>) fire at all.
        /// </summary>
        /// <remarks>
        /// WHY this is not <see cref="IsClient"/>: the mirror says PreRender is client-side, but
        /// CoreAI's solo process both renders and is the server, and IsClient stays false there so
        /// server-side Lua is never told it is a client. Gating the render phase on IsClient would
        /// silently stop every solo game's per-frame render handler. The question the phase actually
        /// asks is "does anything get drawn here", and only a dedicated server answers no.
        /// </remarks>
        bool RendersFrames { get; }
    }

    /// <summary>Solo/loopback topology: the mod runtime is the server authority with no client
    /// execution context and no Studio, and any reachable Lua runs while the runtime is live.</summary>
    public sealed class RbxSoloRuntimeTopology : IRbxRuntimeTopology
    {
        /// <summary>Shared solo topology backing every RunService until the host/client slice lands.</summary>
        public static readonly RbxSoloRuntimeTopology Shared = new RbxSoloRuntimeTopology();

        // WHY: per the Roblox reference mirror, IsClient is true only inside a client execution
        // context (a LocalScript, a ModuleScript required by one, or a RunContext.Client Script)
        // and false in all other cases; the solo runtime has no client context, so it answers
        // false here while IsServer answers true.
        public bool IsServer => true;

        public bool IsClient => false;

        // WHY: CoreAI is not Roblox Studio, and mods must never branch on Studio (built players
        // are the only target), so this is unconditionally false rather than a host capability.
        public bool IsStudio => false;

        public bool IsRunning => true;

        public bool RendersFrames => true;
    }

    /// <summary>
    /// Topology derived from a live network bridge: the transport decides which side this process is.
    /// </summary>
    /// <remarks>
    /// WHY derived rather than configured: CoreAI already had a second "am I the host" answer in the
    /// AI authority layer, and two independently configured answers eventually disagree — at which
    /// point a script gets one story and the command pipeline another. The bridge knows the truth
    /// because it is the thing connected to the other side, so it is the single source.
    /// </remarks>
    public sealed class RbxBridgeRuntimeTopology : IRbxRuntimeTopology
    {
        private readonly Networking.INetworkBridge _bridge;

        /// <summary>Reads the topology from the bridge on every query.</summary>
        public RbxBridgeRuntimeTopology(Networking.INetworkBridge bridge)
        {
            _bridge = bridge ?? throw new System.ArgumentNullException(nameof(bridge));
        }

        /// <inheritdoc />
        public bool IsServer =>
            _bridge.Topology != Networking.RbxNetworkTopology.Client;

        /// <inheritdoc />
        /// <remarks>
        /// WHY only a pure client answers true, even on a host: in Roblox IsClient is true inside a
        /// CLIENT execution context, and CoreAI does not yet let a mod declare which context it runs
        /// in. Answering true on a host would tell server-side Lua it is a client, which is worse
        /// than the conservative answer — it is a wrong one that a script would branch on.
        /// </remarks>
        public bool IsClient => _bridge.Topology == Networking.RbxNetworkTopology.Client;

        /// <inheritdoc />
        public bool IsStudio => false;

        /// <inheritdoc />
        public bool IsRunning => true;

        /// <inheritdoc />
        public bool RendersFrames =>
            _bridge.Topology != Networking.RbxNetworkTopology.DedicatedServer;
    }
}

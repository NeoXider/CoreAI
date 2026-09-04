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
    }
}

namespace CoreAI.Authority
{
    /// <summary>
    /// Defines which network peers may execute AI-generated commands.
    /// </summary>
    public enum AiNetworkExecutionPolicy
    {
        /// <summary>Allows AI commands to execute on every participating peer.</summary>
        AllPeers = 0,

        /// <summary>Allows AI commands to execute only on the authoritative host.</summary>
        HostOnly = 1,

        /// <summary>Allows AI commands to execute only on non-host client peers.</summary>
        ClientPeersOnly = 2
    }
}

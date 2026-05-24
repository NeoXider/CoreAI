namespace CoreAI.Authority
{
    /// <summary>
    /// Describes the local peer authority state used by CoreAI network checks.
    /// </summary>
    public interface IAiNetworkPeer
    {
        /// <summary>Gets whether this peer is the authoritative host.</summary>
        bool IsHostAuthority { get; }

        /// <summary>
        /// Gets whether this peer is a non-host client.
        /// </summary>
        bool IsPureClient { get; }
    }
}

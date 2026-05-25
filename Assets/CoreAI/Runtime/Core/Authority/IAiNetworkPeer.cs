namespace CoreAI.Authority
{
    /// <summary>
    /// Describes the local peer authority state used by CoreAI network checks.
    /// </summary>
    public interface IAiNetworkPeer
    {
        /// <summary>True when this peer may execute authoritative AI-side world changes.</summary>
        bool IsHostAuthority { get; }

        /// <summary>
        /// True when this peer is a presentation client and must not execute authoritative AI mutations.
        /// </summary>
        bool IsPureClient { get; }
    }
}

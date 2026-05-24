namespace CoreAI.Authority
{
    /// <summary>Reports solo-player authority state for projects without networking.</summary>
    public sealed class DefaultSoloNetworkPeer : IAiNetworkPeer
    {
        /// <inheritdoc />
        public bool IsHostAuthority => true;

        /// <inheritdoc />
        public bool IsPureClient => false;
    }
}

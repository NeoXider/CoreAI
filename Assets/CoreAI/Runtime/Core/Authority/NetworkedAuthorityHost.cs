using System;

namespace CoreAI.Authority
{
    /// <summary>
    /// Routes AI command execution through the configured network authority policy.
    /// </summary>
    public sealed class NetworkedAuthorityHost : IAuthorityHost
    {
        private readonly IAiNetworkPeer _peer;
        private readonly AiNetworkExecutionPolicy _policy;

        /// <param name="peer">The peer value.</param>
        /// <param name="policy">The policy value.</param>
        public NetworkedAuthorityHost(IAiNetworkPeer peer, AiNetworkExecutionPolicy policy)
        {
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _policy = policy;
        }

        /// <inheritdoc />
        public bool CanRunAiTasks =>
            _policy switch
            {
                AiNetworkExecutionPolicy.AllPeers => true,
                AiNetworkExecutionPolicy.HostOnly => _peer.IsHostAuthority,
                AiNetworkExecutionPolicy.ClientPeersOnly => _peer.IsPureClient,
                _ => true
            };
    }
}

using System;

namespace CoreAI.Authority
{
    /// <summary>
    /// <see cref="IAuthorityHost"/> на основе <see cref="IAiNetworkPeer"/> и политики выполнения.
    /// </summary>
    public sealed class NetworkedAuthorityHost : IAuthorityHost
    {
        private readonly IAiNetworkPeer _peer;
        private readonly AiNetworkExecutionPolicy _policy;

        /// <param name="peer">Роль узла в сети.</param>
        /// <param name="policy">Кто может вызывать ИИ.</param>
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

using CoreAI.Authority;
using UnityEngine;

namespace CoreAI.Composition
{
    /// <summary>
    /// Базовый компонент для описания роли текущего узла в сети. Реализуйте в игре (Unity Netcode: IsServer / IsHost / IsClient).
    /// </summary>
    public abstract class CoreAiNetworkPeerBehaviour : MonoBehaviour, IAiNetworkPeer
    {
        /// <inheritdoc />
        public abstract bool IsHostAuthority { get; }

        /// <inheritdoc />
        public abstract bool IsPureClient { get; }
    }
}
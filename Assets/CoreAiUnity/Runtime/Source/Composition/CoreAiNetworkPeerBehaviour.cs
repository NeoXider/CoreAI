using CoreAI.Authority;
using UnityEngine;

namespace CoreAI.Composition
{
    /// <summary>
    /// Unity component that exposes local network authority flags to CoreAI.
    /// </summary>
    public abstract class CoreAiNetworkPeerBehaviour : MonoBehaviour, IAiNetworkPeer
    {
        /// <inheritdoc />
        public abstract bool IsHostAuthority { get; }

        /// <inheritdoc />
        public abstract bool IsPureClient { get; }
    }
}

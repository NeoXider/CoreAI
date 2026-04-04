namespace CoreAI.Authority
{
    /// <summary>Одиночная игра или отсутствие сетевого слоя: считаемся хостом, не «чистым» клиентом.</summary>
    public sealed class DefaultSoloNetworkPeer : IAiNetworkPeer
    {
        /// <inheritdoc />
        public bool IsHostAuthority => true;

        /// <inheritdoc />
        public bool IsPureClient => false;
    }
}
namespace CoreAI.Mcp.Server
{
    /// <summary>A transport the host must advance every frame (the bridge link polls from the game loop).</summary>
    internal interface IMcpTickingTransport
    {
        /// <summary>Advances the transport; called from the host's frame loop.</summary>
        void Tick();
    }

    /// <summary>
    /// <see cref="IMcpServerTransport"/> over a <see cref="McpBridgeLink"/>: "running" means linking (attached or
    /// retrying); <see cref="LastLinkError"/> says why it is not attached. Engine-free.
    /// </summary>
    internal sealed class McpBridgeTransport : IMcpServerTransport, IMcpTransportDiagnostics, IMcpTickingTransport
    {
        private readonly McpBridgeLink _link;

        public McpBridgeTransport(McpBridgeLink link)
        {
            _link = link ?? throw new System.ArgumentNullException(nameof(link));
        }

        /// <summary>The link this transport drives.</summary>
        public McpBridgeLink Link => _link;

        public bool IsRunning => _link.IsStarted;

        public string Url => _link.Url;

        public string LastLinkError => _link.LastLinkError;

        public void Start() => _link.Start();

        public void Tick() => _link.Tick();

        public void Dispose() => _link.Stop();
    }
}

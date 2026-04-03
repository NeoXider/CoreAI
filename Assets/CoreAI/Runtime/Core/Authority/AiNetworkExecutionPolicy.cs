namespace CoreAI.Authority
{
    /// <summary>
    /// Где разрешён запуск LLM/оркестратора в мультиплеере. В одиночке используйте <see cref="DefaultSoloNetworkPeer"/>.
    /// </summary>
    public enum AiNetworkExecutionPolicy
    {
        /// <summary>По умолчанию: ИИ может выполняться на каждом узле (дублирование вызовов — осознанный выбор).</summary>
        AllPeers = 0,

        /// <summary>Только узел с авторитетом (хост / dedicated server / solo).</summary>
        HostOnly = 1,

        /// <summary>Только «чистые» клиенты без роли хоста (не listen-server).</summary>
        ClientPeersOnly = 2
    }
}

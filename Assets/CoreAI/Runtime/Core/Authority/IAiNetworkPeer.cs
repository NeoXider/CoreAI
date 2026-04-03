namespace CoreAI.Authority
{
    /// <summary>
    /// Роль текущего процесса в сети (solo / Netcode / кастом). Реализация в игре — MonoBehaviour в сборке Source.
    /// </summary>
    public interface IAiNetworkPeer
    {
        /// <summary>Узел владеет серверным/хостовым авторитетом (в solo всегда true).</summary>
        bool IsHostAuthority { get; }

        /// <summary>
        /// Узел — только удалённый клиент (без host authority). В solo/listen на машине хоста — false.
        /// </summary>
        bool IsPureClient { get; }
    }
}

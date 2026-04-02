namespace CoreAI.Authority
{
    /// <summary>
    /// Узел, разрешённый запускать оркестратор и LLM (хост или локальный solo).
    /// </summary>
    public interface IAuthorityHost
    {
        bool CanRunAiTasks { get; }
    }

    public sealed class SoloAuthorityHost : IAuthorityHost
    {
        public bool CanRunAiTasks => true;
    }
}

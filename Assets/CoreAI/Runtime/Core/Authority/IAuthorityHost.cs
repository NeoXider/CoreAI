namespace CoreAI.Authority
{
    /// <summary>
    /// Узел, разрешённый запускать оркестратор и LLM (хост или локальный solo).
    /// </summary>
    public interface IAuthorityHost
    {
        /// <summary>Разрешено ли на этом узле вызывать LLM и оркестратор (хост / solo).</summary>
        bool CanRunAiTasks { get; }
    }

    /// <summary>Локальный solo: ИИ всегда разрешён (заглушка для мультиплеерного хоста).</summary>
    public sealed class SoloAuthorityHost : IAuthorityHost
    {
        /// <inheritdoc />
        public bool CanRunAiTasks => true;
    }
}
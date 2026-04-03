namespace CoreAI.Ai
{
    /// <summary>
    /// Заготовка под уточнение кода/JSON от модели (фаза B+).
    /// </summary>
    public interface ICodeRefiner
    {
        /// <summary>Постобработка сырого вывода модели перед публикацией (пока не используется в MVP).</summary>
        string Refine(string rawModelOutput, string agentRoleId);
    }

    /// <summary>Заглушка: возвращает вход без изменений.</summary>
    public sealed class CodeRefinerStub : ICodeRefiner
    {
        /// <inheritdoc />
        public string Refine(string rawModelOutput, string agentRoleId) => rawModelOutput;
    }
}

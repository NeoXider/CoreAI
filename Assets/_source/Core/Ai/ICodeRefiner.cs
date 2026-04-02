namespace CoreAI.Ai
{
    /// <summary>
    /// Заготовка под уточнение кода/JSON от модели (фаза B+).
    /// </summary>
    public interface ICodeRefiner
    {
        string Refine(string rawModelOutput, string agentRoleId);
    }

    public sealed class CodeRefinerStub : ICodeRefiner
    {
        public string Refine(string rawModelOutput, string agentRoleId) => rawModelOutput;
    }
}

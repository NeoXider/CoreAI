using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Разрешение <see cref="ILlmClient"/> по роли агента (потокобезопасно для чтения после сборки).
    /// </summary>
    public interface ILlmClientRegistry
    {
        /// <summary>Внутренний клиент для роли (без декораторов логирования снаружи).</summary>
        ILlmClient ResolveClientForRole(string roleId);
    }

    /// <summary>Смена маршрутизации без пересоздания VContainer scope.</summary>
    public interface ILlmRoutingController
    {
        /// <summary>Пересобрать клиенты из манифеста (или сброс на legacy-клиент).</summary>
        void ApplyManifest(LlmRoutingManifest manifest);
    }
}

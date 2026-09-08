using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Decorates an existing memory store and maps role ids to scoped keys.
    /// <para>
    /// Также это единственное место, где «сбросить историю» дотягивается до свёрнутого summary.
    /// Rolling summary — производная от истории: он хранится в отдельном
    /// <see cref="IConversationSummaryStore"/>, но существует только как пересказ ТЕХ реплик, которые
    /// лежали в истории. Потребитель, который зовёт <see cref="ClearChatHistory"/> (новая миссия в
    /// RedoSchool, кнопка «очистить чат»), про второй стор не знает и знать не должен; когда он об этом
    /// забывал, оба менеджера контекста отдавали summary прошлого урока в КАЖДЫЙ ход нового — ещё до
    /// всякой компакции, — а при первой компакции старый пересказ сливался с новым.
    /// </para>
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecorator : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore
    {
        private readonly IAgentMemoryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;
        private readonly IConversationSummaryStore _conversationSummaries;

        /// <summary>
        /// Creates a scoped memory store wrapper.
        /// </summary>
        /// <param name="inner">Backing store; keys it receives are already scoped.</param>
        /// <param name="scopeProvider">Scope provider shared with every other scoped decorator of this host.</param>
        /// <param name="conversationSummaries">
        /// Стор summary, зарегистрированный в этом же скоупе как <see cref="IConversationSummaryStore"/>
        /// (то есть уже обёрнутый в <see cref="ScopedConversationSummaryStoreDecorator"/> с тем же
        /// <paramref name="scopeProvider"/>): ему передаётся сырой role id, ключ он выводит сам, и выводит
        /// тот же. <c>null</c> — у хоста нет rolling summary, чистить нечего.
        /// </param>
        public ScopedAgentMemoryStoreDecorator(
            IAgentMemoryStore inner,
            IAgentMemoryScopeProvider scopeProvider,
            IConversationSummaryStore conversationSummaries = null)
        {
            _inner = inner ?? new NullAgentMemoryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
            _conversationSummaries = conversationSummaries;
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return _inner.TryLoad(ToScopedKey(roleId), out state);
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            string key = ToScopedKey(roleId);
            if (_inner is IAgentMemoryLoadDiagnostics diagnostics)
            {
                return diagnostics.TryLoadDetailed(key, out state);
            }

            // WHY: An inner store without the capability cannot tell "missing" from "unreadable"; report
            // the optimistic NotFound rather than blocking every first write behind a false Failed.
            return _inner.TryLoad(key, out state) && state != null
                ? AgentMemoryLoadStatus.Loaded
                : AgentMemoryLoadStatus.NotFound;
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            _inner.Save(ToScopedKey(roleId), state);
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            _inner.Clear(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            _inner.ClearChatHistory(ToScopedKey(roleId));
            // WHY: Summary без истории, из которой он свёрнут, — не память, а чужой урок в промпте.
            // Сценария «сбросить историю, но оставить её пересказ» в коде нет ни у одного вызывающего
            // (CoreAi.ClearContext, CoreAiChatService.ClearHistory, AgentConfig.ClearMemory, сброс миссии в
            // RedoSchool), поэтому и флага-исключения нет.
            _conversationSummaries?.ClearSummary(roleId);
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            _inner.AppendChatMessage(ToScopedKey(roleId), role, content, persistToDisk);
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            return _inner.GetChatHistory(ToScopedKey(roleId), maxMessages);
        }

        /// <inheritdoc />
        public Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            // WHY: The extension delegates to an inner IAtomicAgentMemoryStore when available; otherwise it owns
            // a per-inner-store, per-scoped-key gate. Passing the scoped key here is essential: locking the raw
            // role id would serialize unrelated students and delegating the raw role would leak their state.
            return _inner.MutateAsync(ToScopedKey(roleId), mutator, cancellationToken);
        }

        private string ToScopedKey(string roleId)
        {
            return AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
        }
    }
}

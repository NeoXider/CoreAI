using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Kilocode-style compaction: same budget partition as <see cref="DeterministicConversationContextManager"/>,
    /// but older prefix is merged into a rolling summary via an auxiliary <see cref="ILlmClient.CompleteAsync"/> call.
    /// Synchronous <see cref="BuildSnapshot"/> stays deterministic (bullet rollup) to avoid blocking callers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Important:</b> the orchestrator’s <b>main agent system prompt</b> (role instructions, universal prefix,
    /// memory block, tool contract, etc.) is <b>never</b> sent to the compaction LLM. Only chat transcript lines from
    /// <see cref="IAgentMemoryStore.GetChatHistory"/> (plus stored rolling summary) appear in <see cref="LlmCompletionRequest.UserPayload"/>.
    /// The compaction call uses its own compact <see cref="LlmContextCompactionOptions.SystemPrompt"/> (default via
    /// <see cref="LlmContextCompactionOptions.DefaultSystemPrompt"/>) and <see cref="LlmCompletionRequest.ChatHistory"/> stays <c>null</c>.
    /// </para>
    /// <para>
    /// After compaction, the orchestrator merges the updated summary under <c>## Conversation Summary</c> into the main
    /// system prompt separately — that text is consumed by the <b>main</b> model, not re-fed into the compaction pass.
    /// </para>
    /// </remarks>
    public sealed class LlmAssistedConversationContextManager : IAsyncConversationContextManager
    {
        private readonly IConversationSummaryStore _summaryStore;
        private readonly ITokenEstimator _estimator;
        private readonly ILlmClient _compactionLlm;
        private readonly LlmContextCompactionOptions _options;
        private readonly DeterministicConversationContextManager _deterministicFacade;

        /// <summary>
        /// Creates an LLM-assisted compaction manager. <paramref name="compactionLlm"/> is typically the same
        /// <see cref="ILlmClient"/> as the orchestrator; routing may steer <see cref="LlmContextCompactionOptions.CompactorAgentRoleId"/> to a lighter profile.
        /// </summary>
        public LlmAssistedConversationContextManager(
            IConversationSummaryStore summaryStore,
            ITokenEstimator tokenEstimator,
            ILlmClient compactionLlm,
            LlmContextCompactionOptions options = null)
        {
            _summaryStore = summaryStore ?? throw new ArgumentNullException(nameof(summaryStore));
            _estimator = tokenEstimator ?? new HeuristicTokenEstimator();
            _compactionLlm = compactionLlm ?? throw new ArgumentNullException(nameof(compactionLlm));
            _options = options ?? LlmContextCompactionOptions.Default();
            _deterministicFacade = new DeterministicConversationContextManager(_summaryStore, _estimator);
        }

        /// <inheritdoc />
        public ConversationContextSnapshot BuildSnapshot(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs = null)
        {
            return _deterministicFacade.BuildSnapshot(roleId, history, roleConfig, buildArgs);
        }

        /// <inheritdoc />
        public async Task<ConversationContextSnapshot> BuildSnapshotAsync(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs,
            string orchestrationTraceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (history == null || history.Length == 0)
            {
                return new ConversationContextSnapshot();
            }

            int budget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            (int splitExclusive, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(history, _estimator, budget);

            string storedSummary = _summaryStore.LoadSummary(roleId) ?? "";
            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryIfNeeded(storedSummary, buildArgs);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = recent.ToArray(),
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut)
                };
            }

            string compactedSummary;
            try
            {
                compactedSummary = await SummarizeViaLlmAsync(
                    storedSummary,
                    history,
                    splitExclusive,
                    orchestrationTraceId ?? "t",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Instance.Warn(
                    $"[LlmAssistedConversationContextManager] LLM compaction failed; using bullet fallback: {ex.Message}",
                    LogTag.Llm);
                compactedSummary = ConversationBulletSummary.Format(storedSummary, history, splitExclusive);
            }

            if (string.IsNullOrWhiteSpace(compactedSummary))
            {
                compactedSummary = ConversationBulletSummary.Format(storedSummary, history, splitExclusive);
            }

            compactedSummary = LimitSummaryIfNeeded(compactedSummary, buildArgs);
            _summaryStore.SaveSummary(roleId, compactedSummary);

            return new ConversationContextSnapshot
            {
                Summary = compactedSummary,
                RecentMessages = recent.ToArray(),
                WasCompacted = true
            };
        }

        private async Task<string> SummarizeViaLlmAsync(
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            string traceIdBase,
            CancellationToken cancellationToken)
        {
            // Compactor-only prompts: never the main role system (Teacher, Creator, tool contract, etc.).
            string userPayload = BuildCompactionUserPayload(priorSummary, history, splitExclusive, _options);
            string compactTrace = $"{traceIdBase.Trim()}:compact";

            LlmCompletionResult result = await _compactionLlm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = _options.CompactorAgentRoleId ?? BuiltInAgentRoleIds.ContextCompactionAux,
                    SystemPrompt = _options.SystemPrompt ?? LlmContextCompactionOptions.DefaultSystemPrompt,
                    UserPayload = userPayload,
                    ChatHistory = null, // verbatim tail is not duplicated here; transcript is folded into UserPayload
                    TraceId = compactTrace,
                    Tools = Array.Empty<ILlmTool>(),
                    ForcedToolMode = LlmToolChoiceMode.None,
                    SendTemperature = true,
                    Temperature = _options.Temperature,
                    MaxOutputTokens = _options.MaxSummaryOutputTokens > 0 ? _options.MaxSummaryOutputTokens : null,
                    ContextWindowTokens = 8192
                },
                cancellationToken).ConfigureAwait(false);

            if (result == null || !result.Ok || string.IsNullOrWhiteSpace(result.Content))
            {
                return null;
            }

            return NormalizeSummaryText(result.Content, _options.MaxSummaryChars);
        }

        private static string BuildCompactionUserPayload(string priorSummary, ChatMessage[] history, int splitExclusive, LlmContextCompactionOptions options)
        {
            int maxChars = options.MaxPayloadChars;
            int maxPerMsg = options.MaxPerMessageChars;
            StringBuilder sb = new(2048);
            sb.AppendLine("## Prior rolling summary (may be empty — still produce an updated summary)");
            sb.AppendLine(string.IsNullOrWhiteSpace(priorSummary) ? "(none)" : priorSummary.Trim());
            sb.AppendLine();
            sb.AppendLine("## Dialogue lines to fold into the rolling summary (older than the live tail)");
            for (int i = 0; i < splitExclusive; i++)
            {
                string role = string.IsNullOrWhiteSpace(history[i].Role) ? "unknown" : history[i].Role.Trim();
                string content = history[i].Content ?? "";
                if (content.Length > maxPerMsg)
                {
                    content = content.Substring(0, maxPerMsg).TrimEnd() + "…";
                }

                sb.Append("- ").Append(role).Append(": ").AppendLine(content);
            }

            sb.AppendLine();
            sb.AppendLine("Output a compact updated rolling summary (bullets or short paragraphs). Do not repeat wording unnecessarily.");

            string text = sb.ToString();
            return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "\n…[truncated]";
        }

        private static string NormalizeSummaryText(string content, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            string s = content.Trim();
            if (s.Length > maxChars)
            {
                s = s.Substring(0, maxChars).TrimEnd() + "…";
            }

            return s;
        }

        private string LimitSummaryIfNeeded(string summary, ConversationContextBuildArgs buildArgs)
        {
            int cap = buildArgs?.MaxRolledSummaryTokens ?? 0;
            if (cap <= 0)
            {
                return summary ?? "";
            }

            return ConversationRolledSummaryLimiter.Apply(summary, _estimator, cap);
        }
    }
}

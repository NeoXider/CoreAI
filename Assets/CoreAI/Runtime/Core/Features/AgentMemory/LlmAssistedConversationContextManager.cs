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
    /// Prompt safety: the full agent system prompt (including role rules,
    /// memory block, tool contract, etc.) is <b>never</b> sent to the compaction LLM. Only chat transcript lines from
    /// <see cref="IAgentMemoryStore.GetChatHistory"/> (plus stored rolling summary) appear in <see cref="LlmCompletionRequest.UserPayload"/>.
    /// The compaction call uses its own compact <see cref="LlmContextCompactionOptions.SystemPrompt"/> (default via
    /// <see cref="LlmContextCompactionOptions.DefaultSystemPrompt"/>) and <see cref="LlmCompletionRequest.ChatHistory"/> stays <c>null</c>.
    /// </para>
    /// <para>
    /// After compaction, the orchestrator merges the updated summary under <c>## Conversation Summary</c> into the main
    /// request payload before appending the retained recent chat turns.
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

            // WHY: Compaction folds the old prefix into the durable rolling summary, so it must see
            // the FULL history. Pruning first would drop superseded tool results before they are
            // summarized and they would vanish from every future prompt without a trace. Pruning
            // still applies, but only to the emitted recent tail (prompt-level noise control).
            string summaryRoleId = roleId;
            IAsyncConversationSummaryStore asyncStore = DeterministicConversationContextManager.RequireAsyncStore(_summaryStore, ref summaryRoleId);
            string storedSummary = await asyncStore.LoadSummaryAsync(summaryRoleId, cancellationToken) ?? "";
            cancellationToken.ThrowIfCancellationRequested();
            // WHY: The persisted summary carries a machine-only fold marker as its final line; every
            // snapshot/LLM-facing path must see only the clean prose.
            string cleanStoredSummary = ConversationFoldMarker.Strip(storedSummary);
            int budget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            if (history.Length <= DeterministicConversationContextManager.ResolveMessageLimit(roleConfig) &&
                !ConversationContextBudgetTokens.ShouldPartitionForCompaction(
                    history,
                    _estimator,
                    budget,
                    buildArgs))
            {
                return new ConversationContextSnapshot
                {
                    Summary = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs),
                    RecentMessages = PruneIfEnabled(history, buildArgs),
                    WasCompacted = false
                };
            }

            (int splitExclusive, List<ChatMessage> recent) =
                DeterministicConversationContextManager.PartitionHistory(history, _estimator, budget, roleConfig);

            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs),
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut)
                };
            }

            int foldStart = ConversationBulletSummary.FindFoldStart(
                storedSummary, history, splitExclusive, out ConversationFoldProbeResult probe);
            if (probe == ConversationFoldProbeResult.NoMatch)
            {
                // WHY: All persisted fold anchors vanished from history (heavy pruning/trimming); folding
                // from 0 once is the graceful floor, and the marker written below prevents any recurrence.
                Log.Instance.Warn(
                    $"[LlmAssistedConversationContextManager] Fold watermark not found in history for role '{roleId}'; re-folding entire prefix once.",
                    LogTag.Llm);
            }

            if (foldStart >= splitExclusive)
            {
                return new ConversationContextSnapshot
                {
                    Summary = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs),
                    RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs),
                    WasCompacted = true
                };
            }

            string compactedSummary;
            try
            {
                compactedSummary = await SummarizeViaLlmAsync(
                    cleanStoredSummary,
                    history,
                    splitExclusive,
                    foldStart,
                    orchestrationTraceId ?? "t",
                    cancellationToken);
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
                compactedSummary = ConversationBulletSummary.Format(
                    cleanStoredSummary, history, splitExclusive, foldStart);
            }

            compactedSummary = ConversationFoldMarker.Strip(compactedSummary);
            if (string.IsNullOrWhiteSpace(compactedSummary))
            {
                compactedSummary = ConversationBulletSummary.Format(
                    cleanStoredSummary, history, splitExclusive, foldStart);
            }

            compactedSummary = LimitSummaryIfNeeded(compactedSummary, buildArgs);

            // WHY: The limiter runs BEFORE stamping so the fold marker (final line of the persisted text)
            // can never be trimmed away; the snapshot keeps the clean summary without the marker.
            string persistedSummary = ConversationFoldMarker.Stamp(compactedSummary, history, splitExclusive);

            ConversationContextSnapshot snapshot = new()
            {
                Summary = compactedSummary,
                RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs),
                WasCompacted = true
            };

            cancellationToken.ThrowIfCancellationRequested();
            snapshot.CommitSummaryAsync = token => asyncStore.SaveSummaryAsync(summaryRoleId, persistedSummary, token);
            if (buildArgs?.DeferSummaryPersistence != true) await snapshot.CommitAsync(cancellationToken);

            return snapshot;
        }

        private async Task<string> SummarizeViaLlmAsync(
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive,
            string traceIdBase,
            CancellationToken cancellationToken)
        {
            // Compactor-only prompts: never the main role system (Teacher, Creator, tool contract, etc.).
            string userPayload = BuildCompactionUserPayload(
                priorSummary, history, splitExclusive, startInclusive, _options);
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
                    ContextWindowTokens = CoreAISettings.DefaultContextWindowTokens
                },
                cancellationToken).ConfigureAwait(false);

            if (result == null || !result.Ok || string.IsNullOrWhiteSpace(result.Content))
            {
                return null;
            }

            return NormalizeSummaryText(result.Content, _options.MaxSummaryChars);
        }

        private static string BuildCompactionUserPayload(
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive,
            LlmContextCompactionOptions options)
        {
            int maxChars = options.MaxPayloadChars;
            int maxPerMsg = options.MaxPerMessageChars;
            StringBuilder sb = new(2048);
            sb.AppendLine("## Prior rolling summary (may be empty — still produce an updated summary)");
            sb.AppendLine(string.IsNullOrWhiteSpace(priorSummary) ? "(none)" : priorSummary.Trim());
            sb.AppendLine();
            sb.AppendLine("## Dialogue lines to fold into the rolling summary (older than the live tail)");
            for (int i = Math.Max(0, startInclusive); i < splitExclusive; i++)
            {
                string role = string.IsNullOrWhiteSpace(history[i].Role) ? "unknown" : history[i].Role.Trim();
                // WHY: The summarizer sees tool messages through THE SAME projection as the main prompt.
                // Without it the raw durable "## Tool Results" block with its JSON tails went into the
                // compactor, which - following its "preserve identifiers and numbers" instruction -
                // carried it into the summary, and the summary came back into the prompt bypassing
                // ToolResultPromptProjection - so the model again echoed to the child the machine register
                // the projection exists to strip.
                string content = ToolResultPromptProjection.ForPrompt(history[i].Role, history[i].Content ?? "");
                if (content.Length > maxPerMsg)
                {
                    content = content.Substring(0, maxPerMsg).TrimEnd() + "…";
                }

                sb.Append("- ").Append(role).Append(": ").AppendLine(content);
            }

            sb.AppendLine();
            sb.AppendLine(
                "Output a compact updated rolling summary (bullets or short paragraphs). Do not repeat wording unnecessarily.");

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

        private static ChatMessage[] PruneIfEnabled(ChatMessage[] history, ConversationContextBuildArgs buildArgs)
        {
            if (buildArgs == null || !buildArgs.EnableContextPruning)
            {
                return history;
            }

            return ConversationHistoryPruner.Prune(history, buildArgs.MaxRetainedToolResultMessages);
        }
    }
}

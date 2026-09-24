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
                    Summary = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs, out int storedDropped),
                    RecentMessages = PruneIfEnabled(history, buildArgs, out int pruned1),
                    PrunedMessageCount = pruned1,
                    WasCompacted = false,
                    SummaryTokensDropped = storedDropped
                };
            }

            (int splitExclusive, List<ChatMessage> recent) =
                DeterministicConversationContextManager.PartitionHistory(history, _estimator, budget, roleConfig);

            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs, out int summaryDropped);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs, out int pruned2),
                    PrunedMessageCount = pruned2,
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut),
                    SummaryTokensDropped = summaryDropped
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
                    Summary = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs, out int foldedDropped),
                    RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs, out int pruned3),
                    PrunedMessageCount = pruned3,
                    WasCompacted = true,
                    SummaryTokensDropped = foldedDropped
                };
            }

            string compactedSummary;
            // WHY a separate fold end: the compactor payload is capped (MaxPayloadChars), and the messages that did
            // not fit were never summarized. Stamping the fold marker at splitExclusive declared them retold, so a
            // bounded store could evict them and they were gone for good. Only what the compactor actually received
            // is marked folded; the rest is folded by the next compaction.
            int foldedExclusive = splitExclusive;
            try
            {
                (compactedSummary, foldedExclusive) = await SummarizeViaLlmAsync(
                    roleId,
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
                compactedSummary = FormatBulletFallback(roleId, cleanStoredSummary, history, splitExclusive, foldStart);
                foldedExclusive = splitExclusive;
            }

            compactedSummary = ConversationFoldMarker.Strip(compactedSummary);
            if (string.IsNullOrWhiteSpace(compactedSummary))
            {
                compactedSummary = FormatBulletFallback(roleId, cleanStoredSummary, history, splitExclusive, foldStart);
                foldedExclusive = splitExclusive;
            }

            compactedSummary = LimitSummaryIfNeeded(compactedSummary, buildArgs, out int compactedDropped);

            // WHY: The limiter runs BEFORE stamping so the fold marker (final line of the persisted text)
            // can never be trimmed away; the snapshot keeps the clean summary without the marker.
            string persistedSummary = ConversationFoldMarker.Stamp(compactedSummary, history, foldedExclusive);

            ConversationContextSnapshot snapshot = new()
            {
                Summary = compactedSummary,
                RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs, out int pruned4),
                PrunedMessageCount = pruned4,
                WasCompacted = true,
                SummaryTokensDropped = compactedDropped,
                DeferredFoldMessageCount = Math.Max(0, splitExclusive - foldedExclusive)
            };

            cancellationToken.ThrowIfCancellationRequested();
            snapshot.CommitSummaryAsync = token => asyncStore.SaveSummaryAsync(summaryRoleId, persistedSummary, token);
            if (buildArgs?.DeferSummaryPersistence != true) await snapshot.CommitAsync(cancellationToken);

            return snapshot;
        }

        private static string FormatBulletFallback(
            string roleId,
            string cleanStoredSummary,
            ChatMessage[] history,
            int splitExclusive,
            int foldStart)
        {
            string folded = ConversationBulletSummary.Format(
                cleanStoredSummary, history, splitExclusive, foldStart, out ConversationSummaryClipStats clip);
            string clipLine = clip.Describe(nameof(LlmAssistedConversationContextManager), roleId);
            if (clipLine != null)
            {
                Log.Instance.Info(clipLine, LogTag.Llm);
            }

            return folded;
        }

        private async Task<(string summary, int foldedExclusive)> SummarizeViaLlmAsync(
            string roleId,
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive,
            string traceIdBase,
            CancellationToken cancellationToken)
        {
            // Compactor-only prompts: never the main role system (Teacher, Creator, tool contract, etc.).
            string userPayload = BuildCompactionUserPayload(
                priorSummary, history, splitExclusive, startInclusive, _options, out CompactionPayloadClipStats clip,
                out int foldedExclusive);
            string clipLine = clip.Describe(roleId, _options);
            if (clipLine != null)
            {
                if (clip.DeferredMessages > 0 || clip.FirstLineClippedToFit)
                {
                    Log.Instance.Warn(clipLine, LogTag.Llm);
                }
                else
                {
                    Log.Instance.Info(clipLine, LogTag.Llm);
                }
            }

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
                return (null, splitExclusive);
            }

            string normalized = NormalizeSummaryText(result.Content, _options.MaxSummaryChars,
                out int summaryOriginalChars, out int summaryDroppedChars);
            if (summaryDroppedChars > 0)
            {
                Log.Instance.Warn(
                    $"[LlmAssistedConversationContextManager] Compacted summary for role '{roleId}' clipped to " +
                    $"MaxSummaryChars={_options.MaxSummaryChars}: {summaryOriginalChars} chars total -> " +
                    $"{summaryOriginalChars - summaryDroppedChars} kept, {summaryDroppedChars} dropped.",
                    LogTag.Llm);
            }

            return (normalized, foldedExclusive);
        }

        /// <summary>What <see cref="BuildCompactionUserPayload"/> cut; logged once per compaction call.</summary>
        internal struct CompactionPayloadClipStats
        {
            /// <summary>Dialogue lines that exceeded <see cref="LlmContextCompactionOptions.MaxPerMessageChars"/>.</summary>
            public int ClippedMessages;

            /// <summary>Characters removed from those lines; each carries a <c>…[+N chars]</c> marker.</summary>
            public int MessageDroppedChars;

            /// <summary>Messages that did not fit <see cref="LlmContextCompactionOptions.MaxPayloadChars"/>.</summary>
            public int DeferredMessages;

            /// <summary>Characters of those messages' lines.</summary>
            public int DeferredChars;

            /// <summary>Length of the payload that was sent.</summary>
            public int PayloadChars;

            /// <summary>
            /// The first dialogue line alone did not fit <see cref="LlmContextCompactionOptions.MaxPayloadChars"/> and
            /// was sent clipped, so the fold still moves forward by one message.
            /// </summary>
            public bool FirstLineClippedToFit;

            /// <summary>The log line, or <c>null</c> when the payload went out whole.</summary>
            public string Describe(string roleId, LlmContextCompactionOptions options)
            {
                if (ClippedMessages <= 0 && DeferredMessages <= 0 && !FirstLineClippedToFit)
                {
                    return null;
                }

                string line =
                    $"[LlmAssistedConversationContextManager] Compaction payload for role '{roleId}' clipped: " +
                    $"{ClippedMessages} line(s) over MaxPerMessageChars={options.MaxPerMessageChars} lost " +
                    $"{MessageDroppedChars} chars; payload {PayloadChars} chars sent (MaxPayloadChars={options.MaxPayloadChars})";
                if (FirstLineClippedToFit)
                {
                    line += "; the first line alone exceeded MaxPayloadChars and was sent clipped to fit";
                }

                return DeferredMessages <= 0
                    ? line + "."
                    : line + $"; {DeferredMessages} message(s) ({DeferredChars} chars) did not fit, are not in this " +
                      "turn's prompt and stay unfolded until the next compaction.";
            }
        }

        internal static string BuildCompactionUserPayload(
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive,
            LlmContextCompactionOptions options,
            out CompactionPayloadClipStats clip)
        {
            return BuildCompactionUserPayload(priorSummary, history, splitExclusive, startInclusive, options,
                out clip, out _);
        }

        /// <summary>
        /// Builds the compactor payload from whole dialogue lines. Lines are added oldest first while the payload,
        /// footer included, fits <see cref="LlmContextCompactionOptions.MaxPayloadChars"/>; the first line that does
        /// not fit ends the fold, and <paramref name="foldedExclusive"/> says how far the payload really reaches.
        /// The first line always goes in (clipped to the room left when it alone is too long), so a fold always
        /// makes progress.
        /// </summary>
        internal static string BuildCompactionUserPayload(
            string priorSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive,
            LlmContextCompactionOptions options,
            out CompactionPayloadClipStats clip,
            out int foldedExclusive)
        {
            clip = default;
            int maxChars = options.MaxPayloadChars;
            int maxPerMsg = options.MaxPerMessageChars;
            StringBuilder sb = new(2048);
            sb.AppendLine("## Prior rolling summary (may be empty — still produce an updated summary)");
            sb.AppendLine(string.IsNullOrWhiteSpace(priorSummary) ? "(none)" : priorSummary.Trim());
            sb.AppendLine();
            sb.AppendLine("## Dialogue lines to fold into the rolling summary (older than the live tail)");
            string footer = Environment.NewLine +
                            "Output a compact updated rolling summary (bullets or short paragraphs). Do not repeat wording unnecessarily." +
                            Environment.NewLine;

            int first = Math.Max(0, startInclusive);
            foldedExclusive = splitExclusive;
            for (int i = first; i < splitExclusive; i++)
            {
                string role = string.IsNullOrWhiteSpace(history[i].Role) ? "unknown" : history[i].Role.Trim();
                // WHY: The summarizer sees tool messages through THE SAME projection as the main prompt.
                // Without it the raw durable "## Tool Results" block with its JSON tails went into the
                // compactor, which - following its "preserve identifiers and numbers" instruction -
                // carried it into the summary, and the summary came back into the prompt bypassing
                // ToolResultPromptProjection - so the model again echoed to the child the machine register
                // the projection exists to strip.
                string original = ToolResultPromptProjection.ForPrompt(history[i].Role, history[i].Content ?? "");
                string content = original;
                int lineDropped = 0;
                if (maxPerMsg >= 0 && content.Length > maxPerMsg)
                {
                    if (maxPerMsg == 0)
                    {
                        lineDropped = content.Length;
                        content = TruncationMarker.Format(lineDropped);
                    }
                    else
                    {
                        content = TruncationMarker.ClipPrefix(content, maxPerMsg, out lineDropped);
                    }
                }

                string prefix = "- " + role + ": ";
                int lineLength = prefix.Length + content.Length + Environment.NewLine.Length;
                if (maxChars > 0 && sb.Length + lineLength + footer.Length > maxChars)
                {
                    if (i > first)
                    {
                        foldedExclusive = i;
                        for (int j = i; j < splitExclusive; j++)
                        {
                            clip.DeferredMessages++;
                            clip.DeferredChars += (history[j].Content ?? "").Length;
                        }

                        break;
                    }

                    // WHY: the first line alone does not fit; sending it clipped keeps the fold moving - with nothing
                    // folded the same oversized line would block every later compaction.
                    clip.FirstLineClippedToFit = true;
                    int room = Math.Min(content.Length,
                        maxChars - sb.Length - footer.Length - prefix.Length - Environment.NewLine.Length);
                    if (room > 0)
                    {
                        content = TruncationMarker.ClipToFit(original, room, out lineDropped);
                    }
                    else
                    {
                        lineDropped = original.Length;
                        content = TruncationMarker.Format(lineDropped);
                    }
                }

                if (lineDropped > 0)
                {
                    clip.ClippedMessages++;
                    clip.MessageDroppedChars += lineDropped;
                }

                sb.Append(prefix).AppendLine(content);
            }

            sb.Append(footer);
            string text = sb.ToString();
            clip.PayloadChars = text.Length;
            return text;
        }

        internal static string NormalizeSummaryText(string content, int maxChars, out int originalChars,
            out int droppedChars)
        {
            originalChars = 0;
            droppedChars = 0;
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            string s = content.Trim();
            originalChars = s.Length;
            // WHY ClipToFit: MaxSummaryChars bounds the text that is stored, so the marker counts against it.
            return TruncationMarker.ClipToFit(s, maxChars, out droppedChars);
        }

        /// <summary>
        /// Applies the caller's explicit <see cref="ConversationContextBuildArgs.MaxRolledSummaryTokens"/> and
        /// reports the estimated tokens it removed, so the orchestrator logs the cut with its numbers.
        /// </summary>
        internal string LimitSummaryIfNeeded(string summary, ConversationContextBuildArgs buildArgs, out int droppedTokens)
        {
            droppedTokens = 0;
            int cap = buildArgs?.MaxRolledSummaryTokens ?? 0;
            string text = summary ?? "";
            if (cap <= 0 || string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            string trimmed = text.Trim();
            int before = _estimator.EstimateText(trimmed);
            if (before <= cap)
            {
                return trimmed;
            }

            // WHY cap - 1, as in DeterministicConversationContextManager: the limiter fits the kept suffix and then
            // prefixes an ellipsis, which can cost one more token. Fitting to the cap itself left the result one
            // token over, so the persisted summary was cut again - and reported again - on every later turn.
            string limited = ConversationRolledSummaryLimiter.Apply(trimmed, _estimator, Math.Max(1, cap - 1));
            droppedTokens = Math.Max(1, before - _estimator.EstimateText(limited));
            return limited;
        }

        private static ChatMessage[] PruneIfEnabled(ChatMessage[] history, ConversationContextBuildArgs buildArgs,
            out int prunedCount)
        {
            prunedCount = 0;
            if (buildArgs == null || !buildArgs.EnableContextPruning)
            {
                return history;
            }

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, buildArgs.MaxRetainedToolResultMessages);
            prunedCount = Math.Max(0, (history?.Length ?? 0) - (pruned?.Length ?? 0));
            return pruned;
        }
    }
}

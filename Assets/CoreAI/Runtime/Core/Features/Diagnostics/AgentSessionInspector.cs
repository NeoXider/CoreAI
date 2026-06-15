using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Session;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Read-only diagnostics API for inspecting the prompt, tools, memory, history, summary, and budget of an agent role.
    /// </summary>
    public sealed class AgentSessionInspector
    {
        private readonly AiPromptComposer _promptComposer;
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly IConversationSummaryStore _summaryStore;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;
        private readonly IContextBudgetPolicy _contextBudgetPolicy;
        private readonly ITokenEstimator _tokenEstimator;

        public AgentSessionInspector(
            AiPromptComposer promptComposer,
            IAgentSystemPromptProvider systemPrompts,
            IAgentMemoryStore memoryStore,
            IConversationSummaryStore summaryStore,
            AgentMemoryPolicy memoryPolicy,
            ICoreAISettings settings,
            IContextBudgetPolicy contextBudgetPolicy = null,
            ITokenEstimator tokenEstimator = null)
        {
            _promptComposer = promptComposer ?? throw new ArgumentNullException(nameof(promptComposer));
            _systemPrompts = systemPrompts;
            _memoryStore = memoryStore;
            _summaryStore = summaryStore;
            _memoryPolicy = memoryPolicy ?? throw new ArgumentNullException(nameof(memoryPolicy));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _contextBudgetPolicy = contextBudgetPolicy ?? new DefaultContextBudgetPolicy();
            _tokenEstimator = tokenEstimator ?? new HeuristicTokenEstimator();
        }

        /// <summary>
        /// Returns the currently known role ids from the active policy.
        /// </summary>
        public IReadOnlyList<string> GetKnownRoleIds()
        {
            List<string> roleIds = new();
            IReadOnlyList<string> policyRoles = _memoryPolicy.GetKnownRoleIds();
            for (int i = 0; i < policyRoles.Count; i++)
            {
                AddUnique(roleIds, policyRoles[i]);
            }

            for (int i = 0; i < BuiltInAgentRoleIds.AllBuiltInRoles.Count; i++)
            {
                AddUnique(roleIds, BuiltInAgentRoleIds.AllBuiltInRoles[i]);
            }

            roleIds.Sort(StringComparer.Ordinal);
            return roleIds;
        }

        /// <summary>
        /// Inspects a role using a neutral diagnostics request with no live user hint.
        /// </summary>
        public AgentSessionSnapshot Inspect(string roleId)
        {
            return Inspect(roleId, null);
        }

        /// <summary>
        /// Inspects a role using optional request fields for request-scoped prompt sections and tool filters.
        /// The inspection is read-only and does not call the conversation context manager, because some managers
        /// persist new summaries while building a request.
        /// </summary>
        public AgentSessionSnapshot Inspect(string roleId, AiTaskRequest request)
        {
            string resolvedRoleId = string.IsNullOrWhiteSpace(roleId)
                ? BuiltInAgentRoleIds.Creator
                : roleId.Trim();
            AiTaskRequest inspectRequest = CloneRequestForRole(request, resolvedRoleId);

            AgentMemoryPolicy.RoleMemoryConfig roleConfig = _memoryPolicy.GetRoleConfig(resolvedRoleId);
            string traceId = "agent-session-inspector";
            string resolvedSystem = _promptComposer.GetSystemPrompt(resolvedRoleId);
            string systemWithRuntime = _promptComposer.AppendRuntimeContext(
                resolvedSystem,
                inspectRequest,
                resolvedRoleId,
                traceId);

            string memoryText = "";
            string systemWithMemory = systemWithRuntime;
            if (_memoryPolicy.IsMemoryEnabled(resolvedRoleId) &&
                _memoryStore != null &&
                _memoryStore.TryLoad(resolvedRoleId, out AgentMemoryState state) &&
                !string.IsNullOrWhiteSpace(state?.Memory))
            {
                memoryText = state.Memory.Trim();
                systemWithMemory = systemWithRuntime.Trim() + "\n\n## Memory\n" + memoryText;
            }

            IReadOnlyList<ILlmTool> tools = _memoryPolicy.GetToolsForRole(resolvedRoleId);
            tools = FilterToolsForRequest(tools, inspectRequest);
            string systemWithTools = AiToolContractPromptFormatter.AppendToolContract(
                systemWithMemory,
                tools,
                inspectRequest,
                _settings);

            string conversationSummary = _summaryStore?.LoadSummary(resolvedRoleId) ?? "";
            string systemForBudget = systemWithTools;
            if (roleConfig.WithChatHistory &&
                !_settings.PlaceLiveContextInTail &&
                !string.IsNullOrWhiteSpace(conversationSummary))
            {
                systemWithTools = systemWithTools.Trim() + "\n\n## Conversation Summary\n" +
                                  conversationSummary.Trim();
            }

            string userPayload = BuildUserPayloadEstimate(inspectRequest);
            ContextBudget budget = ComputeBudget(roleConfig, systemForBudget, userPayload, tools, inspectRequest);
            int historyBudget = ResolveHistoryBudget(budget, roleConfig);

            ChatMessage[] storedHistory = _memoryStore?.GetChatHistory(resolvedRoleId, 0) ??
                                          Array.Empty<ChatMessage>();
            ChatMessage[] requestHistoryEstimate = EstimateRequestHistory(roleConfig, storedHistory, historyBudget);

            AgentSessionSnapshot snapshot = new()
            {
                SnapshotSource = "live container",
                RoleId = resolvedRoleId,
                RoleIsExplicitlyConfigured = _memoryPolicy.HasRole(resolvedRoleId),
                UniversalSystemPromptPrefix = _memoryPolicy.IsUniversalPrefixOverridden(resolvedRoleId)
                    ? ""
                    : _settings.UniversalSystemPromptPrefix ?? "",
                BaseSystemPrompt = ResolveBaseSystemPrompt(resolvedRoleId),
                AdditionalSystemPrompt = ResolveAdditionalSystemPrompt(resolvedRoleId),
                ResolvedSystemPrompt = resolvedSystem,
                ResolvedSystemPromptWithRuntimeContext = systemWithRuntime,
                ResolvedSystemPromptWithMemoryAndTools = systemForBudget,
                ResolvedSystemPromptFinalEstimate = systemWithTools,
                MemoryText = memoryText,
                ConversationSummary = conversationSummary,
                UserPayloadEstimate = userPayload,
                RoleConfig = ToSnapshot(roleConfig),
                Budget = ToBudgetSnapshot(
                    budget,
                    systemForBudget,
                    userPayload,
                    tools,
                    storedHistory,
                    requestHistoryEstimate)
            };

            AddTools(snapshot.Tools, tools);
            AddMessages(snapshot.ChatHistory, storedHistory);
            AddMessages(snapshot.EstimatedRequestChatHistory, requestHistoryEstimate);
            AddNotes(snapshot, roleConfig, inspectRequest);
            return snapshot;
        }

        /// <summary>
        /// Builds a best-effort read-only snapshot from serialized settings/prompts/policy inputs.
        /// This path intentionally does not call runtime context providers or request composition services.
        /// </summary>
        public static AgentSessionSnapshot InspectSerializedInputs(
            string roleId,
            ICoreAISettings settings,
            AgentPromptsDefinition promptsDefinition = null,
            AgentMemoryPolicy memoryPolicy = null,
            IAgentMemoryStore memoryStore = null,
            IConversationSummaryStore summaryStore = null,
            IAgentSystemPromptProvider fallbackSystemPrompts = null,
            IContextBudgetPolicy contextBudgetPolicy = null,
            ITokenEstimator tokenEstimator = null)
        {
            settings ??= new CoreAISettingsOptions();
            memoryPolicy ??= new AgentMemoryPolicy();
            contextBudgetPolicy ??= new DefaultContextBudgetPolicy();
            tokenEstimator ??= new HeuristicTokenEstimator();

            string resolvedRoleId = string.IsNullOrWhiteSpace(roleId)
                ? BuiltInAgentRoleIds.Creator
                : roleId.Trim();
            AgentMemoryPolicy.RoleMemoryConfig roleConfig = memoryPolicy.GetRoleConfig(resolvedRoleId);

            IAgentSystemPromptProvider systemPrompts = BuildSerializedSystemPromptProvider(
                promptsDefinition,
                fallbackSystemPrompts);

            string baseSystemPrompt = ResolveBaseSystemPrompt(systemPrompts, resolvedRoleId);
            string additionalSystemPrompt = ResolveAdditionalSystemPrompt(memoryPolicy, resolvedRoleId);
            bool skipUniversalPrefix = memoryPolicy.IsUniversalPrefixOverridden(resolvedRoleId) ||
                                       IsUniversalPrefixOverridden(promptsDefinition, resolvedRoleId);
            string universalPrefix = skipUniversalPrefix ? "" : settings.UniversalSystemPromptPrefix ?? "";
            string resolvedSystem = ComposeSystemPrompt(
                universalPrefix,
                baseSystemPrompt,
                additionalSystemPrompt);

            string memoryText = "";
            string systemWithMemory = resolvedSystem;
            if (memoryPolicy.IsMemoryEnabled(resolvedRoleId) &&
                memoryStore != null &&
                memoryStore.TryLoad(resolvedRoleId, out AgentMemoryState state) &&
                !string.IsNullOrWhiteSpace(state?.Memory))
            {
                memoryText = state.Memory.Trim();
                systemWithMemory = resolvedSystem.Trim() + "\n\n## Memory\n" + memoryText;
            }

            IReadOnlyList<ILlmTool> tools = memoryPolicy.GetToolsForRole(resolvedRoleId);
            AiTaskRequest inspectRequest = new() { RoleId = resolvedRoleId };
            string systemWithTools = AiToolContractPromptFormatter.AppendToolContract(
                systemWithMemory,
                tools,
                inspectRequest,
                settings);

            string conversationSummary = summaryStore?.LoadSummary(resolvedRoleId) ?? "";
            string systemForBudget = systemWithTools;
            if (roleConfig.WithChatHistory &&
                !settings.PlaceLiveContextInTail &&
                !string.IsNullOrWhiteSpace(conversationSummary))
            {
                systemWithTools = systemWithTools.Trim() + "\n\n## Conversation Summary\n" +
                                  conversationSummary.Trim();
            }

            ContextBudget budget = ComputeBudget(
                roleConfig,
                systemForBudget,
                "",
                tools,
                inspectRequest,
                settings,
                contextBudgetPolicy,
                tokenEstimator);

            ChatMessage[] storedHistory = memoryStore?.GetChatHistory(resolvedRoleId, 0) ??
                                          Array.Empty<ChatMessage>();

            AgentSessionSnapshot snapshot = new()
            {
                SnapshotSource = "edit-mode (serialized scene)",
                RoleId = resolvedRoleId,
                RoleIsExplicitlyConfigured = memoryPolicy.HasRole(resolvedRoleId),
                UniversalSystemPromptPrefix = universalPrefix,
                BaseSystemPrompt = baseSystemPrompt,
                AdditionalSystemPrompt = additionalSystemPrompt,
                ResolvedSystemPrompt = resolvedSystem,
                ResolvedSystemPromptWithRuntimeContext = AgentSessionSnapshot.UnavailableInEditMode,
                ResolvedSystemPromptWithMemoryAndTools = systemForBudget,
                ResolvedSystemPromptFinalEstimate = systemWithTools,
                MemoryText = memoryText,
                ConversationSummary = conversationSummary,
                UserPayloadEstimate = AgentSessionSnapshot.UnavailableInEditMode,
                RoleConfig = ToSnapshot(roleConfig),
                Budget = ToBudgetSnapshot(
                    budget,
                    systemForBudget,
                    "",
                    tools,
                    storedHistory,
                    Array.Empty<ChatMessage>(),
                    tokenEstimator)
            };

            AddTools(snapshot.Tools, tools);
            AddMessages(snapshot.ChatHistory, storedHistory);
            snapshot.EstimatedRequestChatHistory.Add(new AgentSessionChatMessageSnapshot
            {
                Role = "diagnostics",
                Content = AgentSessionSnapshot.UnavailableInEditMode,
                Timestamp = 0
            });
            AddSerializedNotes(snapshot, roleConfig);
            return snapshot;
        }

        /// <summary>
        /// Returns role ids visible from serialized policy and prompt manifest data.
        /// </summary>
        public static IReadOnlyList<string> GetKnownRoleIds(
            AgentMemoryPolicy memoryPolicy,
            AgentPromptsDefinition promptsDefinition)
        {
            List<string> roleIds = new();
            IReadOnlyList<string> policyRoles = memoryPolicy?.GetKnownRoleIds() ?? Array.Empty<string>();
            for (int i = 0; i < policyRoles.Count; i++)
            {
                AddUnique(roleIds, policyRoles[i]);
            }

            for (int i = 0; i < BuiltInAgentRoleIds.AllBuiltInRoles.Count; i++)
            {
                AddUnique(roleIds, BuiltInAgentRoleIds.AllBuiltInRoles[i]);
            }

            if (promptsDefinition != null)
            {
                foreach (AgentPromptEntryDefinition entry in promptsDefinition.EnumerateEntries())
                {
                    AddUnique(roleIds, entry?.RoleId);
                }
            }

            roleIds.Sort(StringComparer.Ordinal);
            return roleIds;
        }

        private string ResolveBaseSystemPrompt(string roleId)
        {
            return ResolveBaseSystemPrompt(_systemPrompts, roleId);
        }

        private string ResolveAdditionalSystemPrompt(string roleId)
        {
            return ResolveAdditionalSystemPrompt(_memoryPolicy, roleId);
        }

        private string BuildUserPayloadEstimate(AiTaskRequest request)
        {
            try
            {
                return _promptComposer.BuildUserPayload(new GameSessionSnapshot(), request);
            }
            catch (Exception ex)
            {
                return $"(Could not build user payload estimate: {ex.Message})";
            }
        }

        private ContextBudget ComputeBudget(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            string system,
            string userPayload,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest request)
        {
            int contextWindowTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : _settings.ContextWindowTokens;
            int? maxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens, roleConfig.MaxOutputTokens);

            return _contextBudgetPolicy.Compute(new ContextBudgetRequest
            {
                MaxContextTokens = contextWindowTokens,
                SystemPrompt = system,
                UserPayload = userPayload,
                Tools = tools,
                MaxOutputTokens = maxOutputTokens,
                ContextRetryLevel = 0
            }, _tokenEstimator);
        }

        private static ContextBudget ComputeBudget(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            string system,
            string userPayload,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest request,
            ICoreAISettings settings,
            IContextBudgetPolicy contextBudgetPolicy,
            ITokenEstimator tokenEstimator)
        {
            int contextWindowTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : settings.ContextWindowTokens;
            int? maxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens, roleConfig.MaxOutputTokens);

            return contextBudgetPolicy.Compute(new ContextBudgetRequest
            {
                MaxContextTokens = contextWindowTokens,
                SystemPrompt = system,
                UserPayload = userPayload,
                Tools = tools,
                MaxOutputTokens = maxOutputTokens,
                ContextRetryLevel = 0
            }, tokenEstimator);
        }

        private int ResolveHistoryBudget(ContextBudget budget, AgentMemoryPolicy.RoleMemoryConfig roleConfig)
        {
            int historyBudget = budget.HistoryTokenBudget;
            if (!_settings.EnableConversationHistorySummarization)
            {
                historyBudget = AiOrchestrator.UnlimitedHistoryTokenBudget;
            }
            else if (_settings.ConversationHistoryRecentTokenBudgetOverride > 0)
            {
                historyBudget = Math.Max(32, _settings.ConversationHistoryRecentTokenBudgetOverride);
            }

            if (!roleConfig.WithChatHistory)
            {
                return 0;
            }

            return historyBudget;
        }

        private ChatMessage[] EstimateRequestHistory(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ChatMessage[] storedHistory,
            int historyBudget)
        {
            if (!roleConfig.WithChatHistory || storedHistory == null || storedHistory.Length == 0)
            {
                return Array.Empty<ChatMessage>();
            }

            int maxMessages = roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30;
            ChatMessage[] capped = TakeTail(storedHistory, maxMessages);
            if (historyBudget <= 0)
            {
                return capped;
            }

            (_, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(capped, _tokenEstimator, historyBudget);
            return recent.ToArray();
        }

        private AgentSessionBudgetSnapshot ToBudgetSnapshot(
            ContextBudget budget,
            string system,
            string userPayload,
            IReadOnlyList<ILlmTool> tools,
            ChatMessage[] storedHistory,
            ChatMessage[] requestHistoryEstimate)
        {
            return new AgentSessionBudgetSnapshot
            {
                ContextWindowTokens = budget.MaxContextTokens,
                ReservedForCompletionTokens = budget.ReservedForCompletion,
                EstimatedFixedPromptTokens = budget.EstimatedFixedPromptTokens,
                HistoryTokenBudget = budget.HistoryTokenBudget,
                ReservedSlackTokens = budget.ReservedSlackTokens,
                EstimatedSystemTokens = _tokenEstimator.EstimateText(system ?? ""),
                EstimatedUserTokens = _tokenEstimator.EstimateText(userPayload ?? ""),
                EstimatedToolsTokens = EstimateToolsTokens(tools),
                EstimatedStoredChatHistoryTokens = EstimateMessages(storedHistory),
                EstimatedRequestChatHistoryTokens = EstimateMessages(requestHistoryEstimate)
            };
        }

        private static AgentSessionBudgetSnapshot ToBudgetSnapshot(
            ContextBudget budget,
            string system,
            string userPayload,
            IReadOnlyList<ILlmTool> tools,
            ChatMessage[] storedHistory,
            ChatMessage[] requestHistoryEstimate,
            ITokenEstimator tokenEstimator)
        {
            return new AgentSessionBudgetSnapshot
            {
                ContextWindowTokens = budget.MaxContextTokens,
                ReservedForCompletionTokens = budget.ReservedForCompletion,
                EstimatedFixedPromptTokens = budget.EstimatedFixedPromptTokens,
                HistoryTokenBudget = budget.HistoryTokenBudget,
                ReservedSlackTokens = budget.ReservedSlackTokens,
                EstimatedSystemTokens = tokenEstimator.EstimateText(system ?? ""),
                EstimatedUserTokens = tokenEstimator.EstimateText(userPayload ?? ""),
                EstimatedToolsTokens = EstimateToolsTokens(tools, tokenEstimator),
                EstimatedStoredChatHistoryTokens = EstimateMessages(storedHistory, tokenEstimator),
                EstimatedRequestChatHistoryTokens = EstimateMessages(requestHistoryEstimate, tokenEstimator)
            };
        }

        private int EstimateToolsTokens(IReadOnlyList<ILlmTool> tools)
        {
            return EstimateToolsTokens(tools, _tokenEstimator);
        }

        private static int EstimateToolsTokens(IReadOnlyList<ILlmTool> tools, ITokenEstimator tokenEstimator)
        {
            if (tools == null || tools.Count == 0)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < tools.Count; i++)
            {
                ILlmTool tool = tools[i];
                if (tool == null)
                {
                    continue;
                }

                sum += tokenEstimator.EstimateText(tool.Name ?? "");
                sum += tokenEstimator.EstimateText(tool.Description ?? "");
                sum += tokenEstimator.EstimateText(tool.ParametersSchema ?? "");
                sum += 8;
            }

            return sum;
        }

        private int EstimateMessages(ChatMessage[] messages)
        {
            return EstimateMessages(messages, _tokenEstimator);
        }

        private static int EstimateMessages(ChatMessage[] messages, ITokenEstimator tokenEstimator)
        {
            if (messages == null || messages.Length == 0)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < messages.Length; i++)
            {
                sum += tokenEstimator.EstimateText(messages[i].Content ?? "");
            }

            return sum;
        }

        private static AgentSessionRoleConfigSnapshot ToSnapshot(AgentMemoryPolicy.RoleMemoryConfig config)
        {
            return new AgentSessionRoleConfigSnapshot
            {
                UseMemoryTool = config.UseMemoryTool,
                DefaultAction = config.DefaultAction,
                AllowDuplicateToolCalls = config.AllowDuplicateToolCalls,
                WithChatHistory = config.WithChatHistory,
                PersistChatHistory = config.PersistChatHistory,
                ContextTokens = config.ContextTokens,
                MaxChatHistoryMessages = config.MaxChatHistoryMessages,
                MaxOutputTokens = config.MaxOutputTokens,
                Temperature = config.Temperature,
                UseLlmContextCompaction = config.UseLlmContextCompaction
            };
        }

        private static void AddTools(List<AgentSessionToolSnapshot> output, IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null)
            {
                return;
            }

            for (int i = 0; i < tools.Count; i++)
            {
                ILlmTool tool = tools[i];
                if (tool == null)
                {
                    continue;
                }

                output.Add(new AgentSessionToolSnapshot
                {
                    Name = tool.Name ?? "",
                    Description = tool.Description ?? "",
                    ParametersSchema = tool.ParametersSchema ?? "",
                    AllowDuplicates = tool.AllowDuplicates
                });
            }
        }

        private static void AddMessages(List<AgentSessionChatMessageSnapshot> output, ChatMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }

            for (int i = 0; i < messages.Length; i++)
            {
                output.Add(new AgentSessionChatMessageSnapshot
                {
                    Role = messages[i].Role ?? "",
                    Content = messages[i].Content ?? "",
                    Timestamp = messages[i].Timestamp
                });
            }
        }

        private static void AddNotes(
            AgentSessionSnapshot snapshot,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            AiTaskRequest request)
        {
            snapshot.Notes.Add(
                "Token counts are estimates from the active ITokenEstimator and are intended for debugging, not provider billing.");
            snapshot.Notes.Add(
                "Estimated request chat history is computed read-only from stored history, role max message count, and the active context budget policy.");
            snapshot.Notes.Add(
                "The inspector does not call IConversationContextManager because some implementations persist newly compacted summaries during request composition.");

            if (!roleConfig.WithChatHistory)
            {
                snapshot.Notes.Add("Chat history is disabled for this role policy; stored history is still displayed if present.");
            }

            if (request.ForcedToolMode == LlmToolChoiceMode.None)
            {
                snapshot.Notes.Add("The supplied diagnostics request disables tools via ForcedToolMode=None.");
            }
        }

        private static void AddSerializedNotes(
            AgentSessionSnapshot snapshot,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig)
        {
            snapshot.Notes.Add(
                "Token counts are estimates from DefaultContextBudgetPolicy and the supplied ITokenEstimator; Edit Mode has no live request payload.");
            snapshot.Notes.Add(
                "Runtime context overlay, user payload estimate, and estimated request chat history are " +
                AgentSessionSnapshot.UnavailableInEditMode + ".");
            snapshot.Notes.Add(
                "Stored memory and chat history are read from the supplied store without saving or clearing anything.");

            if (!roleConfig.WithChatHistory)
            {
                snapshot.Notes.Add("Chat history is disabled for this role policy; stored history is still displayed if present.");
            }
        }

        private static IAgentSystemPromptProvider BuildSerializedSystemPromptProvider(
            AgentPromptsDefinition promptsDefinition,
            IAgentSystemPromptProvider fallbackSystemPrompts)
        {
            List<IAgentSystemPromptProvider> chain = new();
            if (promptsDefinition != null)
            {
                chain.Add(new DefinitionAgentSystemPromptProvider(promptsDefinition));
            }

            if (fallbackSystemPrompts != null)
            {
                chain.Add(fallbackSystemPrompts);
            }

            chain.Add(new BuiltInDefaultAgentSystemPromptProvider());
            return new ChainedAgentSystemPromptProvider(chain);
        }

        private static string ResolveBaseSystemPrompt(IAgentSystemPromptProvider systemPrompts, string roleId)
        {
            if (systemPrompts != null &&
                systemPrompts.TryGetSystemPrompt(roleId, out string prompt) &&
                !string.IsNullOrWhiteSpace(prompt))
            {
                return prompt.Trim();
            }

            return $"You are agent \"{roleId}\".";
        }

        private static string ResolveAdditionalSystemPrompt(AgentMemoryPolicy memoryPolicy, string roleId)
        {
            return memoryPolicy != null &&
                   memoryPolicy.TryGetAdditionalSystemPrompt(roleId, out string prompt) &&
                   !string.IsNullOrWhiteSpace(prompt)
                ? prompt.Trim()
                : "";
        }

        private static string ComposeSystemPrompt(
            string universalPrefix,
            string baseSystemPrompt,
            string additionalSystemPrompt)
        {
            System.Text.StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(universalPrefix))
            {
                sb.Append(universalPrefix.TrimEnd());
                sb.Append('\n');
            }

            sb.Append(baseSystemPrompt ?? "");
            if (!string.IsNullOrWhiteSpace(additionalSystemPrompt))
            {
                sb.Append("\n\n");
                sb.Append(additionalSystemPrompt.Trim());
            }

            return sb.ToString();
        }

        private static bool IsUniversalPrefixOverridden(
            AgentPromptsDefinition promptsDefinition,
            string roleId)
        {
            if (promptsDefinition == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            string trimmedRoleId = roleId.Trim();
            foreach (AgentPromptEntryDefinition entry in promptsDefinition.EnumerateEntries())
            {
                if (entry != null &&
                    entry.OverrideUniversalPrefix &&
                    string.Equals(entry.RoleId?.Trim(), trimmedRoleId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class DefinitionAgentSystemPromptProvider : IAgentSystemPromptProvider
        {
            private readonly AgentPromptsDefinition _definition;

            public DefinitionAgentSystemPromptProvider(AgentPromptsDefinition definition)
            {
                _definition = definition;
            }

            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = null;
                if (_definition == null || string.IsNullOrWhiteSpace(roleId))
                {
                    return false;
                }

                string trimmedRoleId = roleId.Trim();
                foreach (AgentPromptEntryDefinition entry in _definition.EnumerateEntries())
                {
                    if (entry == null ||
                        string.IsNullOrWhiteSpace(entry.RoleId) ||
                        string.IsNullOrWhiteSpace(entry.SystemPrompt))
                    {
                        continue;
                    }

                    if (!string.Equals(entry.RoleId.Trim(), trimmedRoleId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    systemPrompt = entry.SystemPrompt;
                    return true;
                }

                return false;
            }
        }

        private static AiTaskRequest CloneRequestForRole(AiTaskRequest request, string roleId)
        {
            if (request == null)
            {
                return new AiTaskRequest { RoleId = roleId };
            }

            return new AiTaskRequest
            {
                RoleId = roleId,
                Hint = request.Hint ?? "",
                LuaRepairGeneration = request.LuaRepairGeneration,
                LuaRepairPreviousCode = request.LuaRepairPreviousCode ?? "",
                LuaRepairErrorMessage = request.LuaRepairErrorMessage ?? "",
                TraceId = request.TraceId ?? "",
                Priority = request.Priority,
                SourceTag = request.SourceTag ?? "",
                CancellationScope = request.CancellationScope ?? "",
                LuaScriptVersionKey = request.LuaScriptVersionKey ?? "",
                DataOverlayVersionKeysCsv = request.DataOverlayVersionKeysCsv ?? "",
                ForcedToolMode = request.ForcedToolMode,
                RequiredToolName = request.RequiredToolName ?? "",
                AllowedToolNames = request.AllowedToolNames,
                MaxOutputTokens = request.MaxOutputTokens
            };
        }

        private static IReadOnlyList<ILlmTool> FilterToolsForRequest(IReadOnlyList<ILlmTool> tools, AiTaskRequest task)
        {
            if (tools == null || tools.Count == 0 || task == null)
            {
                return tools;
            }

            if (task.ForcedToolMode == LlmToolChoiceMode.None)
            {
                return Array.Empty<ILlmTool>();
            }

            if (task.AllowedToolNames == null)
            {
                return tools;
            }

            if (task.AllowedToolNames.Length == 0)
            {
                return Array.Empty<ILlmTool>();
            }

            HashSet<string> allowed = new(StringComparer.Ordinal);
            foreach (string name in task.AllowedToolNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    allowed.Add(name.Trim());
                }
            }

            if (allowed.Count == 0)
            {
                return Array.Empty<ILlmTool>();
            }

            List<ILlmTool> filtered = new();
            foreach (ILlmTool tool in tools)
            {
                if (tool != null && allowed.Contains(tool.Name))
                {
                    filtered.Add(tool);
                }
            }

            return filtered;
        }

        private static int? ResolveMaxOutputTokens(int? perCall, int? perAgent)
        {
            if (perCall.HasValue && perCall.Value > 0)
            {
                return perCall.Value;
            }

            if (perAgent.HasValue && perAgent.Value > 0)
            {
                return perAgent.Value;
            }

            return null;
        }

        private static ChatMessage[] TakeTail(ChatMessage[] messages, int maxMessages)
        {
            if (messages == null || messages.Length == 0 || maxMessages <= 0 || messages.Length <= maxMessages)
            {
                return messages ?? Array.Empty<ChatMessage>();
            }

            ChatMessage[] result = new ChatMessage[maxMessages];
            Array.Copy(messages, messages.Length - maxMessages, result, 0, maxMessages);
            return result;
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], trimmed, StringComparison.Ordinal))
                {
                    return;
                }
            }

            values.Add(trimmed);
        }
    }
}

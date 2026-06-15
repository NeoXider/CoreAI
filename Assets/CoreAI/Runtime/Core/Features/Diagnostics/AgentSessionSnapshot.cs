using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Serializable read-only snapshot of the context CoreAI can inspect for one agent role.
    /// </summary>
    [Serializable]
    public sealed class AgentSessionSnapshot
    {
        public const string UnavailableInEditMode = "(unavailable in Edit Mode)";

        public string SnapshotSource;
        public string RoleId;
        public bool RoleIsExplicitlyConfigured;
        public string UniversalSystemPromptPrefix;
        public string BaseSystemPrompt;
        public string AdditionalSystemPrompt;
        public string ResolvedSystemPrompt;
        public string ResolvedSystemPromptWithRuntimeContext;
        public string ResolvedSystemPromptWithMemoryAndTools;
        public string ResolvedSystemPromptFinalEstimate;
        public string MemoryText;
        public string ConversationSummary;
        public string UserPayloadEstimate;
        public AgentSessionRoleConfigSnapshot RoleConfig;
        public AgentSessionBudgetSnapshot Budget;
        public List<AgentSessionToolSnapshot> Tools = new();
        public List<AgentSessionChatMessageSnapshot> ChatHistory = new();
        public List<AgentSessionChatMessageSnapshot> EstimatedRequestChatHistory = new();
        public List<string> Notes = new();

        /// <summary>Full dump (statistics + session) as a single string, e.g. for "copy both".</summary>
        public string ToReadableText()
        {
            StringBuilder sb = new();
            sb.Append(ToStatsText());
            sb.AppendLine();
            sb.Append(ToSessionText());
            return sb.ToString();
        }

        /// <summary>Role config, token/context budget, and diagnostic notes — the "statistics" view.</summary>
        public string ToStatsText()
        {
            StringBuilder sb = new();
            AppendHeader(sb);

            sb.AppendLine("Role Config");
            sb.AppendLine("-----------");
            sb.AppendLine($"WithChatHistory: {RoleConfig.WithChatHistory}");
            sb.AppendLine($"PersistChatHistory: {RoleConfig.PersistChatHistory}");
            sb.AppendLine($"ContextTokens: {RoleConfig.ContextTokens}");
            sb.AppendLine($"MaxChatHistoryMessages: {RoleConfig.MaxChatHistoryMessages}");
            sb.AppendLine($"UseMemoryTool: {RoleConfig.UseMemoryTool}");
            sb.AppendLine($"UseLlmContextCompaction: {RoleConfig.UseLlmContextCompaction}");
            sb.AppendLine($"MaxOutputTokens: {NullableToString(RoleConfig.MaxOutputTokens)}");
            sb.AppendLine($"Temperature: {NullableToString(RoleConfig.Temperature)}");
            sb.AppendLine($"AllowDuplicateToolCalls: {NullableToString(RoleConfig.AllowDuplicateToolCalls)}");
            sb.AppendLine();

            sb.AppendLine("Token / Context Budget");
            sb.AppendLine("----------------------");
            sb.AppendLine($"Context window tokens: {Budget.ContextWindowTokens}");
            sb.AppendLine($"Reserved completion tokens: {Budget.ReservedForCompletionTokens}");
            sb.AppendLine($"Fixed prompt tokens (budget policy): {Budget.EstimatedFixedPromptTokens}");
            sb.AppendLine($"History token budget: {Budget.HistoryTokenBudget}");
            sb.AppendLine($"Reserved slack tokens: {Budget.ReservedSlackTokens}");
            sb.AppendLine($"System tokens estimate: {Budget.EstimatedSystemTokens}");
            sb.AppendLine($"User tokens estimate: {Budget.EstimatedUserTokens}");
            sb.AppendLine($"Tools tokens estimate: {Budget.EstimatedToolsTokens}");
            sb.AppendLine($"Stored chat history tokens estimate: {Budget.EstimatedStoredChatHistoryTokens}");
            sb.AppendLine($"Estimated request chat history tokens: {Budget.EstimatedRequestChatHistoryTokens}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(SnapshotSource))
            {
                sb.AppendLine("Snapshot");
                sb.AppendLine("--------");
                sb.AppendLine($"Source: {SnapshotSource}");
                sb.AppendLine();
            }

            if (Notes.Count > 0)
            {
                sb.AppendLine("Notes");
                sb.AppendLine("-----");
                for (int i = 0; i < Notes.Count; i++)
                {
                    sb.Append("- ");
                    sb.AppendLine(Notes[i] ?? "");
                }
            }

            return sb.ToString();
        }

        /// <summary>System prompt parts, memory, summary, tools, and chat history — the "session" view.</summary>
        public string ToSessionText()
        {
            StringBuilder sb = new();
            AppendHeader(sb);

            AppendSection(sb, "Universal System Prompt Prefix", UniversalSystemPromptPrefix);
            AppendSection(sb, "Base System Prompt", BaseSystemPrompt);
            AppendSection(sb, "Additional System Prompt", AdditionalSystemPrompt);
            AppendSection(sb, "Resolved System Prompt", ResolvedSystemPrompt);
            AppendSection(sb, "Resolved System Prompt + Runtime Context", ResolvedSystemPromptWithRuntimeContext);
            AppendSection(sb, "Memory", MemoryText);
            AppendSection(sb, "Conversation Summary", ConversationSummary);
            AppendSection(sb, "Resolved System Prompt + Memory + Tools", ResolvedSystemPromptWithMemoryAndTools);
            AppendSection(sb, "Final System Prompt Estimate", ResolvedSystemPromptFinalEstimate);
            AppendSection(sb, "User Payload Estimate", UserPayloadEstimate);

            sb.AppendLine("Tools");
            sb.AppendLine("-----");
            if (Tools.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                for (int i = 0; i < Tools.Count; i++)
                {
                    AgentSessionToolSnapshot tool = Tools[i];
                    sb.AppendLine($"{i + 1}. {tool.Name}");
                    sb.AppendLine($"   Description: {EmptyLabel(tool.Description)}");
                    sb.AppendLine($"   AllowDuplicates: {tool.AllowDuplicates}");
                    sb.AppendLine($"   ParametersSchema: {EmptyLabel(tool.ParametersSchema)}");
                }
            }

            sb.AppendLine();
            AppendMessages(sb, "Stored Chat History", ChatHistory);
            AppendMessages(sb, "Estimated Request Chat History", EstimatedRequestChatHistory);

            return sb.ToString();
        }

        private void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("CoreAI Agent Session Inspector");
            sb.AppendLine("==============================");
            if (!string.IsNullOrWhiteSpace(SnapshotSource))
            {
                sb.AppendLine($"Snapshot source: {SnapshotSource}");
            }

            sb.AppendLine($"RoleId: {RoleId}");
            sb.AppendLine($"Explicit policy role: {RoleIsExplicitlyConfigured}");
            sb.AppendLine();
        }

        private static void AppendSection(StringBuilder sb, string title, string content)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            sb.AppendLine(EmptyLabel(content));
            sb.AppendLine();
        }

        private static void AppendMessages(
            StringBuilder sb,
            string title,
            IReadOnlyList<AgentSessionChatMessageSnapshot> messages)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            if (messages == null || messages.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                AgentSessionChatMessageSnapshot message = messages[i];
                sb.AppendLine($"{i + 1}. [{message.Role}] {message.Timestamp}");
                sb.AppendLine(message.Content ?? "");
                sb.AppendLine();
            }
        }

        private static string EmptyLabel(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        }

        private static string NullableToString<T>(T? value) where T : struct
        {
            return value.HasValue ? value.Value.ToString() : "(null)";
        }
    }

    [Serializable]
    public sealed class AgentSessionToolSnapshot
    {
        public string Name;
        public string Description;
        public string ParametersSchema;
        public bool AllowDuplicates;
    }

    [Serializable]
    public sealed class AgentSessionChatMessageSnapshot
    {
        public string Role;
        public string Content;
        public long Timestamp;
    }

    [Serializable]
    public struct AgentSessionRoleConfigSnapshot
    {
        public bool UseMemoryTool;
        public MemoryToolAction DefaultAction;
        public bool? AllowDuplicateToolCalls;
        public bool WithChatHistory;
        public bool PersistChatHistory;
        public int ContextTokens;
        public int MaxChatHistoryMessages;
        public int? MaxOutputTokens;
        public float? Temperature;
        public bool UseLlmContextCompaction;
    }

    [Serializable]
    public struct AgentSessionBudgetSnapshot
    {
        public int ContextWindowTokens;
        public int ReservedForCompletionTokens;
        public int EstimatedFixedPromptTokens;
        public int HistoryTokenBudget;
        public int ReservedSlackTokens;
        public int EstimatedSystemTokens;
        public int EstimatedUserTokens;
        public int EstimatedToolsTokens;
        public int EstimatedStoredChatHistoryTokens;
        public int EstimatedRequestChatHistoryTokens;
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Data that describes one sub-agent a lead agent may hand a task to: the role that runs it, the
    /// description the lead reads when it chooses, the tools the sub-agent may call, the routing profile,
    /// and how far a single delegation may go (output, tool roundtrips, result size, deadline).
    /// <para>
    /// Immutable and validated on construction, so a definition that exists is a definition that can run.
    /// The tool allowlist can never contain the delegate tool (<see cref="DelegateTaskTool.DefaultName"/>):
    /// a sub-agent does not hand work to further sub-agents, and the refusal happens here, when the data
    /// is written, not when a model first tries it.
    /// </para>
    /// </summary>
    public sealed class SubAgentDefinition
    {
        /// <summary>Tool-call roundtrip cap a definition gets when it names none.</summary>
        public const int DefaultMaxToolCallRoundtrips = 12;

        /// <summary>Result size (characters) a definition gets when it names none.</summary>
        public const int DefaultMaxResultChars = 4000;

        /// <summary>Deadline a definition gets when it names none, counted from the moment the run starts.</summary>
        public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(5);

        private static readonly Regex IdPattern = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);

        /// <summary>Creates a validated definition.</summary>
        /// <param name="id">Name the lead passes as <c>subagent</c>: 1-64 letters, digits, <c>_</c>, <c>.</c> or <c>-</c>.</param>
        /// <param name="roleId">Agent role that runs the delegated turn (prompt and response policy come from it).</param>
        /// <param name="description">What the sub-agent is good at, written for the lead.</param>
        /// <param name="allowedToolNames">The only tools the sub-agent may call; empty means none. Never the delegate tool.</param>
        /// <param name="routingProfileId">Explicit LLM routing profile; empty keeps the role's routing.</param>
        /// <param name="maxOutputTokens">Response token cap: <c>null</c> inherits, <c>0</c> is unlimited, positive caps.</param>
        /// <param name="maxToolCallRoundtrips">Tool roundtrip cap for one delegation: <c>0</c> is unlimited, positive caps.</param>
        /// <param name="maxResultChars">Largest result text handed back to the lead; longer text is clipped.</param>
        /// <param name="deadline">Longest a single run may take once it has started; must be positive.</param>
        /// <param name="instructions">Optional per-sub-agent instructions sent in the request's cache-safe tail.</param>
        /// <param name="useRoleMemory">
        /// Opt-in: let the run see the role's durable memory snapshot. Off by default, also when the sub-agent shares
        /// the lead's role, so a delegation starts from the task alone. Writing memory still needs the memory tool
        /// in <paramref name="allowedToolNames"/>.
        /// </param>
        public SubAgentDefinition(
            string id,
            string roleId,
            string description,
            IEnumerable<string> allowedToolNames,
            string routingProfileId = "",
            int? maxOutputTokens = null,
            int maxToolCallRoundtrips = DefaultMaxToolCallRoundtrips,
            int maxResultChars = DefaultMaxResultChars,
            TimeSpan? deadline = null,
            string instructions = "",
            bool useRoleMemory = false)
        {
            string trimmedId = id?.Trim() ?? "";
            if (!IdPattern.IsMatch(trimmedId))
            {
                throw new ArgumentException(
                    $"Sub-agent id '{id}' is invalid: use 1-64 letters, digits, '_', '.' or '-'.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(roleId))
            {
                throw new ArgumentException($"Sub-agent '{trimmedId}' needs a role id.", nameof(roleId));
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException(
                    $"Sub-agent '{trimmedId}' needs a description: it is what the lead reads when it chooses.",
                    nameof(description));
            }

            if (maxOutputTokens.HasValue && maxOutputTokens.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxOutputTokens),
                    "Max output tokens must be null (inherit), 0 (unlimited) or positive.");
            }

            if (maxToolCallRoundtrips < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxToolCallRoundtrips),
                    "Max tool-call roundtrips must be 0 (unlimited) or positive.");
            }

            if (maxResultChars < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxResultChars), "Max result chars must be positive.");
            }

            TimeSpan resolvedDeadline = deadline ?? DefaultDeadline;
            if (resolvedDeadline <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(deadline), "A sub-agent deadline must be positive.");
            }

            Id = trimmedId;
            RoleId = roleId.Trim();
            Description = description.Trim();
            AllowedToolNames = NormalizeAllowlist(trimmedId, allowedToolNames);
            RoutingProfileId = routingProfileId?.Trim() ?? "";
            MaxOutputTokens = maxOutputTokens;
            MaxToolCallRoundtrips = maxToolCallRoundtrips;
            MaxResultChars = maxResultChars;
            Deadline = resolvedDeadline;
            Instructions = instructions?.Trim() ?? "";
            UseRoleMemory = useRoleMemory;
        }

        /// <summary>Name the lead passes as <c>subagent</c>; compared case-insensitively.</summary>
        public string Id { get; }

        /// <summary>Agent role that runs the delegated turn.</summary>
        public string RoleId { get; }

        /// <summary>What the sub-agent is good at, written for the lead.</summary>
        public string Description { get; }

        /// <summary>The only tools the sub-agent may call (trimmed, distinct, never the delegate tool).</summary>
        public IReadOnlyList<string> AllowedToolNames { get; }

        /// <summary>Explicit LLM routing profile; empty keeps the role's routing.</summary>
        public string RoutingProfileId { get; }

        /// <summary>Response token cap: <c>null</c> inherits, <c>0</c> is unlimited, positive caps.</summary>
        public int? MaxOutputTokens { get; }

        /// <summary>Tool roundtrip cap for one delegation: <c>0</c> is unlimited, positive caps.</summary>
        public int MaxToolCallRoundtrips { get; }

        /// <summary>Largest result text handed back to the lead; longer text is clipped with a marker.</summary>
        public int MaxResultChars { get; }

        /// <summary>Longest one run may take once it has started (waiting for a free slot does not count).</summary>
        public TimeSpan Deadline { get; }

        /// <summary>Optional per-sub-agent instructions, sent in the request's cache-safe tail.</summary>
        public string Instructions { get; }

        /// <summary>True when the run may see the role's durable memory snapshot (opt-in; off by default).</summary>
        public bool UseRoleMemory { get; }

        /// <summary>True when <paramref name="toolName"/> is in <see cref="AllowedToolNames"/> (ordinal).</summary>
        public bool AllowsTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            string trimmed = toolName.Trim();
            foreach (string allowed in AllowedToolNames)
            {
                if (string.Equals(allowed, trimmed, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<string> NormalizeAllowlist(string id, IEnumerable<string> allowedToolNames)
        {
            List<string> names = new();
            if (allowedToolNames == null)
            {
                return names.AsReadOnly();
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string name in allowedToolNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException(
                        $"Sub-agent '{id}' lists a blank tool name in its allowlist.", nameof(allowedToolNames));
                }

                string trimmed = name.Trim();
                if (string.Equals(trimmed, DelegateTaskTool.DefaultName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Sub-agent '{id}' may never be given the delegate tool '{DelegateTaskTool.DefaultName}': " +
                        "sub-agents do not delegate further.", nameof(allowedToolNames));
                }

                if (seen.Add(trimmed))
                {
                    names.Add(trimmed);
                }
            }

            return names.AsReadOnly();
        }
    }
}

using System;

namespace CoreAI.RobloxApi.Instances
{
    /// <summary>
    /// Stable machine error codes for the Roblox API layer (roadmap §5.2.7). The wire names
    /// (SCREAMING_SNAKE) are part of the AI self-repair contract and never change.
    /// </summary>
    public enum RbxErrorCode
    {
        NotImplemented,
        BadArgument,
        UnknownService,
        InstanceDestroyed,
        ParentLocked,
        BudgetExceeded,
        SignalCascade,
        ThreadCap,
        CyclicRequire,
        ApiVersionMismatch,
        NotAuthority,
        PayloadTooLarge,
        ContextViolation
    }

    /// <summary>
    /// Structured error for the Roblox API layer following the roadmap §5.2.7 format:
    /// <c>[mod:id script:path line:n] CODE: message | fix: suggestion</c>. Mod context is optional
    /// at the Domain level — the scripting/marshalling layer attaches it via <see cref="WithContext"/>.
    /// </summary>
    public sealed class RbxError : Exception
    {
        /// <summary>Stable machine code.</summary>
        public RbxErrorCode Code { get; }

        /// <summary>The message body without prefix/fix decoration.</summary>
        public string RawMessage { get; }

        /// <summary>One actionable suggestion, present tense, one sentence.</summary>
        public string Fix { get; }

        /// <summary>Owning mod id; null when no mod context is attached yet.</summary>
        public string ModId { get; }

        /// <summary>Author-relative script path; null without mod context.</summary>
        public string Script { get; }

        /// <summary>Author (post-source-map) line; 0 without mod context.</summary>
        public int Line { get; }

        public RbxError(RbxErrorCode code, string message, string fix = null,
            string modId = null, string script = null, int line = 0)
            : base(Format(code, message, fix, modId, script, line))
        {
            Code = code;
            RawMessage = message;
            Fix = fix;
            ModId = modId;
            Script = script;
            Line = line;
        }

        /// <summary>Returns a copy of this error with mod/script/line context attached.</summary>
        public RbxError WithContext(string modId, string script, int line)
        {
            return new RbxError(Code, RawMessage, Fix, modId, script, line);
        }

        /// <summary>Canonical SCREAMING_SNAKE wire name for a code (§5.2.7, stable from day one).</summary>
        public static string ToWireName(RbxErrorCode code)
        {
            switch (code)
            {
                case RbxErrorCode.NotImplemented: return "NOT_IMPLEMENTED";
                case RbxErrorCode.BadArgument: return "BAD_ARGUMENT";
                case RbxErrorCode.UnknownService: return "UNKNOWN_SERVICE";
                case RbxErrorCode.InstanceDestroyed: return "INSTANCE_DESTROYED";
                case RbxErrorCode.ParentLocked: return "PARENT_LOCKED";
                case RbxErrorCode.BudgetExceeded: return "BUDGET_EXCEEDED";
                case RbxErrorCode.SignalCascade: return "SIGNAL_CASCADE";
                case RbxErrorCode.ThreadCap: return "THREAD_CAP";
                case RbxErrorCode.CyclicRequire: return "CYCLIC_REQUIRE";
                case RbxErrorCode.ApiVersionMismatch: return "API_VERSION_MISMATCH";
                case RbxErrorCode.NotAuthority: return "NOT_AUTHORITY";
                case RbxErrorCode.PayloadTooLarge: return "PAYLOAD_TOO_LARGE";
                case RbxErrorCode.ContextViolation: return "CONTEXT_VIOLATION";
                default: throw new ArgumentOutOfRangeException(nameof(code), code, null);
            }
        }

        /// <summary>Roadmap §5.2.7 human-readable line; the mod prefix is omitted when no context exists.</summary>
        public static string Format(RbxErrorCode code, string message, string fix,
            string modId, string script, int line)
        {
            string prefix = modId == null
                ? string.Empty
                : "[mod:" + modId + " script:" + (script ?? "?") + " line:" + line + "] ";
            string suffix = string.IsNullOrEmpty(fix) ? string.Empty : " | fix: " + fix;
            return prefix + ToWireName(code) + ": " + message + suffix;
        }

        /// <summary>Loud stub per the roadmap stub-error contract; names the phase that completes it.</summary>
        public static RbxError NotImplemented(string feature, string phase, string workaround)
        {
            return new RbxError(RbxErrorCode.NotImplemented,
                feature + " is planned for " + phase + ".", workaround);
        }

        public static RbxError BadArgument(string message, string fix)
        {
            return new RbxError(RbxErrorCode.BadArgument, message, fix);
        }

        /// <summary>Exact Roblox message text (roadmap §5.2.4): "X is not a valid Service name".</summary>
        public static RbxError UnknownService(string serviceName)
        {
            return new RbxError(RbxErrorCode.UnknownService,
                serviceName + " is not a valid Service name",
                "call game:GetService with an exact service class name, e.g. \"Workspace\"");
        }

        public static RbxError InstanceDestroyed(string memberName, string instanceName, InstanceId id)
        {
            return new RbxError(RbxErrorCode.InstanceDestroyed,
                memberName + " on destroyed instance " + instanceName + " (id " + id.Value + ")",
                "drop references to destroyed instances and create a new Instance instead");
        }

        /// <summary>Exact message per roadmap D6.</summary>
        public static RbxError ParentLocked(string instanceName)
        {
            return new RbxError(RbxErrorCode.ParentLocked,
                "The Parent property of " + instanceName + " is locked, use a new Instance instead",
                "create a new Instance instead of reusing a destroyed one");
        }
    }
}

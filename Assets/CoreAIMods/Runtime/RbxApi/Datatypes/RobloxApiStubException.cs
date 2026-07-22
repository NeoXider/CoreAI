using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Structured loud-stub error for deliberately unimplemented Roblox API surface.
    /// The message body follows the roadmap's stable stub-error contract
    /// (ROBLOX_API_ROADMAP.md §5.2.7): "&lt;CODE&gt;: &lt;message&gt; | fix: &lt;fix&gt;".
    /// The mod/script/line prefix is added by the error formatter upstream (MVP1 task 10);
    /// this exception only carries the machine fields.
    /// </summary>
    public sealed class RobloxApiStubException : InvalidOperationException
    {
        /// <summary>Stable machine code, e.g. "NOT_IMPLEMENTED" or "BAD_ARGUMENT".</summary>
        public string Code { get; }

        /// <summary>One actionable suggestion, present tense.</summary>
        public string Fix { get; }

        public RobloxApiStubException(string code, string message, string fix)
            : base($"{code}: {message} | fix: {fix}")
        {
            Code = code;
            Fix = fix;
        }

        /// <summary>Creates the standard NOT_IMPLEMENTED stub error naming the roadmap phase.</summary>
        public static RobloxApiStubException NotImplemented(string what, string phase, string fix)
        {
            return new RobloxApiStubException(
                "NOT_IMPLEMENTED",
                $"{what} is planned for {phase}.",
                fix);
        }

        /// <summary>Creates the standard BAD_ARGUMENT error naming the expected type/position.</summary>
        public static RobloxApiStubException BadArgument(string message, string fix)
        {
            return new RobloxApiStubException("BAD_ARGUMENT", message, fix);
        }
    }
}

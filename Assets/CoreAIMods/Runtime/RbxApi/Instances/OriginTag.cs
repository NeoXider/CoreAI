namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Ownership-ledger origin tags (roadmap §2, one-shot execution + ownership ledger):
    /// <c>mod:&lt;id&gt;</c> for mod-created, <c>console:&lt;invocationId&gt;</c> for execute_lua
    /// one-shots, <c>ai:&lt;modId&gt;</c> for game-sanctioned AI generation, null for host/world
    /// objects. Tags enable selective cleanup/undo ("remove everything from invocation N").
    /// </summary>
    public static class OriginTag
    {
        public const string ModPrefix = "mod:";
        public const string ConsolePrefix = "console:";
        public const string AiPrefix = "ai:";

        public static string FromMod(string modId)
        {
            return ModPrefix + modId;
        }

        public static string FromConsole(string invocationId)
        {
            return ConsolePrefix + invocationId;
        }

        public static string FromAi(string modId)
        {
            return AiPrefix + modId;
        }

        /// <summary>Null (host origin) or one of the three known prefixes with a non-empty payload.</summary>
        public static bool IsValid(string originTag)
        {
            if (originTag == null)
            {
                return true;
            }

            return HasPayload(originTag, ModPrefix)
                   || HasPayload(originTag, ConsolePrefix)
                   || HasPayload(originTag, AiPrefix);
        }

        private static bool HasPayload(string tag, string prefix)
        {
            return tag.Length > prefix.Length
                   && tag.StartsWith(prefix, System.StringComparison.Ordinal);
        }
    }
}

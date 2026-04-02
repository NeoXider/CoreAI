using System;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public static class ArenaWavePlanParser
    {
        public const string CommandType = "ArenaWavePlan";

        public static bool TryParse(string raw, out ArenaWavePlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var json = TryExtractFirstJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                var env = JsonUtility.FromJson<ArenaWavePlanEnvelope>(json);
                if (env == null || !string.Equals(env.commandType, CommandType, StringComparison.Ordinal))
                    return false;
                if (env.payload == null)
                    return false;
                plan = env.payload;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string TryExtractFirstJsonObject(string s)
        {
            // Ищем первый JSON-объект { ... } в ответе LLM (если там есть пояснения/markdown).
            var start = s.IndexOf('{');
            if (start < 0)
                return null;
            var depth = 0;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return s.Substring(start, i - start + 1);
                }
            }

            return null;
        }
    }
}


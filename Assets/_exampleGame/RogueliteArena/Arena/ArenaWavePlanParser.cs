using System;
using CoreAI.Ai;
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

            if (!LlmResponseSanitizer.TryPrepareJsonObject(raw, out var json) ||
                string.IsNullOrWhiteSpace(json))
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
    }
}


using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Session;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that returns the CURRENT host game-state (session telemetry) on demand. This is the "parallel
    /// capability" alternative to baking a telemetry snapshot into every user message: an agent calls
    /// <c>game_state</c> to pull fresh values (wave, score, mode, player stats, ...) when it needs them, so
    /// stale state never accumulates in the conversation history. Reads the live
    /// <see cref="ISessionTelemetryProvider"/> the host updates from gameplay.
    /// </summary>
    public sealed class GameStateLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly ISessionTelemetryProvider _telemetry;

        public GameStateLlmTool(ISessionTelemetryProvider telemetry)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        public override string Name => "game_state";

        public override string Description =>
            "Return the current live game state (host telemetry: e.g. wave, score, mode, player stats) as a " +
            "JSON object. Call this whenever you need up-to-date values; do not rely on numbers from earlier " +
            "messages, which may be stale.";

        // Re-reading state is always valid: the world changes between calls.
        public override bool AllowDuplicates => true;

        public AIFunction CreateAIFunction()
        {
            Func<CancellationToken, Task<string>> func = GetStateAsync;
            return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        private Task<string> GetStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(BuildStateJson(_telemetry.BuildSnapshot()));
        }

        /// <summary>Serializes the snapshot's telemetry into a compact JSON object for the model.</summary>
        internal static string BuildStateJson(GameSessionSnapshot snap)
        {
            StringBuilder sb = new(128);
            sb.Append("{\"telemetry\":{");
            bool first = true;
            if (snap?.Telemetry != null)
            {
                foreach (KeyValuePair<string, string> kv in snap.Telemetry)
                {
                    if (string.IsNullOrEmpty(kv.Key))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append('"').Append(Escape(kv.Key)).Append("\":\"").Append(Escape(kv.Value)).Append('"');
                }
            }

            sb.Append("}}");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}

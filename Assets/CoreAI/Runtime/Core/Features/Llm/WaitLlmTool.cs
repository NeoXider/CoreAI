using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Lets an agent deliberately wait before continuing the same tool-calling turn.
    /// </summary>
    public sealed class WaitLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        public const double DefaultMaxSeconds = 60d;

        private readonly double _maxSeconds;

        public WaitLlmTool(double maxSeconds = DefaultMaxSeconds)
        {
            _maxSeconds = maxSeconds > 0d && !double.IsNaN(maxSeconds) && !double.IsInfinity(maxSeconds)
                ? maxSeconds
                : DefaultMaxSeconds;
        }

        public override string Name => "wait";

        public override string Description =>
            "Pause for a requested number of seconds, then return control to the model so it can continue. " +
            "Use for polling, cooldowns, async game state changes, or waiting for an external process.";

        public override string ParametersSchema => JsonParams(
            ("seconds", "number", true, $"Seconds to wait. Must be greater than 0 and at most {_maxSeconds:0.###}."),
            ("reason", "string", false, "Short reason for the wait, used only for diagnostics.")
        );

        public override bool AllowDuplicates => true;

        public AIFunction CreateAIFunction()
        {
            Func<double, string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        public async Task<string> ExecuteAsync(
            [Description("Seconds to wait. Must be greater than 0 and at most the tool's configured maximum.")]
            double seconds,
            [Description("Short reason for the wait, used only for diagnostics.")]
            string reason = "",
            CancellationToken cancellationToken = default)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0d)
            {
                return Serialize(new WaitResult
                {
                    Success = false,
                    Error = "seconds must be greater than 0."
                });
            }

            double clamped = Math.Min(seconds, _maxSeconds);
            TimeSpan delay = TimeSpan.FromSeconds(clamped);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            return Serialize(new WaitResult
            {
                Success = true,
                Message = $"DONE: waited {clamped:0.###} second(s).",
                RequestedSeconds = seconds,
                WaitedSeconds = clamped,
                Reason = reason ?? ""
            });
        }

        private static string Serialize(WaitResult result)
        {
            return JsonConvert.SerializeObject(result);
        }

        public sealed class WaitResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }
            public double RequestedSeconds { get; set; }
            public double WaitedSeconds { get; set; }
            public string Reason { get; set; }
        }
    }
}

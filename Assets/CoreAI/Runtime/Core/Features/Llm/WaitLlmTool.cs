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
        private readonly ILlmAsyncMarshaler _asyncMarshaler;

        public WaitLlmTool(double maxSeconds = DefaultMaxSeconds, ILlmAsyncMarshaler asyncMarshaler = null)
        {
            _maxSeconds = maxSeconds > 0d && !double.IsNaN(maxSeconds) && !double.IsInfinity(maxSeconds)
                ? maxSeconds
                : DefaultMaxSeconds;
            // WHY: the portable default is PassThrough, whose DelayAsync falls back to Task.Delay.
            // On WebGL Task.Delay never fires (there is no timer), so a Unity host must pass its
            // frame-driven marshaler here (UnityMainThreadLlmAsyncMarshaler.DelayAsync).
            _asyncMarshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
        }

        public override string Name => "wait";

        public override string Description =>
            "Pause for a requested number of seconds, then return control to the model so it can continue. " +
            "Use for polling, cooldowns, async game state changes, or waiting for an external process.";

        public override string ParametersSchema => JsonParams(
            ("seconds", "number", true,
                $"Seconds to wait. Must be greater than 0; values above {_maxSeconds:0.###} are clamped to that maximum."),
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

        /// <summary>Runs the wait and returns the serialized result.</summary>
        /// <param name="seconds">Seconds to wait. Must be greater than 0; values above the configured maximum are clamped.</param>
        /// <param name="reason">Short reason for the wait, used only for diagnostics.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        // WHY: MEAI awaits this task with ConfigureAwait(false) inside its binary. In the WebGL player
        // the result must be published with the host SynchronizationContext cleared, or the model turn
        // never resumes after an asynchronously completed body (see MeaiToolTaskBridge).
        public Task<string> ExecuteAsync(
            [Description("Seconds to wait. Must be greater than 0; values above the configured maximum are clamped.")]
            double seconds,
            [Description("Short reason for the wait, used only for diagnostics.")]
            string reason = "",
            CancellationToken cancellationToken = default) =>
            MeaiToolTaskBridge.Publish(ExecuteBodyAsync(seconds, reason, cancellationToken));

        private async Task<string> ExecuteBodyAsync(double seconds, string reason, CancellationToken cancellationToken)
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
            // WHY: the delay is scheduled by the host, not by System.Threading.Timer. On WebGL a
            // direct Task.Delay never fires, so calling it here would hang the model turn silently.
            int milliseconds = (int)Math.Min(Math.Max(clamped * 1000d, 1d), int.MaxValue);
            await _asyncMarshaler.DelayAsync(milliseconds, cancellationToken);

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

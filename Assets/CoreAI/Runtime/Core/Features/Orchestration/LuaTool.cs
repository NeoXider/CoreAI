using System;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI <see cref="AIFunction"/> that runs Lua for the Programmer agent, used in the native tool-calling path instead of fenced Lua blocks.
    /// </summary>
    public sealed class LuaTool
    {
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private readonly ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaGenerationRateLimiter _rateLimiter;

        public LuaTool(ILuaExecutor executor, ICoreAISettings settings, ILog logger,
            LuaGenerationRateLimiter rateLimiter = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
        }

        /// <summary>Rate limiter shared with (or mirroring) the envelope pipeline.</summary>
        public LuaGenerationRateLimiter RateLimiter => _rateLimiter;

        /// <summary>Builds the MEAI tool surface for <c>execute_lua</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = "execute_lua",
                Description = "Execute Lua code. Use this to run game logic, create items, report events, etc."
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Runs Lua returned from the model payload.</summary>
        /// <param name="code">Source to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<string> ExecuteAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                return SerializeResult(new LuaResult { Success = false, Error = "Lua code is required" });
            }

            if (!_rateLimiter.TryAcquire(Clock.Elapsed.TotalSeconds))
            {
                string limitError =
                    $"Lua rate limit exceeded ({_rateLimiter.MaxPerWindow} per {_rateLimiter.WindowSeconds:0}s); call rejected.";
                if (_settings.LogToolCallResults)
                {
                    _logger.Warn($"[Tool Call] execute_lua: {limitError}");
                }

                return SerializeResult(new LuaResult { Success = false, Error = limitError });
            }

            if (_settings.LogToolCalls)
            {
                _logger.Info($"[Tool Call] execute_lua: code length={code.Length}");
            }

            if (_settings.LogToolCallArguments)
            {
                string preview = code.Length > 150 ? code.Substring(0, 150) : code;
                _logger.Info($"  code preview: {preview}");
            }

            try
            {
                LuaResult result = await _executor.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);

                if (_settings.LogToolCallResults)
                {
                    string outputPreview =
                        result.Output?.Length > 100 ? result.Output.Substring(0, 100) : result.Output;
                    _logger.Info(
                        $"[Tool Call] execute_lua: {(result.Success ? "SUCCESS" : "FAILED")} - output={outputPreview}");
                }

                return SerializeResult(result);
            }
            catch (Exception ex)
            {
                if (_settings.LogToolCallResults)
                {
                    _logger.Error($"[Tool Call] execute_lua: FAILED - {ex}");
                }

                return SerializeResult(new LuaResult
                {
                    Success = false,
                    Error = $"Lua execution failed: {ex.Message}"
                });
            }
        }

        private static string SerializeResult(LuaResult result)
        {
            return JsonConvert.SerializeObject(result);
        }

        /// <summary>Lua execution outcome for JSON serialization back to the model.</summary>
        public sealed class LuaResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        /// <summary>Abstraction over the concrete Lua host (testable without Unity).</summary>
        public interface ILuaExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken);
        }
    }
}
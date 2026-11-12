using System;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI <see cref="AIFunction"/> that runs Lua for the Programmer agent, used in the native tool-calling path instead of fenced Lua blocks.
    /// </summary>
    public sealed class LuaTool
    {
        public const string ExecuteLuaToolName = "execute_lua";

        // WHY this short: the description is sent on every request. The invented-API list, the scene queries and the
        // mod globals live in the Programmer prompt, the references in the skills it names.
        public const string ExecuteLuaDescription =
            "Run sandboxed Lua once in the live game; returns Success, Output (the return value), Printed, " +
            "Error. Use it to build, change or inspect the world now; code that must keep running (handlers, " +
            "timers) belongs in a mod (manage_mods). Build Roblox-style (Instance.new('Part'), workspace, Vector3, " +
            "CFrame, Color3): read_skill('Rbx API') first. Rule slots: logic_list(), then " +
            "logic_define('slot', function(...) return value end). unity_* reflection: read_skill('Full Lua'). " +
            "report(msg) says what changed.";

        internal const string ExecuteLuaCodeParameterDescription =
            "Lua source, run once. Return a short value to inspect something; report(message) says what changed.";

        private readonly ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaGenerationRateLimiter _rateLimiter;
        private readonly IMutationExecutor _mutationExecutor;
        private readonly IActorIdentityProvider _actorIdentityProvider;
        private readonly string _roleId;

        public LuaTool(ILuaExecutor executor, ICoreAISettings settings, ILog logger,
            LuaGenerationRateLimiter rateLimiter = null,
            IActorIdentityProvider actorIdentityProvider = null,
            string roleId = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
            _actorIdentityProvider = actorIdentityProvider;
            _roleId = roleId;
            _mutationExecutor = executor as IMutationExecutor;
            if (_actorIdentityProvider != null && _mutationExecutor == null)
            {
                throw new ArgumentException(
                    "Actor-scoped execute_lua composition requires a mutation-envelope executor.",
                    nameof(executor));
            }
        }

        /// <summary>Rate limiter shared with (or mirroring) the envelope pipeline.</summary>
        public LuaGenerationRateLimiter RateLimiter => _rateLimiter;

        /// <summary>Builds the MEAI tool surface for <c>execute_lua</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = ExecuteLuaToolName,
                Description = ExecuteLuaDescription
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Runs Lua returned from the model payload.</summary>
        /// <param name="code">Source to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        // WHY: MEAI awaits this task with ConfigureAwait(false) inside its binary. In the WebGL player
        // the result must be published with the host SynchronizationContext cleared, or the model turn
        // never resumes after an asynchronously completed body (see MeaiToolTaskBridge).
        public Task<string> ExecuteAsync(
            [Description(ExecuteLuaCodeParameterDescription)] string code,
            CancellationToken cancellationToken = default) =>
            MeaiToolTaskBridge.Publish(ExecuteBodyAsync(code, cancellationToken));

        private async Task<string> ExecuteBodyAsync(string code, CancellationToken cancellationToken)
        {
            if (_actorIdentityProvider != null && _mutationExecutor != null)
            {
                ActorContext actorContext;
                try
                {
                    actorContext = _actorIdentityProvider.GetActorContext(_roleId);
                }
                catch (Exception ex)
                {
                    return SerializeResult(new LuaResult { Success = false, Error = ex.Message });
                }

                return await ExecuteCoreAsync(
                    code,
                    token => _mutationExecutor.ExecuteAsync(code, actorContext, token),
                    cancellationToken);
            }

            return await ExecuteCoreAsync(
                code,
                token => _executor.ExecuteAsync(code, token),
                cancellationToken);
        }

        private async Task<string> ExecuteCoreAsync(
            string code,
            Func<CancellationToken, Task<LuaResult>> execute,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return SerializeResult(new LuaResult { Success = false, Error = "Lua code is required" });
            }

            if (!_rateLimiter.TryAcquire(out LuaRateLimitBudget budget))
            {
                string limitError = DescribeRateLimitRefusal(budget);
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
                LuaResult result = await execute(cancellationToken);
                if (_settings.LogToolCalls)
                {
                    _logger.Info($"[Tool Call] execute_lua: executor returned success={result.Success}");
                }

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

        /// <summary>
        /// The error of a call refused by the rate limit: says the code did not run and names when the next call
        /// is accepted, so the model waits instead of retrying at once.
        /// </summary>
        internal static string DescribeRateLimitRefusal(LuaRateLimitBudget budget)
        {
            return "Lua rate limit exceeded; execute_lua was NOT executed. " + budget.Describe() + " Wait, then retry.";
        }

        private static readonly JsonSerializerSettings TrimNulls = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private static string SerializeResult(LuaResult result)
        {
            // WHY: trim boilerplate the model does not need — an empty/`nil` Output and a null Error are
            // pure noise on success. A successful side-effect call serialises to {"Success":true}; a real
            // return value or a failure Error still rides along. Keeps weak local models from reasoning over
            // "Output":"nil" / "Error":null on every tool result.
            string output = result.Output;
            if (string.IsNullOrEmpty(output) || string.Equals(output, "nil", StringComparison.Ordinal))
            {
                output = null;
            }

            return JsonConvert.SerializeObject(
                new LuaResult
                {
                    Success = result.Success,
                    Output = output,
                    Printed = string.IsNullOrEmpty(result.Printed) ? null : result.Printed,
                    Error = string.IsNullOrEmpty(result.Error) ? null : result.Error
                },
                TrimNulls);
        }

        /// <summary>Lua execution outcome for JSON serialization back to the model.</summary>
        public sealed class LuaResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }

            /// <summary>
            /// What the chunk wrote with <c>report()</c> and <c>print()</c>, one line each, the last lines first to
            /// stay when it is long; null when it wrote nothing.
            /// </summary>
            public string Printed { get; set; }

            public string Error { get; set; }
        }

        /// <summary>Abstraction over the concrete Lua host (testable without Unity).</summary>
        public interface ILuaExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken);
        }

        /// <summary>Actor-scoped mutation executor used by the shipped persistent-mod composition.</summary>
        public interface IMutationExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, ActorContext actorContext,
                MutationEnvelope mutationEnvelope, CancellationToken cancellationToken);

            Task<LuaResult> ExecuteAsync(string code, ActorContext actorContext,
                CancellationToken cancellationToken);
        }
    }
}

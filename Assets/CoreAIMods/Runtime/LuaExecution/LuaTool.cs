using System;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using System.Globalization;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI <see cref="AIFunction"/> that runs Lua for the Programmer agent, used in the native tool-calling path instead of fenced Lua blocks.
    /// </summary>
    public sealed class LuaTool
    {
        public const string ExecuteLuaToolName = "execute_lua";

        public const string ExecuteLuaDescription =
            "Execute sandboxed Lua code using only globals exposed by the current game. " +
            "Runtime rule slots are changed through the logic_* API: call logic_list() when unsure, then use " +
            "logic_define('slot_name', function(...) return value end); for example " +
            "logic_define('loot_formula', function(bossMaxHp) return 1000 end). " +
            "Build and edit world objects Roblox-style (Instance.new('Part'), game/workspace, Vector3/CFrame/Color3) - " +
            "call read_skill('Rbx API') for the reference; report() alone is not a scene change. " +
            "Inspect the existing scene with the read-only queries coreai_world_find(pattern), coreai_world_pos(name), and coreai_world_exists(name). " +
            "Full reflection (unity_* scene APIs) is a rarely-needed backup; when a task truly needs it, call read_skill('Full Lua') first. " +
            "Do not hard-code visual recipes; inspect the scene/components first, then use the smallest real API that matches the host. " +
            "Use report(message) to describe the applied change. Do not invent helper globals; " +
            "only call APIs listed by the prompt/tool contract or discovered from the environment. " +
            "Never call invented APIs such as game.rules, game_rules, game.enemies, game.create, game.destroy, or GameObject.Find from Lua.";

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

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
            if (_actorIdentityProvider != null)
            {
                Func<string, string, string, long, CancellationToken, Task<string>> mutationFunc =
                    ExecuteMutationAsync;
                AIFunctionFactoryOptions mutationOptions = new()
                {
                    Name = ExecuteLuaToolName,
                    Description = ExecuteLuaDescription
                };
                return AIFunctionFactory.Create(mutationFunc, mutationOptions);
            }

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
        public async Task<string> ExecuteAsync(
            [Description(
                "Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. Build world objects Roblox-style (Instance.new('Part'), game/workspace) - see read_skill('Rbx API'). Inspect the scene with coreai_world_find(pattern), coreai_world_pos(name), coreai_world_exists(name). Full reflection is a rarely-needed backup - see read_skill('Full Lua'). Return compact JSON/string for diagnostics.")]
            string code,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteCoreAsync(
                code,
                token => _executor.ExecuteAsync(code, token),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Runs an actor-scoped Lua mutation under optimistic concurrency and idempotency checks.</summary>
        public async Task<string> ExecuteMutationAsync(
            [Description("Lua code to execute against the declared mutation target.")]
            string code,
            [Description("Caller-generated idempotency key, unique for this actor and logical operation.")]
            string operation_id,
            [Description("Stable target InstanceId encoded as an unsigned decimal string.")]
            string target_instance_id,
            [Description("Target revision observed before submitting this operation.")]
            long expected_revision,
            CancellationToken cancellationToken = default)
        {
            if (_actorIdentityProvider == null || _mutationExecutor == null)
            {
                return SerializeResult(new LuaResult
                {
                    Success = false,
                    Error = "Actor-scoped execute_lua is not configured."
                });
            }

            if (!ulong.TryParse(target_instance_id, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong targetValue)
                || targetValue == 0UL)
            {
                return SerializeResult(new LuaResult
                {
                    Success = false,
                    Error = "target_instance_id must be a non-zero unsigned decimal InstanceId."
                });
            }

            ActorContext actorContext;
            MutationEnvelope envelope;
            try
            {
                actorContext = _actorIdentityProvider.GetActorContext(_roleId);
                envelope = new MutationEnvelope(
                    actorContext.ActorId,
                    new InstanceId(targetValue),
                    operation_id,
                    expected_revision);
            }
            catch (Exception ex)
            {
                return SerializeResult(new LuaResult { Success = false, Error = ex.Message });
            }

            return await ExecuteCoreAsync(
                code,
                token => _mutationExecutor.ExecuteAsync(code, actorContext, envelope, token),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ExecuteCoreAsync(
            string code,
            Func<CancellationToken, Task<LuaResult>> execute,
            CancellationToken cancellationToken)
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
                LuaResult result = await execute(cancellationToken).ConfigureAwait(false);

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
                    Error = string.IsNullOrEmpty(result.Error) ? null : result.Error
                },
                TrimNulls);
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

        /// <summary>Actor-scoped mutation executor used by the shipped persistent-mod composition.</summary>
        public interface IMutationExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, ActorContext actorContext,
                MutationEnvelope mutationEnvelope, CancellationToken cancellationToken);
        }
    }
}

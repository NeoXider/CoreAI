using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>Why a <c>GenerateTextAsync</c> request produced no text: the stable kind a script and a C# caller can branch on.</summary>
    public enum RbxTextGenerationFailure
    {
        /// <summary>No failure.</summary>
        None = 0,

        /// <summary>The model or its transport failed.</summary>
        ProviderError = 1,

        /// <summary>The request ran out of time.</summary>
        Timeout = 2,

        /// <summary>The model answered nothing.</summary>
        EmptyResponse = 3,

        /// <summary>The answer did not parse against the request's <c>JsonSchema</c>.</summary>
        SchemaMismatch = 4,

        /// <summary>The moderation filter refused the prompt or the answer.</summary>
        Moderated = 5,

        /// <summary>The world's request quota is spent.</summary>
        QuotaExceeded = 6,

        /// <summary>The request was stopped (the script's thread or the world ended).</summary>
        Cancelled = 7
    }

    /// <summary>One <c>GenerateTextAsync</c> request after validation.</summary>
    public sealed class RbxTextGenerationRequest
    {
        /// <summary>The generator's system prompt.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>The player's or script's prompt.</summary>
        public string UserPrompt { get; set; } = "";

        /// <summary>The conversation so far (from the context token), oldest first.</summary>
        public IReadOnlyList<RbxTextGenerationTurn> History { get; set; } = Array.Empty<RbxTextGenerationTurn>();

        /// <summary>Response token cap.</summary>
        public int MaxTokens { get; set; }

        /// <summary>A json-schema.org schema the answer must conform to; empty for free text.</summary>
        public string JsonSchema { get; set; } = "";

        /// <summary>Sampling temperature (already clamped).</summary>
        public float Temperature { get; set; } = RbxTextGenerator.DefaultTemperature;

        /// <summary>Top-p (already clamped).</summary>
        public float TopP { get; set; } = RbxTextGenerator.DefaultTopP;

        /// <summary>Seed; 0 leaves the provider's default.</summary>
        public int Seed { get; set; }

        /// <summary>The mod that asked; empty for an ownerless chunk.</summary>
        public string ModId { get; set; } = "";
    }

    /// <summary>The answer of one request.</summary>
    public sealed class RbxTextGenerationResult
    {
        /// <summary>True when <see cref="GeneratedText"/> holds the answer.</summary>
        public bool Ok { get; set; }

        /// <summary>The generated text (for a schema request, the JSON text).</summary>
        public string GeneratedText { get; set; } = "";

        /// <summary>The model that answered.</summary>
        public string Model { get; set; } = "";

        /// <summary>Why there is no text.</summary>
        public RbxTextGenerationFailure Failure { get; set; }

        /// <summary>A sentence for the script's error; never provider text.</summary>
        public string Message { get; set; } = "";

        /// <summary>A failed result.</summary>
        public static RbxTextGenerationResult Failed(RbxTextGenerationFailure failure, string message)
        {
            return new RbxTextGenerationResult { Ok = false, Failure = failure, Message = message ?? "" };
        }
    }

    /// <summary>
    /// The model behind <c>TextGenerator</c>. The default runs through the host's orchestration service; a test or a
    /// host with its own text endpoint replaces it. Called on the world's thread; it may complete on any thread.
    /// </summary>
    public interface IRbxTextGenerationBackend
    {
        /// <summary>Generates the answer of <paramref name="request"/>; never throws for a model failure.</summary>
        Task<RbxTextGenerationResult> GenerateAsync(RbxTextGenerationRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The default text backend: one history-free, memory-free, tool-free turn of the host-chosen role through
    /// <see cref="IAiOrchestrationService"/>, with the generator's system prompt replacing the role's base prompt and
    /// the conversation of the context token written into the user payload. Uses the typed result path, so a provider
    /// failure is a typed result, never an exception and never the provider's text.
    /// </summary>
    public sealed class OrchestratorTextGenerationBackend : IRbxTextGenerationBackend
    {
        private readonly IAiOrchestrationService _orchestrator;
        private readonly CoreAiLuaAiOptions _options;

        /// <summary>Creates the backend.</summary>
        public OrchestratorTextGenerationBackend(IAiOrchestrationService orchestrator, CoreAiLuaAiOptions options = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _options = options ?? new CoreAiLuaAiOptions();
        }

        /// <inheritdoc />
        public async Task<RbxTextGenerationResult> GenerateAsync(RbxTextGenerationRequest request,
            CancellationToken cancellationToken)
        {
            AiTaskRequest task = BuildTask(request ?? new RbxTextGenerationRequest(), _options);
            task.CallerCancellationToken = cancellationToken;
            LlmCompletionResult result;
            try
            {
                result = _orchestrator is IAiTaskResultService typed && typed.SupportsTaskResults
                    ? await typed.RunTaskResultAsync(task, cancellationToken)
                    : await CollectStreamAsync(task, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.Cancelled, "the request was stopped");
            }
            catch (LlmRequestFailedException failed)
            {
                return FromCode(failed.ErrorCode);
            }
            catch (Exception ex)
            {
                return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.ProviderError,
                    "the model pipeline failed (" + ex.GetType().Name + ")");
            }

            if (result == null)
            {
                return FromCode(LlmErrorCode.EmptyResponse);
            }

            if (!result.Ok)
            {
                return FromCode(result.ErrorCode);
            }

            return new RbxTextGenerationResult
            {
                Ok = true,
                GeneratedText = result.Content ?? "",
                Model = string.IsNullOrWhiteSpace(result.Model) ? "coreai" : result.Model
            };
        }

        /// <summary>The orchestration request one generation sends; public so a test or a host can inspect it.</summary>
        public static AiTaskRequest BuildTask(RbxTextGenerationRequest request, CoreAiLuaAiOptions options)
        {
            CoreAiLuaAiOptions resolved = options ?? new CoreAiLuaAiOptions();
            StringBuilder hint = new(256);
            if (request.History != null && request.History.Count > 0)
            {
                hint.Append("Conversation so far:\n");
                foreach (RbxTextGenerationTurn turn in request.History)
                {
                    hint.Append("User: ").Append(turn.UserPrompt).Append('\n');
                    hint.Append("Assistant: ").Append(turn.GeneratedText).Append('\n');
                }

                hint.Append('\n').Append("User: ");
            }

            hint.Append(request.UserPrompt ?? "");
            string instructions = string.IsNullOrWhiteSpace(request.JsonSchema)
                ? ""
                : "Answer with one JSON value only (no prose, no code fences) that conforms to this JSON Schema:\n" +
                  request.JsonSchema.Trim();
            string modId = (request.ModId ?? "").Trim();
            return new AiTaskRequest
            {
                RoleId = string.IsNullOrWhiteSpace(resolved.TextGeneratorRoleId)
                    ? BuiltInAgentRoleIds.AiNpc
                    : resolved.TextGeneratorRoleId.Trim(),
                RoutingProfileId = (resolved.TextGeneratorRoutingProfileId ?? "").Trim(),
                SystemPrompt = request.SystemPrompt ?? "",
                RequestSystemInstructions = instructions,
                Hint = hint.ToString(),
                MaxOutputTokens = request.MaxTokens > 0 ? request.MaxTokens : (int?)null,
                Temperature = request.Temperature,
                Sampling = new LlmSamplingOptions
                {
                    TopP = request.TopP,
                    Seed = request.Seed != 0 ? request.Seed : (long?)null
                },
                Priority = resolved.TextGeneratorPriority,
                HistoryMode = AiTaskHistoryMode.None,
                MemoryMode = AiTaskMemoryMode.None,
                AllowedToolNames = Array.Empty<string>(),
                SourceTag = modId.Length > 0 ? "Mod:" + modId : "Mod",
                TraceId = "textgen-" + Guid.NewGuid().ToString("N").Substring(0, 12)
            };
        }

        private async Task<LlmCompletionResult> CollectStreamAsync(AiTaskRequest task, CancellationToken token)
        {
            StringBuilder text = new();
            string model = "";
            await foreach (LlmStreamChunk chunk in _orchestrator.RunStreamingAsync(task, token))
            {
                if (chunk == null)
                {
                    continue;
                }

                if (LlmTurnFailure.IsFailure(chunk))
                {
                    return LlmTurnFailure.ToFailedResult(chunk, text.ToString());
                }

                text.Append(chunk.Text ?? "");
                if (!string.IsNullOrEmpty(chunk.Model))
                {
                    model = chunk.Model;
                }

                if (chunk.IsDone)
                {
                    break;
                }
            }

            return new LlmCompletionResult { Ok = true, Content = text.ToString(), Model = model };
        }

        private static RbxTextGenerationResult FromCode(LlmErrorCode code)
        {
            switch (code)
            {
                case LlmErrorCode.Cancelled:
                    return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.Cancelled, "the request was stopped");
                case LlmErrorCode.Timeout:
                    return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.Timeout, "the model did not answer in time");
                case LlmErrorCode.EmptyResponse:
                    return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.EmptyResponse, "the model answered nothing");
                case LlmErrorCode.RateLimited:
                case LlmErrorCode.QuotaExceeded:
                case LlmErrorCode.ClientLimitExceeded:
                    return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.QuotaExceeded,
                        "the model's request limit is reached; try again later");
                default:
                    return RbxTextGenerationResult.Failed(RbxTextGenerationFailure.ProviderError,
                        "the model is unavailable right now (" + code + ")");
            }
        }
    }
}

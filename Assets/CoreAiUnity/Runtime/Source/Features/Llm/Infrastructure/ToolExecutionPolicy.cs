#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Shared tool-call execution policy: duplicate detection, consecutive error tracking,
    /// <see cref="CoreAi.NotifyToolExecuted"/> wrapper.
    /// Used by both <see cref="SmartToolCallingChatClient"/> (non-streaming)
    /// and <see cref="MeaiLlmClient"/> (streaming) to keep behavior consistent.
    /// </summary>
    public sealed class ToolExecutionPolicy
    {
        private readonly IGameLogger _logger;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _roleId;
        private readonly int _maxConsecutiveErrors;

        private int _consecutiveErrors;
        private readonly HashSet<string> _executedSignatures = new();

        public ToolExecutionPolicy(
            IGameLogger logger,
            ICoreAISettings settings,
            IReadOnlyList<ILlmTool> originalTools,
            bool allowDuplicateToolCalls,
            string roleId,
            int maxConsecutiveErrors = 3)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _originalTools = originalTools ?? new List<ILlmTool>();
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _roleId = roleId ?? "Unknown";
            _maxConsecutiveErrors = Math.Max(1, maxConsecutiveErrors);
        }

        /// <summary>Current consecutive error count (for diagnostics/testing).</summary>
        public int ConsecutiveErrors => _consecutiveErrors;

        /// <summary>Whether max consecutive errors threshold has been reached.</summary>
        public bool IsMaxErrorsReached => _consecutiveErrors >= _maxConsecutiveErrors;

        /// <summary>
        /// Reset duplicate signatures and error counter. Call at the start of each
        /// top-level request to allow the same tool to be used across independent requests.
        /// </summary>
        public void Reset()
        {
            _consecutiveErrors = 0;
            _executedSignatures.Clear();
        }

        /// <summary>
        /// Check whether the given tool calls are a duplicate of a previously
        /// executed set. Returns duplicate error messages if blocked, otherwise null.
        /// </summary>
        public List<MEAI.FunctionResultContent> CheckDuplicate(List<MEAI.FunctionCallContent> toolCalls)
        {
            if (_allowDuplicateToolCalls)
            {
                return null;
            }

            // Exclude tools that explicitly allow duplicates
            var toolsToCheck = toolCalls.Where(fc =>
            {
                var originalTool = _originalTools.FirstOrDefault(t => t.Name == fc.Name);
                return originalTool == null || !originalTool.AllowDuplicates;
            }).ToList();

            if (toolsToCheck.Count == 0)
            {
                return null;
            }

            string signature = string.Join("|", toolsToCheck.Select(t =>
                $"{t.Name}:{string.Join(",", t.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? Enumerable.Empty<string>())}"));

            if (string.IsNullOrEmpty(signature) || _executedSignatures.Add(signature))
            {
                return null;
            }

            // Duplicate detected
            _logger.LogWarning(GameLogFeature.Llm,
                $"[ToolPolicy] ⚠ DUPLICATE TOOL CALL DETECTED: {signature}. Rejecting.");

            List<MEAI.FunctionResultContent> results = new();
            foreach (MEAI.FunctionCallContent fc in toolCalls)
            {
                results.Add(new MEAI.FunctionResultContent(fc.CallId,
                    "Error: You just executed this exact same tool call with the exact same arguments on the previous step. " +
                    "Do not repeat identical steps. Proceed to the NEXT step or provide a final text response."));
            }

            RecordFailure();
            return results;
        }

        /// <summary>
        /// Execute a single tool call: resolve AIFunction, invoke, track success/failure,
        /// and send <see cref="CoreAi.NotifyToolExecuted"/>.
        /// </summary>
        public async Task<ToolCallResult> ExecuteSingleAsync(
            MEAI.FunctionCallContent fc,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            MEAI.AIFunction aiFunc = chatOptions?.Tools?.OfType<MEAI.AIFunction>()
                .FirstOrDefault(f => string.Equals(f.Name, fc.Name, StringComparison.Ordinal));

            if (aiFunc == null)
            {
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Tool '{fc.Name}' not found"),
                    Succeeded = false
                };
            }

            try
            {
                MEAI.AIFunctionArguments args = fc.Arguments != null
                    ? new MEAI.AIFunctionArguments(fc.Arguments)
                    : null;
                object result = await aiFunc.InvokeAsync(args, cancellationToken);
                string resultText = result?.ToString() ?? "";
                bool succeeded = !resultText.Contains("\"Success\":false") &&
                                 !resultText.Contains("\"success\":false");

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"[ToolPolicy] {fc.Name}: {(succeeded ? "SUCCESS" : "FAILED")}");
                }

                // Notify subscribers
                try
                {
                    CoreAi.NotifyToolExecuted(_roleId, fc.Name, fc.Arguments, result);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"[ToolPolicy] Notification error for tool '{fc.Name}': {notifyEx.Message}");
                }

                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, resultText),
                    Succeeded = succeeded
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(GameLogFeature.Llm, $"[ToolPolicy] {fc.Name} threw: {ex.Message}");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Error: {ex.Message}"),
                    Succeeded = false
                };
            }
        }

        /// <summary>
        /// Execute a batch of tool calls, tracking cumulative success/failure.
        /// Returns the list of result contents and an aggregate success flag.
        /// </summary>
        public async Task<BatchToolCallResult> ExecuteBatchAsync(
            List<MEAI.FunctionCallContent> toolCalls,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            // 1. Check for duplicates first
            List<MEAI.FunctionResultContent> duplicateResults = CheckDuplicate(toolCalls);
            if (duplicateResults != null)
            {
                return new BatchToolCallResult
                {
                    Results = duplicateResults.Cast<MEAI.AIContent>().ToList(),
                    AnyFailed = true
                };
            }

            // 2. Execute each tool call
            List<MEAI.AIContent> results = new();
            bool anyFailed = false;

            foreach (MEAI.FunctionCallContent fc in toolCalls)
            {
                ToolCallResult r = await ExecuteSingleAsync(fc, chatOptions, cancellationToken);
                results.Add(r.Result);
                if (!r.Succeeded) anyFailed = true;
            }

            // 3. Update error counter
            if (!anyFailed)
            {
                RecordSuccess();
            }
            else
            {
                RecordFailure();
            }

            return new BatchToolCallResult { Results = results, AnyFailed = anyFailed };
        }

        /// <summary>Record that all tools in the current iteration succeeded.</summary>
        public void RecordSuccess()
        {
            _consecutiveErrors = 0;
            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    "[ToolPolicy] ✓ All succeeded, error counter reset to 0");
            }
        }

        /// <summary>Record that at least one tool in the current iteration failed.</summary>
        public void RecordFailure()
        {
            _consecutiveErrors++;
            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"[ToolPolicy] ✗ Some failed, error counter={_consecutiveErrors}/{_maxConsecutiveErrors}");
            }
        }

        /// <summary>Build a terminal error response when max errors reached.</summary>
        public MEAI.ChatResponse BuildMaxErrorsResponse()
        {
            _logger.LogWarning(GameLogFeature.Llm,
                $"[ToolPolicy] ⚠ Max consecutive errors ({_maxConsecutiveErrors}), stopping.");

            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                "{\"error\":\"Agent aborted due to hitting maximum consecutive tool processing errors.\"}"))
            {
                FinishReason = MEAI.ChatFinishReason.Stop
            };
        }

        /// <summary>Result of a single tool call execution.</summary>
        public struct ToolCallResult
        {
            public MEAI.FunctionResultContent Result;
            public bool Succeeded;
        }

        /// <summary>Result of batch tool call execution.</summary>
        public struct BatchToolCallResult
        {
            public List<MEAI.AIContent> Results;
            public bool AnyFailed;
        }
    }
}
#endif

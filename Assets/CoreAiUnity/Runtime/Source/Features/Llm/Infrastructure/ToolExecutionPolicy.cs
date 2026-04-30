#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using MessagePipe;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;

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
        private readonly string _traceId;
        private readonly int _maxConsecutiveErrors;

        private int _consecutiveErrors;
        private readonly HashSet<string> _executedSignatures = new();
        private readonly List<LlmToolCallTrace> _executedTraces = new();

        public ToolExecutionPolicy(
            IGameLogger logger,
            ICoreAISettings settings,
            IReadOnlyList<ILlmTool> originalTools,
            bool allowDuplicateToolCalls,
            string roleId,
            int maxConsecutiveErrors = 3,
            string traceId = "")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _originalTools = originalTools ?? new List<ILlmTool>();
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _roleId = roleId ?? "Unknown";
            _traceId = traceId ?? "";
            _maxConsecutiveErrors = Math.Max(1, maxConsecutiveErrors);
        }

        /// <summary>Current consecutive error count (for diagnostics/testing).</summary>
        public int ConsecutiveErrors => _consecutiveErrors;

        /// <summary>Whether max consecutive errors threshold has been reached.</summary>
        public bool IsMaxErrorsReached => _consecutiveErrors >= _maxConsecutiveErrors;

        /// <summary>
        /// Snapshot of every tool call observed during this request lifetime
        /// (native, text-extracted, duplicate, missing). Order preserved.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> ExecutedTraces => _executedTraces;

        /// <summary>
        /// Reset duplicate signatures, error counter, and trace log. Call at the start of each
        /// top-level request to allow the same tool to be used across independent requests.
        /// </summary>
        public void Reset()
        {
            _consecutiveErrors = 0;
            _executedSignatures.Clear();
            _executedTraces.Clear();
        }

        /// <summary>
        /// Record a synthetic trace entry for a tool call that was not actually invoked
        /// (e.g., text-extracted JSON when no AIFunction is bound, or duplicate suppressed).
        /// </summary>
        public void RecordSyntheticTrace(string toolName, bool success, double durationMs, string source)
        {
            _executedTraces.Add(new LlmToolCallTrace(toolName, success, durationMs, source));
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
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "duplicate"));
            }

            RecordFailure();
            return results;
        }

        /// <summary>
        /// Tries to repair a tool call where the model used the wrong casing or a slightly
        /// different name. Returns the repaired FunctionCallContent if a case-insensitive match
        /// is found among registered tools; null if the name cannot be resolved at all.
        /// Mirrors Kilo's <c>experimental_repairToolCall</c> hook.
        /// </summary>
        public MEAI.FunctionCallContent TryRepairToolName(MEAI.FunctionCallContent fc)
        {
            if (fc == null) return null;

            // If no tools registered (e.g. tools only in ChatOptions), skip repair — let AIFunction lookup decide
            if (_originalTools.Count == 0) return fc;

            // Already an exact match — nothing to repair
            if (_originalTools.Any(t => string.Equals(t.Name, fc.Name, StringComparison.Ordinal)))
                return fc;

            // Case-insensitive fallback
            ILlmTool match = _originalTools.FirstOrDefault(
                t => string.Equals(t.Name, fc.Name, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"[ToolPolicy] ⚡ Repaired tool name casing: '{fc.Name}' → '{match.Name}'");
                return new MEAI.FunctionCallContent(fc.CallId, match.Name, fc.Arguments);
            }

            _logger.LogWarning(GameLogFeature.Llm,
                $"[ToolPolicy] ✖ Unknown tool name: '{fc.Name}' — no repair found. Available: [{string.Join(", ", _originalTools.Select(t => t.Name))}]");
            return null;
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
            // === Kilo-style repair: fix wrong casing before lookup ===
            MEAI.FunctionCallContent repairedFc = TryRepairToolName(fc);
            if (repairedFc == null)
            {
                // Name not found even after case-insensitive search
                RecordSyntheticTrace(fc.Name ?? "", false, 0d, "unknown-tool");
                LogCallLine(fc, false, 0d, $"Tool '{fc.Name}' not found (no repair match)");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId,
                        $"Error: Unknown tool '{fc.Name}'. Available tools: [{string.Join(", ", _originalTools.Select(t => t.Name))}]"),
                    Succeeded = false
                };
            }
            fc = repairedFc;

            MEAI.AIFunction aiFunc = chatOptions?.Tools?.OfType<MEAI.AIFunction>()
                .FirstOrDefault(f => string.Equals(f.Name, fc.Name, StringComparison.Ordinal));

            if (aiFunc == null)
            {
                PublishFailed(BuildInfo(fc), $"Tool '{fc.Name}' not found", 0d);
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "missing"));
                LogCallLine(fc, false, 0d, $"Tool '{fc.Name}' not found");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Tool '{fc.Name}' not found"),
                    Succeeded = false
                };
            }

            try
            {
                LlmToolCallInfo info = BuildInfo(fc);
                PublishStarted(info);
                Stopwatch sw = Stopwatch.StartNew();
                MEAI.AIFunctionArguments args = fc.Arguments != null
                    ? new MEAI.AIFunctionArguments(fc.Arguments)
                    : null;
                object result = await aiFunc.InvokeAsync(args, cancellationToken);
                sw.Stop();
                string resultText = result?.ToString() ?? "";
                bool succeeded = !resultText.Contains("\"Success\":false") &&
                                 !resultText.Contains("\"success\":false");

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"[ToolPolicy] {fc.Name}: {(succeeded ? "SUCCESS" : "FAILED")}");
                }

                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (succeeded)
                {
                    PublishCompleted(info, SafeResultJson(resultText), elapsedMs);
                }
                else
                {
                    PublishFailed(info, SafeResultJson(resultText), elapsedMs);
                }

                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", succeeded, elapsedMs, "native"));
                LogCallLine(fc, succeeded, elapsedMs, resultText);

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
                PublishFailed(BuildInfo(fc), ex.Message, 0d);
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "native"));
                LogCallLine(fc, false, 0d, $"threw: {ex.Message}");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Error: {ex.Message}"),
                    Succeeded = false
                };
            }
        }

        /// <summary>
        /// Standalone diagnostic line emitted after every tool call (regardless of source) so operators
        /// can see exactly which tool ran with which args and whether it succeeded. Honours the
        /// <see cref="ICoreAISettings.LogToolCalls"/> / <c>LogToolCallArguments</c> / <c>LogToolCallResults</c>
        /// switches independently of the streaming-step trace.
        /// </summary>
        private void LogCallLine(MEAI.FunctionCallContent fc, bool succeeded, double durationMs, string resultText)
        {
            if (!_settings.LogToolCalls)
            {
                return;
            }

            string status = succeeded ? "OK" : "FAIL";
            string args = "";
            if (_settings.LogToolCallArguments && fc?.Arguments != null && fc.Arguments.Count > 0)
            {
                try { args = " args=" + JsonConvert.SerializeObject(fc.Arguments); } catch { args = ""; }
            }

            string preview = "";
            if (_settings.LogToolCallResults && !string.IsNullOrEmpty(resultText))
            {
                const int max = 240;
                string trimmed = resultText.Length <= max ? resultText : resultText.Substring(0, max) + "…";
                preview = " result=" + trimmed.Replace('\n', ' ');
            }

            string traceTag = string.IsNullOrEmpty(_traceId) ? "" : $"traceId={_traceId} ";
            _logger.LogInfo(GameLogFeature.Llm,
                $"[ToolCall] {traceTag}role={_roleId} tool={fc?.Name ?? "?"} status={status} dur={durationMs:F0}ms{args}{preview}");
        }

        private LlmToolCallInfo BuildInfo(MEAI.FunctionCallContent fc)
        {
            return new LlmToolCallInfo(
                _traceId,
                _roleId,
                fc?.CallId ?? "",
                fc?.Name ?? "",
                SafeArgumentsJson(fc));
        }

        private string SafeArgumentsJson(MEAI.FunctionCallContent fc)
        {
            if (!_settings.LogToolCallArguments || fc?.Arguments == null)
            {
                return "";
            }

            try
            {
                return JsonConvert.SerializeObject(fc.Arguments);
            }
            catch
            {
                return "";
            }
        }

        private string SafeResultJson(string result)
        {
            if (!_settings.LogToolCallResults || string.IsNullOrEmpty(result))
            {
                return "";
            }

            const int max = 2000;
            return result.Length <= max ? result : result.Substring(0, max);
        }

        private void PublishStarted(LlmToolCallInfo info)
        {
            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallStarted>()
                    .Publish(new LlmToolCallStarted(info));
            }
            catch
            {
            }
        }

        private void PublishCompleted(LlmToolCallInfo info, string resultJson, double durationMs)
        {
            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallCompleted>()
                    .Publish(new LlmToolCallCompleted(info, resultJson, durationMs));
            }
            catch
            {
            }
        }

        private void PublishFailed(LlmToolCallInfo info, string error, double durationMs)
        {
            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallFailed>()
                    .Publish(new LlmToolCallFailed(info, error, durationMs));
            }
            catch
            {
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

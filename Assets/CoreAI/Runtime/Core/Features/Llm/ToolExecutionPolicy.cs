#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI;
using CoreAI.Logging;
using CoreAI.Messaging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Shared tool-call execution policy: duplicate detection, consecutive error tracking,
    /// <see cref="IToolExecutionNotifier"/> wrapper.
    /// Used by both <see cref="SmartToolCallingChatClient"/> (non-streaming)
    /// and the streaming path to keep behavior consistent.
    /// </summary>
    public sealed class ToolExecutionPolicy
    {
        private const string EmptyToolResultPayload =
            "{\"Success\":true,\"Message\":\"Tool completed without an explicit result payload.\"}";

        private readonly ILog _logger;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _roleId;
        private readonly string _traceId;
        private readonly int _maxConsecutiveErrors;
        private readonly IToolCallEventPublisher _eventPublisher;
        private readonly IToolExecutionNotifier _notifier;

        private static long _toolNameRepairCount;

        private int _consecutiveErrors;
        private readonly HashSet<string> _executedSignatures = new();
        private readonly List<LlmToolCallTrace> _executedTraces = new();

        public ToolExecutionPolicy(
            ILog logger,
            ICoreAISettings settings,
            IReadOnlyList<ILlmTool> originalTools,
            bool allowDuplicateToolCalls,
            string roleId,
            int maxConsecutiveErrors = 3,
            string traceId = "",
            IToolCallEventPublisher eventPublisher = null,
            IToolExecutionNotifier notifier = null)
        {
            _logger = logger ?? NullLog.Instance;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _originalTools = originalTools ?? new List<ILlmTool>();
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _roleId = roleId ?? "Unknown";
            _traceId = traceId ?? "";
            _maxConsecutiveErrors = Math.Max(1, maxConsecutiveErrors);
            _eventPublisher = eventPublisher ?? NullToolCallEventPublisher.Instance;
            _notifier = notifier ?? NullToolExecutionNotifier.Instance;
        }

        /// <summary>
        /// Process-wide count of tool calls whose name had to be repaired by
        /// <see cref="TryRepairToolName"/> (e.g. wrong casing emitted by the model).
        /// A steadily climbing value signals systemic prompt degradation: the model is no longer
        /// reproducing exact tool names. Diagnostics only — surface it on dashboards alongside
        /// <see cref="RateLimiterMetrics"/>. Reset with <see cref="ResetToolNameRepairCount"/>.
        /// </summary>
        public static long ToolNameRepairCount => Interlocked.Read(ref _toolNameRepairCount);

        /// <summary>Resets <see cref="ToolNameRepairCount"/> to zero (tests / diagnostics sessions).</summary>
        public static void ResetToolNameRepairCount()
        {
            Interlocked.Exchange(ref _toolNameRepairCount, 0);
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
        public void RecordSyntheticTrace(string toolName, bool success, double durationMs, string source,
            string detail = "")
        {
            _executedTraces.Add(new LlmToolCallTrace(toolName, success, durationMs, source, detail));
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
            List<MEAI.FunctionCallContent> toolsToCheck = toolCalls.Where(fc =>
            {
                ILlmTool match = _originalTools.FirstOrDefault(t =>
                    string.Equals(t.Name, fc.Name, StringComparison.OrdinalIgnoreCase));
                return match == null || !match.AllowDuplicates;
            }).ToList();

            if (toolsToCheck.Count == 0)
            {
                return null;
            }

            string batchSig = string.Join("|", toolsToCheck
                .OrderBy(fc => fc.Name, StringComparer.OrdinalIgnoreCase)
                .Select(fc =>
                {
                    string argsSig = "";
                    try
                    {
                        argsSig = CanonicalizeArguments(fc.Arguments);
                    }
                    catch
                    {
                        /* swallow */
                    }

                    return $"{fc.Name}({argsSig})";
                }));

            if (!_executedSignatures.Add(batchSig))
            {
                List<MEAI.FunctionResultContent> errs = new();
                foreach (MEAI.FunctionCallContent fc in toolCalls)
                {
                    string duplicate = $"Duplicate tool call '{fc.Name}' with same arguments - skipped.";
                    _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "duplicate", duplicate));
                    errs.Add(new MEAI.FunctionResultContent(fc.CallId, duplicate));
                }

                return errs;
            }

            return null;
        }

        /// <summary>
        /// Builds an order-independent signature of a tool call's arguments by serializing from a
        /// key-sorted projection. The model can re-emit an identical call with a different key order
        /// (streamed vs text-extracted reconstructions enumerate the dictionary differently); without
        /// sorting, that produced a different signature and slipped past the duplicate guard. Sorting the
        /// top-level keys matches the existing OrderBy on tool name so semantically identical calls collide.
        /// </summary>
        private static string CanonicalizeArguments(IDictionary<string, object> arguments)
        {
            if (arguments == null || arguments.Count == 0)
            {
                return "";
            }

            SortedDictionary<string, object> sorted = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> kv in arguments)
            {
                sorted[kv.Key] = kv.Value;
            }

            return JsonConvert.SerializeObject(sorted);
        }

        /// <summary>
        /// Try to repair the tool name casing. Returns a new <see cref="MEAI.FunctionCallContent"/>
        /// with the corrected name, or null if the tool is genuinely unknown.
        /// </summary>
        public MEAI.FunctionCallContent TryRepairToolName(MEAI.FunctionCallContent fc)
        {
            if (fc == null)
            {
                return null;
            }

            if (_originalTools == null || _originalTools.Count == 0)
            {
                return fc;
            }

            if (_originalTools.Any(t => string.Equals(t.Name, fc.Name, StringComparison.Ordinal)))
            {
                return fc;
            }

            // Case-insensitive fallback
            ILlmTool match =
                _originalTools.FirstOrDefault(t => string.Equals(t.Name, fc.Name, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                Interlocked.Increment(ref _toolNameRepairCount);
                _logger.Warn(
                    $"[ToolPolicy] Repaired tool name casing: '{fc.Name}' -> '{match.Name}'", LogTag.Llm);
                return new MEAI.FunctionCallContent(fc.CallId, match.Name, fc.Arguments);
            }

            _logger.Warn(
                $"[ToolPolicy] Unknown tool name: '{fc.Name}' - no repair found. Available: [{string.Join(", ", _originalTools.Select(t => t.Name))}]",
                LogTag.Llm);
            return null;
        }

        /// <summary>
        /// Execute a single tool call: resolve AIFunction, invoke, track success/failure,
        /// and send <see cref="IToolExecutionNotifier.NotifyToolExecuted"/>.
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
                string unknown =
                    $"Error: Unknown tool '{fc.Name}'. Available tools: [{string.Join(", ", _originalTools.Select(t => t.Name))}]";
                RecordSyntheticTrace(fc.Name ?? "", false, 0d, "unknown-tool", unknown);
                LogCallLine(fc, false, 0d, $"Tool '{fc.Name}' not found (no repair match)");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, unknown),
                    Succeeded = false
                };
            }

            fc = repairedFc;

            // === Parse-error guard: never invoke a tool with malformed/truncated argument JSON ===
            // The streaming accumulator surfaces unparseable tool-call arguments by injecting a
            // ParseErrorKey marker instead of dropping them. Such args are bogus, so short-circuit
            // here (before invoking the real tool) and ask the model to resend complete JSON.
            if (HasParseErrorMarker(fc.Arguments))
            {
                string parseError =
                    $"Error: Tool '{fc.Name}' arguments JSON was truncated or malformed and could not be parsed. " +
                    "Retry the same tool call and emit the complete, valid JSON arguments object.";
                _eventPublisher.PublishFailed(BuildInfo(fc), parseError, 0d);
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "parse-error", parseError));
                LogCallLine(fc, false, 0d, parseError);
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, parseError),
                    Succeeded = false
                };
            }

            MEAI.AIFunction aiFunc = chatOptions?.Tools?.OfType<MEAI.AIFunction>()
                .FirstOrDefault(f => string.Equals(f.Name, fc.Name, StringComparison.Ordinal));

            if (aiFunc == null)
            {
                string missing = $"Tool '{fc.Name}' not found";
                _eventPublisher.PublishFailed(BuildInfo(fc), missing, 0d);
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "missing", missing));
                LogCallLine(fc, false, 0d, missing);
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, missing),
                    Succeeded = false
                };
            }

            try
            {
                LlmToolCallInfo info = BuildInfo(fc);
                _eventPublisher.PublishStarted(info);
                Stopwatch sw = Stopwatch.StartNew();

                string validationError = ValidateRequiredArguments(fc);
                if (!string.IsNullOrEmpty(validationError))
                {
                    sw.Stop();
                    _logger.Warn($"[ToolPolicy] {fc.Name} rejected: {validationError}", LogTag.Llm);
                    _eventPublisher.PublishFailed(info, validationError, sw.Elapsed.TotalMilliseconds);
                    _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, sw.Elapsed.TotalMilliseconds,
                        "schema-validation", validationError));
                    LogCallLine(fc, false, sw.Elapsed.TotalMilliseconds, validationError);
                    return new ToolCallResult
                    {
                        Result = new MEAI.FunctionResultContent(fc.CallId, validationError),
                        Succeeded = false
                    };
                }

                MEAI.AIFunctionArguments args = null;
                if (fc.Arguments != null)
                {
                    // MEAI's AIFunctionFactory cannot convert Newtonsoft JObject/JArray to CLR
                    // chokepoint for ALL tool calls (native, text-extracted, function-call syntax).
                    Dictionary<string, object> normalized = new(fc.Arguments);
                    foreach (string key in new List<string>(normalized.Keys))
                    {
                        if (normalized[key] is JObject jo)
                        {
                            normalized[key] = jo.ToString(Formatting.None);
                        }
                        else if (normalized[key] is JArray ja)
                        {
                            normalized[key] = ja.ToString(Formatting.None);
                        }
                    }

                    args = new MEAI.AIFunctionArguments(normalized);
                }

                ILlmAsyncMarshaler marshaler =
                    _settings.ToolInvocationMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;

                // === Per-tool timeout: wrap cancellation token ===
                int toolTimeoutMs = _settings.DefaultToolTimeoutMs;
                object result;
                if (toolTimeoutMs > 0)
                {
                    using CancellationTokenSource cts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(toolTimeoutMs);
                    try
                    {
                        result = await marshaler
                            .InvokeAsync<object>(
                                async () =>
                                    await aiFunc.InvokeAsync(args, cts.Token).ConfigureAwait(false),
                                cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Tool-level timeout fired, not outer cancellation
                        sw.Stop();
                        string timeoutMsg = $"Error: Tool '{fc.Name}' timed out after {toolTimeoutMs}ms";
                        _logger.Warn($"[ToolPolicy] Timeout: {timeoutMsg}", LogTag.Llm);
                        _eventPublisher.PublishFailed(info, timeoutMsg, sw.Elapsed.TotalMilliseconds);
                        _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, sw.Elapsed.TotalMilliseconds,
                            "timeout", timeoutMsg));
                        LogCallLine(fc, false, sw.Elapsed.TotalMilliseconds, timeoutMsg);
                        return new ToolCallResult
                        {
                            Result = new MEAI.FunctionResultContent(fc.CallId, timeoutMsg),
                            Succeeded = false
                        };
                    }
                }
                else
                {
                    result = await marshaler
                        .InvokeAsync<object>(
                            async () =>
                                await aiFunc.InvokeAsync(args, cancellationToken).ConfigureAwait(false),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                sw.Stop();
                string resultText = NormalizeToolResultText(result);

                // === Tool result truncation ===
                int maxResultChars = _settings.MaxToolResultChars;
                if (maxResultChars > 0 && resultText.Length > maxResultChars)
                {
                    int originalLen = resultText.Length;
                    resultText = resultText.Substring(0, maxResultChars) +
                                 $"\n...[truncated: {originalLen} chars total -> {maxResultChars} shown]";
                    _logger.Info(
                        $"[ToolPolicy] Tool '{fc.Name}' result truncated: {originalLen} -> {maxResultChars} chars",
                        LogTag.Llm);
                }

                bool succeeded = IsToolResultSuccess(resultText);

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.Info(
                        $"[ToolPolicy] {fc.Name}: {(succeeded ? "SUCCESS" : "FAILED")}", LogTag.Llm);
                }

                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (succeeded)
                {
                    _eventPublisher.PublishCompleted(info, SafeResultJson(resultText), elapsedMs);
                }
                else
                {
                    _eventPublisher.PublishFailed(info, SafeResultJson(resultText), elapsedMs);
                }

                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", succeeded, elapsedMs, "native",
                    resultText));
                LogCallLine(fc, succeeded, elapsedMs, resultText);

                // Notify subscribers
                try
                {
                    _notifier.NotifyToolExecuted(_roleId, fc.Name, fc.Arguments, result);
                }
                catch (Exception notifyEx)
                {
                    _logger.Warn(
                        $"[ToolPolicy] Notification error for tool '{fc.Name}': {notifyEx.Message}", LogTag.Llm);
                }

                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, resultText),
                    Succeeded = succeeded
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"[ToolPolicy] {fc.Name} threw: {ex.Message}", LogTag.Llm);
                _eventPublisher.PublishFailed(BuildInfo(fc), ex.Message, 0d);
                _executedTraces.Add(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "native", ex.Message));
                LogCallLine(fc, false, 0d, $"threw: {ex.Message}");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Error: {ex.Message}"),
                    Succeeded = false
                };
            }
        }

        private static string NormalizeToolResultText(object result)
        {
            string resultText = result?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(resultText) ? EmptyToolResultPayload : resultText;
        }

        /// <summary>
        /// Detects whether an arguments dictionary carries the streaming
        /// <see cref="ToolCallArgumentMarkers.ParseErrorKey"/> marker (boolean <c>true</c>), meaning the
        /// originating tool-call argument JSON was malformed/truncated and must not be executed.
        /// </summary>
        private static bool HasParseErrorMarker(IDictionary<string, object> arguments)
        {
            if (arguments == null ||
                !arguments.TryGetValue(ToolCallArgumentMarkers.ParseErrorKey, out object value))
            {
                return false;
            }

            return value switch
            {
                bool flag => flag,
                JValue jv => jv.Type == JTokenType.Boolean && jv.Value<bool>(),
                _ => false
            };
        }

        private string ValidateRequiredArguments(MEAI.FunctionCallContent fc)
        {
            ILlmTool tool = _originalTools.FirstOrDefault(t =>
                string.Equals(t.Name, fc?.Name, StringComparison.Ordinal));
            if (tool == null || string.IsNullOrWhiteSpace(tool.ParametersSchema) ||
                tool.ParametersSchema.Trim() == "{}")
            {
                return "";
            }

            List<string> required = ReadRequiredParameters(tool.ParametersSchema);
            if (required.Count == 0)
            {
                return "";
            }

            List<string> missing = new();
            foreach (string name in required)
            {
                if (fc?.Arguments == null || !fc.Arguments.TryGetValue(name, out object value) ||
                    IsMissingArgumentValue(value))
                {
                    missing.Add(name);
                }
            }

            if (missing.Count == 0)
            {
                return "";
            }

            string schema = CompactSchema(tool.ParametersSchema, 1200);
            return
                $"Error: Tool '{tool.Name}' is missing required argument(s): {string.Join(", ", missing)}. " +
                $"Retry the same tool call with JSON arguments matching this schema: {schema}";
        }

        private static List<string> ReadRequiredParameters(string schema)
        {
            try
            {
                JObject root = JObject.Parse(schema);
                JArray required = root["required"] as JArray;
                if (required == null)
                {
                    return new List<string>();
                }

                List<string> result = new();
                foreach (JToken token in required)
                {
                    string value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value.Trim());
                    }
                }

                return result;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static bool IsMissingArgumentValue(object value)
        {
            if (value == null)
            {
                return true;
            }

            if (value is string text)
            {
                return string.IsNullOrWhiteSpace(text);
            }

            if (value is JValue jValue)
            {
                if (jValue.Type == JTokenType.Null || jValue.Type == JTokenType.Undefined)
                {
                    return true;
                }

                if (jValue.Type == JTokenType.String)
                {
                    return string.IsNullOrWhiteSpace(jValue.Value<string>());
                }
            }

            return false;
        }

        private static string CompactSchema(string schema, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return "{}";
            }

            string compact = schema.Trim().Replace("\r", "").Replace("\n", "");
            return compact.Length <= maxChars ? compact : compact.Substring(0, maxChars) + "...";
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
                try
                {
                    args = " args=" + JsonConvert.SerializeObject(fc.Arguments);
                }
                catch
                {
                    args = "";
                }
            }

            string preview = "";
            if (_settings.LogToolCallResults && !string.IsNullOrEmpty(resultText))
            {
                const int max = 240;
                string trimmed = resultText.Length <= max ? resultText : resultText.Substring(0, max) + "...";
                preview = " result=" + trimmed.Replace('\n', ' ');
            }

            string traceTag = string.IsNullOrEmpty(_traceId) ? "" : $"traceId={_traceId} ";
            _logger.Info(
                $"[ToolCall] {traceTag}role={_roleId} tool={fc?.Name ?? "?"} status={status} dur={durationMs:F0}ms{args}{preview}",
                LogTag.Llm);
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
                    AnyFailed = true,
                    AllFailed = true
                };
            }

            // 2. Execute each tool call
            List<MEAI.AIContent> results = new();
            bool anyFailed = false;
            bool allFailed = true;

            foreach (MEAI.FunctionCallContent fc in toolCalls)
            {
                ToolCallResult r = await ExecuteSingleAsync(fc, chatOptions, cancellationToken).ConfigureAwait(false);
                results.Add(r.Result);
                if (!r.Succeeded)
                {
                    anyFailed = true;
                }
                else
                {
                    allFailed = false;
                }
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

            return new BatchToolCallResult
            {
                Results = results,
                AnyFailed = anyFailed,
                AllFailed = anyFailed && allFailed
            };
        }

        /// <summary>Record that all tools in the current iteration succeeded.</summary>
        public void RecordSuccess()
        {
            _consecutiveErrors = 0;
            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.Info(
                    "[ToolPolicy] All succeeded, error counter reset to 0", LogTag.Llm);
            }
        }

        /// <summary>Record that at least one tool in the current iteration failed.</summary>
        public void RecordFailure()
        {
            _consecutiveErrors++;
            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.Info(
                    $"[ToolPolicy] Some failed, error counter={_consecutiveErrors}/{_maxConsecutiveErrors}",
                    LogTag.Llm);
            }
        }

        /// <summary>Build a terminal error response when max errors reached.</summary>
        public MEAI.ChatResponse BuildMaxErrorsResponse()
        {
            _logger.Warn(
                $"[ToolPolicy] Max consecutive errors ({_maxConsecutiveErrors}) reached, stopping.", LogTag.Llm);

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

            /// <summary>
            /// True when every tool call in the batch failed (including duplicate-suppressed batches).
            /// Used by <see cref="SmartToolCallingChatClient"/> to mark the iteration's messages as
            /// pure error feedback that can be dropped from history once a later retry succeeds.
            /// </summary>
            public bool AllFailed;
        }

        /// <summary>
        /// Determines whether a tool result indicates success. Uses proper JSON parsing
        /// to check for a top-level "Success" or "success" property set to false.
        /// Falls back to a string heuristic when the result is not valid JSON.
        /// </summary>
        internal static bool IsToolResultSuccess(string resultText)
        {
            if (string.IsNullOrEmpty(resultText))
            {
                return true; // empty result is not a failure signal
            }

            // Fast path: if the text doesn't contain the word at all, it's a success.
            if (!resultText.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Attempt structured JSON parse for reliable detection.
            try
            {
                JObject json = JObject.Parse(resultText);
                JToken token =
                    json["Success"] ?? json["success"] ?? json["SUCCESS"];
                if (token != null && token.Type == JTokenType.Boolean)
                {
                    return (bool)token;
                }

                return true;
            }
            catch
            {
                // This preserves backward compatibility for tools that return plain text.
                return !resultText.Contains("\"Success\":false") &&
                       !resultText.Contains("\"success\":false");
            }
        }
    }
}
#endif

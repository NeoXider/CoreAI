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

        /// <summary>
        /// Guards <see cref="_executedTraces"/> appends. Under concurrent batch execution
        /// (<see cref="ICoreAISettings.MaxParallelToolCalls"/> &gt; 1) several <see cref="ExecuteSingleAsync"/>
        /// invocations may complete on different threads and add traces simultaneously; a plain
        /// <see cref="List{T}"/> is not safe for concurrent <c>Add</c>. Trace ordering is by completion time,
        /// which is intentionally diagnostic-only and independent of the order-preserving result collation.
        /// </summary>
        private readonly object _traceLock = new();

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
            AddTrace(new LlmToolCallTrace(toolName, success, durationMs, source, detail));
        }

        /// <summary>
        /// Thread-safe append to <see cref="_executedTraces"/>. Used by every trace site so concurrent
        /// <see cref="ExecuteSingleAsync"/> completions cannot corrupt the underlying list.
        /// </summary>
        private void AddTrace(LlmToolCallTrace trace)
        {
            lock (_traceLock)
            {
                _executedTraces.Add(trace);
            }
        }

        /// <summary>
        /// Check whether the given tool calls contain duplicate slots. Returns duplicate
        /// error messages for the suppressed slots if any were found, otherwise null.
        /// </summary>
        public List<MEAI.FunctionResultContent> CheckDuplicate(List<MEAI.FunctionCallContent> toolCalls)
        {
            DuplicatePlan plan = BuildDuplicatePlan(toolCalls);
            if (!plan.HasDuplicates)
            {
                return null;
            }

            List<MEAI.FunctionResultContent> errs = new();
            for (int i = 0; i < plan.IndexedResults.Length; i++)
            {
                if (plan.IsDuplicateIndex[i])
                {
                    errs.Add(plan.IndexedResults[i].Result);
                }
            }

            return errs;
        }

        private sealed class DuplicatePlan
        {
            public ToolCallResult[] IndexedResults = Array.Empty<ToolCallResult>();
            public bool[] IsDuplicateIndex = Array.Empty<bool>();
            public bool HasDuplicates;
            public bool HasExecutable;
        }

        private DuplicatePlan BuildDuplicatePlan(List<MEAI.FunctionCallContent> toolCalls)
        {
            DuplicatePlan plan = new()
            {
                IndexedResults = new ToolCallResult[toolCalls?.Count ?? 0],
                IsDuplicateIndex = new bool[toolCalls?.Count ?? 0],
                HasExecutable = toolCalls != null && toolCalls.Count > 0
            };

            if (_allowDuplicateToolCalls || toolCalls == null || toolCalls.Count == 0)
            {
                return plan;
            }

            string[] signatures = new string[toolCalls.Count];
            List<string> reducedSignatures = new();
            for (int i = 0; i < toolCalls.Count; i++)
            {
                if (TryBuildDuplicateSignature(toolCalls[i], out string signature))
                {
                    signatures[i] = signature;
                    reducedSignatures.Add(signature);
                }
            }

            if (reducedSignatures.Count == 0)
            {
                return plan;
            }

            string batchSig = string.Join("|", reducedSignatures.OrderBy(s => s, StringComparer.Ordinal));
            if (!_executedSignatures.Add(batchSig))
            {
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (signatures[i] != null)
                    {
                        MarkDuplicate(plan, i, toolCalls[i]);
                    }
                }

                return plan;
            }

            HashSet<string> seenInBatch = new(StringComparer.Ordinal);
            for (int i = 0; i < toolCalls.Count; i++)
            {
                string signature = signatures[i];
                if (signature == null)
                {
                    continue;
                }

                if (!seenInBatch.Add(signature))
                {
                    MarkDuplicate(plan, i, toolCalls[i]);
                }
            }

            return plan;
        }

        private void MarkDuplicate(DuplicatePlan plan, int index, MEAI.FunctionCallContent fc)
        {
            string duplicate = $"Duplicate tool call '{fc.Name}' with same arguments - skipped.";
            AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "duplicate", duplicate));
            plan.IndexedResults[index] = new ToolCallResult
            {
                Result = new MEAI.FunctionResultContent(fc.CallId, duplicate),
                Succeeded = false
            };
            plan.IsDuplicateIndex[index] = true;
            plan.HasDuplicates = true;
            plan.HasExecutable = plan.IsDuplicateIndex.Any(isDuplicate => !isDuplicate);
        }

        private bool TryBuildDuplicateSignature(MEAI.FunctionCallContent fc, out string signature)
        {
            signature = null;
            string canonicalName = GetCanonicalToolName(fc?.Name, out ILlmTool match, out bool ambiguous);
            if (canonicalName == null)
            {
                canonicalName = fc?.Name ?? "";
            }

            if (match != null && match.AllowDuplicates)
            {
                return false;
            }

            string argsSig = "";
            try
            {
                argsSig = CanonicalizeArguments(fc?.Arguments);
            }
            catch
            {
                /* swallow */
            }

            signature = $"{canonicalName}({argsSig})";
            return true;
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

            string canonicalName = GetCanonicalToolName(fc.Name, out ILlmTool match, out bool ambiguous);
            if (canonicalName != null && string.Equals(canonicalName, fc.Name, StringComparison.Ordinal))
            {
                return fc;
            }

            if (ambiguous)
            {
                _logger.Warn(
                    $"[ToolPolicy] Unknown tool name: '{fc.Name}' is ambiguous under case-insensitive repair. Available: [{string.Join(", ", _originalTools.Select(t => t.Name))}]",
                    LogTag.Llm);
                return null;
            }

            if (canonicalName != null)
            {
                Interlocked.Increment(ref _toolNameRepairCount);
                _logger.Warn(
                    $"[ToolPolicy] Repaired tool name casing: '{fc.Name}' -> '{canonicalName}'", LogTag.Llm);
                return new MEAI.FunctionCallContent(fc.CallId, canonicalName, fc.Arguments);
            }

            _logger.Warn(
                $"[ToolPolicy] Unknown tool name: '{fc.Name}' - no repair found. Available: [{string.Join(", ", _originalTools.Select(t => t.Name))}]",
                LogTag.Llm);
            return null;
        }

        private string GetCanonicalToolName(string name, out ILlmTool match, out bool ambiguous)
        {
            match = null;
            ambiguous = false;

            if (_originalTools == null || _originalTools.Count == 0)
            {
                return name;
            }

            match = _originalTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            if (match != null)
            {
                return match.Name;
            }

            List<ILlmTool> matches = _originalTools
                .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            if (matches.Count == 1)
            {
                match = matches[0];
                return match.Name;
            }

            ambiguous = matches.Count > 1;
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
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "parse-error", parseError));
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
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "missing", missing));
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
                    AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, sw.Elapsed.TotalMilliseconds,
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
                        AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, sw.Elapsed.TotalMilliseconds,
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
                bool succeeded = IsToolResultSuccess(resultText);

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

                AddTrace(new LlmToolCallTrace(fc.Name ?? "", succeeded, elapsedMs, "native",
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Outer cancellation must propagate; it is never converted into a "failed result".
                // (Tool-level timeouts are handled above and surfaced as a normal error result instead.)
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[ToolPolicy] {fc.Name} threw: {ex.Message}", LogTag.Llm);
                _eventPublisher.PublishFailed(BuildInfo(fc), ex.Message, 0d);
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "native", ex.Message));
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
        /// Names of state-mutating built-in tools that must never run concurrently. These tools write to a
        /// shared store (long-term memory, the installed Lua mods registry, the skills registry); racing two
        /// of them risks lost updates or torn reads. Matched case-insensitively against the (possibly repaired)
        /// tool name. See <see cref="IsSerializedTool"/> and <see cref="ExecuteBatchAsync"/>.
        /// </summary>
        private static readonly HashSet<string> SerializedMutatingToolNames =
            new(StringComparer.OrdinalIgnoreCase) { "memory", "manage_mods", "manage_skills" };

        /// <summary>
        /// Whether a tool call must be serialized relative to other mutating calls in the same batch.
        /// The rule is intentionally conservative: any call whose name is in
        /// <see cref="SerializedMutatingToolNames"/> joins a single ordered serialization chain so that no two
        /// state-mutating built-ins ever overlap, even with different names. All other (independent / read-only)
        /// tools run fully in parallel under the concurrency limit.
        /// </summary>
        private static bool IsSerializedTool(MEAI.FunctionCallContent fc)
        {
            return fc?.Name != null && SerializedMutatingToolNames.Contains(fc.Name);
        }

        /// <summary>
        /// Execute a batch of tool calls, tracking cumulative success/failure.
        /// Returns the list of result contents and an aggregate success flag.
        /// <para>
        /// Concurrency model (<see cref="ICoreAISettings.MaxParallelToolCalls"/>): when the limit is &gt; 1 and
        /// the batch has more than one call, independent tool calls execute concurrently with bounded
        /// parallelism (a <see cref="SemaphoreSlim"/> of that size). State-mutating built-ins
        /// (<see cref="SerializedMutatingToolNames"/>) are run on a single ordered serialization chain so they
        /// never race each other. Regardless of completion order, results are collated back into the
        /// <b>original call order</b> (indexed array). The consecutive-error counter is updated exactly once,
        /// after ordered collation, with the same semantics as the sequential path. A value &lt;= 1 (or a
        /// single-call batch) takes a strictly-sequential fast path that is byte-identical to the legacy loop.
        /// Outer cancellation cancels all in-flight calls and propagates as <see cref="OperationCanceledException"/>.
        /// </para>
        /// </summary>
        public async Task<BatchToolCallResult> ExecuteBatchAsync(
            List<MEAI.FunctionCallContent> toolCalls,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            // 1. Check duplicates per slot so mixed batches can still execute allowed calls.
            DuplicatePlan duplicatePlan = BuildDuplicatePlan(toolCalls);
            if (duplicatePlan.HasDuplicates && !duplicatePlan.HasExecutable)
            {
                // Every call in the batch was a duplicate - this counts as a failed iteration for the
                // consecutive-error guard, same as the sequential/concurrent paths below. Without this, a
                // model stuck repeating the same call forever never trips the max-consecutive-errors guard.
                RecordFailure();
                return new BatchToolCallResult
                {
                    Results = duplicatePlan.IndexedResults.Select(r => (MEAI.AIContent)r.Result).ToList(),
                    AnyFailed = true,
                    AllFailed = true
                };
            }

            int maxParallel = Math.Max(1, _settings.MaxParallelToolCalls);

            // 2a. Sequential fast-path: byte-identical to the legacy loop.
            if (maxParallel <= 1 || toolCalls.Count <= 1)
            {
                List<MEAI.AIContent> seqResults = new();
                bool seqAnyFailed = false;
                bool seqAllFailed = true;

                for (int i = 0; i < toolCalls.Count; i++)
                {
                    ToolCallResult r = duplicatePlan.IsDuplicateIndex[i]
                        ? duplicatePlan.IndexedResults[i]
                        : await ExecuteSingleAsync(toolCalls[i], chatOptions, cancellationToken).ConfigureAwait(false);
                    seqResults.Add(r.Result);
                    if (!r.Succeeded)
                    {
                        seqAnyFailed = true;
                    }
                    else
                    {
                        seqAllFailed = false;
                    }
                }

                if (!seqAnyFailed)
                {
                    RecordSuccess();
                }
                else
                {
                    RecordFailure();
                }

                return new BatchToolCallResult
                {
                    Results = seqResults,
                    AnyFailed = seqAnyFailed,
                    AllFailed = seqAnyFailed && seqAllFailed
                };
            }

            // 2b. Concurrent path with bounded parallelism + serialization of mutating built-ins.
            ToolCallResult[] indexed = duplicatePlan.IndexedResults;
            using SemaphoreSlim gate = new(maxParallel, maxParallel);

            // Single ordered chain for all serialized (mutating) tool calls so none of them overlap.
            // Each serialized call awaits the previous serialized call before running.
            Task serialChain = Task.CompletedTask;
            List<Task> tasks = new(toolCalls.Count);

            for (int i = 0; i < toolCalls.Count; i++)
            {
                int index = i;
                MEAI.FunctionCallContent fc = toolCalls[index];
                if (duplicatePlan.IsDuplicateIndex[index])
                {
                    continue;
                }

                if (IsSerializedTool(fc))
                {
                    Task previous = serialChain;
                    serialChain = RunGuardedAsync(previous);
                    tasks.Add(serialChain);
                }
                else
                {
                    tasks.Add(RunGuardedAsync(Task.CompletedTask));
                }

                // Local function captures index/fc; gate bounds total in-flight concurrency.
                async Task RunGuardedAsync(Task waitFor)
                {
                    if (waitFor != null && !waitFor.IsCompleted)
                    {
                        // Serialization ordering: wait for the prior serialized call. Swallow its
                        // fault/cancellation here (it is observed via its own slot) so chaining never throws.
                        try
                        {
                            await waitFor.ConfigureAwait(false);
                        }
                        catch
                        {
                            /* prior serialized call's outcome handled in its own slot */
                        }
                    }

                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        indexed[index] =
                            await ExecuteSingleAsync(fc, chatOptions, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }

            // Awaiting WhenAll surfaces OperationCanceledException on outer cancellation (never swallowed).
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 3. Collate strictly in original call order.
            List<MEAI.AIContent> results = new(indexed.Length);
            bool anyFailed = false;
            bool allFailed = true;
            foreach (ToolCallResult r in indexed)
            {
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

            // 4. Update error counter once, after ordered collation (deterministic regardless of
            // completion order): the whole batch counts as one failure if any call failed.
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
        /// Determines whether a tool result indicates success. Uses top-level JSON
        /// failure keys first, then conservative plain-text failure prefixes.
        /// </summary>
        internal static bool IsToolResultSuccess(string resultText)
        {
            if (string.IsNullOrEmpty(resultText))
            {
                return true; // empty result is not a failure signal
            }

            // Attempt structured JSON parse for reliable detection.
            try
            {
                JObject json = JObject.Parse(resultText);
                foreach (JProperty property in json.Properties())
                {
                    // A truthy "error" value is a failure signal. Many result contracts (e.g. MemoryResult)
                    // always serialize an "Error" property, null on success - presence alone must not count.
                    if (string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase) &&
                        IsTruthyErrorValue(property.Value))
                    {
                        return false;
                    }

                    if ((string.Equals(property.Name, "ok", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(property.Name, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(property.Name, "success", StringComparison.OrdinalIgnoreCase)) &&
                        IsExplicitFalse(property.Value))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                string trimmed = resultText.TrimStart();
                if (StartsWithFailurePrefix(trimmed))
                {
                    return false;
                }

                // Preserve legacy non-JSON detection without treating ordinary text containing
                // "success" as a failure.
                return !ContainsLegacySuccessFalse(trimmed);
            }
        }

        private static bool IsTruthyErrorValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return false;
            }

            if (token.Type == JTokenType.String)
            {
                return !string.IsNullOrEmpty(token.Value<string>());
            }

            if (token.Type == JTokenType.Boolean)
            {
                // Some contracts use "error": false to mean "no error".
                return token.Value<bool>();
            }

            // Non-null object/array/number error payloads are still a meaningful failure signal.
            return true;
        }

        private static bool IsExplicitFalse(JToken token)
        {
            if (token == null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return !token.Value<bool>();
            }

            if (token.Type == JTokenType.String &&
                bool.TryParse(token.Value<string>(), out bool parsed))
            {
                return !parsed;
            }

            return false;
        }

        private static bool StartsWithFailurePrefix(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string lower = text.ToLowerInvariant();
            return lower.StartsWith("failed", StringComparison.Ordinal) ||
                   lower.StartsWith("failure", StringComparison.Ordinal) ||
                   lower.StartsWith("error:", StringComparison.Ordinal) ||
                   lower.StartsWith("exception", StringComparison.Ordinal) ||
                   lower.StartsWith("system.exception", StringComparison.Ordinal);
        }

        private static bool ContainsLegacySuccessFalse(string text)
        {
            return text.IndexOf("\"Success\":false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("\"success\":false", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif

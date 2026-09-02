#if COREAI_LLM
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
    /// <para>
    /// <b>Ни одного <c>ConfigureAwait(false)</c> в этом файле — это требование, а не стиль.</b>
    /// В Unity WebGL-плеере пула потоков нет, а <c>SynchronizationContext.Current</c> есть
    /// (<c>UnitySynchronizationContext</c>). Из-за этого продолжение <c>await x.ConfigureAwait(false)</c>
    /// признаётся «неинлайнимым» (<c>SynchronizationContext</c> не базового типа) и ставится в очередь
    /// пула — то есть НЕ ВЫПОЛНЯЕТСЯ НИКОГДА. Отказ безмолвный: не исключение, а вечное ожидание.
    /// Пока каждый инструмент завершался синхронно, суспенда не было и дефект не проявлялся; первый
    /// же ОЖИДАЮЩИЙ инструмент (карточка квиза, которая ждёт ответа ученика) вешал ход учителя
    /// навсегда — ученик оставался с вечным индикатором «печатает» и заблокированным вводом.
    /// Тот же вывод уже записан в <c>MeaiOpenAiChatClient</c>, <c>AiOrchestrator</c>,
    /// <c>QueuedAiOrchestrator</c>, <c>LoggingLlmClientDecorator</c> и <c>FetchSseOpenAiTransport</c>;
    /// этот файл был последним на пути LLM, где правило не соблюдалось.
    /// </para>
    /// <para>
    /// Практическое следствие: продолжения возвращаются на цикл хоста (в Unity — на player loop).
    /// Для переносимых хостов без <c>SynchronizationContext</c> поведение не меняется вовсе — там
    /// захватывать нечего, и продолжение резюмируется ровно так же, как с <c>ConfigureAwait(false)</c>.
    /// </para>
    /// </summary>
    public sealed class ToolExecutionPolicy
    {
        private const string EmptyToolResultPayload =
            "{\"Success\":true,\"Message\":\"Tool completed without an explicit result payload.\"}";

        private readonly ILog _logger;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _actorId;
        private readonly string _roleId;
        private readonly string _traceId;
        private readonly int _maxConsecutiveErrors;
        private readonly IToolCallEventPublisher _eventPublisher;
        private readonly IToolExecutionNotifier _notifier;

        private static long _toolNameRepairCount;

        // Extra grace added to the per-call tool timeout when draining a streamed turn's in-flight
        // calls at completion, so a call already near its own timeout is not abandoned a hair early.
        private const int DrainGraceMarginMs = 1000;

        private int _consecutiveErrors;
        private readonly HashSet<string> _executedSignatures = new();
        private readonly Dictionary<string, bool[]> _partialBatchSuccesses = new();
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
            IToolExecutionNotifier notifier = null,
            string actorId = "")
        {
            _logger = logger ?? NullLog.Instance;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _originalTools = originalTools ?? new List<ILlmTool>();
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _actorId = actorId ?? "";
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
        /// <remarks>
        /// A real COPY taken under the append lock. Returning the live list let a worker abandoned at the
        /// drain deadline append while a caller was still enumerating, throwing
        /// "Collection was modified" at the end of the turn, far from the cause.
        /// </remarks>
        public IReadOnlyList<LlmToolCallTrace> ExecutedTraces
        {
            get
            {
                lock (_traceLock)
                {
                    return _executedTraces.ToArray();
                }
            }
        }

        /// <summary>
        /// Reset duplicate signatures, error counter, and trace log. Call at the start of each
        /// top-level request to allow the same tool to be used across independent requests.
        /// </summary>
        public void Reset()
        {
            _consecutiveErrors = 0;
            _executedSignatures.Clear();
            _partialBatchSuccesses.Clear();
            lock (_traceLock)
            {
                _executedTraces.Clear();
            }
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
            // This entry point has no execution phase, so register the signature immediately
            // (legacy semantics: a checked batch counts as seen).
            if (plan.PendingBatchSignature != null && !plan.HasDuplicates)
            {
                _executedSignatures.Add(plan.PendingBatchSignature);
            }

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
            public string[] Signatures = Array.Empty<string>();

            /// <summary>
            /// Batch signature awaiting registration. Registered only AFTER the batch executes
            /// with at least one success: a failed call must stay retryable with identical args
            /// (e.g. after a transient tool timeout), otherwise the echo guard suppresses the
            /// very retry the error feedback asks the model to make.
            /// </summary>
            public string PendingBatchSignature;
        }

        private DuplicatePlan BuildDuplicatePlan(List<MEAI.FunctionCallContent> toolCalls)
        {
            DuplicatePlan plan = new()
            {
                IndexedResults = new ToolCallResult[toolCalls?.Count ?? 0],
                IsDuplicateIndex = new bool[toolCalls?.Count ?? 0],
                Signatures = new string[toolCalls?.Count ?? 0],
                HasExecutable = toolCalls != null && toolCalls.Count > 0
            };

            if (_allowDuplicateToolCalls || toolCalls == null || toolCalls.Count == 0)
            {
                return plan;
            }

            List<string> reducedSignatures = new();
            for (int i = 0; i < toolCalls.Count; i++)
            {
                if (TryBuildDuplicateSignature(toolCalls[i], out string signature))
                {
                    plan.Signatures[i] = signature;
                    reducedSignatures.Add(signature);
                }
            }

            if (reducedSignatures.Count == 0)
            {
                return plan;
            }

            string batchSig = string.Join("|", reducedSignatures.OrderBy(s => s, StringComparer.Ordinal));
            if (_executedSignatures.Contains(batchSig))
            {
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (plan.Signatures[i] != null)
                    {
                        MarkDuplicate(plan, i, toolCalls[i]);
                    }
                }

                return plan;
            }

            if (_partialBatchSuccesses.TryGetValue(batchSig, out bool[] succeededSlots))
            {
                int count = Math.Min(toolCalls.Count, succeededSlots.Length);
                for (int i = 0; i < count; i++)
                {
                    if (succeededSlots[i] && plan.Signatures[i] != null)
                    {
                        MarkDuplicate(plan, i, toolCalls[i]);
                    }
                }
            }

            plan.PendingBatchSignature = batchSig;

            // Intra-batch repeats are deliberately NOT suppressed: "spawn tree x3" in one turn is a
            // legitimate request and must execute all three (Claude/Cursor parity). The runaway-echo
            // bug this used to guard (one turn's calls re-executing every roundtrip) is fixed at the
            // wire level - every tool_call_id gets its own tool-role reply - and the cross-turn
            // whole-batch echo branch above still catches a model re-sending an identical turn.
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

            // Mutating built-ins are never exempt from the cross-turn replay guard. A tool can
            // legitimately allow identical calls inside one turn (manage_skills is one example),
            // but replaying the same completed turn must not apply its side effects again.
            if (match != null && match.AllowDuplicates && !IsSerializedToolName(canonicalName))
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
        /// Effective per-call timeout for one tool: <see cref="ILlmTool.ToolTimeoutMsOverride"/> when the
        /// tool declares one, otherwise <see cref="ICoreAISettings.DefaultToolTimeoutMs"/>. The returned
        /// value keeps the global meaning unchanged — positive = budget in ms, <c>&lt;= 0</c> = no
        /// per-call deadline — so every caller stays a plain <c>&gt; 0</c> check.
        /// <para>
        /// Resolution is BY NAME through <see cref="GetCanonicalToolName"/> (the same case-insensitive
        /// repair the rest of the policy uses) because the invocation site only has the model's
        /// <see cref="MEAI.FunctionCallContent"/> and an <see cref="MEAI.AIFunction"/> resolved out of
        /// <see cref="MEAI.ChatOptions.Tools"/> — never the <see cref="ILlmTool"/> instance. A name the
        /// role's tool list does not contain (an unbound call, a function expanded out of
        /// <see cref="IAIFunctionsLlmTool"/> under a different name) simply falls back to the global
        /// setting, which is what happened before overrides existed.
        /// </para>
        /// </summary>
        private int ResolveToolTimeoutMs(string toolName)
        {
            GetCanonicalToolName(toolName, out ILlmTool tool, out _);
            return tool?.ToolTimeoutMsOverride ?? _settings.DefaultToolTimeoutMs;
        }

        /// <summary>
        /// Effective grace budget for the bounded drain of one streamed turn: the LONGEST per-call
        /// timeout among the calls scheduled into <see cref="StreamedTurn.InFlight"/>, or <c>0</c>
        /// ("wait for natural completion") as soon as one of them runs with its deadline disabled.
        /// <para>
        /// The drain is a single deadline covering several concurrent calls, so it has to be the
        /// maximum: taking the global default instead would abandon a legitimately long call (a card
        /// the student is still answering) after the budget meant for a short one, and its slot would
        /// collate as a "did not complete" failure while the tool was working exactly as designed.
        /// With no overrides in play every entry equals <see cref="ICoreAISettings.DefaultToolTimeoutMs"/>,
        /// so the value is identical to what this method replaced.
        /// </para>
        /// </summary>
        private int ResolveDrainTimeoutMs(StreamedTurn turn)
        {
            if (turn.InFlightTimeoutsMs.Count == 0)
            {
                return _settings.DefaultToolTimeoutMs;
            }

            int longest = 0;
            foreach (int timeoutMs in turn.InFlightTimeoutsMs)
            {
                if (timeoutMs <= 0)
                {
                    return 0;
                }

                longest = Math.Max(longest, timeoutMs);
            }

            return longest;
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
                int toolTimeoutMs = ResolveToolTimeoutMs(fc.Name);
                object result;
                if (toolTimeoutMs > 0)
                {
                    using CancellationTokenSource cts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    // WHY NOT CancelAfter: it is backed by System.Threading.Timer, which never fires in a
                    // Unity WebGL player. The per-call tool timeout would then not exist there at all — a
                    // tool body that never returns would hang the whole turn instead of failing with the
                    // message below. The host schedules this delay on its own loop, where no timer is needed.
                    Task timeoutDelay = marshaler.DelayAsync(toolTimeoutMs, cts.Token);
                    try
                    {
                        Task<object> invokeTask = marshaler
                            .InvokeAsync<object>(
                                async () =>
                                    await aiFunc.InvokeAsync(args, cts.Token),
                                cts.Token);

                        await Task.WhenAny(invokeTask, timeoutDelay);

                        // Whoever lost the race stops here: on a timeout this cancellation is what makes the
                        // tool body observe the deadline, on a normal finish it releases the delay the host
                        // still has scheduled. Cancel is idempotent, so one unconditional call covers both.
                        cts.Cancel();
                        result = await invokeTask;
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
                    finally
                    {
                        // The losing delay is FAULTED, not merely cancelled, whenever a host bridges a
                        // cancelled UniTask into a Task (AsTask turns cancellation into an exception), so it
                        // has to be observed in every outcome or it resurfaces as UnobservedTaskException.
                        _ = ObserveAsync(timeoutDelay);
                    }
                }
                else
                {
                    // No per-call deadline: either globally (DefaultToolTimeoutMs <= 0) or for this tool
                    // alone (ILlmTool.ToolTimeoutMsOverride <= 0). The body is invoked with the REQUEST
                    // token, so LlmRequestTimeoutSeconds — an idle budget re-armed by tool-call events and
                    // streamed chunks, enforced by TimeoutLlmClientDecorator and, on Unity, by
                    // CoreAiChatService's PlayerLoop timer — remains the only thing that ends a tool body
                    // that never returns.
                    result = await marshaler.InvokeAsync<object>(
                        async () => await aiFunc.InvokeAsync(args, cancellationToken),
                        cancellationToken);
                }

                sw.Stop();
                string resultText = NormalizeToolResultText(result);
                bool succeeded = IsToolResultSuccess(resultText);
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
                // Argument/JSON conversion failures (MEAI could not coerce the model's arguments
                // into the delegate's parameter types) are actionable: append the tool's compact
                // schema so the model can retry with correctly-shaped arguments instead of
                // guessing from an opaque exception message.
                string errorMessage = ex.Message;
                bool argConversion = LooksLikeArgumentConversionError(ex);
                if (argConversion)
                {
                    string schemaHint = BuildSchemaRetryHint(fc.Name);
                    if (!string.IsNullOrEmpty(schemaHint))
                    {
                        errorMessage = $"{errorMessage} {schemaHint}";
                    }
                }

                _logger.Error($"[ToolPolicy] {fc.Name} threw: {errorMessage}", LogTag.Llm);
                _eventPublisher.PublishFailed(BuildInfo(fc), errorMessage, 0d);
                // WHY: Tool bodies convert their own exceptions to error results; first-party tools and
                // the DelegateLlmTool wrapper enforce this. Exceptions escaping InvokeAsync are therefore
                // MEAI argument-coercion failures that occur before the body runs and remain retry-safe.
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d,
                    argConversion ? "arg-conversion" : "native", errorMessage));
                LogCallLine(fc, false, 0d, $"threw: {errorMessage}");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Error: {errorMessage}"),
                    Succeeded = false
                };
            }
        }

        /// <summary>
        /// Whether an exception thrown by a tool invocation looks like an argument/JSON type
        /// conversion failure (as opposed to a genuine tool-body error). Checks the whole exception
        /// chain: MEAI's AIFunctionFactory frequently wraps the real JsonException/FormatException
        /// in an outer InvalidOperationException.
        /// </summary>
        internal static bool LooksLikeArgumentConversionError(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is JsonException ||
                    current is System.Text.Json.JsonException ||
                    current is InvalidCastException ||
                    current is FormatException ||
                    current is ArgumentException)
                {
                    return true;
                }

                if (current.Message != null &&
                    current.Message.IndexOf("convert", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the same compact-schema retry suffix the missing-required-argument path emits,
        /// or an empty string when no meaningful schema is registered for the tool.
        /// </summary>
        private string BuildSchemaRetryHint(string toolName)
        {
            ILlmTool tool = _originalTools.FirstOrDefault(t =>
                string.Equals(t.Name, toolName, StringComparison.Ordinal));
            if (tool == null || string.IsNullOrWhiteSpace(tool.ParametersSchema) ||
                tool.ParametersSchema.Trim() == "{}")
            {
                return "";
            }

            string schema = CompactSchema(tool.ParametersSchema, 1200);
            return $"Retry the same tool call with JSON arguments matching this schema: {schema}";
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
                SafeArgumentsJson(fc),
                _actorId);
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
        private static readonly HashSet<string> SerializedMutatingToolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "memory",
            "manage_mods",
            "manage_skills",
            "world_command",
            "component_command",
            "execute_lua",
            "call_skill_tool"
        };

        /// <summary>
        /// Whether a tool call must be serialized relative to other mutating calls in the same batch.
        /// The rule is intentionally conservative: any call whose name is in
        /// <see cref="SerializedMutatingToolNames"/> joins a single ordered serialization chain so that no two
        /// state-mutating built-ins ever overlap, even with different names. All other (independent / read-only)
        /// tools run fully in parallel under the concurrency limit.
        /// </summary>
        private static bool IsSerializedTool(MEAI.FunctionCallContent fc)
        {
            return IsSerializedToolName(fc?.Name);
        }

        private static bool IsSerializedToolName(string toolName)
        {
            return toolName != null && SerializedMutatingToolNames.Contains(toolName);
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
                        : await ExecuteSingleAsync(toolCalls[i], chatOptions, cancellationToken);
                    duplicatePlan.IndexedResults[i] = r;
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

                // Partial success IS forward progress: only a batch where EVERY call failed counts
                // toward the consecutive-error abort. Otherwise three 4-of-5-successful spawn
                // batches in a row would kill a run that is visibly building the scene.
                if (seqAnyFailed && seqAllFailed)
                {
                    RecordFailure();
                }
                else
                {
                    RecordSuccess();
                    RegisterExecutionOutcome(duplicatePlan, duplicatePlan.IndexedResults, seqAnyFailed, seqAllFailed);
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
                        // Serialization ordering: wait for the prior serialized call. Swallow its fault
                        // here (it is observed via its own slot) so chaining never throws on a failure.
                        try
                        {
                            await waitFor;
                        }
                        catch (OperationCanceledException)
                        {
                            // WHY: The only OCE that escapes ExecuteSingleAsync is outer cancellation
                            // (per-call timeouts become error results), so swallowing it here would make a
                            // cancelled predecessor look faulted and let the rest of the chain keep running.
                            throw;
                        }
                        catch (Exception)
                        {
                            /* prior serialized call's outcome handled in its own slot */
                        }
                    }

                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        indexed[index] =
                            await ExecuteSingleAsync(fc, chatOptions, cancellationToken);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }

            // Awaiting WhenAll surfaces OperationCanceledException on outer cancellation (never swallowed).
            await Task.WhenAll(tasks);

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
            // completion order). Partial success is forward progress: only an ALL-failed batch
            // counts toward the consecutive-error abort.
            if (anyFailed && allFailed)
            {
                RecordFailure();
            }
            else
            {
                RecordSuccess();
                RegisterExecutionOutcome(duplicatePlan, indexed, anyFailed, allFailed);
            }

            return new BatchToolCallResult
            {
                Results = results,
                AnyFailed = anyFailed,
                AllFailed = anyFailed && allFailed
            };
        }

        /// <summary>
        /// Per-turn state for execute-as-you-stream: tool calls executed AS THEY ARRIVE in the SSE
        /// stream (see <see cref="ExecuteStreamedAsync"/>), while duplicate suppression and the
        /// consecutive-error counter keep the same TURN-level semantics as
        /// <see cref="ExecuteBatchAsync"/>. With <see cref="ICoreAISettings.MaxParallelToolCalls"/>
        /// &gt; 1 arrived calls are scheduled concurrently (mirroring the batch concurrent path) and
        /// their results are collated back into ARRIVAL order at completion. Create via
        /// <see cref="BeginStreamedTurn"/>, finish via <see cref="CompleteStreamedTurnAsync"/> (or
        /// the synchronous <see cref="CompleteStreamedTurn"/> when nothing is left in flight).
        /// </summary>
        public sealed class StreamedTurn
        {
            /// <summary>
            /// One slot per arrived call, in arrival order (batch parity: original call order).
            /// A slot is filled inline (sequential mode, duplicate suppression) or by a scheduled
            /// worker (parallel mode); a slot still empty at finalization means the call never
            /// finished (cancellation / mid-stream abort) and collates as an explicit failure.
            /// AnyFailed/AllFailed and the results list are computed at completion from these
            /// slots - worker tasks never mutate shared turn flags.
            /// </summary>
            internal readonly List<StreamedSlot> Slots = new();

            internal readonly List<string> Signatures = new();

            /// <summary>Scheduled (parallel-mode) call tasks, drained at turn completion.</summary>
            internal readonly List<Task> InFlight = new();

            /// <summary>
            /// Effective per-call timeout of each entry in <see cref="InFlight"/>, captured at scheduling
            /// time. The drain deadline is derived from these (see <see cref="ResolveDrainTimeoutMs"/>):
            /// by then the tool names are no longer at hand, and a turn can mix a tool that waits for the
            /// student with ordinary short ones, so a single shared budget has to be the longest of them.
            /// </summary>
            internal readonly List<int> InFlightTimeoutsMs = new();

            /// <summary>
            /// Mutating calls are buffered until the complete streamed turn is known. This lets the
            /// whole-turn replay guard reject an echoed multi-call turn before any side effect runs.
            /// Production streaming always completes through <see cref="CompleteStreamedTurnAsync"/>.
            /// </summary>
            internal readonly List<DeferredStreamedCall> DeferredMutations = new();

            internal readonly HashSet<StreamedSlot> PreviouslySuccessfulSlots = new();
            internal bool IsFinalized;

            /// <summary>
            /// Single ordered chain for serialized (mutating) tool calls, batch parity: each
            /// serialized call awaits the previous one so no two mutating built-ins ever overlap.
            /// </summary>
            internal Task SerialChain = Task.CompletedTask;

            /// <summary>
            /// Bounds in-flight concurrency in parallel mode; created lazily on the first
            /// scheduled call so the sequential fast-path allocates nothing extra.
            /// </summary>
            internal SemaphoreSlim Gate;
        }

        /// <summary>
        /// One arrival-indexed result slot of a <see cref="StreamedTurn"/>. The result is stored
        /// as a boxed <see cref="ToolCallResult"/> behind a volatile reference: a reference
        /// publish is atomic, so a finalizer that gave up waiting (cancelled token, see
        /// <see cref="CompleteStreamedTurnAsync"/>) can never observe a torn struct write from a
        /// worker that completes concurrently.
        /// </summary>
        internal sealed class StreamedSlot
        {
            internal readonly string CallId;
            private object _boxedResult;

            internal StreamedSlot(string callId)
            {
                CallId = callId;
            }

            internal void Set(ToolCallResult result)
            {
                Volatile.Write(ref _boxedResult, result);
            }

            internal bool TryGet(out ToolCallResult result)
            {
                object boxed = Volatile.Read(ref _boxedResult);
                if (boxed == null)
                {
                    result = default;
                    return false;
                }

                result = (ToolCallResult)boxed;
                return true;
            }
        }

        internal sealed class DeferredStreamedCall
        {
            internal readonly MEAI.FunctionCallContent FunctionCall;
            internal readonly MEAI.ChatOptions ChatOptions;
            internal readonly StreamedSlot Slot;

            internal DeferredStreamedCall(
                MEAI.FunctionCallContent functionCall,
                MEAI.ChatOptions chatOptions,
                StreamedSlot slot)
            {
                FunctionCall = functionCall;
                ChatOptions = chatOptions;
                Slot = slot;
            }
        }

        /// <summary>Starts a streamed turn (execute-as-you-stream counterpart of one batch).</summary>
        public StreamedTurn BeginStreamedTurn()
        {
            return new StreamedTurn();
        }

        /// <summary>
        /// Executes one tool call the moment it arrives in the stream. Mirrors the batch path per
        /// call: only a CROSS-turn echo (a single call whose signature was already registered by an
        /// earlier turn in this request) is suppressed with the same "Duplicate tool call" result
        /// the batch path produces; exact repeats WITHIN the same turn ("spawn tree x3") execute,
        /// and tools with AllowDuplicates are exempt entirely. Signature bookkeeping and the
        /// cross-turn echo check always resolve synchronously at arrival (arrival ORDER is what
        /// makes them deterministic), so a suppressed call fills its slot immediately and returns
        /// its result in both modes.
        /// <para>
        /// Sequential mode (<see cref="ICoreAISettings.MaxParallelToolCalls"/> &lt;= 1): the call
        /// executes inline and its result is returned, byte-identical to the pre-parallel
        /// behavior. Parallel mode: the call's arrival slot is reserved and a bounded-concurrency
        /// worker is scheduled mirroring the batch concurrent path (mutating built-ins join the
        /// turn's single serialization chain, everything else is gate-bounded); the method
        /// returns <c>null</c> and the result surfaces in <see cref="CompleteStreamedTurnAsync"/>,
        /// collated in arrival order. The streaming caller discards the per-call return value
        /// either way. The consecutive-error counter is NOT touched here — the whole turn records
        /// once at completion, exactly like a batch.
        /// </para>
        /// </summary>
        public async Task<ToolCallResult?> ExecuteStreamedAsync(
            StreamedTurn turn,
            MEAI.FunctionCallContent fc,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            string signature = null;
            bool hasSignature = !_allowDuplicateToolCalls && TryBuildDuplicateSignature(fc, out signature);
            if (hasSignature)
            {
                turn.Signatures.Add(signature);
            }

            // Cross-turn echo guard, batch parity for the single-call turn: a one-call batch
            // registers the call's own signature as its turn signature, so when a model re-issues
            // the identical single call it already ran last turn, ExecuteBatchAsync suppresses it —
            // mirror that here. A multi-call echo turn cannot be detected mid-stream (its combined
            // signature only exists once the turn ends); that direction is covered by the wire
            // protocol sending every tool result (models stop re-issuing "unanswered" calls).
            bool crossTurnEcho = hasSignature && _executedSignatures.Contains(signature);

            StreamedSlot slot = new(fc.CallId);
            turn.Slots.Add(slot);

            // Batch parity with BuildDuplicatePlan: only the CROSS-turn echo is suppressed. An exact
            // repeat within the same turn ("spawn tree x3") is a legitimate request and executes.
            if (hasSignature && crossTurnEcho)
            {
                string duplicate = $"Duplicate tool call '{fc.Name}' with same arguments - skipped.";
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "duplicate", duplicate));
                ToolCallResult suppressed = new()
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, duplicate),
                    Succeeded = false
                };
                slot.Set(suppressed);
                return suppressed;
            }

            // A streamed multi-call echo cannot be identified until the turn is complete. Buffer
            // state-mutating calls so CompleteStreamedTurnAsync can compare the combined signature
            // before any side effect is applied. Read-only calls keep execute-as-you-stream latency.
            if (IsSerializedTool(fc))
            {
                turn.DeferredMutations.Add(new DeferredStreamedCall(fc, chatOptions, slot));
                return null;
            }

            int maxParallel = Math.Max(1, _settings.MaxParallelToolCalls);
            if (maxParallel <= 1)
            {
                // Sequential fast-path: byte-identical to the pre-parallel streamed behavior.
                ToolCallResult executed =
                    await ExecuteSingleAsync(fc, chatOptions, cancellationToken);
                slot.Set(executed);
                return executed;
            }

            // Parallel scheduling, batch parity with the ExecuteBatchAsync concurrent path:
            // mutating built-ins chain onto SerialChain so they never overlap; everything else
            // runs gate-bounded. The per-call result is deferred to CompleteStreamedTurnAsync.
            turn.Gate ??= new SemaphoreSlim(maxParallel, maxParallel);

            // Captured here, not at drain time: this is the last place the call's NAME is available,
            // and the drain deadline must cover the longest budget it scheduled.
            turn.InFlightTimeoutsMs.Add(ResolveToolTimeoutMs(fc.Name));
            if (IsSerializedTool(fc))
            {
                Task previous = turn.SerialChain;
                Task chained = RunGuardedAsync(previous);
                turn.SerialChain = chained;
                turn.InFlight.Add(chained);
            }
            else
            {
                turn.InFlight.Add(RunGuardedAsync(Task.CompletedTask));
            }

            return null;

            // Local function captures slot/fc; the gate bounds total in-flight concurrency.
            async Task RunGuardedAsync(Task waitFor)
            {
                if (waitFor != null && !waitFor.IsCompleted)
                {
                    // Serialization ordering: wait for the prior serialized call. Swallow its
                    // fault/cancellation here (it is observed via its own slot) so chaining never throws.
                    try
                    {
                        await waitFor;
                    }
                    catch
                    {
                        /* prior serialized call's outcome handled in its own slot */
                    }
                }

                await turn.Gate.WaitAsync(cancellationToken);
                try
                {
                    slot.Set(await ExecuteSingleAsync(fc, chatOptions, cancellationToken));
                }
                finally
                {
                    turn.Gate.Release();
                }
            }
        }

        /// <summary>
        /// Synchronous turn completion, valid ONLY when no scheduled call is still running — i.e.
        /// the sequential path (<see cref="ICoreAISettings.MaxParallelToolCalls"/> &lt;= 1, nothing
        /// is ever scheduled) or a parallel turn whose workers all happened to finish already.
        /// A genuinely in-flight call cannot be awaited here without sync-over-async (which
        /// deadlocks the Unity main thread), so that case throws instead of blocking: use
        /// <see cref="CompleteStreamedTurnAsync"/>. See that overload for the completion semantics.
        /// </summary>
        public BatchToolCallResult CompleteStreamedTurn(StreamedTurn turn)
        {
            if (turn.DeferredMutations.Count > 0)
            {
                throw new InvalidOperationException(
                    "CompleteStreamedTurn cannot execute deferred mutating tool calls synchronously. " +
                    "Use CompleteStreamedTurnAsync.");
            }

            foreach (Task inFlight in turn.InFlight)
            {
                if (!inFlight.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "CompleteStreamedTurn was called while a scheduled streamed tool call is " +
                        "still in flight (MaxParallelToolCalls > 1). Use CompleteStreamedTurnAsync.");
                }

                // Observe faults/cancellations of completed workers so they never surface as
                // UnobservedTaskException; the per-call outcomes are read from the slots.
                _ = inFlight.Exception;
            }

            return FinalizeStreamedTurn(turn);
        }

        /// <summary>
        /// Ends a streamed turn: drains every scheduled in-flight call, collates the slots
        /// strictly in ARRIVAL order, records ONE success/failure against the consecutive-error
        /// counter (batch parity) and registers the turn's combined signature so a later turn that
        /// re-sends the exact same batch through <see cref="ExecuteBatchAsync"/> is still caught
        /// as an echo. If the combined signature was ALREADY registered earlier in this request,
        /// the whole turn is a cross-turn echo: it records ONE failure (never a success) and
        /// reports AnyFailed and AllFailed, exactly like the all-duplicate batch branch in
        /// <see cref="ExecuteBatchAsync"/>. Otherwise returns the same shape
        /// <see cref="ExecuteBatchAsync"/> would for the whole turn.
        /// <para>
        /// Finalization never throws away the turn's accounting — it is also the mid-stream-abort
        /// path. On outer cancellation (or a tool that ignores its token) it stops waiting once
        /// <paramref name="cancellationToken"/> fires, marks the still-unfinished slots as failed
        /// with an explicit result, and still records/registers the turn.
        /// </para>
        /// </summary>
        public async Task<BatchToolCallResult> CompleteStreamedTurnAsync(
            StreamedTurn turn,
            CancellationToken cancellationToken)
        {
            if (turn.DeferredMutations.Count > 0)
            {
                string combined = BuildStreamedTurnSignature(turn);
                bool fullEcho = combined != null && _executedSignatures.Contains(combined);
                _partialBatchSuccesses.TryGetValue(combined ?? "", out bool[] partialSuccesses);
                for (int deferredIndex = 0; deferredIndex < turn.DeferredMutations.Count; deferredIndex++)
                {
                    DeferredStreamedCall deferred = turn.DeferredMutations[deferredIndex];
                    int slotIndex = turn.Slots.IndexOf(deferred.Slot);
                    bool alreadySucceeded = partialSuccesses != null &&
                                            slotIndex >= 0 &&
                                            slotIndex < partialSuccesses.Length &&
                                            partialSuccesses[slotIndex];
                    if (fullEcho || alreadySucceeded)
                    {
                        string duplicate =
                            $"Duplicate tool call '{deferred.FunctionCall.Name}' with same arguments - skipped.";
                        AddTrace(new LlmToolCallTrace(
                            deferred.FunctionCall.Name ?? "", false, 0d, "duplicate", duplicate));
                        deferred.Slot.Set(new ToolCallResult
                        {
                            Result = new MEAI.FunctionResultContent(deferred.FunctionCall.CallId, duplicate),
                            Succeeded = false
                        });
                        if (alreadySucceeded)
                        {
                            turn.PreviouslySuccessfulSlots.Add(deferred.Slot);
                        }

                        continue;
                    }

                    // All mutating built-ins share this ordered loop, so different mutation types
                    // cannot race each other even when MaxParallelToolCalls is greater than one.
                    try
                    {
                        deferred.Slot.Set(await ExecuteSingleAsync(
                            deferred.FunctionCall,
                            deferred.ChatOptions,
                            cancellationToken));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        deferred.Slot.Set(CreateFinalizedFailure(
                            deferred.Slot.CallId,
                            "Error: Tool call was cancelled while finalizing the streamed turn."));
                    }
                }

                turn.DeferredMutations.Clear();
            }

            if (turn.InFlight.Count > 0)
            {
                Task allInFlight = Task.WhenAll(turn.InFlight);
                int toolTimeoutMs = ResolveDrainTimeoutMs(turn);
                if (!allInFlight.IsCompleted && (cancellationToken.CanBeCanceled || toolTimeoutMs > 0))
                {
                    // Bounded drain: workers observe cancellation cooperatively, but a tool body that
                    // ignores its token must not hang finalization forever — the mid-stream-abort call
                    // site relies on this method returning even though it passes an uncancellable token
                    // (so the turn is recorded before the transport failure surfaces). Wait for whichever
                    // fires first: every in-flight call finishing, the caller's token, or a grace deadline
                    // covering a well-behaved per-call timeout with margin. A slot still unfinished after
                    // that collates as an explicit failure below. The deadline is what protects the
                    // CancellationToken.None abort path (a cancellation-ignoring tool can't wedge it).
                    // The budget comes from ResolveDrainTimeoutMs, i.e. the LONGEST per-call timeout this
                    // turn actually scheduled — a tool that declared a longer one (a card waiting for the
                    // student) must not be cut down to a budget meant for a short tool.
                    using CancellationTokenSource deadline =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    // WHY NOT RunContinuationsAsynchronously: it forbids inline resumption, so the
                    // awaiting continuation is handed to the thread pool. A Unity WebGL player has no
                    // thread pool, and the continuation is then never run at all — the drain parks
                    // forever and the caller's chat UI animates with no response. Exactly the defect
                    // already fixed once in FetchSseOpenAiTransport.StreamState; this was the second
                    // instance of it.
                    // The price of inline resumption is explicit and accepted: this signal is set from the
                    // registration below, i.e. from inside whatever code calls Cancel() on the caller's
                    // token, so the rest of this method (FinalizeStreamedTurn plus the caller's await tail
                    // up to its next real asynchronous point) runs synchronously on that canceller's stack.
                    // There is no lock held across it, so no deadlock — and a drain that never resumes at
                    // all is not a trade worth making to avoid it.
                    TaskCompletionSource<object> drainSignal = new();
                    using CancellationTokenRegistration registration = deadline.Token.Register(
                        state => ((TaskCompletionSource<object>)state).TrySetResult(null),
                        drainSignal);

                    // WHY NOT CancelAfter: it is backed by System.Threading.Timer, which does not fire
                    // in WebGL — the grace deadline this method advertises would simply not exist there,
                    // leaving a tool body that ignores its token able to wedge finalization forever.
                    // The host-provided delay runs on the frame loop where there is no timer.
                    Task graceDelay = toolTimeoutMs > 0
                        ? (_settings.ToolInvocationMarshaler ?? PassThroughLlmAsyncMarshaler.Instance)
                            .DelayAsync(toolTimeoutMs + DrainGraceMarginMs, deadline.Token)
                        : null;

                    await (graceDelay == null
                        ? Task.WhenAny(allInFlight, drainSignal.Task)
                        : Task.WhenAny(allInFlight, drainSignal.Task, graceDelay));

                    if (graceDelay != null)
                    {
                        // Stop the grace delay explicitly: disposing a CancellationTokenSource does NOT
                        // cancel it, so without this the delay would keep the host's loop scheduling it for
                        // the full grace window after the drain has already finished. Cancel is idempotent,
                        // so this is equally safe when the deadline is the thing that just fired.
                        deadline.Cancel();

                        // Observe the delay in EVERY outcome, including the one where it won the race:
                        // cancellation is not "not a fault" here, because a host that bridges a cancelled
                        // UniTask into a Task (AsTask) reports it as an exception, and a delay left
                        // unobserved resurfaces as UnobservedTaskException in an unrelated frame.
                        _ = ObserveAsync(graceDelay);
                    }
                }
                else if (!allInFlight.IsCompleted)
                {
                    // Only reached when per-call tool timeouts are explicitly disabled — globally
                    // (DefaultToolTimeoutMs <= 0) or by one of this turn's tools
                    // (ILlmTool.ToolTimeoutMsOverride <= 0) — AND the caller passed an uncancellable
                    // token: honour the "no timeout" choice and wait for natural completion. This is the
                    // one place where disabling the deadline can genuinely park finalization: the
                    // mid-stream-abort call site passes CancellationToken.None on purpose, so nothing
                    // above is left to cancel the tool. That is why a waiting tool should prefer a large
                    // finite budget over turning the deadline off.
                    try
                    {
                        await allInFlight;
                    }
                    catch (Exception)
                    {
                        // WHY: This branch is only reached with an uncancellable token, so there is no outer
                        // cancellation to propagate, and the mid-stream-abort caller relies on this method
                        // returning so the turn is still recorded. Per-call outcomes are read from the slots.
                    }
                }

                // Observe faults/cancellations so worker exceptions (only outer-cancellation OCEs escape
                // ExecuteSingleAsync) never surface as UnobservedTaskException — both when the drain
                // finished and when it gave up waiting and the fault is still to come.
                _ = ObserveAsync(allInFlight);
            }

            return FinalizeStreamedTurn(turn);
        }

        /// <summary>
        /// Observes the eventual outcome of a task nobody awaits, so a fault never resurfaces as an
        /// <c>UnobservedTaskException</c> in an unrelated frame.
        /// <para>
        /// WHY NOT <c>ContinueWith(..., TaskScheduler.Default)</c>: <c>ExecuteSynchronously</c> is a hint,
        /// not a guarantee — the TPL is free to queue the continuation to the thread pool instead, and a
        /// Unity WebGL player has no thread pool, so the fault would stay unobserved there forever. An
        /// <c>await</c> needs no scheduler at all.
        /// </para>
        /// </summary>
        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // Observing IS the purpose: real outcomes are reported through the per-call slots.
            }
        }

        /// <summary>
        /// Shared completion tail of <see cref="CompleteStreamedTurn"/> and
        /// <see cref="CompleteStreamedTurnAsync"/>: arrival-order slot collation, the whole-turn
        /// echo branch, and exactly one consecutive-error record for the turn.
        /// </summary>
        private BatchToolCallResult FinalizeStreamedTurn(StreamedTurn turn)
        {
            if (turn.IsFinalized)
            {
                throw new InvalidOperationException("The streamed turn has already been finalized.");
            }

            turn.IsFinalized = true;
            // Collate strictly in arrival order (batch parity: original call order), independent
            // of completion order. A slot still empty here means its call never produced a result
            // before finalization (cancelled / stream aborted mid-flight): it collates as an
            // explicit failure so a partially-applied turn keeps full result accounting.
            List<MEAI.AIContent> results = new(turn.Slots.Count);
            bool anyFailed = false;
            bool allFailed = true;
            bool[] logicalSuccesses = new bool[turn.Slots.Count];
            for (int slotIndex = 0; slotIndex < turn.Slots.Count; slotIndex++)
            {
                StreamedSlot slot = turn.Slots[slotIndex];
                if (!slot.TryGet(out ToolCallResult r))
                {
                    r = CreateFinalizedFailure(slot.CallId,
                        "Error: Tool call did not complete - the turn was finalized (cancelled " +
                        "or stream aborted) while the call was still in flight.");
                }

                logicalSuccesses[slotIndex] =
                    r.Succeeded || turn.PreviouslySuccessfulSlots.Contains(slot);

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

            // The gate is only safe to dispose once no worker can touch it; a worker abandoned by
            // the bounded drain may still call Release, so in that case the gate is left to the GC
            // (a SemaphoreSlim whose AvailableWaitHandle was never read holds no unmanaged state).
            if (turn.Gate != null && turn.InFlight.TrueForAll(t => t.IsCompleted))
            {
                turn.Gate.Dispose();
            }

            if (turn.Signatures.Count > 0)
            {
                // Check the combined turn signature BEFORE recording the turn's outcome, mirroring
                // ExecuteBatchAsync where BuildDuplicatePlan checks the whole-batch signature first.
                // Contains() means this exact turn already executed earlier in the request: a
                // whole-turn echo. Read-only calls may already have executed because a multi-call
                // signature does not exist mid-stream; mutating calls are deferred and suppressed in
                // CompleteStreamedTurnAsync. This branch restores turn-level error accounting: otherwise
                // successful read-only results reset the counter, and a model stuck
                // echoing the same batch never trips the max-consecutive-errors guard (it runs to the
                // iteration cap instead).
                string combined = BuildStreamedTurnSignature(turn);
                if (_executedSignatures.Contains(combined))
                {
                    string echoDetail =
                        $"Whole-turn echo: identical streamed turn ({turn.Slots.Count} calls) already executed " +
                        "earlier in this request - counted as a failed iteration.";
                    AddTrace(new LlmToolCallTrace("", false, 0d, "duplicate", echoDetail));
                    RecordFailure();
                    return new BatchToolCallResult
                    {
                        Results = results,
                        AnyFailed = true,
                        AllFailed = true
                    };
                }

                // Register only when the turn made progress: a fully-failed turn must stay
                // retryable with identical args (the error feedback explicitly asks for a retry).
                if (!anyFailed)
                {
                    _executedSignatures.Add(combined);
                    _partialBatchSuccesses.Remove(combined);
                }
                else if (!allFailed || logicalSuccesses.Any(succeeded => succeeded))
                {
                    if (_partialBatchSuccesses.TryGetValue(combined, out bool[] existing) &&
                        existing.Length == logicalSuccesses.Length)
                    {
                        for (int i = 0; i < logicalSuccesses.Length; i++)
                        {
                            logicalSuccesses[i] = logicalSuccesses[i] || existing[i];
                        }
                    }

                    if (logicalSuccesses.All(succeeded => succeeded))
                    {
                        _partialBatchSuccesses.Remove(combined);
                        _executedSignatures.Add(combined);
                    }
                    else
                    {
                        _partialBatchSuccesses[combined] = logicalSuccesses;
                    }
                }
            }

            if (turn.Slots.Count > 0)
            {
                // Partial success is forward progress: only an ALL-failed turn counts toward the
                // consecutive-error abort (see ExecuteBatchAsync for the same rule).
                if (anyFailed && allFailed)
                {
                    RecordFailure();
                }
                else
                {
                    RecordSuccess();
                }
            }

            return new BatchToolCallResult
            {
                Results = results,
                AnyFailed = anyFailed,
                AllFailed = turn.Slots.Count > 0 && anyFailed && allFailed
            };
        }

        private static string BuildStreamedTurnSignature(StreamedTurn turn)
        {
            return turn.Signatures.Count == 0
                ? null
                : string.Join("|", turn.Signatures.OrderBy(s => s, StringComparer.Ordinal));
        }

        private static ToolCallResult CreateFinalizedFailure(string callId, string message)
        {
            return new ToolCallResult
            {
                Result = new MEAI.FunctionResultContent(callId, message),
                Succeeded = false
            };
        }

        /// <summary>
        /// Registers a batch signature for the cross-turn echo guard. Called only after the batch
        /// executed with at least one success, so a fully-failed batch stays retryable verbatim.
        /// </summary>
        private void RegisterExecutionOutcome(
            DuplicatePlan plan,
            ToolCallResult[] indexedResults,
            bool anyFailed,
            bool allFailed)
        {
            if (plan?.PendingBatchSignature == null || allFailed)
            {
                return;
            }

            if (!anyFailed)
            {
                _executedSignatures.Add(plan.PendingBatchSignature);
                _partialBatchSuccesses.Remove(plan.PendingBatchSignature);
                return;
            }

            bool[] successes = _partialBatchSuccesses.TryGetValue(plan.PendingBatchSignature, out bool[] existing)
                ? (bool[])existing.Clone()
                : new bool[indexedResults.Length];
            if (successes.Length != indexedResults.Length)
            {
                successes = new bool[indexedResults.Length];
            }

            for (int i = 0; i < indexedResults.Length; i++)
            {
                successes[i] = successes[i] || plan.IsDuplicateIndex[i] || indexedResults[i].Succeeded;
            }

            if (successes.All(succeeded => succeeded))
            {
                _partialBatchSuccesses.Remove(plan.PendingBatchSignature);
                _executedSignatures.Add(plan.PendingBatchSignature);
            }
            else
            {
                _partialBatchSuccesses[plan.PendingBatchSignature] = successes;
            }
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

                    // WHY: MCP-style "isError": only an explicit boolean true is a failure signal;
                    // any other shape stays neutral (top-level only, no nested probing).
                    if (string.Equals(property.Name, "isError", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.Type == JTokenType.Boolean &&
                        property.Value.Value<bool>())
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

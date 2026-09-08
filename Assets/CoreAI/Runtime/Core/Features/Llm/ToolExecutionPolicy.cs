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
        /// <summary>
        /// Что видит модель вместо ПУСТОГО результата инструмента. Пустое tool-сообщение провайдеры
        /// отвергают, а молчаливая подмена на «успех» скрывала бы, что инструменту нечего было сказать —
        /// поэтому конверт честный: <c>empty:true</c> отличает «данных нет» от настоящего ответа, а
        /// <c>ok:true</c> ровно повторяет вердикт <see cref="IsToolResultSuccess"/> для пустой строки.
        /// </summary>
        internal const string EmptyToolResultPayload =
            "{\"ok\":true,\"empty\":true,\"message\":\"The tool returned an empty result. It counts as completed; there is no data to read from it.\"}";

        /// <summary>
        /// Пометка, которой заканчивается обрезанный по <see cref="ICoreAISettings.MaxToolResultChars"/>
        /// результат. Обрезка обязана быть видна модели явно — иначе она дочитывает оборванный JSON как
        /// полный и строит ответ на половине данных.
        /// </summary>
        internal const string TruncatedResultMarker = "...[truncated: ";

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
        private int _hasAbandonedInvocation;

        /// <summary>
        /// Non-zero once a tool declaring <see cref="ILlmTool.EndsTurn"/> completed SUCCESSFULLY in this
        /// request. An <see cref="int"/> written with <see cref="Interlocked"/> rather than a bool field:
        /// <see cref="ExecuteSingleAsync"/> runs concurrently under
        /// <see cref="ICoreAISettings.MaxParallelToolCalls"/> &gt; 1, so several calls may finish on
        /// different threads at once.
        /// </summary>
        private int _turnEndingToolSucceeded;

        /// <summary>
        /// Сигнатуры <c>имя(канонизированные аргументы)</c> ОТДЕЛЬНЫХ вызовов, которые УСПЕШНО
        /// выполнились в предыдущих ходах этого запроса. Ключ per-call, а не по батчу: батчевая
        /// сигнатура пропускала эхо, если модель добавляла или убирала соседний вызов
        /// (ход 1 = [A], ход 2 = [A, B] → A исполнялся второй раз), и обещание доков «свой ключ
        /// идемпотентности не нужен» не выполнялось ровно там, где автор инструмента на него положился.
        /// Регистрируется только успех: упавший вызов обязан оставаться повторяемым с теми же аргументами.
        /// </summary>
        private readonly HashSet<string> _succeededCallSignatures = new();

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
        public bool IsMaxErrorsReached => _consecutiveErrors >= _maxConsecutiveErrors ||
                                          Volatile.Read(ref _hasAbandonedInvocation) != 0;

        /// <summary>
        /// True once a tool declaring <see cref="ILlmTool.EndsTurn"/> completed SUCCESSFULLY during this
        /// request. The agentic loops (<see cref="SmartToolCallingChatClient"/> and the streaming loop in
        /// <c>MeaiLlmClient</c>) read it right after a batch/turn and close the turn instead of sending the
        /// tool result back for another roundtrip — see <see cref="ILlmTool.EndsTurn"/> for why the model
        /// must not get that roundtrip.
        /// <para>
        /// A FAILED call of the same tool deliberately leaves this false: the error result has to reach the
        /// model so it can retry, exactly like every other failure in this class.
        /// </para>
        /// </summary>
        public bool TurnEndingToolSucceeded => Volatile.Read(ref _turnEndingToolSucceeded) != 0;

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
        /// Reset duplicate signatures, error counter, and trace log. An abandoned invocation keeps execution blocked. Call at the start of each
        /// top-level request to allow the same tool to be used across independent requests.
        /// </summary>
        public void Reset()
        {
            _consecutiveErrors = 0;
            Interlocked.Exchange(ref _turnEndingToolSucceeded, 0);
            _succeededCallSignatures.Clear();
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
        /// План одного батча: какие слоты — эхо уже успевшего вызова (заполнены no-op'ом заранее), какие
        /// исполняются, и какая per-call сигнатура регистрируется у слота ПОСЛЕ его успеха.
        /// </summary>
        private sealed class DuplicatePlan
        {
            public ToolCallResult[] IndexedResults = Array.Empty<ToolCallResult>();
            public bool[] IsDuplicateIndex = Array.Empty<bool>();
            public string[] Signatures = Array.Empty<string>();
            public ResolvedCall[] Calls = Array.Empty<ResolvedCall>();
            public bool HasExecutable;
        }

        /// <summary>One immutable resolution shared by scheduling, policy metadata and invocation.</summary>
        private sealed class ResolvedCall
        {
            public MEAI.FunctionCallContent Call;
            public ILlmTool Metadata;
            public ResolvedLlmToolInvocation Invocation;
            public string Error;
        }

        private ResolvedCall ResolveCall(MEAI.FunctionCallContent call, MEAI.ChatOptions options)
        {
            if (options?.ToolMode is MEAI.NoneChatToolMode) return new ResolvedCall { Call = call };
            GetCanonicalToolName(call?.Name, out ILlmTool metadata, out _);
            ResolvedCall resolved = new() { Call = call, Metadata = metadata };
            if (metadata is IResolvedLlmToolCallProvider provider && !HasParseErrorMarker(call?.Arguments))
            {
                if (provider.TryResolveInvocation(call.Arguments, out ResolvedLlmToolInvocation invocation,
                        out string error))
                {
                    resolved.Invocation = invocation;
                    resolved.Metadata = invocation.SourceTool;
                }
                else
                {
                    resolved.Error = error ?? "The delegated tool could not be resolved.";
                }
            }

            return resolved;
        }

        private DuplicatePlan BuildDuplicatePlan(List<MEAI.FunctionCallContent> toolCalls, MEAI.ChatOptions options)
        {
            int count = toolCalls?.Count ?? 0;
            DuplicatePlan plan = new()
            {
                IndexedResults = new ToolCallResult[count],
                IsDuplicateIndex = new bool[count],
                Signatures = new string[count],
                Calls = toolCalls.Select(call => ResolveCall(call, options)).ToArray(),
                HasExecutable = count > 0
            };

            if (options?.ToolMode is MEAI.NoneChatToolMode || _allowDuplicateToolCalls || count == 0)
            {
                return plan;
            }

            // Сравнение только с ПРЕДЫДУЩИМИ ходами: три одинаковых «spawn tree» в одном ходе — законная
            // просьба, и все три исполняются (паритет с Claude/Cursor). Повтор из следующего хода —
            // эхо, и оно гасится по своему ключу независимо от того, что ещё пришло рядом с ним.
            bool anyExecutable = false;
            for (int i = 0; i < count; i++)
            {
                if (TryBuildDuplicateSignature(plan.Calls[i], out string signature))
                {
                    plan.Signatures[i] = signature;
                    if (_succeededCallSignatures.Contains(signature))
                    {
                        plan.IsDuplicateIndex[i] = true;
                        plan.IndexedResults[i] = CreateDuplicateNoOp(toolCalls[i]);
                        continue;
                    }
                }

                anyExecutable = true;
            }

            plan.HasExecutable = anyExecutable;
            return plan;
        }

        /// <summary>
        /// Структурированный no-op для эха: модель получает <c>ok:true, duplicate:true</c> и объяснение, что
        /// вызов уже выполнялся и не повторён. Это НЕ ошибка — ни для модели, ни для счётчика подряд идущих
        /// сбоев: «покажи карточку ещё раз» с теми же аргументами не должно засчитываться как провал хода
        /// и через три повтора обрывать ход сообщением об аборте.
        /// </summary>
        private ToolCallResult CreateDuplicateNoOp(MEAI.FunctionCallContent fc)
        {
            string message = BuildDuplicateNoOpPayload(fc?.Name ?? "");
            AddTrace(new LlmToolCallTrace(fc?.Name ?? "", true, 0d, "duplicate", message));
            return new ToolCallResult
            {
                Result = new MEAI.FunctionResultContent(fc?.CallId, message),
                Succeeded = true
            };
        }

        /// <summary>Текст no-op'а для эха; вынесен, чтобы потоковый и батчевый пути отдавали одно и то же.</summary>
        internal static string BuildDuplicateNoOpPayload(string toolName)
        {
            return JsonConvert.SerializeObject(new
            {
                ok = true,
                duplicate = true,
                message =
                    $"Duplicate tool call '{toolName}' with identical arguments: this exact call already " +
                    "succeeded earlier in this request and was NOT executed again. Use its earlier result; " +
                    "do not repeat the call."
            });
        }

        /// <summary>
        /// Per-call сигнатура <c>имя(канонизированные аргументы)</c> или <c>false</c>, если инструмент
        /// исключён из проверки. Исключение — целиком по флагу автора: <see cref="ILlmTool.AllowDuplicates"/>
        /// означает «повторный идентичный вызов осмыслен» (перезапустить тот же код, перечитать состояние),
        /// и молча отменять это для «особых» имён нельзя — автор строит поведение на обещании доков.
        /// </summary>
        private bool TryBuildDuplicateSignature(ResolvedCall resolved, out string signature)
        {
            MEAI.FunctionCallContent fc = resolved.Call;
            signature = null;
            string canonicalName = GetCanonicalToolName(fc?.Name, out ILlmTool match, out _);
            if (canonicalName == null)
            {
                canonicalName = fc?.Name ?? "";
            }

            if (resolved.Metadata != null && resolved.Metadata.AllowDuplicates)
            {
                return false;
            }

            string argsSig = "";
            try
            {
                argsSig = CanonicalizeArguments(resolved.Invocation?.Arguments ?? fc?.Arguments);
            }
            catch
            {
            }

            signature = $"{resolved.Invocation?.Name ?? canonicalName}({argsSig})";
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
        public Task<ToolCallResult> ExecuteSingleAsync(
            MEAI.FunctionCallContent fc,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            return ExecuteResolvedAsync(ResolveCall(fc, chatOptions), chatOptions, cancellationToken);
        }

        private async Task<ToolCallResult> ExecuteResolvedAsync(
            ResolvedCall resolved,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MEAI.FunctionCallContent fc = resolved.Call;
            if (chatOptions?.ToolMode is MEAI.NoneChatToolMode)
            {
                string disabled = "Error: Tool execution is disabled for this request.";
                RecordSyntheticTrace(fc.Name ?? "", false, 0d, "tools-disabled", disabled);
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, disabled),
                    Succeeded = false
                };
            }
            if (Volatile.Read(ref _hasAbandonedInvocation) != 0)
            {
                string stopped = "Error: Tool execution stopped because an earlier invocation did not observe its deadline.";
                RecordSyntheticTrace(fc.Name, false, 0d, "blocked", stopped);
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, stopped),
                    Succeeded = false
                };
            }
            MEAI.FunctionCallContent repairedFc = TryRepairToolName(fc);
            if (repairedFc == null)
            {
                // Name not found even after case-insensitive search
                string unknown =
                    $"Error: Unknown tool '{fc.Name}'. Available tools: [{string.Join(", ", _originalTools.Select(t => t.Name))}]";
                // Событие сбоя публикуется и для выдуманного моделью имени: подписчик, который ждёт
                // LlmToolCallFailed «в том числе для отсутствующего инструмента», иначе его не увидит,
                // хотя соседняя ветка (имя известно, привязки нет) публикует.
                _eventPublisher.PublishFailed(BuildInfo(fc), unknown, 0d);
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

            if (!string.IsNullOrEmpty(resolved.Error))
            {
                string error = "Error: " + resolved.Error;
                _eventPublisher.PublishFailed(BuildInfo(fc), error, 0d);
                RecordSyntheticTrace(fc.Name, false, 0d, "schema-validation", error);
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, error),
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
                Dictionary<string, object> normalized = null;
                if (fc.Arguments != null)
                {
                    // WHY: shared chokepoint (LlmToolArgumentNormalizer) for ALL tool calls
                    // (native, text-extracted, function-call syntax). A copy keeps the raw
                    // tokens on FunctionCallContent for tracing.
                    normalized = LlmToolArgumentNormalizer.NormalizedCopy(fc.Arguments);

                    args = new MEAI.AIFunctionArguments(normalized);
                }

                // WHY: MEAI owns argument binding. Exceptions crossing its invocation boundary
                // are conservatively treated as possibly invoked, so a retry cannot repeat a mutation.
                ILlmAsyncMarshaler marshaler =
                    _settings.ToolInvocationMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
                int toolTimeoutMs = resolved.Metadata?.ToolTimeoutMsOverride ?? _settings.DefaultToolTimeoutMs;
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
                                    await InvokeResolvedAsync(resolved, aiFunc, args, cts.Token),
                                cts.Token);
                        TraceStep(fc, "invoke-started", invokeTask.IsCompleted ? "sync" : "async");

                        await Task.WhenAny(invokeTask, timeoutDelay);
                        TraceStep(fc, "raced", invokeTask.IsCompleted ? "invoke" : "timeout");

                        // Whoever lost the race stops here: on a timeout this cancellation is what makes the
                        // tool body observe the deadline, on a normal finish it releases the delay the host
                        // still has scheduled. Cancel is idempotent, so one unconditional call covers both.
                        cts.Cancel();
                        if (!invokeTask.IsCompleted)
                        {
                            // WHY: A cancellation-ignoring body cannot hold the request forever. Its
                            // outcome is unknown, so stop future calls and prohibit retrying this turn.
                            Interlocked.Exchange(ref _hasAbandonedInvocation, 1);
                            _ = ObserveAsync(invokeTask);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new OperationCanceledException(cts.Token);
                        }
                        result = await invokeTask;
                        TraceStep(fc, "result-awaited", null);
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
                        async () => await InvokeResolvedAsync(resolved, aiFunc, args, cancellationToken),
                        cancellationToken);
                    TraceStep(fc, "result-awaited", "no-timeout");
                }

                sw.Stop();
                string resultText = NormalizeToolResultText(result);
                bool succeeded = IsToolResultSuccess(resultText);
                int maxResultChars = _settings.MaxToolResultChars;
                if (maxResultChars > 0 && resultText.Length > maxResultChars)
                {
                    int originalLen = resultText.Length;
                    // Обрезка видна модели явной пометкой, а не молча: оборванный JSON без неё дочитывается
                    // как полный. Вердикт успех/сбой уже снят с ПОЛНОГО текста выше.
                    resultText = resultText.Substring(0, maxResultChars) +
                                 $"\n{TruncatedResultMarker}{originalLen} chars total -> {maxResultChars} shown]";
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

                // WHY: the flag is raised HERE, on the only path that knows the call actually produced a
                // successful result. A failing turn-ending tool must keep its ordinary error round: the
                // model is the only thing that can recover from it, and cutting the turn would leave the
                // student in front of a card that was never shown.
                if (succeeded && resolved.Metadata?.EndsTurn == true)
                {
                    Interlocked.Exchange(ref _turnEndingToolSucceeded, 1);
                }

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
                // Подсказка со схемой добавляется по ФОРМЕ исключения — это только текст для модели,
                // чтобы она перевыпустила аргументы, а не гадала по непрозрачному сообщению.
                string errorMessage = ex.Message;
                if (LooksLikeArgumentConversionError(ex))
                {
                    string schemaHint = BuildSchemaRetryHint(fc.Name);
                    if (!string.IsNullOrEmpty(schemaHint))
                    {
                        errorMessage = $"{errorMessage} {schemaHint}";
                    }
                }

                _logger.Error($"[ToolPolicy] {fc.Name} threw: {errorMessage}", LogTag.Llm);
                _eventPublisher.PublishFailed(BuildInfo(fc), errorMessage, 0d);
                // WHY: MEAI owns binding and execution; this boundary cannot prove the body was
                // never entered. Conservatively prevent request retries after a possible side effect.
                AddTrace(new LlmToolCallTrace(fc.Name ?? "", false, 0d, "native", errorMessage));
                LogCallLine(fc, false, 0d, $"threw: {errorMessage}");
                return new ToolCallResult
                {
                    Result = new MEAI.FunctionResultContent(fc.CallId, $"Error: {errorMessage}"),
                    Succeeded = false
                };
            }
        }

        private static async Task<object> InvokeResolvedAsync(ResolvedCall resolved,
            MEAI.AIFunction function, MEAI.AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return resolved.Invocation != null
                ? await resolved.Invocation.InvokeAsync(cancellationToken)
                : await function.InvokeAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Похоже ли исключение вызова на сбой преобразования аргументов. Используется ТОЛЬКО для текста
        /// подсказки со схемой; на источник трассы и retry-безопасность не влияет — это решает
        /// консервативная классификация границы вызова. Проверяется вся цепочка: MEAI часто
        /// заворачивает настоящую JsonException/FormatException во внешнюю InvalidOperationException.
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
        /// <summary>Per-step trace of one tool invocation, emitted only under LogMeaiToolCallingSteps.</summary>
        private void TraceStep(MEAI.FunctionCallContent fc, string step, string detail)
        {
            if (!_settings.LogMeaiToolCallingSteps)
            {
                return;
            }

            _logger.Info(
                $"[ToolPolicy] {fc?.Name}: step={step}{(detail == null ? "" : " " + detail)}",
                LogTag.Llm);
        }

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
        /// Встроенные мутирующие инструменты, которые политика узнаёт ПО ИМЕНИ даже без
        /// <see cref="ILlmTool.IsMutating"/> у их класса: они пишут в общее хранилище (память, реестр
        /// Lua-модов, реестр навыков, мир), и гонка двух таких вызовов теряет записи. Список — только
        /// обратная совместимость для встроенных имён; расширение делается флагом у инструмента, а не
        /// правкой этого файла. Сравнение без учёта регистра, по (возможно исправленному) имени.
        /// </summary>
        private static readonly HashSet<string> BuiltInMutatingToolNames = new(StringComparer.OrdinalIgnoreCase)
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
        /// Обязан ли вызов идти в общей цепочке сериализации мутаций. Правило: инструмент объявил
        /// <see cref="ILlmTool.IsMutating"/> (разрешение по имени из списка роли, как у таймаутов) ИЛИ
        /// его имя — встроенное мутирующее. Все мутирующие вызовы хода делят ОДНУ упорядоченную цепочку,
        /// так что два разных мутирующих инструмента тоже не перекрываются; остальные идут параллельно
        /// под лимитом <see cref="ICoreAISettings.MaxParallelToolCalls"/>.
        /// </summary>
        private bool IsMutatingTool(MEAI.FunctionCallContent fc)
        {
            return IsMutatingToolName(fc?.Name);
        }

        private bool IsMutatingToolName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return false;
            }

            string canonical = GetCanonicalToolName(toolName, out ILlmTool tool, out _) ?? toolName;
            return (tool != null && tool.IsMutating) || BuiltInMutatingToolNames.Contains(canonical);
        }

        /// <summary>
        /// Execute a batch of tool calls, tracking cumulative success/failure.
        /// Returns the list of result contents and an aggregate success flag.
        /// <para>
        /// Concurrency model (<see cref="ICoreAISettings.MaxParallelToolCalls"/>): when the limit is &gt; 1 and
        /// the batch has more than one call, independent tool calls execute concurrently with bounded
        /// parallelism (a <see cref="SemaphoreSlim"/> of that size). Mutating tools
        /// (<see cref="ILlmTool.IsMutating"/> or a name from <see cref="BuiltInMutatingToolNames"/>) are run
        /// on a single ordered serialization chain so they never race each other. Regardless of completion
        /// order, results are collated back into the <b>original call order</b> (indexed array). The
        /// consecutive-error counter is updated exactly once, after ordered collation, with the same
        /// semantics as the sequential path. A value &lt;= 1 (or a single-call batch) takes a
        /// strictly-sequential fast path that is byte-identical to the legacy loop.
        /// Outer cancellation cancels all in-flight calls and propagates as <see cref="OperationCanceledException"/>.
        /// </para>
        /// <para>
        /// Эхо (вызов, чья per-call сигнатура уже успела раньше в этом запросе) не исполняется и получает
        /// структурированный no-op с <c>ok:true</c>. Учёт хода ведётся ТОЛЬКО по исполненным слотам:
        /// no-op — не успех и не сбой, а отсутствие движения. Ход из одних no-op'ов не двигает счётчик
        /// подряд идущих сбоев ни в какую сторону; от модели, которая эхо-ит бесконечно, защищает
        /// лимит roundtrip'ов, а не счётчик ошибок.
        /// </para>
        /// </summary>
        public async Task<BatchToolCallResult> ExecuteBatchAsync(
            List<MEAI.FunctionCallContent> toolCalls,
            MEAI.ChatOptions chatOptions,
            CancellationToken cancellationToken)
        {
            if (toolCalls == null || toolCalls.Count == 0)
            {
                return new BatchToolCallResult { Results = new List<MEAI.AIContent>() };
            }

            // 1. Эхо решается по слотам, чтобы смешанный батч исполнил всё, что не эхо.
            DuplicatePlan duplicatePlan = BuildDuplicatePlan(toolCalls, chatOptions);
            if (!duplicatePlan.HasExecutable)
            {
                return new BatchToolCallResult
                {
                    Results = duplicatePlan.IndexedResults.Select(r => (MEAI.AIContent)r.Result).ToList(),
                    AnyFailed = false,
                    AllFailed = false,
                    AllDuplicates = true
                };
            }

            int maxParallel = Math.Max(1, _settings.MaxParallelToolCalls);

            // 2a. Sequential fast-path: byte-identical to the legacy loop.
            if (maxParallel <= 1 || toolCalls.Count <= 1)
            {
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (!duplicatePlan.IsDuplicateIndex[i])
                    {
                        duplicatePlan.IndexedResults[i] =
                            await ExecuteResolvedAsync(duplicatePlan.Calls[i], chatOptions, cancellationToken);
                    }
                }

                return CollateBatch(duplicatePlan);
            }

            // 2b. Concurrent path with bounded parallelism + serialization of mutating tools.
            ToolCallResult[] indexed = duplicatePlan.IndexedResults;
            using SemaphoreSlim gate = new(maxParallel, maxParallel);

            // Single ordered chain for all mutating tool calls so none of them overlap.
            // Each mutating call awaits the previous mutating call before running.
            Task serialChain = Task.CompletedTask;
            List<Task> tasks = new(toolCalls.Count);

            for (int i = 0; i < toolCalls.Count; i++)
            {
                int index = i;
                MEAI.FunctionCallContent fc = toolCalls[index];
                ResolvedCall resolved = duplicatePlan.Calls[index];
                if (duplicatePlan.IsDuplicateIndex[index])
                {
                    continue;
                }

                if (IsMutatingTool(fc) || resolved.Metadata?.IsMutating == true)
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
                            await ExecuteResolvedAsync(resolved, chatOptions, cancellationToken);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }

            // Awaiting WhenAll surfaces OperationCanceledException on outer cancellation (never swallowed).
            await Task.WhenAll(tasks);

            // 3-4. Collate strictly in original call order, then record the turn exactly once
            // (deterministic regardless of completion order).
            return CollateBatch(duplicatePlan);
        }

        /// <summary>
        /// Общий хвост батча: результаты в исходном порядке вызовов, ОДНА запись в счётчик подряд идущих
        /// сбоев и регистрация per-call сигнатур успевших слотов. Частичный успех — движение вперёд: к
        /// аборту ведёт только батч, где упал КАЖДЫЙ исполненный вызов, иначе три батча «4 из 5 удались»
        /// подряд убивали бы прогон, который на глазах строит сцену. Слоты-эхо в учёте не участвуют.
        /// </summary>
        private BatchToolCallResult CollateBatch(DuplicatePlan plan)
        {
            List<MEAI.AIContent> results = new(plan.IndexedResults.Length);
            int executed = 0;
            int executedFailed = 0;
            for (int i = 0; i < plan.IndexedResults.Length; i++)
            {
                ToolCallResult r = plan.IndexedResults[i];
                results.Add(r.Result);
                if (plan.IsDuplicateIndex[i])
                {
                    continue;
                }

                executed++;
                if (!r.Succeeded)
                {
                    executedFailed++;
                }
                else if (plan.Signatures[i] != null)
                {
                    _succeededCallSignatures.Add(plan.Signatures[i]);
                }
            }

            RecordTurnOutcome(executed, executedFailed);
            return new BatchToolCallResult
            {
                Results = results,
                AnyFailed = executedFailed > 0,
                AllFailed = executed > 0 && executedFailed == executed,
                AllDuplicates = executed == 0
            };
        }

        /// <summary>
        /// Одна запись в счётчик за ход: все исполненные упали — сбой; хоть один удался — успех (сброс);
        /// исполненных нет (ход из одних no-op'ов эха) — счётчик не трогается вовсе, потому что не было
        /// ни ошибки, ни движения.
        /// </summary>
        private void RecordTurnOutcome(int executed, int executedFailed)
        {
            if (executed == 0)
            {
                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.Info(
                        "[ToolPolicy] Echo-only turn (every call already succeeded earlier); error counter untouched",
                        LogTag.Llm);
                }

                return;
            }

            if (executedFailed == executed)
            {
                RecordFailure();
            }
            else
            {
                RecordSuccess();
            }
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

            /// <summary>
            /// Per-call сигнатура каждого слота (<c>null</c> — инструмент исключён из проверки эха),
            /// параллельно <see cref="Slots"/>. Регистрируется при финализации только у успевших слотов.
            /// </summary>
            internal readonly List<string> SlotSignatures = new();

            /// <summary>Слоты, заполненные no-op'ом эха: в учёте хода они не участвуют.</summary>
            internal readonly HashSet<StreamedSlot> DuplicateSlots = new();

            /// <summary>Scheduled (parallel-mode) call tasks, drained at turn completion.</summary>
            internal readonly List<Task> InFlight = new();

            /// <summary>
            /// Effective per-call timeout of each entry in <see cref="InFlight"/>, captured at scheduling
            /// time. The drain deadline is derived from these (see <see cref="ResolveDrainTimeoutMs"/>):
            /// by then the tool names are no longer at hand, and a turn can mix a tool that waits for the
            /// student with ordinary short ones, so a single shared budget has to be the longest of them.
            /// </summary>
            internal readonly List<int> InFlightTimeoutsMs = new();

            internal bool IsFinalized;

            /// <summary>
            /// Single ordered chain for mutating tool calls, batch parity: each mutating call awaits
            /// the previous one so no two mutating tools ever overlap.
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

        /// <summary>Starts a streamed turn (execute-as-you-stream counterpart of one batch).</summary>
        public StreamedTurn BeginStreamedTurn()
        {
            return new StreamedTurn();
        }

        /// <summary>
        /// Executes one tool call the moment it arrives in the stream. Mirrors the batch path per
        /// call: only a CROSS-turn echo (a call whose per-call signature already SUCCEEDED in an
        /// earlier turn of this request) is answered with the same structured no-op the batch path
        /// produces — without executing; exact repeats WITHIN the same turn ("spawn tree x3") execute,
        /// and tools with AllowDuplicates are exempt entirely. The echo check resolves synchronously
        /// at arrival (arrival ORDER is what makes it deterministic) and BEFORE any side effect, for
        /// mutating and read-only tools alike — a per-call key needs no knowledge of the rest of the
        /// turn, which is why mutations are no longer buffered until completion.
        /// <para>
        /// Sequential mode (<see cref="ICoreAISettings.MaxParallelToolCalls"/> &lt;= 1): the call
        /// executes inline and its result is returned, byte-identical to the pre-parallel
        /// behavior. Parallel mode: the call's arrival slot is reserved and a bounded-concurrency
        /// worker is scheduled mirroring the batch concurrent path (mutating tools join the
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
            ResolvedCall resolved = ResolveCall(fc, chatOptions);
            string signature = null;
            bool hasSignature = chatOptions?.ToolMode is not MEAI.NoneChatToolMode &&
                !_allowDuplicateToolCalls && TryBuildDuplicateSignature(resolved, out signature);

            StreamedSlot slot = new(fc.CallId);
            turn.Slots.Add(slot);
            turn.SlotSignatures.Add(hasSignature ? signature : null);

            if (hasSignature && _succeededCallSignatures.Contains(signature))
            {
                ToolCallResult suppressed = CreateDuplicateNoOp(fc);
                turn.DuplicateSlots.Add(slot);
                slot.Set(suppressed);
                return suppressed;
            }

            int maxParallel = Math.Max(1, _settings.MaxParallelToolCalls);
            if (maxParallel <= 1)
            {
                // Sequential fast-path: byte-identical to the pre-parallel streamed behavior.
                ToolCallResult executed =
                    await ExecuteResolvedAsync(resolved, chatOptions, cancellationToken);
                TraceStep(fc, "streamed-sequential-done", executed.Succeeded ? "ok" : "failed");
                slot.Set(executed);
                return executed;
            }

            // Parallel scheduling, batch parity with the ExecuteBatchAsync concurrent path:
            // mutating tools chain onto SerialChain so they never overlap; everything else
            // runs gate-bounded. The per-call result is deferred to CompleteStreamedTurnAsync.
            turn.Gate ??= new SemaphoreSlim(maxParallel, maxParallel);

            // Captured here, not at drain time: this is the last place the call's NAME is available,
            // and the drain deadline must cover the longest budget it scheduled.
            turn.InFlightTimeoutsMs.Add(resolved.Metadata?.ToolTimeoutMsOverride ?? _settings.DefaultToolTimeoutMs);
            if (IsMutatingTool(fc) || resolved.Metadata?.IsMutating == true)
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
                    slot.Set(await ExecuteResolvedAsync(resolved, chatOptions, cancellationToken));
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
        /// counter (batch parity) and registers the per-call signature of every slot that SUCCEEDED,
        /// so a later turn that re-sends any of those calls — through this path or through
        /// <see cref="ExecuteBatchAsync"/> — gets the structured no-op instead of a second execution.
        /// Returns the same shape <see cref="ExecuteBatchAsync"/> would for the whole turn.
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
        /// <see cref="CompleteStreamedTurnAsync"/>: arrival-order slot collation, per-call signature
        /// registration for the slots that succeeded, and exactly one consecutive-error record for
        /// the turn (echo no-op slots take no part in it — same rule as <see cref="CollateBatch"/>).
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
            int executed = 0;
            int executedFailed = 0;
            for (int slotIndex = 0; slotIndex < turn.Slots.Count; slotIndex++)
            {
                StreamedSlot slot = turn.Slots[slotIndex];
                if (!slot.TryGet(out ToolCallResult r))
                {
                    r = CreateFinalizedFailure(slot.CallId,
                        "Error: Tool call did not complete - the turn was finalized (cancelled " +
                        "or stream aborted) while the call was still in flight.");
                }

                results.Add(r.Result);
                if (turn.DuplicateSlots.Contains(slot))
                {
                    continue;
                }

                executed++;
                if (!r.Succeeded)
                {
                    executedFailed++;
                }
                else if (turn.SlotSignatures[slotIndex] != null)
                {
                    _succeededCallSignatures.Add(turn.SlotSignatures[slotIndex]);
                }
            }

            // The gate is only safe to dispose once no worker can touch it; a worker abandoned by
            // the bounded drain may still call Release, so in that case the gate is left to the GC
            // (a SemaphoreSlim whose AvailableWaitHandle was never read holds no unmanaged state).
            if (turn.Gate != null && turn.InFlight.TrueForAll(t => t.IsCompleted))
            {
                turn.Gate.Dispose();
            }

            if (turn.Slots.Count > 0)
            {
                RecordTurnOutcome(executed, executedFailed);
            }

            return new BatchToolCallResult
            {
                Results = results,
                AnyFailed = executedFailed > 0,
                AllFailed = executed > 0 && executedFailed == executed,
                AllDuplicates = turn.Slots.Count > 0 && executed == 0
            };
        }

        private static ToolCallResult CreateFinalizedFailure(string callId, string message)
        {
            return new ToolCallResult
            {
                Result = new MEAI.FunctionResultContent(callId, message),
                Succeeded = false
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

        /// <summary>
        /// Терминальный ответ, когда предел подряд идущих сбоев достигнут, а сводочный ход без
        /// инструментов текста не дал. Это текст ассистента, который увидит ПОЛЬЗОВАТЕЛЬ, — поэтому
        /// обычная фраза с причиной, а не служебный JSON: раньше наружу уходило
        /// <c>{"error":"Agent aborted …"}</c> как реплика, и ученик читал сырой объект.
        /// </summary>
        public MEAI.ChatResponse BuildMaxErrorsResponse()
        {
            _logger.Warn(
                $"[ToolPolicy] Max consecutive errors ({_maxConsecutiveErrors}) reached, stopping.", LogTag.Llm);

            bool hasFailure = false;
            LlmToolCallTrace lastFailure = default;
            lock (_traceLock)
            {
                for (int i = _executedTraces.Count - 1; i >= 0; i--)
                {
                    if (!_executedTraces[i].Success)
                    {
                        lastFailure = _executedTraces[i];
                        hasFailure = true;
                        break;
                    }
                }
            }

            string text =
                $"I could not finish this request: {_maxConsecutiveErrors} tool calls in a row failed, " +
                "so I stopped instead of retrying further.";
            if (hasFailure && !string.IsNullOrWhiteSpace(lastFailure.Name))
            {
                const int maxDetailChars = 200;
                string detail = (lastFailure.Detail ?? "").Replace('\n', ' ').Trim();
                if (detail.Length > maxDetailChars)
                {
                    detail = detail.Substring(0, maxDetailChars) + "...";
                }

                text += string.IsNullOrEmpty(detail)
                    ? $" The last failing tool was '{lastFailure.Name}'."
                    : $" The last failing tool was '{lastFailure.Name}': {detail}";
            }

            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text))
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

            /// <summary>True when at least one EXECUTED call failed. Echo no-op slots never count.</summary>
            public bool AnyFailed;

            /// <summary>
            /// True when every EXECUTED tool call in the batch failed. Used by
            /// <see cref="SmartToolCallingChatClient"/> to mark the iteration's messages as pure error
            /// feedback that can be dropped from history once a later retry succeeds. An echo-only
            /// batch is NOT "all failed" — see <see cref="AllDuplicates"/>.
            /// </summary>
            public bool AllFailed;

            /// <summary>
            /// True when every call of the batch was an echo of a call that already succeeded earlier in
            /// this request: nothing executed, the model got structured no-ops, and the turn is neither
            /// progress nor failure (the consecutive-error counter was left untouched).
            /// </summary>
            public bool AllDuplicates;
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

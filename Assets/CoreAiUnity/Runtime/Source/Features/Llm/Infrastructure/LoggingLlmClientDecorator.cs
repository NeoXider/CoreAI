using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Оборачивает <see cref="ILlmClient"/>: сквозной <see cref="LlmCompletionRequest.TraceId"/>, время вызова,
    /// токены (если бэкенд заполнил <see cref="LlmCompletionResult"/>), превью промптов и ответа.
    /// </summary>
    public sealed class LoggingLlmClientDecorator : ILlmClient
    {
        private const int SystemPreviewChars = 1200;
        private const int UserPreviewChars = 1600;
        private const int ResponsePreviewChars = 2400;

        private readonly ILlmClient _inner;
        private readonly IGameLogger _logger;
        private readonly string _backendLabel;
        private readonly float _requestTimeoutSeconds;

        /// <param name="requestTimeoutSeconds">0 — без лимита; иначе отмена <see cref="CompleteAsync"/> по истечении секунд (совместно с внешним token).</param>
        public LoggingLlmClientDecorator(ILlmClient inner, IGameLogger logger, float requestTimeoutSeconds = 0f)
        {
            _inner = inner;
            _logger = logger;
            _requestTimeoutSeconds = requestTimeoutSeconds < 0f ? 0f : requestTimeoutSeconds;
            _backendLabel = inner?.GetType().Name ?? "?";
        }

        /// <summary>Нижележащий клиент (без декораторов — самый внешний из цепочки).</summary>
        public ILlmClient Inner => _inner;

        /// <summary>Снимает все <see cref="LoggingLlmClientDecorator"/> с вершины цепочки.</summary>
        public static ILlmClient Unwrap(ILlmClient client)
        {
            ILlmClient c = client;
            while (c is LoggingLlmClientDecorator d)
            {
                c = d.Inner;
            }

            return c;
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"LLM | backend={_backendLabel} | request=null");
                return new LlmCompletionResult { Ok = false, Error = "LlmCompletionRequest is null" };
            }

            if (_inner is RoutingLlmClient routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "—" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(роль не задана)"
                : request.AgentRoleId.Trim();
            string system = request.SystemPrompt ?? "";
            string user = request.UserPayload ?? "";
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}→{request.RoutingProfileId.Trim()}";

            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ▶ traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({system.Length} симв.): {Preview(system, SystemPreviewChars)}\n" +
                $"  user ({user.Length} симв.): {Preview(user, UserPreviewChars)}\n" +
                $"  {FormatPromptBudgetLine(system, user, request.Tools)}");

            Stopwatch sw = Stopwatch.StartNew();
            LlmCompletionResult result;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (_requestTimeoutSeconds > 0f)
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
                }

                try
                {
                    result = await _inner.CompleteAsync(request, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    string msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLM ⏱ traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | {msg}");
                    return new LlmCompletionResult { Ok = false, Error = msg };
                }
            }

            sw.Stop();
            double wallMs = sw.Elapsed.TotalMilliseconds;

            if (result == null)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | результат null");
                return new LlmCompletionResult { Ok = false, Error = "null result" };
            }

            if (!result.Ok && _requestTimeoutSeconds > 0f && !cancellationToken.IsCancellationRequested &&
                string.Equals(result.Error, "Cancelled", StringComparison.Ordinal))
            {
                string msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ⏱ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {msg}");
                return new LlmCompletionResult { Ok = false, Error = msg };
            }

            if (!result.Ok)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {result.Error ?? "(без текста)"}");
                return result;
            }

            string content = result.Content ?? "";
            string tokLine = FormatTokenLine(result, wallMs, content.Length, system, user, request.Tools);
            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ◀ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {tokLine}\n" +
                $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");

            return result;
        }

        /// <summary>
        /// Декорированный стриминг: пробрасывает чанки наружу как есть (чтобы UI
        /// видел токены по мере поступления), но параллельно накапливает превью
        /// ответа для финального лога. Таймаут из <c>_requestTimeoutSeconds</c>
        /// применяется ко всему стриму.
        /// </summary>
        /// <remarks>
        /// Без этого override'а default-реализация <see cref="ILlmClient.CompleteStreamingAsync"/>
        /// делала fallback к <see cref="CompleteAsync"/> и выдавала весь ответ одним
        /// чанком в конце генерации — стриминг в UI не был виден.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"LLM stream | backend={_backendLabel} | request=null");
                yield return new LlmStreamChunk { IsDone = true, Error = "LlmCompletionRequest is null" };
                yield break;
            }

            if (_inner is RoutingLlmClient routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "—" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(роль не задана)"
                : request.AgentRoleId.Trim();
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}→{request.RoutingProfileId.Trim()}";
            string streamSystem = request.SystemPrompt ?? "";
            string streamUser = request.UserPayload ?? "";
            IReadOnlyList<ILlmTool> streamTools = request.Tools;

            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ▶ (stream) traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({streamSystem.Length} симв.): {Preview(request.SystemPrompt, SystemPreviewChars)}\n" +
                $"  user ({streamUser.Length} симв.): {Preview(request.UserPayload, UserPreviewChars)}\n" +
                $"  {FormatPromptBudgetLine(streamSystem, streamUser, streamTools)}");

            Stopwatch sw = Stopwatch.StartNew();
            StringBuilder accumulated = new();
            int chunkCount = 0;
            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            string terminalError = null;

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_requestTimeoutSeconds > 0f)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
            }

            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            string initError = null;
            try
            {
                enumerator = _inner.CompleteStreamingAsync(request, linked.Token).GetAsyncEnumerator(linked.Token);
            }
            catch (Exception ex)
            {
                sw.Stop();
                initError = ex.Message;
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | init failed: {ex.Message}");
            }

            if (initError != null)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = initError };
                yield break;
            }

            try
            {
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk current = null;
                    bool failedWithTimeout = false;
                    bool failedWithException = false;
                    string exceptionMessage = null;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failedWithTimeout = true;
                        hasNext = false;
                    }
                    catch (Exception ex)
                    {
                        failedWithException = true;
                        exceptionMessage = ex.Message;
                        hasNext = false;
                    }

                    if (failedWithTimeout)
                    {
                        terminalError = $"LLM stream timeout ({_requestTimeoutSeconds}s)";
                        yield return new LlmStreamChunk { IsDone = true, Error = terminalError };
                        yield break;
                    }

                    if (failedWithException)
                    {
                        terminalError = exceptionMessage;
                        yield return new LlmStreamChunk { IsDone = true, Error = exceptionMessage };
                        yield break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    if (current != null && !string.IsNullOrEmpty(current.Text))
                    {
                        accumulated.Append(current.Text);
                        chunkCount++;
                    }

                    if (current != null)
                    {
                        if (current.PromptTokens.HasValue) promptTokens = current.PromptTokens;
                        if (current.CompletionTokens.HasValue) completionTokens = current.CompletionTokens;
                        if (current.TotalTokens.HasValue) totalTokens = current.TotalTokens;
                        if (!string.IsNullOrEmpty(current.Error)) terminalError = current.Error;
                    }

                    yield return current;
                }
            }
            finally
            {
                sw.Stop();
                if (enumerator != null)
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
                }

                double wallMs = sw.Elapsed.TotalMilliseconds;
                string content = accumulated.ToString();
                LlmCompletionResult synthetic = new()
                {
                    Ok = string.IsNullOrEmpty(terminalError),
                    Content = content,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    Error = terminalError ?? ""
                };
                string tokLine = FormatTokenLine(synthetic, wallMs, content.Length, streamSystem, streamUser, streamTools);

                if (!string.IsNullOrEmpty(terminalError))
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {terminalError}");
                }
                else
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"LLM ◀ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {tokLine}\n" +
                        $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");
                }
            }
        }

        private static string FormatTokenLine(
            LlmCompletionResult result,
            double wallMs,
            int outChars,
            string systemPrompt,
            string userPayload,
            IReadOnlyList<ILlmTool> tools)
        {
            string budgetSuffix = " | " + FormatPromptBudgetLine(systemPrompt ?? "", userPayload ?? "", tools);
            string outWordsPart = outChars > 0
                ? $" | outWords≈{CountWords(result.Content ?? "")}"
                : "";

            if (result.CompletionTokens.HasValue && wallMs > 1)
            {
                double tps = result.CompletionTokens.Value / (wallMs / 1000.0);
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | out≈{tps:F1} tok/s (по completion){outWordsPart}{budgetSuffix}";
            }

            if (result.TotalTokens.HasValue)
            {
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | tok/s н/д{outWordsPart}{budgetSuffix}";
            }

            return
                $"tokens н/д (бэкенд не вернул usage для этого ответа — типично для стриминга/локальных клиентов и части Chat API) | outChars={outChars} | оценка скорости н/д{outWordsPart}{budgetSuffix}";
        }

        /// <summary>
        /// Маркер блока памяти в system, как в <see cref="CoreAI.Ai.AiOrchestrator"/> (BuildRequest).
        /// </summary>
        internal const string OrchestratorMemorySectionDelimiter = "\n\n## Memory\n";

        /// <summary>
        /// Делит <paramref name="systemPrompt"/> на «чистый» системный текст и тело памяти после <see cref="OrchestratorMemorySectionDelimiter"/>.
        /// </summary>
        internal static void SplitSystemCoreAndMemory(string systemPrompt, out string corePrompt, out string memoryBody)
        {
            corePrompt = systemPrompt ?? "";
            memoryBody = "";
            if (string.IsNullOrEmpty(systemPrompt))
            {
                return;
            }

            int idx = systemPrompt.IndexOf(OrchestratorMemorySectionDelimiter, StringComparison.Ordinal);
            if (idx < 0)
            {
                return;
            }

            corePrompt = systemPrompt.Substring(0, idx).TrimEnd();
            memoryBody = systemPrompt.Substring(idx + OrchestratorMemorySectionDelimiter.Length).Trim();
        }

        /// <summary>
        /// Грубая оценка размера промпта для логов и бюджетирования, когда API не возвращает usage.
        /// estTok = ceil(chars/4) на каждую часть (эвристика для латиницы/смеси; для оптимизации сравнивайте относительные величины).
        /// Слова — по пробелам (для русского без дефисов как разделителей слов).
        /// Разбор system: всего / core (промпт без ## Memory) / memory / оценка каталога tools (имя+описание+schema;
        /// для LLMUnity близко к тексту, добавляемому к system внутри адаптера; при native tool calling JSON может отличаться).
        /// </summary>
        internal static string FormatPromptBudgetLine(
            string systemPrompt,
            string userPayload,
            IReadOnlyList<ILlmTool> tools = null)
        {
            SplitSystemCoreAndMemory(systemPrompt ?? "", out string core, out string mem);
            int sysChars = systemPrompt?.Length ?? 0;
            int coreChars = core.Length;
            int memChars = mem.Length;
            int toolsChars = EstimateToolsCatalogChars(tools);
            int toolCount = tools?.Count ?? 0;

            int chatChars = userPayload?.Length ?? 0;
            int coreTok = EstimateTokensRough(core);
            int memTok = EstimateTokensRough(mem);
            int toolsTok = EstimateTokensRoughFromCharCount(toolsChars);
            int chatTok = EstimateTokensRough(userPayload);
            int coreWords = CountWords(core);
            int memWords = CountWords(mem);
            int toolsWords = CountWords(BuildToolsCatalogBlobForWordCount(tools));
            int chatWords = CountWords(userPayload);

            int sysTokFromParts = coreTok + memTok;
            int sysTokWhole = EstimateTokensRough(systemPrompt);
            return
                $"promptBudget systemSplit chars total={sysChars} core={coreChars} memory={memChars} toolsDef≈{toolsChars}({toolCount} tools) " +
                $"| system estTok≈{sysTokWhole} (core≈{coreTok} mem≈{memTok} toolsDef≈{toolsTok}; partsSum≈{sysTokFromParts + toolsTok}) " +
                $"| system words≈{coreWords + memWords + toolsWords} (core≈{coreWords} mem≈{memWords} tools≈{toolsWords}) " +
                $"| chat chars={chatChars} estTok≈{chatTok} words≈{chatWords} " +
                $"[estTok=⌈chars/4⌉; toolsDef≈размер описаний инструментов]";
        }

        /// <summary>
        /// Оценка символов, которые LLMUnity добавляет к system при наличии tools (см. LlmUnityMeaiChatClient).
        /// </summary>
        internal static int EstimateToolsCatalogChars(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return 0;
            }

            int n = LlmUnityToolsRulesPreambleCharCount;
            foreach (ILlmTool t in tools)
            {
                if (t == null)
                {
                    continue;
                }

                string name = t.Name ?? "";
                string desc = t.Description ?? "";
                string schema = t.ParametersSchema ?? "";
                n += "- name: ".Length + name.Length + "\n  description: ".Length + desc.Length + "\n".Length;
                n += "  parameters schema: ".Length + schema.Length + "\n".Length;
            }

            return n;
        }

        private static int LlmUnityToolsRulesPreambleCharCount =>
            "\n\nCRITICAL SYSTEM RULES FOR TOOLS:\n".Length +
            "1. You have access to the following tools. You MUST use one if it matches the user request.\n".Length +
            "2. To use a tool, output ONLY valid JSON matching this format: ```json\n{\"name\": \"tool_name\", \"arguments\": {\"arg\": \"val\"}}\n```\n".Length +
            "3. DO NOT output conversational text if you call a tool. ONLY output the JSON block.\n\nAVAILABLE TOOLS:\n".Length;

        private static string BuildToolsCatalogBlobForWordCount(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new();
            foreach (ILlmTool t in tools)
            {
                if (t == null)
                {
                    continue;
                }

                sb.Append(t.Name);
                sb.Append(' ');
                sb.Append(t.Description);
                sb.Append(' ');
                sb.Append(t.ParametersSchema);
                sb.Append(' ');
            }

            return sb.ToString();
        }

        private static int EstimateTokensRoughFromCharCount(int charCount)
        {
            if (charCount <= 0)
            {
                return 0;
            }

            return (charCount + 3) / 4;
        }

        internal static int EstimateTokensRough(string text)
        {
            return EstimateTokensRoughFromCharCount(string.IsNullOrEmpty(text) ? 0 : text.Length);
        }

        internal static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int count = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ws = char.IsWhiteSpace(c);
                if (!ws && !inWord)
                {
                    inWord = true;
                    count++;
                }
                else if (ws)
                {
                    inWord = false;
                }
            }

            return count;
        }

        private static string Fmt(int? n)
        {
            return n.HasValue ? n.Value.ToString() : "—";
        }

        private static string Preview(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(пусто)";
            }

            string t = text.Trim();
            if (t.Length <= maxChars)
            {
                return t;
            }

            return t.Substring(0, maxChars) + $"... [+{t.Length - maxChars} симв.]";
        }
    }
}
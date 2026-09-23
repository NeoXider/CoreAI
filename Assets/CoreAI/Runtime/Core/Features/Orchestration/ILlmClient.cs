using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stable error category for LLM failures that UI, retry policies, and backend integrations can handle without parsing strings.
    /// </summary>
    public enum LlmErrorCode
    {
        /// <summary>No error was reported.</summary>
        None = 0,

        /// <summary>The request exceeded its timeout.</summary>
        Timeout = 1,

        /// <summary>The caller cancelled the request.</summary>
        Cancelled = 2,

        /// <summary>The provider returned no usable content.</summary>
        EmptyResponse = 3,

        /// <summary>Authorization failed or the token has expired.</summary>
        AuthExpired = 4,

        /// <summary>The backend or local limiter rejected the request because quota was exhausted.</summary>
        QuotaExceeded = 5,

        /// <summary>The provider or backend rate limited the request.</summary>
        RateLimited = 6,

        /// <summary>The provider or backend is unavailable.</summary>
        BackendUnavailable = 7,

        /// <summary>The request was malformed or rejected by validation.</summary>
        InvalidRequest = 8,

        /// <summary>
        /// The provider returned an error that does not fit a more specific category.
        /// <para>
        /// Treated as TRANSIENT by the retry/fallback decorators, so a failure that a replay can never
        /// fix must not land here — see <see cref="PermanentProviderError"/> and
        /// <see cref="PaymentRequired"/>.
        /// </para>
        /// </summary>
        ProviderError = 9,

        /// <summary>Routing could not resolve a usable backend.</summary>
        RoutingError = 10,

        /// <summary>The request exceeded the model or provider context window.</summary>
        ContextLengthExceeded = 11,

        /// <summary>
        /// The provider account cannot pay for the request (HTTP 402): credits or balance are exhausted.
        /// Replaying the identical request with the identical credentials returns the identical answer,
        /// so this is never retried and never used to switch backends.
        /// </summary>
        PaymentRequired = 12,

        /// <summary>
        /// The provider refused the request permanently and no more specific category applies
        /// (an unclassified 4xx). Same-request replay is pointless, so it is never retried.
        /// </summary>
        PermanentProviderError = 13,

        /// <summary>
        /// A LOCAL client-side cap rejected the request before it left the process: the
        /// <c>ClientLimited</c> per-session request budget or its prompt-size cap. Neither the account
        /// quota (<see cref="QuotaExceeded"/>) nor a provider rate limit (<see cref="RateLimited"/>) is
        /// involved — the backend was never asked. Never retried, never used to switch backends.
        /// </summary>
        ClientLimitExceeded = 14
    }

    /// <summary>
    /// Exception type used by LLM adapters to preserve structured failure details across abstraction layers.
    /// </summary>
    public sealed class LlmClientException : Exception
    {
        /// <summary>Creates an exception with structured LLM failure metadata.</summary>
        public LlmClientException(
            string message,
            LlmErrorCode errorCode = LlmErrorCode.ProviderError,
            int? httpStatus = null,
            int? retryAfterSeconds = null,
            string providerErrorBody = null)
            : base(message ?? "")
        {
            ErrorCode = errorCode;
            HttpStatus = httpStatus;
            RetryAfterSeconds = retryAfterSeconds;
            ProviderErrorBody = providerErrorBody ?? "";
        }

        /// <summary>Stable failure category.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>HTTP status code when the failure came from an HTTP transport.</summary>
        public int? HttpStatus { get; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; }
    }

    /// <summary>
    /// Raised when the chat/orchestrator layer cancels the request due to <see cref="ICoreAISettings.LlmRequestTimeoutSeconds"/>
    /// while the caller's own <see cref="CancellationToken"/> was not cancelled (library timeout, not user stop).
    /// Inherits <see cref="OperationCanceledException"/> so existing cancellation handlers still run; use
    /// <c>is LlmOperationTimeoutException</c> or <see cref="Messaging.LlmRequestCompleted"/> <c>ErrorCode</c> to distinguish.
    /// </summary>
    public sealed class LlmOperationTimeoutException : OperationCanceledException
    {
        public LlmOperationTimeoutException()
            : base("LLM request timed out.")
        {
        }
    }

    /// <summary>
    /// The one rule every layer uses to tell a library timeout from a cancellation.
    /// <list type="number">
    /// <item>The caller's own token was cancelled: <see cref="LlmErrorCode.Cancelled"/>, whatever the
    /// exception is - an <see cref="OperationCanceledException"/>, a library timeout, a typed
    /// <see cref="LlmClientException"/> or an untyped transport fault. The caller asked to stop; whatever the
    /// pipeline reported while stopping is the stop, for the user-facing code, the completion metric and the
    /// circuit breaker alike. Endpoint health is the one consumer that still looks at the fault itself: a
    /// permanent refusal that raced the cancel is found through <see cref="FindClientException"/>.</item>
    /// <item>An <see cref="LlmOperationTimeoutException"/> (also inside an <see cref="AggregateException"/>):
    /// <see cref="LlmErrorCode.Timeout"/>. The library raises that type only when its own deadline fired
    /// while the caller's token was alive.</item>
    /// <item>Any other <see cref="OperationCanceledException"/> (also inside an <see cref="AggregateException"/>):
    /// <see cref="LlmErrorCode.Cancelled"/> - someone other than the caller stopped the work
    /// (<c>CoreAi.StopAgent</c>, a cancellation scope, a disposed orchestrator). It is not a timeout, and it
    /// must not be presented as one.</item>
    /// <item>Anything else while the caller is alive: <see cref="LlmErrorCode.None"/> - a failure the layer
    /// classifies by its own means (the typed code of an <see cref="LlmClientException"/>, or a provider error).</item>
    /// </list>
    /// </summary>
    public static class LlmCancellation
    {
        /// <summary>Error text every layer uses for a failure it reports as the caller's cancellation.</summary>
        public const string CancelledErrorText = "cancelled";

        /// <summary>
        /// Classifies <paramref name="exception"/> against the caller's token (see the class summary). Returns
        /// <see cref="LlmErrorCode.None"/> only while the caller is alive and the exception is neither a timeout
        /// nor a cancellation.
        /// </summary>
        public static LlmErrorCode Classify(Exception exception, CancellationToken callerToken)
        {
            // WHY no look at the exception once the caller has cancelled: a transport that is being torn down
            // reports the fallout (a disposed socket, an aborted request, a timer that raced the stop), not the
            // cause. Reading the fault instead of the token made the layers disagree - one presented a
            // provider error, the next counted an outage, a third retried - about a request nobody was
            // waiting for.
            if (callerToken.IsCancellationRequested)
            {
                return LlmErrorCode.Cancelled;
            }

            if (exception == null)
            {
                return LlmErrorCode.None;
            }

            if (FindTimeout(exception) != null)
            {
                return LlmErrorCode.Timeout;
            }

            // WHY the InnerException chain is NOT walked here: while the token is alive, a transport error
            // with a nested TaskCanceledException is a failure, and reading it as a stop hid real outages.
            return IsCancellationLike(exception) ? LlmErrorCode.Cancelled : LlmErrorCode.None;
        }

        /// <summary>
        /// The <see cref="OperationCanceledException"/> a layer raises for a fault it observed after the caller
        /// cancelled: the caller's cancellation, with the fault attached as <see cref="Exception.InnerException"/>
        /// so diagnostics and endpoint health (<see cref="FindClientException"/>) can still see what happened.
        /// </summary>
        /// <param name="fault">The exception the inner layer raised.</param>
        /// <param name="callerToken">The caller's (cancelled) token.</param>
        /// <param name="source">What failed, for the message: "the primary", "the committed stream".</param>
        public static OperationCanceledException WrapAsCancellation(
            Exception fault,
            CancellationToken callerToken,
            string source)
        {
            return new OperationCanceledException(
                $"The request was cancelled by the caller; {source} then failed with {fault?.GetType().Name ?? "an unknown fault"}.",
                fault,
                callerToken);
        }

        /// <summary>
        /// The first <see cref="LlmClientException"/> carried by <paramref name="exception"/>: the exception
        /// itself, its <see cref="Exception.InnerException"/> chain, or the members of an
        /// <see cref="AggregateException"/>; <c>null</c> when there is none.
        /// <para>
        /// WHY the chain is walked here, unlike in <see cref="Classify(Exception, CancellationToken)"/>: this is
        /// for endpoint health, not for the caller. A layer that turned a post-cancel fault into
        /// <see cref="OperationCanceledException"/> (<see cref="WrapAsCancellation"/>) keeps the refusal
        /// attached, and an expired key or an exhausted balance is a fact about the endpoint whether or not the
        /// caller was still listening.
        /// </para>
        /// </summary>
        public static LlmClientException FindClientException(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is LlmClientException typed)
                {
                    return typed;
                }

                if (current is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        LlmClientException found = FindClientException(inner);
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// <see cref="Classify(Exception, CancellationToken)"/> for a host that runs its OWN deadline on a separate
        /// token: a cancellation observed while <paramref name="deadlineToken"/> had fired and
        /// <paramref name="callerToken"/> was still alive is that host's timeout, not a stop.
        /// <para>
        /// WHY a separate token: a deadline armed on the caller's own token (<c>CancelAfter</c> on the token handed
        /// to a turn) is indistinguishable from a stop - by the time anyone looks, the token is simply cancelled.
        /// Keep the deadline apart and pass both, or raise <see cref="LlmOperationTimeoutException"/> yourself.
        /// </para>
        /// </summary>
        public static LlmErrorCode Classify(
            Exception exception,
            CancellationToken callerToken,
            CancellationToken deadlineToken)
        {
            return ApplyDeadline(Classify(exception, callerToken), callerToken, deadlineToken);
        }

        /// <summary>
        /// The rule of <see cref="Classify(Exception, CancellationToken)"/> for a failure reported as DATA - the
        /// code of a failed result or of a terminal chunk: once the caller's token is cancelled, every failure
        /// code is <see cref="LlmErrorCode.Cancelled"/>; while the caller is alive the code passes through
        /// unchanged. <see cref="LlmErrorCode.None"/> means "no failure" and always passes through.
        /// <para>
        /// WHY the same rule as for a thrown fault: a pipeline that is being torn down reports the stop either
        /// way, and a returned provider error that kept its code while the thrown one read as the stop showed
        /// one user action as two different outcomes. A layer that rewrites the code reissues the item with
        /// <see cref="CancelledErrorText"/> through <c>WithError</c>; endpoint health keeps judging the code the
        /// endpoint itself reported, before the rewrite.
        /// </para>
        /// </summary>
        public static LlmErrorCode ClassifyCode(LlmErrorCode code, CancellationToken callerToken)
        {
            return code != LlmErrorCode.None && callerToken.IsCancellationRequested
                ? LlmErrorCode.Cancelled
                : code;
        }

        /// <summary>
        /// True when a failure reported as data (a failed result, an error chunk) whose own code is
        /// <paramref name="reported"/> must reach the caller as the caller's cancellation and does not already
        /// say so: <see cref="ClassifyCode(LlmErrorCode, CancellationToken)"/>, with a failure that carries no
        /// code read as the <see cref="LlmErrorCode.ProviderError"/> every layer takes it for. Call it only for
        /// an item that failed.
        /// </summary>
        internal static bool IsReportedCallerStop(LlmErrorCode reported, CancellationToken callerToken)
        {
            LlmErrorCode failure = reported == LlmErrorCode.None ? LlmErrorCode.ProviderError : reported;
            return reported != LlmErrorCode.Cancelled &&
                   ClassifyCode(failure, callerToken) == LlmErrorCode.Cancelled;
        }

        /// <summary>
        /// <see cref="ClassifyCode(LlmErrorCode, CancellationToken)"/> with a separate host deadline, by the same
        /// rule as <see cref="Classify(Exception, CancellationToken, CancellationToken)"/>: a caller that cancelled
        /// owns every failure, and a <see cref="LlmErrorCode.Cancelled"/> seen while only the deadline fired is
        /// that deadline's <see cref="LlmErrorCode.Timeout"/>.
        /// </summary>
        public static LlmErrorCode ClassifyCode(
            LlmErrorCode code,
            CancellationToken callerToken,
            CancellationToken deadlineToken)
        {
            return ApplyDeadline(ClassifyCode(code, callerToken), callerToken, deadlineToken);
        }

        private static LlmErrorCode ApplyDeadline(
            LlmErrorCode code,
            CancellationToken callerToken,
            CancellationToken deadlineToken)
        {
            return code == LlmErrorCode.Cancelled &&
                   deadlineToken.IsCancellationRequested &&
                   !callerToken.IsCancellationRequested
                ? LlmErrorCode.Timeout
                : code;
        }

        /// <summary>True when <see cref="Classify(Exception, CancellationToken)"/> reports <see cref="LlmErrorCode.Timeout"/>.</summary>
        public static bool IsTimeout(Exception exception, CancellationToken callerToken)
        {
            return Classify(exception, callerToken) == LlmErrorCode.Timeout;
        }

        /// <summary>
        /// True when <see cref="Classify(Exception, CancellationToken)"/> reports <see cref="LlmErrorCode.Cancelled"/>: the work was
        /// stopped on purpose and must be neither retried nor presented as "the service is not responding".
        /// </summary>
        public static bool IsCancellation(Exception exception, CancellationToken callerToken)
        {
            return Classify(exception, callerToken) == LlmErrorCode.Cancelled;
        }

        /// <summary>
        /// The <see cref="LlmOperationTimeoutException"/> carried by <paramref name="exception"/> directly or
        /// inside an <see cref="AggregateException"/>; <c>null</c> when there is none.
        /// </summary>
        public static LlmOperationTimeoutException FindTimeout(Exception exception)
        {
            switch (exception)
            {
                case LlmOperationTimeoutException typed:
                    return typed;
                case AggregateException aggregate:
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        LlmOperationTimeoutException found = FindTimeout(inner);
                        if (found != null)
                        {
                            return found;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        private static bool IsCancellationLike(Exception exception)
        {
            switch (exception)
            {
                case OperationCanceledException:
                    return true;
                case AggregateException aggregate:
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (IsCancellationLike(inner))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }
    }

    /// <summary>Input for one <see cref="ILlmClient.CompleteAsync"/> call: role, prompts, tracing.</summary>
    public sealed class LlmCompletionRequest
    {
        /// <summary>Stable actor id admitted for this request.</summary>
        public string ActorId { get; set; } = "";

        /// <summary>Role id for backend routing and system prompt selection.</summary>
        public string AgentRoleId { get; set; } = "";

        /// <summary>System instruction for the model.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>Optional user-facing payload when <see cref="ChatHistory"/> is absent or supplementary.</summary>
        public string UserPayload { get; set; } = "";

        /// <summary>Optional MEAI chat history.</summary>
        public IList<Microsoft.Extensions.AI.ChatMessage> ChatHistory { get; set; }

        /// <summary>
        /// Optional files attached to the current user turn. When non-empty, the client builds the current-turn
        /// user message via <see cref="AiUserMessageBuilder.BuildUserMessage"/> (image parts for vision models,
        /// inlined text blocks for every model) instead of wrapping <see cref="UserPayload"/> as plain text.
        /// <c>null</c>/empty preserves the legacy plain-text path.
        /// </summary>
        public IReadOnlyList<AiAttachment> Attachments { get; set; }

        /// <summary>End-to-end trace id (orchestrator / LLM decorator / command router).</summary>
        public string TraceId { get; set; } = "";

        /// <summary>
        /// Stable idempotency token for this logical turn (HTTP <c>Idempotency-Key</c>).
        /// Leave empty to auto-assign once per request object; preserved across decorator retries.
        /// </summary>
        public string IdempotencyKey { get; set; } = "";

        /// <summary>Short backend label after role routing (LLM logs).</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Context budget in tokens (default 128K; routing may override).</summary>
        public int ContextWindowTokens { get; set; } = CoreAISettings.DefaultContextWindowTokens;

        /// <summary>Optional max completion tokens for the model.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Optional per-request cap on tool-call roundtrips (iterations of LLM call + tool batch).
        /// <c>null</c> = inherit the global <see cref="ICoreAISettings.MaxToolCallRoundtrips"/>;
        /// <c>0</c> = UNLIMITED (no safety valve — e.g. a free-build visual scene that emits many spawns);
        /// any positive value caps the loop at that many roundtrips.
        /// </summary>
        public int? MaxToolCallRoundtrips { get; set; }

        /// <summary>
        /// Temperature.
        /// </summary>
        public float Temperature { get; set; } = 0.1f;

        /// <summary>
        /// When <c>true</c>, <see cref="Temperature"/> is passed to backends (MEAI / HTTP). When <c>false</c>,
        /// temperature is omitted so providers and LLMUnity use their defaults.
        /// </summary>
        public bool SendTemperature { get; set; }

        /// <summary>Tools exposed to the model for this request.</summary>
        public IReadOnlyList<ILlmTool> Tools { get; set; }

        /// <summary>Optional per-request allowlist of tool names after orchestrator filtering.</summary>
        public IReadOnlyList<string> AllowedToolNames { get; set; }

        /// <summary>Allow the same tool with identical args back-to-back; <c>null</c> defers to global settings.</summary>
        public bool? AllowDuplicateToolCalls { get; set; }

        /// <summary>
        /// How the LLM backend should treat tool selection for this request.
        /// <see cref="LlmToolChoiceMode.Auto"/> = provider default (model decides).
        /// Adapters in the Unity layer translate this to <c>ChatOptions.ToolMode</c>
        /// (Microsoft.Extensions.AI) or to the equivalent provider-native field.
        /// </summary>
        public LlmToolChoiceMode ForcedToolMode { get; set; } = LlmToolChoiceMode.Auto;

        /// <summary>
        /// Tool name to require when <see cref="ForcedToolMode"/> is
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/>. Must match an <see cref="ILlmTool.Name"/>
        /// in <see cref="Tools"/>. Ignored for other modes.
        /// </summary>
        public string RequiredToolName { get; set; } = "";

        /// <summary>
        /// Opt-in: interpret the assistant's PROSE as tool calls even on an endpoint that advertises a
        /// native tool channel. Default (<c>null</c>/<c>false</c>) does not interpret it.
        /// <para>
        /// The safe default is the whole point. Parsing prose means acting on what the model SAID instead
        /// of on the channel it said it through, and a tutor explaining JSON — the everyday job of a
        /// programming teacher — writes objects that look exactly like a tool call. The framework would
        /// execute the example instead of showing it. Where a native channel exists, calls arrive on it
        /// and the text needs no interpretation at all.
        /// </para>
        /// <para>
        /// Set it to <c>true</c> only for an endpoint you have OBSERVED advertising native tool calling and
        /// then answering with JSON in the text (proxies in front of local models do this). The symptom is
        /// a tool that never runs while the reply contains its call. Nothing in the runtime sets it.
        /// </para>
        /// </summary>
        public bool? AllowTextShapedToolCallsOnNativeEndpoint { get; set; }
    }

    /// <summary>
    /// Diagnostic record describing one tool call observed during an LLM turn.
    /// Captured by the tool-calling cycle (native FunctionCallContent or text-extracted JSON)
    /// regardless of streaming/non-streaming path, so the logging decorator can surface
    /// what tools the model actually invoked. Failure source is captured in <see cref="Success"/>.
    /// </summary>
    public readonly struct LlmToolCallTrace
    {
        /// <summary>Constructs a trace entry for one observed tool call.</summary>
        public LlmToolCallTrace(string name, bool success, double durationMs, string source, string detail = "")
        {
            Name = name ?? "";
            Success = success;
            DurationMs = durationMs;
            Source = source ?? "";
            Detail = detail ?? "";
        }

        /// <summary>
        /// The name the call was made under: <see cref="ILlmTool.Name"/> for an ordinary tool, the function name
        /// (<c>camera_look</c>) for a function of an <see cref="IAIFunctionsLlmTool"/> wrapper, whose own name
        /// (<c>camera</c>) the provider never sees.
        /// </summary>
        public string Name { get; }

        /// <summary>True if the tool returned a non-error result (no <c>"success":false</c>) and did not throw.</summary>
        public bool Success { get; }

        /// <summary>Wall-clock execution time in milliseconds.</summary>
        public double DurationMs { get; }

        /// <summary>How this call was discovered: <c>native</c> (FunctionCallContent), <c>text</c> (extracted JSON), or <c>duplicate</c> / <c>missing</c>.</summary>
        public string Source { get; }

        /// <summary>Tool result or failure detail for UI fallback, diagnostics, and durable tool-result memory.</summary>
        public string Detail { get; }
    }

    /// <summary>Model completion: text, error state, optional usage.</summary>
    public sealed class LlmCompletionResult
    {
        /// <summary>When <c>false</c>, inspect <see cref="Error"/>.</summary>
        public bool Ok { get; set; }

        /// <summary>
        /// Visible assistant answer (or JSON command payload). This is the only completion field that
        /// consumers may display, publish, or append to agent memory/history. Provider reasoning is never
        /// promoted into this field, including when the provider returns empty content.
        /// </summary>
        public string Content { get; set; } = "";

        /// <summary>
        /// Hidden model reasoning for this completion (provider <c>reasoning_content</c> and/or
        /// inline <c>&lt;think&gt;</c> blocks). Empty when the model produced none. Kept separate
        /// from <see cref="Content"/> so hosts can render a collapsible "thinking" section without
        /// polluting the visible answer. Treat this as ephemeral diagnostics: never copy it into
        /// MemoryTool, chat history, generated notes, command payloads, or automatic assistant records.
        /// </summary>
        public string ReasoningContent { get; set; } = "";

        /// <summary>Failure or cancellation message.</summary>
        public string Error { get; set; } = "";

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; set; } = "";

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Set from OpenAI-compatible HTTP when the payload includes <c>usage</c>.</summary>
        public int? PromptTokens { get; set; }

        /// <summary>
        /// Prompt tokens of the LAST tool roundtrip only (actual context width), for prompt-size
        /// calibration. <see cref="PromptTokens"/> stays cumulative across the turn so
        /// <c>Prompt + Completion == Total</c> holds for cost telemetry.
        /// </summary>
        public int? LastRoundtripPromptTokens { get; set; }

        /// <summary>Completion tokens from usage (HTTP).</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>Total tokens from usage (HTTP).</summary>
        public int? TotalTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens read from cache.</summary>
        public int CacheReadTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens written to cache.</summary>
        public int CacheWriteTokens { get; set; }

        /// <summary>
        /// Tool calls observed during this turn (native + text-extracted, in execution order).
        /// Empty when the model produced only text. Used by <see cref="LoggingLlmClientDecorator"/>
        /// to log <c>tools=[name(ok,12ms),name(fail,4ms)]</c>.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> ExecutedToolCalls { get; set; } = Array.Empty<LlmToolCallTrace>();

        /// <summary>
        /// A copy with the failure rewritten to <paramref name="error"/> / <paramref name="errorCode"/>; every
        /// other field is carried over. WHY a copy: an inner client may reuse or cache the instance it returned,
        /// so a decorator that re-classifies a failure must not mutate what it received.
        /// </summary>
        public LlmCompletionResult WithError(string error, LlmErrorCode errorCode)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Content = Content,
                ReasoningContent = ReasoningContent,
                Error = error ?? "",
                ErrorCode = errorCode,
                HttpStatus = HttpStatus,
                RetryAfterSeconds = RetryAfterSeconds,
                ProviderErrorBody = ProviderErrorBody,
                Model = Model,
                PromptTokens = PromptTokens,
                LastRoundtripPromptTokens = LastRoundtripPromptTokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = TotalTokens,
                CacheReadTokens = CacheReadTokens,
                CacheWriteTokens = CacheWriteTokens,
                ExecutedToolCalls = ExecutedToolCalls
            };
        }
    }

    /// <summary>One streaming chunk: text fragment and completion markers.</summary>
    public sealed class LlmStreamChunk
    {
        /// <summary>
        /// Visible assistant text delta (may be empty on terminal chunks). Consumers build the displayed
        /// and persistable answer from this field only.
        /// </summary>
        public string Text { get; set; } = "";

        /// <summary>
        /// <c>true</c> on the FIRST visible chunk of a NEW assistant message inside one and the same
        /// stream. A single request produces several replies: after every tool round the model starts
        /// speaking anew, while the consumer still sees one continuous stream of chunks.
        /// <para>
        /// Without this marker the consumer has no way to tell "a continuation of the same reply" from
        /// "the next one has started". The price of getting it wrong is visible to the learner: the
        /// accumulator glued the end of one reply directly onto the start of another, and the chat showed
        /// "Check yourself:**Turn complete - waiting for the learner's answer.**" - a colon flush against
        /// a capital letter, two different messages stuck into one.
        /// </para>
        /// <para>
        /// The marker is set on EXACTLY one chunk (the first visible chunk of an iteration, except the
        /// very first one), not on every chunk of a new reply: the consumer must react to it once - a
        /// separator in the accumulator, a new bubble in the UI. Guessing the boundary from punctuation
        /// is not an option: a reply may legitimately end with a colon and legitimately begin with a
        /// lowercase letter.
        /// </para>
        /// </summary>
        public bool StartsNewMessage { get; set; }

        /// <summary>
        /// Hidden reasoning delta (provider <c>reasoning_content</c> or inline <c>&lt;think&gt;</c>
        /// spans). Never part of <see cref="Text"/>: consumers that only read <see cref="Text"/>
        /// keep a clean visible answer, while UIs may accumulate this into an ephemeral "thinking"
        /// section. Never append it to MemoryTool, chat history, generated notes, command payloads,
        /// or automatic assistant records.
        /// </summary>
        public string ReasoningText { get; set; } = "";

        /// <summary><c>true</c> when the stream has finished.</summary>
        public bool IsDone { get; set; }

        /// <summary>Streaming failure text, if any.</summary>
        public string Error { get; set; }

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Usage fields: populated on the final chunk when the backend reports usage.</summary>
        public int? PromptTokens { get; set; }

        /// <summary>
        /// Prompt tokens of the LAST tool roundtrip only (actual context width), for prompt-size
        /// calibration; <see cref="PromptTokens"/> stays cumulative for cost telemetry.
        /// </summary>
        public int? LastRoundtripPromptTokens { get; set; }

        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens read from cache.</summary>
        public int CacheReadTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens written to cache.</summary>
        public int CacheWriteTokens { get; set; }

        /// <summary>
        /// Tool calls executed in this streaming turn (final chunk only). Empty for intermediate
        /// chunks. Mirrors <see cref="LlmCompletionResult.ExecutedToolCalls"/> so the logging
        /// decorator can render the same diagnostic across stream and non-stream paths.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> ExecutedToolCalls { get; set; } = Array.Empty<LlmToolCallTrace>();

        /// <summary>
        /// When <c>true</c> with <see cref="BufferedStreamingNoToolBinding"/>, host UI should show a short
        /// static line from chat config (tool invocation, text-shaped execute, hybrid JSON hold).
        /// </summary>
        public bool BufferedStreamingUseToolProgressHint { get; set; }

        /// <summary>
        /// Whether streaming text is buffered when tool declarations have no runtime binding.
        /// </summary>
        public bool BufferedStreamingNoToolBinding { get; set; }

        /// <summary>
        /// A copy with the failure rewritten to <paramref name="error"/> / <paramref name="errorCode"/>; every
        /// other field is carried over. WHY a copy: the chunk instance may be cached or reused by the inner
        /// client, so a decorator that re-classifies a terminal failure must not mutate what it received.
        /// </summary>
        public LlmStreamChunk WithError(string error, LlmErrorCode errorCode)
        {
            return new LlmStreamChunk
            {
                Text = Text,
                StartsNewMessage = StartsNewMessage,
                ReasoningText = ReasoningText,
                IsDone = IsDone,
                Error = error,
                ErrorCode = errorCode,
                HttpStatus = HttpStatus,
                RetryAfterSeconds = RetryAfterSeconds,
                Model = Model,
                PromptTokens = PromptTokens,
                LastRoundtripPromptTokens = LastRoundtripPromptTokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = TotalTokens,
                CacheReadTokens = CacheReadTokens,
                CacheWriteTokens = CacheWriteTokens,
                ExecutedToolCalls = ExecutedToolCalls,
                BufferedStreamingUseToolProgressHint = BufferedStreamingUseToolProgressHint,
                BufferedStreamingNoToolBinding = BufferedStreamingNoToolBinding
            };
        }
    }

    /// <summary>
    /// Defines completion and streaming operations for LLM backends.
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>
        /// True when this backend receives tool definitions through a native tool/function channel.
        /// Defaults to false so text-shaped/local backends keep the full prompt contract unless they opt in.
        /// </summary>
        virtual bool SupportsNativeToolCalling => false;

        /// <summary>
        /// Role-aware native tool capability. Routing clients should resolve the role-specific backend;
        /// non-routing clients can use <see cref="SupportsNativeToolCalling"/>.
        /// </summary>
        virtual bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return SupportsNativeToolCalling;
        }

        /// <summary>
        /// Route-aware native tool capability: resolves against the endpoint the request would
        /// actually reach (explicit profile, runtime role assignment, or fallback), so the tool
        /// contract follows an endpoint switch. Non-routing clients ignore the profile.
        /// </summary>
        virtual bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return SupportsNativeToolCallingForRole(agentRoleId);
        }

        /// <summary>
        /// Context window of the endpoint the request would actually reach, or null when this
        /// client has no routing knowledge (the caller then falls back to configured budgets).
        /// </summary>
        virtual int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return null;
        }

        /// <summary>Single completion; cancellation and timeouts are applied by outer decorators.</summary>
        Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Attach tools for subsequent calls. Default implementation is no-op.</summary>
        virtual void SetTools(IReadOnlyList<ILlmTool> tools)
        {
        }

        /// <summary>
        /// Streaming completion: yields text chunks as they arrive.
        /// Default implementation falls back to <see cref="CompleteAsync"/> and emits one terminal chunk.
        /// </summary>
        /// <remarks>
        /// <b>Wrappers must override.</b> Any <see cref="ILlmClient"/> that decorates another client (logging, routing, timeout, retry)
        /// must override this member and delegate with <c>await foreach</c>. Otherwise the default DIM body collapses the stream into
        /// a single final chunk after <see cref="CompleteAsync"/> returns, so UI streaming appears to buffer entirely.
        /// </remarks>
        virtual async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            LlmCompletionResult result = await CompleteAsync(request, cancellationToken);
            if (result == null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "null result",
                    ErrorCode = LlmErrorCode.ProviderError
                };
                yield break;
            }

            if (!result.Ok)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = result.Error,
                    ErrorCode = result.ErrorCode,
                    HttpStatus = result.HttpStatus,
                    RetryAfterSeconds = result.RetryAfterSeconds,
                    Model = result.Model
                };
                yield break;
            }

            yield return new LlmStreamChunk
            {
                Text = result.Content ?? "",
                IsDone = true,
                PromptTokens = result.PromptTokens,
                LastRoundtripPromptTokens = result.LastRoundtripPromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens,
                CacheReadTokens = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens,
                Model = result.Model,
                ExecutedToolCalls = result.ExecutedToolCalls
            };
        }
    }

    internal interface ILlmRequestHeaderScope
    {
        IDisposable BeginRequestHeaders(LlmCompletionRequest request);
    }

    internal static class LlmRequestHeaderScopes
    {
        internal static IDisposable Begin(ILlmClient client, LlmCompletionRequest request)
        {
            return (client as ILlmRequestHeaderScope)?.BeginRequestHeaders(request);
        }
    }
}

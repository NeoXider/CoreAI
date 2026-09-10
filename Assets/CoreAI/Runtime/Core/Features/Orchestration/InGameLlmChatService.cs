using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// A chat service that keeps the player's conversation history around LLM requests.
    /// <para>
    /// Refusals are always typed (<see cref="LlmCompletionResult.ErrorCode"/>): an empty message is
    /// <see cref="LlmErrorCode.InvalidRequest"/>, the sliding rate window is
    /// <see cref="LlmErrorCode.RateLimited"/> with <see cref="LlmCompletionResult.RetryAfterSeconds"/>, an
    /// empty model answer is <see cref="LlmErrorCode.EmptyResponse"/>, and an expired timeout is
    /// <see cref="LlmErrorCode.Timeout"/>. Without a code the consumer could not tell a refusal from the
    /// model's answer and displayed the refusal text as a reply.
    /// </para>
    /// <para>
    /// A sliding-window slot is held only for the duration of the attempt and is GIVEN BACK if the attempt
    /// failed (an error, an empty answer, a timeout, cancellation): after a backend failure the player does
    /// not burn their allowance on errors. The slot is reserved AFTER waiting on the gate - a request that
    /// queued up and was cancelled before it started used to burn its slot for nothing; a fast refusal
    /// without waiting comes from the preliminary window check.
    /// </para>
    /// <para>
    /// The timeout is up to the host: with <c>requestTimeoutSecondsProvider</c> the client is wrapped in a
    /// <see cref="TimeoutLlmClientDecorator"/>, so a hung request does not hold the gate forever. In the
    /// Unity pipeline the client is already bounded by an outer decorator and the provider must NOT be
    /// passed - otherwise there would be two identical timers; the provider is for a portable host that
    /// supplies its own <see cref="ILlmClient"/> here.
    /// </para>
    /// </summary>
    public sealed class InGameLlmChatService : IInGameLlmChatService
    {
        private readonly ILlmClient _llm;
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly List<(string Role, string Text)> _turns = new();
        private readonly int _maxMessages;
        private readonly object _historyLock = new();
        private readonly object _rateLock = new();

        // WHY: overlapping requests raced: the second snapshot of the history could miss the first turn,
        // and the appends interleaved. The gate serializes "snapshot -> LLM -> append", so every request
        // sees all previously completed turns in order.
        private readonly SemaphoreSlim _requestGate = new(1, 1);

        private readonly int _maxRequestsPerWindow;
        private readonly TimeSpan _rateLimitWindow;
        private readonly List<DateTime> _acceptedStamps = new();
        private long _totalRejected;

        /// <summary>
        /// Creates the chat service.
        /// </summary>
        /// <param name="llm">The LLM client.</param>
        /// <param name="systemPrompts">The system prompt provider.</param>
        /// <param name="maxMessages">How many messages (individual turns, not pairs) to keep in history.</param>
        /// <param name="maxRequestsPerWindow">Cap of successful requests per sliding window; 0 means no limit.</param>
        /// <param name="rateLimitWindowSeconds">Length of the sliding window in seconds.</param>
        /// <param name="requestTimeoutSecondsProvider">
        /// The timeout of a single request in seconds, re-read on every call; null or a value &lt;= 0 means
        /// no timeout of its own (the client is bounded from the outside, or not bounded at all).
        /// </param>
        /// <param name="asyncMarshaler">The host's delay scheduler for the timeout; null means a managed delay.</param>
        public InGameLlmChatService(
            ILlmClient llm,
            IAgentSystemPromptProvider systemPrompts,
            int maxMessages = 24,
            int maxRequestsPerWindow = 10,
            int rateLimitWindowSeconds = 60,
            Func<float> requestTimeoutSecondsProvider = null,
            ILlmAsyncMarshaler asyncMarshaler = null)
        {
            _llm = llm != null && requestTimeoutSecondsProvider != null
                ? new TimeoutLlmClientDecorator(llm, requestTimeoutSecondsProvider, asyncMarshaler)
                : llm;
            _systemPrompts = systemPrompts;
            _maxMessages = maxMessages;
            _maxRequestsPerWindow = maxRequestsPerWindow;
            _rateLimitWindow = TimeSpan.FromSeconds(rateLimitWindowSeconds);
        }

        /// <inheritdoc />
        public int HistoryPairCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _turns.Count / 2;
                }
            }
        }

        /// <inheritdoc />
        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _turns.Clear();
            }
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> SendPlayerMessageAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "empty message",
                    ErrorCode = LlmErrorCode.InvalidRequest
                };
            }

            // Fast refusal: do not queue behind somebody else's model answer when the window is already full.
            if (IsRateWindowFull(out int retryAfterSeconds))
            {
                return RateLimited(retryAfterSeconds);
            }

            string baseSystem = _systemPrompts.TryGetSystemPrompt(BuiltInAgentRoleIds.SmartChat, out string sys) &&
                                !string.IsNullOrWhiteSpace(sys)
                ? sys.Trim()
                : "You are a helpful in-game assistant.";

            string prefix = CoreAISettings.UniversalSystemPromptPrefix;
            string system = string.IsNullOrWhiteSpace(prefix)
                ? baseSystem
                : prefix.TrimEnd() + "\n" + baseSystem;

            // WHY: _historyLock guards only individual reads/writes (HistoryPairCount / ClearHistory), not
            // the "snapshot -> LLM -> append" sequence. _requestGate serializes that sequence as a whole so
            // that a concurrent request cannot take a snapshot missing the previous turn or wedge its own
            // append in between.
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                if (!TryAcquireRateSlot(out DateTime stamp, out retryAfterSeconds))
                {
                    return RateLimited(retryAfterSeconds);
                }

                bool consumed = false;
                try
                {
                    List<Microsoft.Extensions.AI.ChatMessage> history;
                    lock (_historyLock)
                    {
                        history = new List<Microsoft.Extensions.AI.ChatMessage>(_turns.Count + 1);
                        foreach ((string role, string text) in _turns)
                        {
                            ChatRole chatRole = role == "User"
                                ? ChatRole.User
                                : ChatRole.Assistant;
                            history.Add(new Microsoft.Extensions.AI.ChatMessage(chatRole, text));
                        }

                        history.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, message));
                    }

                    LlmCompletionResult result;
                    try
                    {
                        result = await _llm.CompleteAsync(
                            new LlmCompletionRequest
                            {
                                AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                                SystemPrompt = system,
                                ChatHistory = history,
                                TraceId = Guid.NewGuid().ToString("N")
                            },
                            cancellationToken);
                    }
                    catch (LlmOperationTimeoutException)
                    {
                        // A library timeout while the caller's token is still alive: the service's contract
                        // is a result, not an exception. A genuine caller cancellation passes out as it is.
                        result = Failed(LlmErrorCode.Timeout);
                    }

                    if (result == null)
                    {
                        result = Failed(LlmErrorCode.EmptyResponse);
                    }
                    else if (result.Ok && string.IsNullOrEmpty(result.Content))
                    {
                        // WHY: a "success" with no text for the player is not an answer. Such a result used
                        // to reach the consumer as Ok, and the panel printed an empty reply.
                        result = Failed(LlmErrorCode.EmptyResponse);
                    }

                    if (result.Ok)
                    {
                        consumed = true;
                        lock (_historyLock)
                        {
                            _turns.Add(("User", message.Trim()));
                            _turns.Add(("Assistant", result.Content.Trim()));
                            while (_turns.Count > _maxMessages)
                            {
                                _turns.RemoveAt(0);
                                if (_turns.Count > 0)
                                {
                                    _turns.RemoveAt(0);
                                }
                            }
                        }
                    }

                    return result;
                }
                finally
                {
                    if (!consumed)
                    {
                        ReleaseRateSlot(stamp);
                    }
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        /// <summary>
        /// A snapshot of the rate limiter's state for diagnostics / UI.
        /// </summary>
        public RateLimiterMetrics GetRateLimiterMetrics()
        {
            lock (_rateLock)
            {
                PruneExpired(DateTime.UtcNow);
                return new RateLimiterMetrics(
                    _maxRequestsPerWindow,
                    (int)_rateLimitWindow.TotalSeconds,
                    _acceptedStamps.Count,
                    _totalRejected);
            }
        }

        private static LlmCompletionResult RateLimited(int retryAfterSeconds)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = "rate_limited: too many requests. Please wait before sending another message.",
                ErrorCode = LlmErrorCode.RateLimited,
                RetryAfterSeconds = retryAfterSeconds > 0 ? retryAfterSeconds : null
            };
        }

        private static LlmCompletionResult Failed(LlmErrorCode code)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = LlmErrorPresentation.ForErrorCode(code),
                ErrorCode = code
            };
        }

        /// <summary>Whether the window is full right now (without reserving). A refusal is counted in the metrics.</summary>
        private bool IsRateWindowFull(out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (_maxRequestsPerWindow <= 0)
            {
                return false;
            }

            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                PruneExpired(now);
                if (_acceptedStamps.Count < _maxRequestsPerWindow)
                {
                    return false;
                }

                _totalRejected++;
                retryAfterSeconds = RetryAfterSeconds(now);
                return true;
            }
        }

        /// <summary>
        /// Tries to take one sliding-window slot. The slot is stamped with a time so that it can be given
        /// back if the attempt fails.
        /// </summary>
        private bool TryAcquireRateSlot(out DateTime stamp, out int retryAfterSeconds)
        {
            stamp = default;
            retryAfterSeconds = 0;
            if (_maxRequestsPerWindow <= 0)
            {
                return true;
            }

            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                PruneExpired(now);
                if (_acceptedStamps.Count >= _maxRequestsPerWindow)
                {
                    _totalRejected++;
                    retryAfterSeconds = RetryAfterSeconds(now);
                    return false;
                }

                stamp = now;
                _acceptedStamps.Add(now);
                return true;
            }
        }

        /// <summary>Gives back the slot of a failed attempt: the last stamp with this time is removed.</summary>
        private void ReleaseRateSlot(DateTime stamp)
        {
            if (_maxRequestsPerWindow <= 0)
            {
                return;
            }

            lock (_rateLock)
            {
                for (int i = _acceptedStamps.Count - 1; i >= 0; i--)
                {
                    if (_acceptedStamps[i] == stamp)
                    {
                        _acceptedStamps.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        /// <summary>Call under <see cref="_rateLock"/>.</summary>
        private void PruneExpired(DateTime now)
        {
            DateTime cutoff = now - _rateLimitWindow;
            int expired = 0;
            while (expired < _acceptedStamps.Count && _acceptedStamps[expired] < cutoff)
            {
                expired++;
            }

            if (expired > 0)
            {
                _acceptedStamps.RemoveRange(0, expired);
            }
        }

        /// <summary>In how many seconds the oldest slot of the window frees up. Call under <see cref="_rateLock"/>.</summary>
        private int RetryAfterSeconds(DateTime now)
        {
            if (_acceptedStamps.Count == 0)
            {
                return 0;
            }

            double seconds = (_acceptedStamps[0] + _rateLimitWindow - now).TotalSeconds;
            return seconds <= 0 ? 1 : (int)Math.Ceiling(seconds);
        }
    }
}

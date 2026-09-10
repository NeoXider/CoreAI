using System;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Messaging;
using MessagePipe;
using UnityEngine;
using VContainer;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Runtime data source shared by token-budget UIs (<see cref="CoreAiTokenBudgetOverlay"/> and
    /// <see cref="CoreAiTokenBudgetUiView"/>): finds the scene <see cref="CoreAILifetimeScope"/>,
    /// subscribes to <see cref="LlmUsageReported"/> and feeds a <see cref="TokenBudgetCalculator"/>.
    /// Resolve attempts are throttled and safe to retry every frame via <see cref="TickResolve"/>.
    /// </summary>
    public sealed class TokenBudgetRuntimeSource : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private CoreAILifetimeScope _scope;
        private IDisposable _usageSubscription;
        private IDisposable _toolCompletedSubscription;
        private IDisposable _toolFailedSubscription;
        private float _nextResolveAttempt;

        public TokenBudgetRuntimeSource(double windowSeconds)
        {
            Calculator = new TokenBudgetCalculator(windowSeconds);
        }

        /// <summary>Thread-safe token aggregator fed by <see cref="LlmUsageReported"/> events.</summary>
        public TokenBudgetCalculator Calculator { get; }

        /// <summary>Chat service for rate-limiter metrics; null until resolved.</summary>
        public IInGameLlmChatService ChatService { get; private set; }

        /// <summary>CoreAI settings for token prices; null until resolved.</summary>
        public ICoreAISettings Settings { get; private set; }

        /// <summary>True once at least one CoreAI service has been resolved from the scope.</summary>
        public bool IsResolved { get; private set; }

        /// <summary>Monotonic seconds since this source was created (rolling-window clock).</summary>
        public double NowSeconds => _clock.Elapsed.TotalSeconds;

        /// <summary>
        /// Retries service resolution at most once per second until something resolves.
        /// Call from <c>Update()</c> in Play Mode.
        /// </summary>
        public void TickResolve()
        {
            if (IsResolved || Time.realtimeSinceStartup < _nextResolveAttempt)
            {
                return;
            }

            _nextResolveAttempt = Time.realtimeSinceStartup + 1f;
            TryResolveServices();
        }

        public void Dispose()
        {
            _usageSubscription?.Dispose();
            _usageSubscription = null;
            _toolCompletedSubscription?.Dispose();
            _toolCompletedSubscription = null;
            _toolFailedSubscription?.Dispose();
            _toolFailedSubscription = null;
        }

        private void TryResolveServices()
        {
            if (_scope == null)
            {
                _scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            }

            if (_scope == null || _scope.Container == null)
            {
                return;
            }

            // WHY TryResolve: a scene whose scope registers none of these used to pay a thrown-and-caught
            // VContainerException per service per second for the whole session (four throws a second
            // under IL2CPP/WebGL, where a throw is a stack unwind through native frames), and a missing
            // registration is the expected outcome here, not an error.
            IObjectResolver container = _scope.Container;
            ChatService = container.TryResolve(out IInGameLlmChatService chatService) ? chatService : null;
            Settings = container.TryResolve(out ICoreAISettings settings) ? settings : null;

            if (_usageSubscription == null &&
                container.TryResolve(out ISubscriber<LlmUsageReported> usage))
            {
                _usageSubscription = usage.Subscribe(OnUsageReported);
            }

            if (_toolCompletedSubscription == null &&
                container.TryResolve(out ISubscriber<LlmToolCallCompleted> completed))
            {
                _toolCompletedSubscription = completed.Subscribe(_ => Calculator.RecordToolCall(true));
            }

            if (_toolFailedSubscription == null &&
                container.TryResolve(out ISubscriber<LlmToolCallFailed> failed))
            {
                _toolFailedSubscription = failed.Subscribe(_ => Calculator.RecordToolCall(false));
            }

            IsResolved = ChatService != null || Settings != null || _usageSubscription != null;
        }

        /// <summary>
        /// Records a usage event into the calculator. May be invoked off the main thread,
        /// so it only touches the thread-safe calculator and the stopwatch clock.
        /// </summary>
        private void OnUsageReported(LlmUsageReported usage)
        {
            Calculator.RecordUsage(
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                _clock.Elapsed.TotalSeconds);
        }
    }
}

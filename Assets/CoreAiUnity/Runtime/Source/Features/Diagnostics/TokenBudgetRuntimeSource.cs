using System;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Messaging;
using MessagePipe;
using UnityEngine;

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

            try
            {
                ChatService = (IInGameLlmChatService)_scope.Container.Resolve(typeof(IInGameLlmChatService));
            }
            catch (Exception)
            {
                ChatService = null;
            }

            try
            {
                Settings = (ICoreAISettings)_scope.Container.Resolve(typeof(ICoreAISettings));
            }
            catch (Exception)
            {
                Settings = null;
            }

            if (_usageSubscription == null)
            {
                try
                {
                    ISubscriber<LlmUsageReported> usage =
                        (ISubscriber<LlmUsageReported>)_scope.Container.Resolve(typeof(ISubscriber<LlmUsageReported>));
                    _usageSubscription = usage.Subscribe(OnUsageReported);
                }
                catch (Exception)
                {
                    _usageSubscription = null;
                }
            }

            if (_toolCompletedSubscription == null)
            {
                try
                {
                    ISubscriber<LlmToolCallCompleted> completed =
                        (ISubscriber<LlmToolCallCompleted>)_scope.Container.Resolve(
                            typeof(ISubscriber<LlmToolCallCompleted>));
                    _toolCompletedSubscription = completed.Subscribe(_ => Calculator.RecordToolCall(true));
                }
                catch (Exception)
                {
                    _toolCompletedSubscription = null;
                }
            }

            if (_toolFailedSubscription == null)
            {
                try
                {
                    ISubscriber<LlmToolCallFailed> failed =
                        (ISubscriber<LlmToolCallFailed>)_scope.Container.Resolve(
                            typeof(ISubscriber<LlmToolCallFailed>));
                    _toolFailedSubscription = failed.Subscribe(_ => Calculator.RecordToolCall(false));
                }
                catch (Exception)
                {
                    _toolFailedSubscription = null;
                }
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

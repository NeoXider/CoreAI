using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using Cysharp.Threading.Tasks;
using MessagePipe;
using VContainer.Unity;
using CoreAI.Infrastructure.World;

namespace CoreAI.Infrastructure.Messaging
{
    /// <summary>
    /// Subscribes to AI command messages and routes them to Lua/world command processors on startup.
    /// </summary>
    public sealed class AiGameCommandRouter : IStartable, IDisposable
    {
        /// <summary>Raised after an AI command message is received by the router.</summary>
        public static event Action<ApplyAiGameCommand> CommandReceived;

        private static int _activeRouterCount;

        private readonly ISubscriber<ApplyAiGameCommand> _subscriber;
        private readonly IGameLogger _logger;
        private readonly ICoreAiWorldCommandExecutor _worldExecutor;
        private IDisposable _subscription;
        private int _disposeState;

        /// <summary>Creates a router bound to MessagePipe, logging, Lua, and world-command services.</summary>
        public AiGameCommandRouter(
            ISubscriber<ApplyAiGameCommand> subscriber,
            IGameLogger logger,
            ICoreAiWorldCommandExecutor worldExecutor)
        {
            _subscriber = subscriber;
            _logger = logger;
            _worldExecutor = worldExecutor;
            System.Threading.Interlocked.Increment(ref _activeRouterCount);
        }

        public void Start()
        {
            _subscription = _subscriber.Subscribe(cmd =>
            {
                if (cmd == null)
                {
                    return;
                }

                ApplyAiGameCommand captured = cmd;
                UniTask.Void(async () =>
                {
                    await UniTask.SwitchToMainThread();
                    if (System.Threading.Volatile.Read(ref _disposeState) != 0)
                    {
                        return;
                    }

                    try
                    {
                        _worldExecutor?.TryExecute(captured);
                        CommandReceived?.Invoke(captured);
                        string pay = captured.JsonPayload ?? "";
                        string shortPay = pay.Length > 200 ? pay.Substring(0, 200) + "..." : pay;
                        string trace = string.IsNullOrWhiteSpace(captured.TraceId) ? "-" : captured.TraceId;
                        _logger.LogInfo(GameLogFeature.MessagePipe,
                            $"ApplyAiGameCommand traceId={trace} type={captured.CommandTypeId} role={captured.SourceRoleId} gen={captured.LuaRepairGeneration} payload={shortPay}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(GameLogFeature.MessagePipe, $"ApplyAiGameCommand handler: {ex.Message}");
                    }
                });
            });
        }

        /// <summary>Releases subscriptions and runtime resources held by this object.</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            _subscription?.Dispose();

            // WHY: CommandReceived is process-global (static) while its subscribers are typically
            // scene-scoped (e.g. AiDashboardPresenter). A subscriber that misses its own unsubscribe
            // would survive a scene reload and receive commands routed by the next scene's router —
            // duplicate world mutations against destroyed objects. Additive/overlapping scopes can own
            // live subscribers concurrently, so only the last router may clear the shared event.
            if (System.Threading.Interlocked.Decrement(ref _activeRouterCount) == 0)
            {
                CommandReceived = null;
            }
        }
    }
}

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
    /// Provides ai game command router functionality.
    /// </summary>
    public sealed class AiGameCommandRouter : IStartable, IDisposable
    {
        /// <summary>Command received.</summary>
        public static event Action<ApplyAiGameCommand> CommandReceived;

        private readonly ISubscriber<ApplyAiGameCommand> _subscriber;
        private readonly IGameLogger _logger;
        private readonly LuaAiEnvelopeProcessor _luaProcessor;
        private readonly ICoreAiWorldCommandExecutor _worldExecutor;
        private IDisposable _subscription;
        private volatile bool _disposed;

        /// <summary>Initializes a new instance of AiGameCommandRouter.</summary>
        public AiGameCommandRouter(
            ISubscriber<ApplyAiGameCommand> subscriber,
            IGameLogger logger,
            LuaAiEnvelopeProcessor luaProcessor,
            ICoreAiWorldCommandExecutor worldExecutor)
        {
            _subscriber = subscriber;
            _logger = logger;
            _luaProcessor = luaProcessor;
            _worldExecutor = worldExecutor;
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
                    if (_disposed)
                    {
                        return;
                    }

                    try
                    {
                        _luaProcessor.Process(captured);
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
            _disposed = true;
            _subscription?.Dispose();
        }
    }
}

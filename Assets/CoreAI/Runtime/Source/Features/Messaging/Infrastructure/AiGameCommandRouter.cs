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
    /// Подписка на команды ИИ: исполнение Lua из конверта + логирование + UI-событие.
    /// Обработка маршалится на главный поток Unity (<see cref="Cysharp.Threading.Tasks.UniTask.SwitchToMainThread"/>),
    /// потому что после <c>ConfigureAwait(false)</c> в <see cref="CoreAI.Ai.QueuedAiOrchestrator"/> продолжение
    /// <see cref="CoreAI.Ai.AiOrchestrator"/> и <c>Publish</c> в MessagePipe часто выполняются с пула потоков.
    /// Подписчики на <see cref="CommandReceived"/> не должны сами полагаться на поток доставки из шины.
    /// Нормативно: <c>Assets/CoreAiUnity/Docs/DGF_SPEC.md</c> §9.4 (ADR-9.4).
    /// </summary>
    public sealed class AiGameCommandRouter : IStartable, IDisposable
    {
        /// <summary>Событие для простого UI (MVP), без жёсткой связи с Canvas.</summary>
        public static event System.Action<ApplyAiGameCommand> CommandReceived;

        private readonly ISubscriber<ApplyAiGameCommand> _subscriber;
        private readonly IGameLogger _logger;
        private readonly LuaAiEnvelopeProcessor _luaProcessor;
        private readonly ICoreAiWorldCommandExecutor _worldExecutor;
        private IDisposable _subscription;
        private volatile bool _disposed;

        /// <summary>Подписка на шину: Lua-процессор + статическое событие для UI + лог с traceId.</summary>
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
                    return;
                var captured = cmd;
                UniTask.Void(async () =>
                {
                    await UniTask.SwitchToMainThread();
                    if (_disposed)
                        return;
                    try
                    {
                        _luaProcessor.Process(captured);
                        _worldExecutor?.TryExecute(captured);
                        CommandReceived?.Invoke(captured);
                        var pay = captured.JsonPayload ?? "";
                        var shortPay = pay.Length > 200 ? pay.Substring(0, 200) + "…" : pay;
                        var trace = string.IsNullOrWhiteSpace(captured.TraceId) ? "—" : captured.TraceId;
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

        /// <summary>Отписаться от MessagePipe.</summary>
        public void Dispose()
        {
            _disposed = true;
            _subscription?.Dispose();
        }
    }
}

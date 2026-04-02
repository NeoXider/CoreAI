using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using MessagePipe;
using VContainer.Unity;

namespace CoreAI.Infrastructure.Messaging
{
    /// <summary>
    /// Подписка на команды ИИ: исполнение Lua из конверта + логирование + UI-событие.
    /// </summary>
    public sealed class AiGameCommandRouter : IStartable, IDisposable
    {
        /// <summary>Событие для простого UI (MVP), без жёсткой связи с Canvas.</summary>
        public static event System.Action<ApplyAiGameCommand> CommandReceived;

        private readonly ISubscriber<ApplyAiGameCommand> _subscriber;
        private readonly IGameLogger _logger;
        private readonly LuaAiEnvelopeProcessor _luaProcessor;
        private IDisposable _subscription;

        public AiGameCommandRouter(
            ISubscriber<ApplyAiGameCommand> subscriber,
            IGameLogger logger,
            LuaAiEnvelopeProcessor luaProcessor)
        {
            _subscriber = subscriber;
            _logger = logger;
            _luaProcessor = luaProcessor;
        }

        public void Start()
        {
            _subscription = _subscriber.Subscribe(cmd =>
            {
                _luaProcessor.Process(cmd);
                CommandReceived?.Invoke(cmd);
                var pay = cmd.JsonPayload ?? "";
                var shortPay = pay.Length > 200 ? pay.Substring(0, 200) + "…" : pay;
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"ApplyAiGameCommand type={cmd.CommandTypeId} role={cmd.SourceRoleId} gen={cmd.LuaRepairGeneration} payload={shortPay}");
            });
        }

        public void Dispose() => _subscription?.Dispose();
    }
}

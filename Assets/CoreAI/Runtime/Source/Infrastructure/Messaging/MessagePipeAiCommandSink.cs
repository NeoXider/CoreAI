using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Infrastructure.Messaging
{
    /// <summary>Мост <see cref="IAiGameCommandSink"/> → MessagePipe <see cref="IPublisher{TMessage}"/>.</summary>
    public sealed class MessagePipeAiCommandSink : IAiGameCommandSink
    {
        private readonly IPublisher<ApplyAiGameCommand> _publisher;

        /// <summary>Создать sink с брокером команд.</summary>
        public MessagePipeAiCommandSink(IPublisher<ApplyAiGameCommand> publisher)
        {
            _publisher = publisher;
        }

        /// <inheritdoc />
        public void Publish(ApplyAiGameCommand command) => _publisher.Publish(command);
    }
}

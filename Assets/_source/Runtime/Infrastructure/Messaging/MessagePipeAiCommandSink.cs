using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Infrastructure.Messaging
{
    public sealed class MessagePipeAiCommandSink : IAiGameCommandSink
    {
        private readonly IPublisher<ApplyAiGameCommand> _publisher;

        public MessagePipeAiCommandSink(IPublisher<ApplyAiGameCommand> publisher)
        {
            _publisher = publisher;
        }

        public void Publish(ApplyAiGameCommand command) => _publisher.Publish(command);
    }
}

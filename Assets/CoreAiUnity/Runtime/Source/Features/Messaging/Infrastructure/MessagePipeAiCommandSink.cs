using CoreAI.Messaging;
using MessagePipe;
using UnityEngine.Scripting;

namespace CoreAI.Infrastructure.Messaging
{
    /// <summary>Publishes AI game commands through MessagePipe.</summary>
    [Preserve]
    public sealed class MessagePipeAiCommandSink : IAiGameCommandSink
    {
        private readonly IPublisher<ApplyAiGameCommand> _publisher;

        /// <summary>Initializes a new instance of MessagePipeAiCommandSink.</summary>
        [Preserve]
        public MessagePipeAiCommandSink(IPublisher<ApplyAiGameCommand> publisher)
        {
            _publisher = publisher;
        }

        /// <inheritdoc />
        public void Publish(ApplyAiGameCommand command)
        {
            _publisher.Publish(command);
        }
    }
}
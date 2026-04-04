using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Исполнение WorldCommand на главном потоке Unity.</summary>
    public interface ICoreAiWorldCommandExecutor
    {
        /// <summary>Попытаться применить команду; <c>false</c> если команда не WorldCommand или payload невалиден.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);
    }
}
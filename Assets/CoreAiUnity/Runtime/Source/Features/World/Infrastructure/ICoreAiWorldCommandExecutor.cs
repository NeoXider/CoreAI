using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Исполнение WorldCommand на главном потоке Unity.</summary>
    public interface ICoreAiWorldCommandExecutor
    {
        /// <summary>Попытаться применить команду; <c>false</c> если команда не WorldCommand или payload невалиден.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);
        
        string[] LastListedAnimations { get; }
        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> LastListedObjects { get; }
    }
}
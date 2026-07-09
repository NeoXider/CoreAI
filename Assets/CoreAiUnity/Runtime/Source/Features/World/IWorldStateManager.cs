using System;

namespace CoreAI.Infrastructure.World
{
    public interface IWorldStateManager
    {
        bool HasSavedState { get; }
        void Save();
        bool TryLoad(string sceneName = null);
        void Reset();
        event Action StateReset;
    }
}

using CoreAI.Infrastructure.World;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    public sealed class WorldStateEntryPoint : IStartable
    {
        private readonly WorldStateManager _manager;

        public WorldStateEntryPoint(WorldStateManager manager)
        {
            _manager = manager;
        }

        void IStartable.Start()
        {
            _manager.Initialize();
        }
    }
}
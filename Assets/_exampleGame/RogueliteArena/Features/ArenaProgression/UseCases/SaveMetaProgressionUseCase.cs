using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class SaveMetaProgressionUseCase : ISaveMetaProgressionUseCase
    {
        private readonly ArenaMetaProgressionState _meta;
        private readonly ArenaMetaSaveGateway _gateway;

        public SaveMetaProgressionUseCase(ArenaMetaProgressionState meta, ArenaMetaSaveGateway gateway)
        {
            _meta = meta;
            _gateway = gateway;
        }

        public void Execute() => _gateway?.Save(_meta);
    }
}

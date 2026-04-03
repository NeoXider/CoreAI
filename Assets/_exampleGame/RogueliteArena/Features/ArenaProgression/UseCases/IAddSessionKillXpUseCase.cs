namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public interface IAddSessionKillXpUseCase
    {
        void Execute(int baseXpAmount, int aliveTeamMembers);
    }
}

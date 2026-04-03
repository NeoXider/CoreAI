namespace CoreAI.ExampleGame.ArenaSurvival.Domain
{
    /// <summary>
    /// Кто исполняет правила забега. Для NGO: <see cref="AuthoritativeHost"/> = listen server / dedicated;
    /// <see cref="ClientPresentationOnly"/> = клиент без симуляции врагов и спавна (только визуал по сетевым данным — позже).
    /// </summary>
    public enum ArenaSimulationRole
    {
        AuthoritativeHost = 0,
        ClientPresentationOnly = 1
    }
}

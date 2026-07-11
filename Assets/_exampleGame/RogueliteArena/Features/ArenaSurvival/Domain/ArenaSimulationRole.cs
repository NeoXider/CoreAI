namespace CoreAI.ExampleGame.ArenaSurvival.Domain
{
    /// <summary>
    /// Describes which peer executes run rules. For NGO, <see cref="AuthoritativeHost"/>
    /// is the listen or dedicated server, while <see cref="ClientPresentationOnly"/> does visuals only.
    /// </summary>
    public enum ArenaSimulationRole
    {
        AuthoritativeHost = 0,
        ClientPresentationOnly = 1
    }
}

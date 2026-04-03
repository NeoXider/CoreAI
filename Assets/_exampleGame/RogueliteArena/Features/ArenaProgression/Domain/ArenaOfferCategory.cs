namespace CoreAI.ExampleGame.ArenaProgression.Domain
{
    /// <summary>Категория выпадения карты после выбора редкости (индексы в ChanceData должны совпадать с порядком в SO).</summary>
    public enum ArenaOfferCategory
    {
        Stat = 0,
        PassiveSlot = 1,
        OfferExtraChoices = 2,
        LegendaryDoublePick = 3
    }
}

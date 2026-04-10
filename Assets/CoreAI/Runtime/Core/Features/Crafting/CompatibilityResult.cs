using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Результат проверки совместимости ингредиентов.
    /// </summary>
    public sealed class CompatibilityResult
    {
        public bool IsCompatible { get; set; }
        public string Reason { get; set; }
        public float CompatibilityScore { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> Bonuses { get; set; } = new();
    }
}

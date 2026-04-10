using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Результат проверки совместимости для LLM.
    /// </summary>
    public sealed class CompatibilityToolResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool IsCompatible { get; set; }
        public float Score { get; set; }
        public string Reason { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> Bonuses { get; set; } = new();
    }
}

using CoreAI.Ai;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Обёртка InventoryTool для ILlmTool интерфейса.
    /// Позволяет PlayerChat агенту вызывать инструмент получения инвентаря.
    /// </summary>
    public sealed class InventoryLlmTool : ILlmTool
    {
        private readonly InventoryTool.IInventoryProvider _provider;

        public InventoryLlmTool(InventoryTool.IInventoryProvider provider)
        {
            _provider = provider;
        }

        public string Name => "get_inventory";

        public string Description =>
            "Get current inventory items from this NPC/merchant. " +
            "Call this tool BEFORE offering items to the player so you know what you can sell. " +
            "Returns a list of items with name, type, quantity, and price.";

        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {}" +
            "}";

        public AIFunction CreateAIFunction()
        {
            InventoryTool tool = new(_provider);
            return tool.CreateAIFunction();
        }
    }
}
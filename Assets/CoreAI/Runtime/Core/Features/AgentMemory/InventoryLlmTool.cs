using System;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that exposes inventory operations to agents.
    /// </summary>
    public sealed class InventoryLlmTool : IAIFunctionLlmTool
    {
        private readonly InventoryTool.IInventoryProvider _provider;

        public InventoryLlmTool(InventoryTool.IInventoryProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public string Name => "get_inventory";
        public bool AllowDuplicates => false;

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

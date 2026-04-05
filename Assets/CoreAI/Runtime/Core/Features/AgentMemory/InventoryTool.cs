using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Пример инструмента для Chat Agent: получение инвентаря NPC/торговца.
    /// Демонстрирует как модель может вызвать инструмент ПЕРЕД ответом пользователю.
    /// </summary>
    public sealed class InventoryTool
    {
        private readonly IInventoryProvider _provider;

        public InventoryTool(IInventoryProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public AIFunction CreateAIFunction()
        {
            Func<CancellationToken, Task<InventoryResult>> func = GetInventoryAsync;
            return AIFunctionFactory.Create(
                func,
                "get_inventory",
                "Get current inventory items from an NPC or merchant. Call this before offering items to the player.");
        }

        public async Task<InventoryResult> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                List<InventoryItem> items = await _provider.GetInventoryAsync(cancellationToken);
                return new InventoryResult
                {
                    Success = true,
                    Items = items
                };
            }
            catch (Exception ex)
            {
                return new InventoryResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        public sealed class InventoryResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public List<InventoryItem> Items { get; set; } = new();
        }

        public sealed class InventoryItem
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Quantity { get; set; }
            public int Price { get; set; }
        }

        public interface IInventoryProvider
        {
            Task<List<InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken);
        }
    }
}
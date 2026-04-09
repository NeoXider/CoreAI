using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
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
        private readonly ICoreAISettings _settings;

        public InventoryTool(IInventoryProvider provider, ICoreAISettings settings = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _settings = settings;
        }

        public AIFunction CreateAIFunction()
        {
            Func<CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = "get_inventory",
                Description =
                    "Get current inventory items from an NPC or merchant. Call this before offering items to the player."
            };
            return AIFunctionFactory.Create(func, options);
        }

        public async Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] get_inventory: fetching items", LogTag.Llm);
            }

            try
            {
                List<InventoryItem> items = await _provider.GetInventoryAsync(cancellationToken);

                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Info($"[Tool Call] get_inventory: SUCCESS - {items?.Count ?? 0} items", LogTag.Llm);
                }

                return SerializeResult(new InventoryResult
                {
                    Success = true,
                    Items = items
                });
            }
            catch (Exception ex)
            {
                if (CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Error($"[Tool Call] get_inventory: FAILED - {ex.Message}", LogTag.Llm);
                }

                return SerializeResult(new InventoryResult
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        private static string SerializeResult(InventoryResult result)
        {
            return JsonConvert.SerializeObject(result);
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
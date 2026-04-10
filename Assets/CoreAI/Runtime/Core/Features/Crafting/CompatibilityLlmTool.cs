using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace CoreAI.Crafting
{
    /// <summary>
    /// ILlmTool обёртка для CompatibilityChecker — позволяет LLM проверять совместимость ингредиентов.
    /// </summary>
    public sealed class CompatibilityLlmTool : LlmToolBase
    {
        private readonly CompatibilityChecker _checker;
        private readonly ICoreAISettings _settings;

        public CompatibilityLlmTool(CompatibilityChecker checker, ICoreAISettings settings = null)
        {
            _checker = checker ?? throw new ArgumentNullException(nameof(checker));
            _settings = settings;
        }

        public override string Name => "check_compatibility";

        public override string Description =>
            "Check compatibility of ingredients/materials before crafting. " +
            "Provide ingredient names as a comma-separated list. " +
            "Returns compatibility score (0-2), warnings, and bonuses. " +
            "Score 0 = incompatible, 1 = neutral, >1 = synergy bonus.";

        public override string ParametersSchema => JsonParams(
            ("ingredients", "string", true, "Comma-separated ingredient names to check compatibility (e.g. 'IronOre,FireStone,WaterFlask')")
        );

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>
        /// Выполняет проверку совместимости.
        /// </summary>
        public Task<string> ExecuteAsync(string ingredients, CancellationToken cancellationToken = default)
        {
            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] check_compatibility: ingredients={ingredients}", LogTag.Llm);
            }

            if (string.IsNullOrWhiteSpace(ingredients))
            {
                return Task.FromResult(JsonConvert.SerializeObject(new CompatibilityToolResult
                {
                    Success = false,
                    Error = "Ingredients parameter is required. Provide comma-separated names."
                }));
            }

            try
            {
                string[] items = ingredients.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> trimmed = new();
                foreach (string item in items)
                {
                    string t = item.Trim();
                    if (!string.IsNullOrEmpty(t))
                        trimmed.Add(t);
                }

                if (trimmed.Count < 2)
                {
                    return Task.FromResult(JsonConvert.SerializeObject(new CompatibilityToolResult
                    {
                        Success = false,
                        Error = "At least 2 ingredients are required for compatibility check."
                    }));
                }

                CompatibilityResult result = _checker.Check(trimmed);

                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Info(
                        $"[Tool Call] check_compatibility: {(result.IsCompatible ? "COMPATIBLE" : "INCOMPATIBLE")} score={result.CompatibilityScore:F2}",
                        LogTag.Llm);
                }

                return Task.FromResult(JsonConvert.SerializeObject(new CompatibilityToolResult
                {
                    Success = true,
                    IsCompatible = result.IsCompatible,
                    Score = result.CompatibilityScore,
                    Reason = result.Reason,
                    Warnings = result.Warnings,
                    Bonuses = result.Bonuses
                }));
            }
            catch (Exception ex)
            {
                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Error($"[Tool Call] check_compatibility: FAILED - {ex.Message}", LogTag.Llm);
                }

                return Task.FromResult(JsonConvert.SerializeObject(new CompatibilityToolResult
                {
                    Success = false,
                    Error = $"Compatibility check failed: {ex.Message}"
                }));
            }
        }

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
}

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
            ("ingredients", "array", true, "Array of ingredient names to check compatibility (e.g. ['IronOre', 'FireStone'])")
        );

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<object, CancellationToken, Task<string>> func = ExecuteAsync;
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
        public Task<string> ExecuteAsync(object ingredientsObj, CancellationToken cancellationToken = default)
        {
            string[] ingredients = null;

            if (ingredientsObj != null)
            {
                if (ingredientsObj is string[] arr)
                    ingredients = arr;
                else if (ingredientsObj is Newtonsoft.Json.Linq.JArray jArr)
                    ingredients = jArr.ToObject<string[]>();
                else if (ingredientsObj is string str)
                {
                    ingredients = str.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }

            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] check_compatibility: ingredients=[{string.Join(", ", ingredients ?? Array.Empty<string>())}]", LogTag.Llm);
            }

            if (ingredients == null || ingredients.Length == 0)
            {
                return Task.FromResult(JsonConvert.SerializeObject(new CompatibilityToolResult
                {
                    Success = false,
                    Error = "Ingredients parameter is required. Provide an array of names."
                }));
            }

            try
            {
                List<string> trimmed = new();
                foreach (string item in ingredients)
                {
                    string t = item?.Trim();
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
    }
}

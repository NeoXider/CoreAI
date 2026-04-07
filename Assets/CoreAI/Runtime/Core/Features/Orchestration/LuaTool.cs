using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI AIFunction для выполнения Lua скриптов от Programmer агента.
    /// Используется в function calling pipeline вместо fenced Lua блоков.
    /// </summary>
    public sealed class LuaTool
    {
        private readonly ILuaExecutor _executor;

        public LuaTool(ILuaExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// Модель вызывает этот инструмент когда хочет выполнить Lua код.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<LuaResult>> func = ExecuteAsync;
            return AIFunctionFactory.Create(
                func,
                "execute_lua",
                "Execute Lua code. Use this to run game logic, create items, report events, etc.");
        }

        /// <summary>
        /// Выполняет Lua код переданный моделью.
        /// </summary>
        /// <param name="code">Lua код для выполнения</param>
        /// <param name="cancellationToken">Токен отмены</param>
        public async Task<LuaResult> ExecuteAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                return new LuaResult { Success = false, Error = "Lua code is required" };
            }

            if (CoreAISettings.LogToolCalls)
            {
                Logging.Log.Instance.Info($"[Tool Call] execute_lua: code length={code.Length}");
            }
            if (CoreAISettings.LogToolCallArguments)
            {
                var preview = code.Length > 150 ? code.Substring(0, 150) : code;
                Logging.Log.Instance.Info($"  code preview: {preview}");
            }

            try
            {
                LuaResult result = await _executor.ExecuteAsync(code, cancellationToken);
                
                if (CoreAISettings.LogToolCallResults)
                {
                    var outputPreview = result.Output?.Length > 100 ? result.Output.Substring(0, 100) : result.Output;
                    Logging.Log.Instance.Info($"[Tool Call] execute_lua: {(result.Success ? "SUCCESS" : "FAILED")} - output={outputPreview}");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                if (CoreAISettings.LogToolCallResults)
                {
                    Logging.Log.Instance.Error($"[Tool Call] execute_lua: FAILED - {ex.Message}");
                }
                
                return new LuaResult
                {
                    Success = false,
                    Error = $"Lua execution failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Результат выполнения Lua.
        /// </summary>
        public sealed class LuaResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// Интерфейс исполнителя Lua - позволяет тестировать без Unity.
        /// </summary>
        public interface ILuaExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken);
        }
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
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

            try
            {
                var result = await _executor.ExecuteAsync(code, cancellationToken);
                return result;
            }
            catch (Exception ex)
            {
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

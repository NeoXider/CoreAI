using System;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI AIFunction для выполнения Lua скриптов от Programmer агента.
    /// Используется в function calling pipeline вместо fenced Lua блоков.
    /// </summary>
    public sealed class LuaTool
    {
        private readonly ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly CoreAI.Logging.ILog _logger;

        public LuaTool(ILuaExecutor executor, ICoreAISettings settings, CoreAI.Logging.ILog logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// Модель вызывает этот инструмент когда хочет выполнить Lua код.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = "execute_lua",
                Description = "Execute Lua code. Use this to run game logic, create items, report events, etc."
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>
        /// Выполняет Lua код переданный моделью.
        /// </summary>
        /// <param name="code">Lua код для выполнения</param>
        /// <param name="cancellationToken">Токен отмены</param>
        public async Task<string> ExecuteAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                return SerializeResult(new LuaResult { Success = false, Error = "Lua code is required" });
            }

            if (_settings.LogToolCalls)
            {
                _logger.Info( $"[Tool Call] execute_lua: code length={code.Length}");
            }

            if (_settings.LogToolCallArguments)
            {
                string preview = code.Length > 150 ? code.Substring(0, 150) : code;
                _logger.Info( $"  code preview: {preview}");
            }

            try
            {
                LuaResult result = await _executor.ExecuteAsync(code, cancellationToken);

                if (_settings.LogToolCallResults)
                {
                    string outputPreview =
                        result.Output?.Length > 100 ? result.Output.Substring(0, 100) : result.Output;
                    _logger.Info( 
                        $"[Tool Call] execute_lua: {(result.Success ? "SUCCESS" : "FAILED")} - output={outputPreview}");
                }

                return SerializeResult(result);
            }
            catch (Exception ex)
            {
                if (_settings.LogToolCallResults)
                {
                    _logger.Error( $"[Tool Call] execute_lua: FAILED - {ex.Message}");
                }

                return SerializeResult(new LuaResult
                {
                    Success = false,
                    Error = $"Lua execution failed: {ex.Message}"
                });
            }
        }

        private static string SerializeResult(LuaResult result)
        {
            return JsonConvert.SerializeObject(result);
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

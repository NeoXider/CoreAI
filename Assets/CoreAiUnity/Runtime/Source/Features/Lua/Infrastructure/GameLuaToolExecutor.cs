#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Production <see cref="LuaTool.ILuaExecutor"/> backing the native <c>execute_lua</c> tool:
    /// runs each chunk in a fresh sandboxed script with the full game bindings, mirroring the
    /// envelope pipeline in <see cref="LuaAiEnvelopeProcessor"/> (same sandbox limits, same
    /// observer notifications, same result/error caps). State persists through C#-side APIs the
    /// bindings expose (logic slots, mods, world commands), not through Lua globals.
    /// </summary>
    public sealed class GameLuaToolExecutor : LuaTool.ILuaExecutor
    {
        private readonly SecureLuaEnvironment _sandbox;
        private readonly IGameLuaRuntimeBindings _bindings;
        private readonly ILuaExecutionObserver _observer;

        /// <summary>
        /// Raised after <c>execute_lua</c> successfully runs a chunk. Scene demos can subscribe to
        /// persist their own game-specific Lua changes without making the generic executor own
        /// scene policy.
        /// </summary>
        public static event Action<string> LuaExecutedSuccessfully;

        public GameLuaToolExecutor(
            SecureLuaEnvironment sandbox,
            IGameLuaRuntimeBindings bindings,
            ILuaExecutionObserver observer)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
        {
            if (!SecureLuaEnvironment.IsSupported)
            {
                return Task.FromResult(new LuaTool.LuaResult
                {
                    Success = false,
                    Error = "CoreAI Lua execution is disabled on this platform."
                });
            }

            try
            {
                LuaApiRegistry registry = new();
                _bindings.RegisterGameplayApis(registry);
                Script script = _sandbox.CreateScript(registry);
                DynValue result = _sandbox.RunChunk(script, code);
                string summary = Truncate(result.ToPrintString(), LuaAiEnvelopeProcessor.MaxResultSummaryLength);
                _observer.OnLuaSuccess(summary);
                LuaExecutedSuccessfully?.Invoke(code ?? "");
                return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = summary });
            }
            catch (Exception ex)
            {
                string message = ex is InterpreterException ie ? ie.Message : ex.Message;
                string flat = Truncate(
                    (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim(),
                    LuaAiEnvelopeProcessor.MaxErrorMessageLength);
                _observer.OnLuaFailure(flat);
                return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = flat });
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, maxLength) + " …(truncated)";
        }
    }
}
#endif

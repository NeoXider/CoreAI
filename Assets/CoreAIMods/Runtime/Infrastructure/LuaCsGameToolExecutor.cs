using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Sandbox.LuaCs;
using Lua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Gameplay bindings seam for the Lua-CSharp one-shot/envelope path. This is the ADDITIVE
    /// Lua-CSharp counterpart of <see cref="CoreAI.Ai.IGameLuaRuntimeBindings"/> (which is coupled to
    /// the MoonSharp <see cref="CoreAI.Sandbox.LuaApiRegistry"/> and therefore cannot be reused for the
    /// managed VM). Implementations register the game's callable APIs on a
    /// <see cref="LuaCsApiRegistry"/>. Both <c>LuaCsWorldRuntimeBindings</c> and
    /// <c>LuaCsFullUnityRuntimeBindings</c> already expose this exact method shape, so a host can adapt
    /// them into this interface for a later type-swap away from the MoonSharp executor.
    /// </summary>
    public interface ILuaCsGameRuntimeBindings
    {
        /// <summary>Registers gameplay-facing Lua-CSharp APIs in the provided registry.</summary>
        void RegisterGameplayApis(LuaCsApiRegistry registry);
    }

    /// <summary>Registers the default Lua-CSharp runtime APIs (mirrors <c>CoreDefaultLuaRuntimeBindings</c>).</summary>
    public sealed class CoreDefaultLuaCsRuntimeBindings : ILuaCsGameRuntimeBindings
    {
        /// <inheritdoc />
        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) counterpart of
    /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor"/>. Implements the SAME
    /// <see cref="LuaTool.ILuaExecutor"/> seam so the native <c>execute_lua</c> tool can later be
    /// re-pointed from the MoonSharp executor to this managed one by type. Each chunk runs in a fresh
    /// sandboxed <see cref="LuaState"/> under the <see cref="LuaCsExecutionGuard"/>, mirroring the
    /// envelope pipeline in <see cref="LuaCsAiEnvelopeProcessor"/> (same sandbox limits, same observer
    /// notifications, same result/error caps). State persists through the C#-side APIs the bindings
    /// expose (logic slots, mods, world commands), not through Lua globals.
    /// </summary>
    public sealed class LuaCsGameToolExecutor : LuaTool.ILuaExecutor
    {
        private readonly LuaCsSecureEnvironment _sandbox;
        private readonly ILuaCsGameRuntimeBindings _bindings;
        private readonly ILuaExecutionObserver _observer;

        /// <summary>
        /// Raised after <c>execute_lua</c> successfully runs a chunk. Mirrors
        /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor.LuaExecutedSuccessfully"/> so scene
        /// demos can persist their own game-specific Lua changes without the generic executor owning
        /// scene policy.
        /// </summary>
        public static event Action<string> LuaExecutedSuccessfully;

        /// <summary>
        /// True when the Lua-CSharp sandbox is available. Lua-CSharp is a managed, AOT-safe VM (the
        /// reason for this migration), so unlike <c>SecureLuaEnvironment</c> this is always supported
        /// — including IL2CPP/WebGL.
        /// </summary>
        public static bool IsSupported => true;

        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
        {
            if (!IsSupported)
            {
                return Task.FromResult(new LuaTool.LuaResult
                {
                    Success = false,
                    Error = "CoreAI Lua execution is disabled on this platform."
                });
            }

            // The world bindings are a shared singleton: a prior chunk that died between
            // coreai_world_begin() and commit/rollback leaves its transaction open, which would
            // silently buffer this chunk's world commands. Reset before running and abort in the
            // finally so a leaked transaction can never bleed across runs in either direction.
            // ILuaTransactionScope is VM-agnostic (LuaCsWorldRuntimeBindings implements it), so the
            // exact reset behavior of the MoonSharp executor is preserved here.
            (_bindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                LuaCsApiRegistry registry = new();
                _bindings.RegisterGameplayApis(registry);
                LuaState state = _sandbox.Create(registry);
                LuaValue[] results = _sandbox.RunChunk(state, code, cancellationToken: cancellationToken);
                string summary = Truncate(Summarize(results), LuaAiEnvelopeProcessor.MaxResultSummaryLength);
                _observer.OnLuaSuccess(summary);
                LuaExecutedSuccessfully?.Invoke(code ?? "");
                return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = summary });
            }
            catch (Exception ex)
            {
                string flat = Truncate(
                    (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim(),
                    LuaAiEnvelopeProcessor.MaxErrorMessageLength);
                _observer.OnLuaFailure(flat);
                return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = flat });
            }
            finally
            {
                (_bindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        /// <summary>Renders the chunk's first return value into a printable summary (VM-agnostic).</summary>
        internal static string Summarize(LuaValue[] results)
        {
            if (results == null || results.Length == 0)
            {
                return "nil";
            }

            return Stringify(results[0]);
        }

        private static string Stringify(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return "nil";
                case LuaValueType.Boolean:
                    return value.Read<bool>() ? "true" : "false";
                case LuaValueType.Number:
                    return value.Read<double>().ToString(CultureInfo.InvariantCulture);
                case LuaValueType.String:
                    return value.Read<string>() ?? "";
                default:
                    return value.ToString();
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, maxLength) + " ...(truncated)";
        }
    }
}

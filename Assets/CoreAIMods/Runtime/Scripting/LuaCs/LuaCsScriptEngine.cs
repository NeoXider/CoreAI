using System.Threading;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IScriptEngine"/> and the composition root's single entry into
    /// the VM: it owns the <see cref="LuaCsSecureEnvironment"/> so nothing outside the adapter layer
    /// creates a <c>LuaState</c> directly.
    /// </summary>
    public sealed class LuaCsScriptEngine : IScriptEngine
    {
        private readonly LuaCsSecureEnvironment _environment;

        public LuaCsScriptEngine(LuaCsSecureEnvironment environment = null)
        {
            _environment = environment ?? new LuaCsSecureEnvironment();
        }

        /// <summary>The wrapped secure environment (adapter-internal).</summary>
        internal LuaCsSecureEnvironment Environment => _environment;

        /// <inheritdoc />
        public string EngineName => "Lua-CSharp";

        /// <inheritdoc />
        public string EngineVersion => "0.5.5 (Lua 5.2, double-only numbers)";

        /// <inheritdoc />
        public IValueMarshaller Marshaller => LuaCsValueMarshaller.Instance;

        /// <inheritdoc />
        public IScriptState CreateState(ScriptSandboxProfile profile = null)
        {
            // WHY: The profile carries no knobs yet; every state gets the full hardening pass
            // (stripped globals, capped string/table builders, guarded coroutine library).
            return new LuaCsScriptState(_environment.Create());
        }

        /// <inheritdoc />
        public IScriptFunctionRegistry CreateFunctionRegistry()
        {
            return new LuaCsApiRegistry();
        }

        /// <inheritdoc />
        public IScriptExecutionGuard CreateGuard(IExecutionBudget budget = null)
        {
            return new LuaCsScriptExecutionGuard(budget);
        }

        /// <inheritdoc />
        public IScriptCoroutine CreateCoroutine(
            IScriptState ownerState,
            object callable,
            IExecutionBudget resumeBudget = null)
        {
            LuaState owner = LuaCsScriptState.Unwrap(ownerState);
            LuaCsCoroutineHandle handle = new(
                owner,
                LuaCsScriptExecutionGuard.UnwrapCallable(callable),
                resumeBudget != null && resumeBudget.MaxSteps > 0
                    ? (int)System.Math.Min(resumeBudget.MaxSteps, int.MaxValue)
                    : LuaCsCoroutineHandle.DefaultBudgetPerResume,
                resumeBudget != null && resumeBudget.TimeoutMs > 0
                    ? resumeBudget.TimeoutMs
                    : LuaCsCoroutineHandle.DefaultResumeTimeoutMs);
            return new LuaCsScriptCoroutine(handle);
        }

        /// <inheritdoc />
        public object[] RunChunk(
            IScriptState state,
            string source,
            IScriptExecutionGuard guard = null,
            CancellationToken cancellationToken = default)
        {
            LuaState lua = LuaCsScriptState.Unwrap(state);
            LuaValue[] results = _environment.RunChunk(
                lua,
                source,
                (guard as LuaCsScriptExecutionGuard)?.Inner,
                cancellationToken);

            object[] boxed = new object[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                boxed[i] = LuaCsValueMarshaller.Box(results[i]);
            }

            return boxed;
        }
    }
}

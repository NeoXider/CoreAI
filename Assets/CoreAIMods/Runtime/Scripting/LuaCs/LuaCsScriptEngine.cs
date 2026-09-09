using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances.Scheduling;
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
        private readonly IRbxRuntimeObservabilitySink _observability;
        private readonly ILuaCsGuardObserver _guardObserver;
        private readonly LuaCsCoroutineBudgetSettings _coroutineResumeBudget;

        public LuaCsScriptEngine(LuaCsSecureEnvironment environment = null,
            IRbxRuntimeObservabilitySink observability = null,
            ILuaCsGuardObserver guardObserver = null,
            LuaCsCoroutineBudgetSettings coroutineResumeBudget = null)
        {
            _environment = environment ?? new LuaCsSecureEnvironment();
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            _guardObserver = guardObserver;
            // WHY never null: CreateCoroutine's fallback below always has a settings object to read,
            // so "nothing registered in composition" and "a settings object whose fields are the
            // documented defaults" behave identically — see LuaCsCoroutineBudgetSettings.
            _coroutineResumeBudget = coroutineResumeBudget ?? new LuaCsCoroutineBudgetSettings();
        }

        /// <summary>The wrapped secure environment (adapter-internal).</summary>
        internal LuaCsSecureEnvironment Environment => _environment;

        /// <summary>
        /// The live per-resume coroutine budget this engine arms, never null. A second surface that
        /// builds its own engine over the SAME sandbox (the one-off <c>execute_lua</c> executor) hands
        /// this object to its engine so a host's <c>ScriptContext:SetTimeout</c> reaches both surfaces
        /// rather than only the persistent mod runtime.
        /// </summary>
        public LuaCsCoroutineBudgetSettings CoroutineResumeBudget => _coroutineResumeBudget;

        /// <inheritdoc />
        public string EngineName => "Lua-CSharp";

        /// <inheritdoc />
        public string EngineVersion => "0.5.6 (Lua 5.2, double-only numbers)";

        /// <inheritdoc />
        public IValueMarshaller Marshaller => LuaCsValueMarshaller.Instance;

        /// <inheritdoc />
        public IScriptState CreateState(ScriptSandboxProfile profile = null)
        {
            // WHY: The profile carries no knobs yet; every state gets the full hardening pass
            // (stripped globals, capped string/table builders, guarded coroutine library). Passing
            // _coroutineResumeBudget (never null, and the SAME live object every other coroutine-handle
            // site in this world reads) closes the raw coroutine.resume escape hatch: a later
            // ScriptContext:SetTimeout now reaches a mod-created raw coroutine's resume guard too, not
            // just the C#-managed LuaCsCoroutineHandle sites.
            return new LuaCsScriptState(_environment.Create(liveResumeBudget: _coroutineResumeBudget));
        }

        /// <inheritdoc />
        public IScriptFunctionRegistry CreateFunctionRegistry()
        {
            return new LuaCsApiRegistry();
        }

        /// <inheritdoc />
        public IScriptExecutionGuard CreateGuard(IExecutionBudget budget = null)
        {
            return new LuaCsScriptExecutionGuard(budget, _observability, _guardObserver);
        }

        /// <inheritdoc />
        public IScriptCoroutine CreateCoroutine(
            IScriptState ownerState,
            object callable,
            IExecutionBudget resumeBudget = null)
        {
            LuaState owner = LuaCsScriptState.Unwrap(ownerState);
            // WHY the branch: an explicit resumeBudget (e.g. a mod's HandlerMaxSteps/HandlerTimeoutMs)
            // is a per-call override and stays frozen exactly as before. Absent one — the common case
            // for task.spawn/defer/delay and every signal-handler thread that reaches this seam — the
            // handle reads the composition's configurable default LIVE on every resume, so a later
            // ScriptContext:SetTimeout affects it too. See LuaCsCoroutineBudgetSettings.
            LuaCsCoroutineHandle handle = resumeBudget != null
                ? new LuaCsCoroutineHandle(
                    owner,
                    LuaCsScriptExecutionGuard.UnwrapCallable(callable),
                    resumeBudget.MaxSteps > 0
                        ? (int)System.Math.Min(resumeBudget.MaxSteps, int.MaxValue)
                        : LuaCsCoroutineHandle.DefaultBudgetPerResume,
                    resumeBudget.TimeoutMs > 0
                        ? resumeBudget.TimeoutMs
                        : LuaCsCoroutineHandle.DefaultResumeTimeoutMs)
                : new LuaCsCoroutineHandle(
                    owner,
                    LuaCsScriptExecutionGuard.UnwrapCallable(callable),
                    _coroutineResumeBudget.BudgetPerResume,
                    _coroutineResumeBudget.ResumeTimeoutMs,
                    liveResumeBudget: _coroutineResumeBudget);
            return new LuaCsScriptCoroutine(handle);
        }

        /// <inheritdoc />
        public object[] RunChunk(
            IScriptState state,
            string source,
            IScriptExecutionGuard guard = null,
            CancellationToken cancellationToken = default)
        {
            LuaValue[] results = _environment.RunChunk(
                LuaCsScriptState.Unwrap(state),
                source,
                ResolveGuard(guard),
                cancellationToken);

            return Box(results);
        }

        /// <inheritdoc />
        public async Task<object[]> RunChunkAsync(
            IScriptState state,
            string source,
            IScriptExecutionGuard guard = null,
            IScriptFrameYielder frameYielder = null,
            CancellationToken cancellationToken = default)
        {
            LuaValue[] results = await _environment.RunChunkAsync(
                LuaCsScriptState.Unwrap(state),
                source,
                ResolveGuard(guard),
                frameYielder,
                cancellationToken);

            return Box(results);
        }

        // WHY: a caller that supplied no guard still has to get the engine's observability/observer
        // wiring, which is why this is not simply "null means defaults" inside the environment.
        private LuaCsExecutionGuard ResolveGuard(IScriptExecutionGuard guard)
        {
            LuaCsExecutionGuard luaGuard = (guard as LuaCsScriptExecutionGuard)?.Inner;
            if (luaGuard == null && (_observability != null || _guardObserver != null))
            {
                luaGuard = new LuaCsExecutionGuard(
                    maxSteps: LuaCsSecureEnvironment.OneShotHardLimitSteps,
                    observability: _observability,
                    guardObserver: _guardObserver);
            }

            return luaGuard;
        }

        private static object[] Box(LuaValue[] results)
        {
            object[] boxed = new object[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                boxed[i] = LuaCsValueMarshaller.Box(results[i]);
            }

            return boxed;
        }
    }
}

using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Factory/facade for one embedded script VM. This is the single seam a future second engine
    /// reimplements: everything above it (mod runtime, gameplay binders, tool executors) depends only on
    /// the neutral contracts in this namespace, and only the engine's adapter layer touches VM types.
    /// </summary>
    public interface IScriptEngine
    {
        /// <summary>Engine family name (e.g. "Lua-CSharp").</summary>
        string EngineName { get; }

        /// <summary>Engine/dialect version description.</summary>
        string EngineVersion { get; }

        /// <summary>The engine's value marshaller (single conversion authority).</summary>
        IValueMarshaller Marshaller { get; }

        /// <summary>Creates a secured, sandboxed state.</summary>
        IScriptState CreateState(ScriptSandboxProfile profile = null);

        /// <summary>Creates an empty function registry compatible with this engine's states.</summary>
        IScriptFunctionRegistry CreateFunctionRegistry();

        /// <summary>Creates an execution guard enforcing <paramref name="budget"/> (null = defaults).</summary>
        IScriptExecutionGuard CreateGuard(IExecutionBudget budget = null);

        /// <summary>Creates a budgeted coroutine from a script function on the owning state.</summary>
        IScriptCoroutine CreateCoroutine(IScriptState ownerState, object callable,
            IExecutionBudget resumeBudget = null);

        /// <summary>
        /// Loads and runs a source chunk on the state under <paramref name="guard"/> (null = the
        /// engine's one-shot default budget). Returns the chunk's results as raw script values.
        /// </summary>
        object[] RunChunk(
            IScriptState state,
            string source,
            IScriptExecutionGuard guard = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Same contract as <see cref="RunChunk"/>, but the execution may release the host frame through
        /// <paramref name="frameYielder"/> instead of blocking the calling loop for its whole run. This
        /// is the entry for one-shot chunks (tool-driven <c>execute_lua</c>), which can legitimately run
        /// for seconds; short handler calls keep using <see cref="RunChunk"/>.
        /// </summary>
        // WHY: a default implementation, not a second required member — an engine that has no
        // asynchronous entry (or a test double) stays valid and simply runs the chunk synchronously,
        // which is exactly what happened before this member existed.
        Task<object[]> RunChunkAsync(
            IScriptState state,
            string source,
            IScriptExecutionGuard guard = null,
            IScriptFrameYielder frameYielder = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(RunChunk(state, source, guard, cancellationToken));
    }
}

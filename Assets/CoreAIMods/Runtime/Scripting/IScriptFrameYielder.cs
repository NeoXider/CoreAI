using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Releases the host's frame from inside a long-running guarded script execution and resumes on a
    /// later host loop iteration.
    /// <para>
    /// A one-shot chunk can legitimately run for seconds before its wall-clock budget cuts it. On a
    /// single-threaded player (WebGL) that is the whole page: a measured runaway froze the browser main
    /// thread for ~6 s. An execution driven through the engine's asynchronous chunk entry hands this port
    /// to its guard, which awaits it whenever a slice of wall-clock has passed, so the host keeps drawing
    /// frames while the script runs.
    /// </para>
    /// </summary>
    // WHY: a small port in the VM-neutral scripting namespace rather than reusing
    // CoreAI.ILlmAsyncMarshaler.DelayAsync (the codebase's established "give the host loop a frame"
    // seam): IScriptEngine is the boundary a second script engine reimplements and must not name an
    // LLM-hosting contract, and ValueTask keeps the no-yield case allocation-free on the guard's hook
    // path. A host that already owns an ILlmAsyncMarshaler can implement this in one line over its
    // DelayAsync(1, token).
    public interface IScriptFrameYielder
    {
        /// <summary>Yields the host frame and completes once the host loop has advanced.</summary>
        /// <param name="cancellationToken">Cancels the wait; the execution is cut like any other trip.</param>
        ValueTask YieldFrameAsync(CancellationToken cancellationToken);
    }
}

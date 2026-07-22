using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// One MCP tool: a name, a human description, a real JSON Schema for its arguments (so clients
    /// validate before calling), and an invocation that returns MCP content.
    /// <para>
    /// WHY: <see cref="InvokeAsync"/> returns <see cref="Task{TResult}"/> (not UniTask) because it is
    /// awaited by the transport thread pool AND marshalled onto the Unity main thread; the reused CoreAI
    /// LLM-tool executors it wraps already expose <c>Task</c>-based entry points. The dispatcher is
    /// responsible for main-thread marshalling — a tool body may touch live game state directly.
    /// </para>
    /// </summary>
    public interface IMcpTool
    {
        /// <summary>Unique tool name exposed in <c>tools/list</c> and matched by <c>tools/call</c>.</summary>
        string Name { get; }

        /// <summary>Human-readable description shown to the model.</summary>
        string Description { get; }

        /// <summary>JSON Schema (object) describing the tool arguments, as a JSON string.</summary>
        string InputSchemaJson { get; }

        /// <summary>Invokes the tool with the client-supplied arguments object.</summary>
        /// <param name="arguments">The <c>arguments</c> member of a <c>tools/call</c>, never null (empty when omitted).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken);
    }
}

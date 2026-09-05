using System;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// An <see cref="IMcpToolResidencyPolicy"/> that delegates to a host-supplied function.
    /// FOR: the delegate/function override channel — the host passes a lambda in composition
    /// without implementing a policy class.
    /// </summary>
    public sealed class FuncMcpToolResidencyPolicy : IMcpToolResidencyPolicy
    {
        private readonly Func<IMcpTool, McpToolResidency> _resolve;

        /// <param name="resolve">Host function mapping a tool to its residency. Null resolves to Native.</param>
        public FuncMcpToolResidencyPolicy(Func<IMcpTool, McpToolResidency> resolve)
        {
            _resolve = resolve;
        }

        /// <inheritdoc />
        public McpToolResidency ResolveFor(IMcpTool tool)
        {
            if (_resolve == null || tool == null)
            {
                return McpToolResidency.Native;
            }

            return _resolve(tool);
        }
    }
}

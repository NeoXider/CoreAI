using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>Test double: a named MCP tool that echoes its arguments back as text.</summary>
    internal sealed class FakeMcpTool : IMcpTool
    {
        private readonly bool _fail;

        public FakeMcpTool(string name, bool fail = false)
        {
            Name = name;
            _fail = fail;
        }

        public string Name { get; }
        public string Description => $"Fake tool {Name}.";
        public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{\"echo\":{\"type\":\"string\"}}}";

        public int InvocationCount { get; private set; }

        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (_fail)
            {
                return Task.FromResult(McpToolResult.Failure($"{Name} failed."));
            }

            string echo = arguments?["echo"]?.ToString() ?? "";
            return Task.FromResult(McpToolResult.Text($"{Name}:{echo}"));
        }
    }
}

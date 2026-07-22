using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>Wires <see cref="ExecuteLuaMcpTool"/> to a stub executor (no Unity, no real Lua VM).</summary>
    public sealed class ExecuteLuaMcpToolEditModeTests
    {
        private sealed class StubExecutor : LuaTool.ILuaExecutor
        {
            private readonly LuaTool.LuaResult _result;
            public string LastCode { get; private set; }

            public StubExecutor(LuaTool.LuaResult result)
            {
                _result = result;
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                LastCode = code;
                return Task.FromResult(_result);
            }
        }

        [Test]
        public async Task Invoke_PassesCodeToExecutor_AndReturnsResultText()
        {
            StubExecutor executor = new(new LuaTool.LuaResult { Success = true, Output = "42" });
            ExecuteLuaMcpTool tool = new(executor);

            McpToolResult result = await tool.InvokeAsync(
                new JObject { ["code"] = "return 42" }, CancellationToken.None);

            Assert.AreEqual("return 42", executor.LastCode);
            Assert.IsFalse(result.IsError);
            JObject payload = JObject.Parse(result.Content[0].Text);
            Assert.IsTrue(payload["Success"]!.Value<bool>());
            Assert.AreEqual("42", payload["Output"]!.ToString());
        }

        [Test]
        public async Task Invoke_FailingResult_IsMarkedError()
        {
            StubExecutor executor = new(new LuaTool.LuaResult { Success = false, Error = "boom" });
            ExecuteLuaMcpTool tool = new(executor);

            McpToolResult result = await tool.InvokeAsync(
                new JObject { ["code"] = "error('boom')" }, CancellationToken.None);

            Assert.IsTrue(result.IsError);
        }

        [Test]
        public async Task Invoke_MissingCode_ReturnsError_WithoutCallingExecutor()
        {
            StubExecutor executor = new(new LuaTool.LuaResult { Success = true });
            ExecuteLuaMcpTool tool = new(executor);

            McpToolResult result = await tool.InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            Assert.IsNull(executor.LastCode, "executor must not run without code.");
        }
    }
}

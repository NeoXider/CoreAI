using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// MCP <c>manage_mods</c> spends the mods scope's shared budget of AI world changes, like the in-game
    /// <c>execute_lua</c> and <c>manage_mods</c>: an external agent cannot bypass the rate limit the player set.
    /// </summary>
    public sealed class McpManageModsRateLimitEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public async Task McpManageMods_SpendsTheSharedBudget_AndIsRefusedWithTheResetTimeOnceItIsExhausted()
        {
            double now = 100d;
            LuaGenerationRateLimiter limiter = new(2, 10d, () => now);
            ManageModsMcpTool tool = NewTool(limiter);
            Assert.IsTrue(limiter.TryAcquire(out _), "an in-game execute_lua spends the first slot");

            McpToolResult loaded = await tool.InvokeAsync(Load("mcp-a"), CancellationToken.None);
            now = 103d;
            McpToolResult refused = await tool.InvokeAsync(Load("mcp-b"), CancellationToken.None);

            Assert.IsFalse(loaded.IsError, loaded.Content[0].Text);
            Assert.AreEqual(2, limiter.GetBudget().Used, "the MCP call spent the same budget");
            Assert.IsTrue(refused.IsError);
            JObject body = JObject.Parse(refused.Content[0].Text);
            Assert.AreEqual("rate_limited", body.Value<string>("status"));
            StringAssert.Contains("next one is accepted in 7.0 s", body.Value<string>("message"));
            Assert.AreSame(limiter, tool.RateLimiter);
        }

        [Test]
        public async Task McpManageMods_ReadOnlyActions_NeverSpendTheBudget()
        {
            LuaGenerationRateLimiter limiter = new(1, 10d, () => 0d);
            ManageModsMcpTool tool = NewTool(limiter);
            Assert.IsTrue(limiter.TryAcquire(out _));

            McpToolResult listed = await tool.InvokeAsync(new JObject { ["action"] = "list" }, CancellationToken.None);

            Assert.IsFalse(listed.IsError, listed.Content[0].Text);
        }

        [Test]
        public async Task ToolProvider_PassesTheSharedLimiterToManageMods()
        {
            LuaGenerationRateLimiter limiter = new(2, 10d, () => 0d);
            McpToolRegistry registry = CoreAiMcpToolProvider.Build(
                null,
                new LuaCsModRuntime(versionStore: new MemoryLuaScriptVersionStore()),
                new CoreAISettingsOptions(),
                NullLog.Instance,
                LuaCapabilities.All,
                HostAdmin(),
                null,
                null,
                null,
                null,
                modRateLimiter: limiter);

            IMcpTool tool = registry.Find("manage_mods");
            Assert.IsNotNull(tool, "the provider publishes manage_mods");

            McpToolResult loaded = await tool.InvokeAsync(Load("mcp-provider"), CancellationToken.None);
            McpToolResult listed = await tool.InvokeAsync(new JObject { ["action"] = "list" }, CancellationToken.None);

            Assert.IsFalse(loaded.IsError, loaded.Content[0].Text);
            Assert.IsFalse(listed.IsError, listed.Content[0].Text);
            Assert.AreEqual(1, limiter.GetBudget().Used,
                "the published tool spent the shared budget on the load and nothing on the listing");
        }

        private static ManageModsMcpTool NewTool(LuaGenerationRateLimiter limiter)
        {
            return new ManageModsMcpTool(
                new LuaCsModRuntime(versionStore: new MemoryLuaScriptVersionStore()),
                new CoreAISettingsOptions(),
                NullLog.Instance,
                LuaCapabilities.All,
                HostAdmin(),
                limiter);
        }

        private static IActorIdentityProvider HostAdmin()
        {
            return new LocalActorIdentityProvider("mcp-host");
        }

        private static JObject Load(string modId)
        {
            return new JObject { ["action"] = "load", ["mod_id"] = modId, ["code"] = "local value = 1" };
        }
    }
}

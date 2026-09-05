using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The "register only what's available" logic in <see cref="CoreAiMcpToolProvider"/>: each tool
    /// appears only when its backing service was supplied.
    /// </summary>
    public sealed class CoreAiMcpToolProviderEditModeTests
    {
        private sealed class StubExecutor : LuaTool.ILuaExecutor
        {
            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                return Task.FromResult(new LuaTool.LuaResult { Success = true });
            }
        }

        private sealed class StubScreenshot : IScreenshotSource
        {
            public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
            {
                base64Png = "AAAA";
                error = null;
                return true;
            }
        }

        private static McpToolRegistry BuildMinimal(
            LuaTool.ILuaExecutor executor = null,
            IReadOnlyList<SkillSet> skills = null,
            IScreenshotSource screenshot = null)
        {
            return CoreAiMcpToolProvider.Build(
                executor,
                null,
                null,
                null,
                LuaCapabilities.All,
                null,
                null,
                null,
                skills,
                screenshot);
        }

        [Test]
        public void Screenshot_AbsentWhenNoSource()
        {
            McpToolRegistry registry = BuildMinimal(new StubExecutor(), screenshot: null);

            Assert.IsFalse(registry.Contains("screenshot"), "screenshot must be omitted without a source.");
            Assert.IsTrue(registry.Contains("execute_lua"));
        }

        [Test]
        public void Screenshot_PresentWhenSourceSupplied()
        {
            McpToolRegistry registry = BuildMinimal(new StubExecutor(), screenshot: new StubScreenshot());

            Assert.IsTrue(registry.Contains("screenshot"));
        }

        [Test]
        public void WorldCommand_AbsentWhenNoWorldTool()
        {
            McpToolRegistry registry = BuildMinimal(new StubExecutor());

            Assert.IsFalse(registry.Contains("world_command"));
        }

        [Test]
        public void ReadSkill_AbsentWhenNoSkills_PresentWhenSupplied()
        {
            McpToolRegistry without = BuildMinimal(new StubExecutor(), new List<SkillSet>());
            Assert.IsFalse(without.Contains("read_skill"));

            List<SkillSet> skills = new()
            {
                SkillSet.FromTextContent("Lua Modding", "desc", "instructions")
            };
            McpToolRegistry with = BuildMinimal(new StubExecutor(), skills);
            Assert.IsTrue(with.Contains("read_skill"));
        }

        [Test]
        public void ExecuteLua_AbsentWhenNoExecutor()
        {
            McpToolRegistry registry = BuildMinimal(null);

            Assert.IsFalse(registry.Contains("execute_lua"));
            // WHY: the provider composition always includes the broker, so an empty composition
            // lists exactly the broker — no domain tool appears without its backing service.
            Assert.AreEqual(1, registry.Count);
            Assert.IsTrue(registry.Contains(CoreAI.Mcp.Tools.CoreAiToolsBrokerMcpTool.ToolName));
        }
    }
}

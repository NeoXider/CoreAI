using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Diagnostics;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The debug tools over fakes: <c>get_unity_logs</c> (cursor, level, missed entries, a cursor from another
    /// process), <c>get_performance</c> (window clamp, counters and what the build lacks) and <c>game_status</c>
    /// (sections present only with their source, a failing section reported in place), plus the provider that
    /// registers them and the dispatcher counters a debugging client and the Hub read.
    /// </summary>
    public sealed class McpDiagnosticsToolsEditModeTests
    {
        [Test]
        public async Task UnityLogs_CursorReturnsOnlyNewEntries_AndNamesTheNextCursor()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Info, "booted");
            GetUnityLogsMcpTool tool = new(buffer);

            JObject first = await Call(tool, new JObject());
            buffer.Append(RuntimeLogLevel.Error, "boom");
            JObject second = await Call(tool, new JObject { ["since_sequence"] = first["next_sequence"] });

            Assert.AreEqual(1, (int)first["count"]);
            Assert.AreEqual("[1] INFO booted", (string)first["logs"]);
            Assert.AreEqual(1, (long)first["next_sequence"]);
            Assert.AreEqual("[2] ERROR boom", (string)second["logs"]);
            Assert.AreEqual(2, (long)second["next_sequence"]);
            Assert.AreEqual(1, (long)second["totals"]["errors"]);
        }

        [Test]
        public async Task UnityLogs_LevelFilter_AndUnknownLevelIsAnError()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Info, "noise");
            buffer.Append(RuntimeLogLevel.Warning, "careful");
            GetUnityLogsMcpTool tool = new(buffer);

            JObject warnings = await Call(tool, new JObject { ["level"] = "warning" });
            McpToolResult unknown = await tool.InvokeAsync(new JObject { ["level"] = "loud" }, CancellationToken.None);

            Assert.AreEqual("[2] WARN careful", (string)warnings["logs"]);
            Assert.IsTrue(unknown.IsError);
            StringAssert.Contains("info, warning, error, exception", unknown.Content[0].Text);
        }

        [Test]
        public async Task UnityLogs_ReportsEntriesEvictedBeforeTheCursor_AndResetsAForeignCursor()
        {
            RuntimeLogBuffer buffer = new(capacity: 2);
            for (int index = 0; index < 5; index++) buffer.Append(RuntimeLogLevel.Info, "m" + index);
            GetUnityLogsMcpTool tool = new(buffer);

            JObject behind = await Call(tool, new JObject { ["since_sequence"] = 1 });
            JObject foreign = await Call(tool, new JObject { ["since_sequence"] = 9000 });

            Assert.AreEqual(2, (long)behind["missed"], "entries 2 and 3 were evicted before this read");
            Assert.AreEqual(2, (int)behind["count"]);
            Assert.IsTrue((bool)foreign["cursor_reset"], "a cursor past this log comes from another process");
            Assert.AreEqual(2, (int)foreign["count"]);
            Assert.AreEqual(5, (long)foreign["next_sequence"]);
        }

        [Test]
        public async Task UnityLogs_StackTracesOnlyWhenAsked()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Exception, "NullReferenceException", "Door.Open ()\nDoor.Update ()");
            GetUnityLogsMcpTool tool = new(buffer);

            Assert.AreEqual("[1] EXCEPTION NullReferenceException", (string)(await Call(tool, new JObject()))["logs"]);
            StringAssert.Contains("Door.Open ()", (string)(await Call(tool, new JObject { ["stack"] = true }))["logs"]);
        }

        [Test]
        public async Task Performance_ClampsTheWindow_AndListsWhatTheBuildLacks()
        {
            FakePerformance performance = new();
            GetPerformanceMcpTool tool = new(performance);

            JObject result = await Call(tool, new JObject { ["window_seconds"] = 600 });
            JObject withoutCounters = await Call(tool, new JObject { ["counters"] = false });

            Assert.AreEqual(GetPerformanceMcpTool.DefaultWindowSeconds, performance.LastWindow, 1e-9,
                "the second call used the default window");
            Assert.AreEqual(59.5, (double)result["fps"]["fps_avg"], 1e-9);
            Assert.AreEqual(12, (long)result["counters"]["draw_calls"]);
            CollectionAssert.AreEqual(new[] { "gpu_ms" }, result["counters"]["unavailable"].ToObject<string[]>());
            Assert.IsNull(withoutCounters["counters"]);
            Assert.AreEqual(1, performance.CounterReads);
        }

        [Test]
        public async Task Performance_WindowOverTheMaximumIsClamped()
        {
            FakePerformance performance = new();
            await Call(new GetPerformanceMcpTool(performance), new JObject { ["window_seconds"] = 600 });
            Assert.AreEqual(GetPerformanceMcpTool.MaxWindowSeconds, performance.LastWindow, 1e-9);
        }

        [Test]
        public async Task Status_ReportsOnlyTheSectionsItHasSources_AndAFailingSectionInPlace()
        {
            RuntimeLogBuffer logs = new();
            logs.Append(RuntimeLogLevel.Error, "x");
            GameStatusMcpTool tool = new(new GameStatusSources
            {
                Engine = () => new JObject { ["platform"] = "WindowsPlayer" },
                Mods = () => throw new InvalidOperationException("runtime disposed"),
                Logs = logs,
                Performance = new FakePerformance()
            });

            JObject status = await Call(tool, new JObject());

            Assert.AreEqual(McpServerInfo.Version, (string)status["coreai_version"]);
            Assert.AreEqual("WindowsPlayer", (string)status["engine"]["platform"]);
            Assert.AreEqual(1, (long)status["logs"]["errors"]);
            Assert.AreEqual(59.5, (double)status["fps"]["fps_avg"], 1e-9);
            StringAssert.Contains("runtime disposed", (string)status["mods"]["error"]);
            Assert.IsNull(status["world"], "no world source, no world section");
            Assert.IsNull(status["mcp"]);
        }

        [Test]
        public void DiagnosticsTools_FollowTheirSources()
        {
            CollectionAssert.AreEqual(new[] { "game_status" },
                Names(McpDiagnosticsTools.Create(null, null, null)));
            CollectionAssert.AreEqual(new[] { "game_status", "get_unity_logs", "get_performance" },
                Names(McpDiagnosticsTools.Create(new RuntimeLogBuffer(), new FakePerformance(), null)));
        }

        [Test]
        public void DiagnosticsTools_PublishValidSchemas_AndShortDescriptions()
        {
            McpToolRegistry registry = new(McpDiagnosticsTools.Create(
                new RuntimeLogBuffer(), new FakePerformance(), null));
            JArray listed = registry.ToListJson();

            Assert.AreEqual(3, listed.Count);
            foreach (JToken tool in listed)
            {
                Assert.AreEqual("object", (string)tool["inputSchema"]["type"], (string)tool["name"]);
                Assert.LessOrEqual(((string)tool["description"]).Length, 300, (string)tool["name"]);
            }
        }

        [Test]
        public async Task Dispatcher_CountsRequestsAndToolFailures()
        {
            McpToolRegistry registry = new(new IMcpTool[] { new FakeMcpTool("ok"), new FakeMcpTool("bad", fail: true) });
            McpRpcDispatcher dispatcher = new(registry, new McpSessionStore(), new InlineMainThreadDispatcher());

            await dispatcher.DispatchAsync(Request(1, "ping", null), CancellationToken.None);
            await dispatcher.DispatchAsync(Request(2, "tools/call", new JObject { ["name"] = "ok" }), CancellationToken.None);
            await dispatcher.DispatchAsync(Request(3, "tools/call", new JObject { ["name"] = "bad" }), CancellationToken.None);

            Assert.AreEqual(3, dispatcher.RequestCount);
            Assert.AreEqual(2, dispatcher.ToolCallCount);
            Assert.AreEqual(1, dispatcher.ToolErrorCount);
            Assert.AreEqual("bad failed.", dispatcher.LastError);
        }

        private static JObject Request(int id, string method, JObject parameters)
        {
            JObject request = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) request["params"] = parameters;
            return request;
        }

        private static async Task<JObject> Call(IMcpTool tool, JObject arguments)
        {
            McpToolResult result = await tool.InvokeAsync(arguments, CancellationToken.None);
            Assert.IsFalse(result.IsError, result.Content[0].Text);
            return JObject.Parse(result.Content[0].Text);
        }

        private static List<string> Names(IReadOnlyList<IMcpTool> tools)
        {
            List<string> names = new();
            foreach (IMcpTool tool in tools) names.Add(tool.Name);
            return names;
        }

        private sealed class FakePerformance : IRuntimePerformanceSource
        {
            public double LastWindow { get; private set; }

            public int CounterReads { get; private set; }

            public FrameRateSnapshot GetFrameRate(double windowSeconds)
            {
                LastWindow = windowSeconds;
                return new FrameRateSnapshot(119, 2d, 59.5, 30d, 16.8, 20d, 33.3, 0);
            }

            public EngineCounterSnapshot GetCounters()
            {
                CounterReads++;
                return new EngineCounterSnapshot(new[] { new EngineCounterValue("draw_calls", 12, "count") },
                    new[] { "gpu_ms" });
            }
        }
    }
}

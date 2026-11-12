using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai.Logging;
using CoreAI.Diagnostics;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// <c>get_mod_logs source=unity</c>: the in-game agents' way to read the engine log and the frame rate. Offered
    /// only when the host supplies the engine log, so a host without it pays nothing in the definition; the mod
    /// source keeps its behaviour.
    /// </summary>
    public sealed class GetModLogsUnitySourceEditModeTests
    {
        [Test]
        public void WithoutTheEngineLog_TheDefinitionHasNoSource()
        {
            GetModLogsLlmTool tool = new(new EmptyModLog());
            AIFunction function = tool.CreateAIFunction();

            Assert.IsFalse(tool.OffersUnitySource);
            StringAssert.DoesNotContain("\"source\"", function.JsonSchema.GetRawText());
            StringAssert.DoesNotContain("\"source\"", tool.ParametersSchema);
            StringAssert.DoesNotContain("source=unity", tool.Description);
        }

        [Test]
        public void WithTheEngineLog_TheDefinitionOffersSource_WithinTheDescriptionLimit()
        {
            GetModLogsLlmTool tool = new(new EmptyModLog(), null, null, new RuntimeLogBuffer(), new FakeFrames());
            AIFunction function = tool.CreateAIFunction();

            Assert.IsTrue(tool.OffersUnitySource);
            StringAssert.Contains("\"source\"", function.JsonSchema.GetRawText());
            StringAssert.Contains("\"source\"", tool.ParametersSchema);
            Assert.LessOrEqual(tool.Description.Length, 500);
            JObject.Parse(tool.ParametersSchema);
        }

        [Test]
        public async Task UnitySource_ReadsWarningsAndErrorsByDefault_WithFps()
        {
            RuntimeLogBuffer engine = new();
            engine.Append(RuntimeLogLevel.Info, "chunk 12 meshed");
            engine.Append(RuntimeLogLevel.Exception, "NullReferenceException: door", "Door.Open ()\nDoor.Tick ()");
            GetModLogsLlmTool tool = new(new EmptyModLog(), null, null, engine, new FakeFrames());

            JObject result = JObject.Parse(await tool.ExecuteWithSourceAsync(source: "unity"));

            Assert.IsTrue((bool)result["success"]);
            Assert.AreEqual("unity", (string)result["source"]);
            Assert.AreEqual(1, (int)result["count"], "info lines are left out unless level asks for them");
            Assert.AreEqual("[2] EXCEPTION NullReferenceException: door\n    Door.Open ()\n    Door.Tick ()",
                (string)result["logs"]);
            Assert.AreEqual(2, (long)result["next_sequence"]);
            Assert.AreEqual(57.5, (double)result["fps"]["fps_avg"], 1e-9);
        }

        [Test]
        public async Task UnitySource_CursorAndLevel()
        {
            RuntimeLogBuffer engine = new();
            engine.Append(RuntimeLogLevel.Info, "a");
            engine.Append(RuntimeLogLevel.Info, "b");
            GetModLogsLlmTool tool = new(new EmptyModLog(), null, null, engine, null);

            JObject result = JObject.Parse(await tool.ExecuteWithSourceAsync(level: "print", since_sequence: 1,
                source: "UNITY"));

            Assert.AreEqual("[2] INFO b", (string)result["logs"]);
            Assert.IsNull(result["fps"], "no frame source, no fps");
        }

        [Test]
        public async Task UnknownSource_FailsInOneSentence_AndModsIsTheDefault()
        {
            GetModLogsLlmTool tool = new(new EmptyModLog(), null, null, new RuntimeLogBuffer(), null);

            JObject unknown = JObject.Parse(await tool.ExecuteWithSourceAsync(source: "disk"));
            JObject mods = JObject.Parse(await tool.ExecuteWithSourceAsync());

            Assert.IsFalse((bool)unknown["success"]);
            StringAssert.Contains("mods, unity", (string)unknown["message"]);
            Assert.IsTrue((bool)mods["success"]);
            Assert.IsNull(mods["source"], "the mod source keeps its own result shape");
        }

        private sealed class FakeFrames : IRuntimePerformanceSource
        {
            public FrameRateSnapshot GetFrameRate(double windowSeconds) =>
                new(287, 5d, 57.5, 40d, 17.4, 19d, 25d, 0);

            public EngineCounterSnapshot GetCounters() => EngineCounterSnapshot.None;
        }

        private sealed class EmptyModLog : ILuaLogService
        {
            public event Action<LuaLogEntry> EntryAppended { add { } remove { } }

            public void Append(LuaLogEntry entry)
            {
            }

            public IReadOnlyList<LuaLogEntry> Query(LuaLogQuery query) => Array.Empty<LuaLogEntry>();

            public void Clear(string modId = null)
            {
            }
        }
    }
}

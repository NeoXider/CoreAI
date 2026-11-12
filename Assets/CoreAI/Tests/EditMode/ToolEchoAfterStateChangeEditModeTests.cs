#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Echo suppression inside one request after a call that changed state. A repeat of a call that already
    /// succeeded is answered "use its earlier result"; for a read that answer goes stale once state changed, so a
    /// successful state-changing call makes the earlier reads run again. What the rule is for stays: identical
    /// reads with nothing changed in between are still suppressed, and an echoed state-changing call is never
    /// replayed. Covered on the batch and the streamed path, sequential and parallel.
    /// </summary>
    public sealed class ToolEchoAfterStateChangeEditModeTests
    {
        private sealed class PlainTool : ILlmTool
        {
            public string Name { get; set; } = "";
            public string Description => "test tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
            public bool IsMutating { get; set; }
        }

        /// <summary>A tool whose <c>read</c> action only reads and whose other actions change state.</summary>
        private sealed class ActionTool : ILlmTool, ILlmToolCallEffects
        {
            public string Name { get; set; } = "";
            public string Description => "test tool with actions";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
            public bool Throws { get; set; }

            public bool ChangesState(string functionName, IDictionary<string, object> arguments)
            {
                if (Throws)
                {
                    throw new InvalidOperationException("classifier failed");
                }

                return LlmToolCallArguments.GetAction(arguments) != "read";
            }
        }

        private sealed class Harness
        {
            private readonly bool _streamed;
            private int _callNumber;

            public Harness(bool streamed, int maxParallel, params ILlmTool[] tools)
            {
                _streamed = streamed;
                Options = new MEAI.ChatOptions { Tools = new List<MEAI.AITool>() };
                foreach (ILlmTool tool in tools)
                {
                    string name = tool.Name;
                    Func<string, string, string> body = (action, target) => Run(name, action, target);
                    Options.Tools.Add(MEAI.AIFunctionFactory.Create(body,
                        new MEAI.AIFunctionFactoryOptions { Name = name, Description = "test" }));
                }

                Policy = new ToolExecutionPolicy(NullLog.Instance,
                    new CoreAISettingsOptions { MaxParallelToolCalls = maxParallel, LogMeaiToolCallingSteps = false },
                    new List<ILlmTool>(tools), false, "test", 3);
            }

            public ToolExecutionPolicy Policy { get; }

            public MEAI.ChatOptions Options { get; }

            public List<string> Runs { get; } = new();

            private string Run(string tool, string action, string target)
            {
                lock (Runs)
                {
                    Runs.Add(tool + ":" + action + ":" + target);
                }

                return target == "fail" ? "{\"success\":false,\"message\":\"failed\"}" : "{\"success\":true}";
            }

            /// <summary>One turn; answers true for each call that was suppressed as an echo.</summary>
            public async Task<bool[]> TurnAsync(params (string Tool, string Action, string Target)[] calls)
            {
                List<MEAI.FunctionCallContent> contents = new();
                foreach ((string tool, string action, string target) in calls)
                {
                    _callNumber++;
                    contents.Add(new MEAI.FunctionCallContent("call-" + _callNumber, tool,
                        new Dictionary<string, object> { ["action"] = action, ["target"] = target }));
                }

                List<MEAI.AIContent> results;
                if (_streamed)
                {
                    ToolExecutionPolicy.StreamedTurn turn = Policy.BeginStreamedTurn();
                    foreach (MEAI.FunctionCallContent content in contents)
                    {
                        await Policy.ExecuteStreamedAsync(turn, content, Options, CancellationToken.None);
                    }

                    results = (await Policy.CompleteStreamedTurnAsync(turn, CancellationToken.None)).Results;
                }
                else
                {
                    results = (await Policy.ExecuteBatchAsync(contents, Options, CancellationToken.None)).Results;
                }

                bool[] echoes = new bool[results.Count];
                for (int index = 0; index < results.Count; index++)
                {
                    JObject json = JObject.Parse(((MEAI.FunctionResultContent)results[index]).Result?.ToString() ?? "{}");
                    echoes[index] = json.Value<bool?>("duplicate") == true;
                }

                return echoes;
            }
        }

        private static IEnumerable<TestCaseData> Paths()
        {
            yield return new TestCaseData(false, 1).SetName("{m}(batch, sequential)");
            yield return new TestCaseData(false, 4).SetName("{m}(batch, parallel)");
            yield return new TestCaseData(true, 1).SetName("{m}(streamed, sequential)");
            yield return new TestCaseData(true, 4).SetName("{m}(streamed, parallel)");
        }

        [TestCaseSource(nameof(Paths))]
        public async Task ARead_RepeatedAfterASuccessfulChange_RunsAgain(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new PlainTool { Name = "reader" },
                new PlainTool { Name = "writer", IsMutating = true });

            await harness.TurnAsync(("reader", "get", "a"));
            await harness.TurnAsync(("writer", "set", "b"));
            bool[] reread = await harness.TurnAsync(("reader", "get", "a"));

            Assert.IsFalse(reread[0], "the earlier result of the read is stale after the change");
            CollectionAssert.AreEqual(new[] { "reader:get:a", "writer:set:b", "reader:get:a" }, harness.Runs);
        }

        [TestCaseSource(nameof(Paths))]
        public async Task ARead_RepeatedWithNothingChanged_IsStillAnEcho(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new PlainTool { Name = "reader" },
                new PlainTool { Name = "other" });

            await harness.TurnAsync(("reader", "get", "a"));
            await harness.TurnAsync(("other", "get", "b"));
            bool[] reread = await harness.TurnAsync(("reader", "get", "a"));

            Assert.IsTrue(reread[0], "a read between them changes nothing, so the loop guard still answers the echo");
            Assert.AreEqual(2, harness.Runs.Count);
        }

        [TestCaseSource(nameof(Paths))]
        public async Task AnEchoedChange_IsNeverReplayed_EvenAfterAnotherChange(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new PlainTool { Name = "writer", IsMutating = true });

            await harness.TurnAsync(("writer", "set", "a"));
            await harness.TurnAsync(("writer", "set", "b"));
            bool[] echo = await harness.TurnAsync(("writer", "set", "a"));

            Assert.IsTrue(echo[0]);
            CollectionAssert.AreEqual(new[] { "writer:set:a", "writer:set:b" }, harness.Runs);
        }

        [TestCaseSource(nameof(Paths))]
        public async Task AFailedChange_LeavesEarlierReadsSuppressed(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new PlainTool { Name = "reader" },
                new PlainTool { Name = "writer", IsMutating = true });

            await harness.TurnAsync(("reader", "get", "a"));
            await harness.TurnAsync(("writer", "set", "fail"));
            bool[] reread = await harness.TurnAsync(("reader", "get", "a"));

            Assert.IsTrue(reread[0], "a failed change changed nothing, so the read is still an echo");
        }

        [TestCaseSource(nameof(Paths))]
        public async Task AReadInTheTurnOfAChange_IsNotRegistered_SoItRunsOnceMore(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new PlainTool { Name = "reader" },
                new PlainTool { Name = "writer", IsMutating = true });

            await harness.TurnAsync(("reader", "get", "a"), ("writer", "set", "b"));
            bool[] second = await harness.TurnAsync(("reader", "get", "a"));
            bool[] third = await harness.TurnAsync(("reader", "get", "a"));

            Assert.IsFalse(second[0], "the read may have run before the change in its turn");
            Assert.IsTrue(third[0], "once read after the change, it is an echo again");
        }

        [TestCaseSource(nameof(Paths))]
        public async Task PerCallEffects_ReadAndEditOfOneTool_BehaveLikeGetSourceAndEdit(bool streamed, int maxParallel)
        {
            Harness harness = new(streamed, maxParallel, new ActionTool { Name = "manage_mods" });

            await harness.TurnAsync(("manage_mods", "read", "page1"));
            bool[] echo = await harness.TurnAsync(("manage_mods", "read", "page1"));
            await harness.TurnAsync(("manage_mods", "edit", "x"));
            bool[] reread = await harness.TurnAsync(("manage_mods", "read", "page1"));
            bool[] editEcho = await harness.TurnAsync(("manage_mods", "edit", "x"));

            Assert.IsTrue(echo[0], "nothing changed between the two reads");
            Assert.IsFalse(reread[0], "the read after the edit runs again although the tool name is a mutating one");
            Assert.IsTrue(editEcho[0], "the edit echo is never replayed");
            CollectionAssert.AreEqual(
                new[] { "manage_mods:read:page1", "manage_mods:edit:x", "manage_mods:read:page1" }, harness.Runs);
        }

        [Test]
        public async Task AClassifierThatThrows_CountsAsAChange()
        {
            Harness harness = new(false, 1, new PlainTool { Name = "reader" },
                new ActionTool { Name = "odd", Throws = true });

            await harness.TurnAsync(("reader", "get", "a"));
            await harness.TurnAsync(("odd", "read", "b"));
            bool[] reread = await harness.TurnAsync(("reader", "get", "a"));

            Assert.IsFalse(reread[0]);
        }

        [Test]
        public void ArgumentReader_ReadsStringsJsonElementsAndTokens()
        {
            System.Text.Json.JsonElement element = System.Text.Json.JsonDocument.Parse("{\"action\":\" Edit \"}")
                .RootElement.GetProperty("action");
            Dictionary<string, object> arguments = new()
            {
                ["text"] = "get_source",
                ["element"] = element,
                ["token"] = new JValue("list"),
                ["number"] = 3,
                ["missing"] = null
            };

            Assert.AreEqual("get_source", LlmToolCallArguments.GetString(arguments, "text"));
            Assert.AreEqual("edit", LlmToolCallArguments.GetAction(arguments, "element"));
            Assert.AreEqual("list", LlmToolCallArguments.GetString(arguments, "token"));
            Assert.AreEqual("3", LlmToolCallArguments.GetString(arguments, "number"));
            Assert.AreEqual("", LlmToolCallArguments.GetString(arguments, "missing"));
            Assert.AreEqual("", LlmToolCallArguments.GetString(arguments, "absent"));
        }

        [Test]
        public void MemoryTool_OnlyReadReads()
        {
            MemoryLlmTool memory = new();

            Assert.IsFalse(memory.ChangesState("memory", new Dictionary<string, object> { ["action"] = "read" }));
            foreach (string action in new[] { "write", "append", "clear", "str_replace", "insert", "delete", "rename" })
            {
                Assert.IsTrue(memory.ChangesState("memory", new Dictionary<string, object> { ["action"] = action }),
                    action);
            }
        }
    }
}
#endif

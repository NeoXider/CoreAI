using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class DeterministicToolContractEditModeTests
    {
        [Test]
        public async Task BuildRequest_SameToolsDifferentInsertionOrder_RendersCanonicalPrefix()
        {
            CapturingLlmClient firstLlm = new();
            CapturingLlmClient secondLlm = new();

            AgentMemoryPolicy firstPolicy = BuildPolicy(new StubTool("z_tool"), new StubTool("a_tool"));
            AgentMemoryPolicy secondPolicy = BuildPolicy(new StubTool("a_tool"), new StubTool("z_tool"));
            TestSettings firstSettings = new();
            TestSettings secondSettings = new();

            AiOrchestrator first = BuildOrchestrator(firstLlm, firstPolicy, firstSettings);
            AiOrchestrator second = BuildOrchestrator(secondLlm, secondPolicy, secondSettings);

            await first.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            await second.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });

            Assert.AreEqual(firstLlm.LastRequest.SystemPrompt, secondLlm.LastRequest.SystemPrompt);
            CollectionAssert.AreEqual(
                new[] { "a_tool", "memory", "z_tool" },
                AiToolOrder.Canonical(firstLlm.LastRequest.Tools).Select(t => t.Name).ToArray());
            CollectionAssert.AreEqual(
                AiToolOrder.Canonical(firstLlm.LastRequest.Tools).Select(t => t.Name).ToArray(),
                AiToolOrder.Canonical(secondLlm.LastRequest.Tools).Select(t => t.Name).ToArray());
            AssertNoGuidOrTimestamp(firstLlm.LastRequest.SystemPrompt);
            AssertNoGuidOrTimestamp(secondLlm.LastRequest.SystemPrompt);
        }

        [Test]
        public void AppendToolContract_CanonicalizesSchemaObjectKeysRecursively()
        {
            const string schemaA =
                "{\"type\":\"object\",\"required\":[\"b\",\"a\"],\"properties\":{\"b\":{\"type\":\"string\",\"description\":\"B\"},\"a\":{\"type\":\"number\",\"description\":\"A\"}}}";
            const string schemaB =
                "{\"properties\":{\"a\":{\"description\":\"A\",\"type\":\"number\"},\"b\":{\"description\":\"B\",\"type\":\"string\"}},\"required\":[\"b\",\"a\"],\"type\":\"object\"}";

            string first = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("same_tool", schemaA) },
                new AiTaskRequest { RoleId = "Teacher" },
                new TestSettings());
            string second = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("same_tool", schemaB) },
                new AiTaskRequest { RoleId = "Teacher" },
                new TestSettings());

            Assert.AreEqual(first, second);
            StringAssert.Contains(
                "schema: {\"properties\":{\"a\":{\"description\":\"A\",\"type\":\"number\"},\"b\":{\"description\":\"B\",\"type\":\"string\"}},\"required\":[\"b\",\"a\"],\"type\":\"object\"}",
                first);
        }

        [Test]
        public async Task BuildRequest_FixedInputs_ProduceIdenticalSystemPrefixWithoutGeneratedIds()
        {
            AgentMemoryPolicy policy = BuildPolicy(new StubTool("z_tool"), new StubTool("a_tool"));
            TestSettings settings = new();
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            string first = llm.LastRequest.SystemPrompt;

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            string second = llm.LastRequest.SystemPrompt;

            Assert.AreEqual(first, second);
            AssertNoGuidOrTimestamp(first);
        }

        [Test]
        public void AppendToolContract_NativeToolCallingWithMemoryTool_IncludesMemoryImperative()
        {
            // Regression: the memory instruction used to live only on the text-shaped path, after the
            // native early-return, so native tool-calling roles (e.g. Creator) got no memory guidance and
            // ignored "remember the ..." tasks.
            string native = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("memory") },
                new AiTaskRequest { RoleId = "Creator" },
                new TestSettings(),
                true);

            StringAssert.Contains("call the memory tool", native,
                "Native tool-calling roles with the memory tool must receive the positive memory instruction.");
        }

        [Test]
        public void AppendToolContract_NativeToolCallingWithoutMemoryTool_OmitsMemoryImperative()
        {
            string native = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("world_command") },
                new AiTaskRequest { RoleId = "Creator" },
                new TestSettings(),
                true);

            StringAssert.DoesNotContain("call the memory tool", native,
                "Roles without the memory tool must not receive memory guidance.");
        }

        /// <summary>
        /// The availability list ends with "do not call any tool not listed", so a multi-function wrapper
        /// must be listed under the function names the provider is offered, not under its own name.
        /// </summary>
        [Test]
        public void BuildRequestToolAvailabilityMessage_MultiFunctionWrapper_ListsItsFunctionNames()
        {
            string message = AiToolContractPromptFormatter.BuildRequestToolAvailabilityMessage(
                new ILlmTool[] { new StubTool("greet"), new MultiFunctionStubTool() },
                new AiTaskRequest { RoleId = "Teacher" });

            StringAssert.Contains("Available tools:\n- camera_look\n- camera_list\n- greet\n",
                message.Replace("\r\n", "\n"));
            StringAssert.DoesNotContain("- camera\n", message.Replace("\r\n", "\n"));
        }

        /// <summary>
        /// WHY: the prompt formatter, every <c>ToolExecutionPolicy</c> and the request allowlist ask for a
        /// wrapper's names on each request, and every ask ran <c>AIFunctionFactory.Create</c> (reflection and
        /// schema generation) for every function. The names are built once per wrapper instance.
        /// </summary>
        [Test]
        public void GetCallableToolNames_Wrapper_BuildsItsFunctionsOncePerInstance()
        {
            CountingWrapperTool wrapper = new();
            ILlmTool[] tools = { new StubTool("greet"), wrapper };
            for (int i = 0; i < 3; i++)
            {
                AiToolContractPromptFormatter.BuildRequestToolAvailabilityMessage(
                    tools, new AiTaskRequest { RoleId = "Teacher" });
#if COREAI_LLM
                _ = new CoreAI.Infrastructure.Llm.ToolExecutionPolicy(null, new TestSettings(), tools, false, "Teacher");
#endif
                SkillSetToolResolver.RestrictToAllowedFunctions(wrapper, new[] { "camera_look" });
                CollectionAssert.AreEqual(new[] { "camera_look", "camera_list" },
                    SkillSetToolResolver.GetCallableToolNames(wrapper));
            }

            Assert.AreEqual(1, wrapper.Enumerations);

            CountingWrapperTool another = new();
            CollectionAssert.AreEqual(new[] { "camera_look", "camera_list" },
                SkillSetToolResolver.GetCallableToolNames(another));
            Assert.AreEqual(1, another.Enumerations, "The cache is per instance.");
        }

        [Test]
        public void GetCallableToolNames_FailedEnumeration_KeepsPartialNames_AndIsNotCached()
        {
            CountingWrapperTool wrapper = new() { FailuresLeft = 1 };

            CollectionAssert.AreEqual(new[] { "camera_look" }, SkillSetToolResolver.GetCallableToolNames(wrapper),
                "The functions built before the failure are still reported.");
            CollectionAssert.AreEqual(new[] { "camera_look", "camera_list" },
                SkillSetToolResolver.GetCallableToolNames(wrapper),
                "A failed enumeration is not cached; the next ask builds the names again.");
            CollectionAssert.AreEqual(new[] { "camera_look", "camera_list" },
                SkillSetToolResolver.GetCallableToolNames(wrapper));
            Assert.AreEqual(2, wrapper.Enumerations, "The complete enumeration is cached.");
        }

        [Test]
        public void RestrictToAllowedFunctions_NarrowsOnlyWhenSomeFunctionsAreAllowed()
        {
            CountingWrapperTool wrapper = new() { TimeoutOverride = 4321 };

            Assert.IsNull(SkillSetToolResolver.RestrictToAllowedFunctions(wrapper, new[] { "greet" }));
            Assert.AreSame(wrapper,
                SkillSetToolResolver.RestrictToAllowedFunctions(wrapper, new[] { "camera_list", "camera_look" }));

            ILlmTool narrowed = SkillSetToolResolver.RestrictToAllowedFunctions(wrapper, new[] { "camera_list", "greet" });

            Assert.IsNotNull(narrowed);
            Assert.AreNotSame(wrapper, narrowed);
            Assert.AreEqual("camera", narrowed.Name);
            Assert.AreEqual(wrapper.Description, narrowed.Description);
            Assert.AreEqual(4321, narrowed.ToolTimeoutMsOverride);
            Assert.IsTrue(narrowed.EndsTurn);
            Assert.IsTrue(narrowed.IsMutating);
            Assert.IsTrue(narrowed.AllowDuplicates);
            CollectionAssert.AreEqual(new[] { "camera_list" }, SkillSetToolResolver.GetCallableToolNames(narrowed));
            CollectionAssert.AreEqual(new[] { "camera_list" },
                ((IAIFunctionsLlmTool)narrowed).CreateAIFunctions().Select(f => f.Name).ToArray());
            string message = AiToolContractPromptFormatter.BuildRequestToolAvailabilityMessage(
                new[] { narrowed }, new AiTaskRequest { RoleId = "Teacher" }).Replace("\r\n", "\n");
            StringAssert.Contains("Available tools:\n- camera_list\n", message);
            StringAssert.DoesNotContain("camera_look", message);
        }

        private static AgentMemoryPolicy BuildPolicy(params ILlmTool[] tools)
        {
            AgentMemoryPolicy policy = new();
            policy.SetToolsForRole("Teacher", tools);
            policy.SetRuntimeContextProvider("Teacher", new TraceEchoRuntimeContextProvider());
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            AgentMemoryPolicy policy,
            TestSettings settings)
        {
            return new AiOrchestrator(
                new TestAuthority(),
                llm,
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new StaticSystemPromptProvider(), new NullUserPromptProvider(), null, null,
                    policy, settings),
                new TestMemoryStore(),
                policy,
                null,
                null,
                settings,
                new LocalActorIdentityProvider("tool-contract-test"));
        }

        private static void AssertNoGuidOrTimestamp(string value)
        {
            Assert.IsFalse(Regex.IsMatch(value ?? "", @"\b[0-9a-fA-F]{32}\b"),
                "Generated compact GUIDs must not appear in the frozen system prefix.");
            Assert.IsFalse(Regex.IsMatch(value ?? "", @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}"),
                "Timestamp-shaped values must not appear in the frozen system prefix.");
        }

        /// <summary>
        /// Regression: a tool description over 500 chars ended in a bare "..." in the tool contract and nothing was
        /// logged. The clip now names its count; because the marker depends only on the description, the cacheable
        /// prefix stays byte-identical from one turn to the next, and the cut is logged once, not every turn.
        /// </summary>
        [Test]
        public void AppendToolContract_LongDescription_ClippedWithCount_ByteIdenticalAcrossCalls_LoggedOnce()
        {
            TruncationMarker.ResetLogOnce();
            using ContractLogCapture log = new();
            string description = "Describes the tool. " + new string('d', 700);
            ILlmTool[] tools = { new DescribedTool("verbose_tool", description) };

            string first = AiToolContractPromptFormatter.AppendToolContract(
                "sys", tools, new AiTaskRequest { RoleId = "Teacher" }, new TestSettings());
            string second = AiToolContractPromptFormatter.AppendToolContract(
                "sys", tools, new AiTaskRequest { RoleId = "Teacher" }, new TestSettings());

            Assert.AreEqual(System.Text.Encoding.UTF8.GetBytes(first), System.Text.Encoding.UTF8.GetBytes(second),
                "the clipped contract must be byte-identical across turns, or the prompt cache breaks");
            int dropped = description.Length - AiToolContractPromptFormatter.ToolDescriptionMaxChars;
            StringAssert.Contains(
                description.Substring(0, AiToolContractPromptFormatter.ToolDescriptionMaxChars) + "…[+" + dropped + " chars]",
                first);
            string[] lines = log.Lines.Where(l => l.Contains("Tool 'verbose_tool' description clipped")).ToArray();
            Assert.AreEqual(1, lines.Length, "one log line per description, not one per turn");
            StringAssert.Contains($"{description.Length} chars total -> 500 shown, {dropped} dropped", lines[0]);
        }

        [Test]
        public void ClipToolDescription_ShortDescription_Unchanged_AndNotLogged()
        {
            TruncationMarker.ResetLogOnce();
            using ContractLogCapture log = new();

            Assert.AreEqual("short and sweet", AiToolContractPromptFormatter.ClipToolDescription("t", "short\nand  sweet"));
            Assert.IsEmpty(log.Lines);
        }

        [Test]
        public void CompactSchema_LongSchema_ClippedWithCount_Deterministic_LoggedOncePerSchema()
        {
            TruncationMarker.ResetLogOnce();
            using ContractLogCapture log = new();
            string schema = "{\"type\":\"object\",\"description\":\"" + new string('s', 1500) + "\"}";

            string first = CoreAI.Infrastructure.Llm.ToolExecutionPolicy.CompactSchema(
                schema, CoreAI.Infrastructure.Llm.ToolExecutionPolicy.SchemaHintMaxChars, "big_schema_tool", Log.Instance);
            string second = CoreAI.Infrastructure.Llm.ToolExecutionPolicy.CompactSchema(
                schema, CoreAI.Infrastructure.Llm.ToolExecutionPolicy.SchemaHintMaxChars, "big_schema_tool", Log.Instance);

            Assert.AreEqual(first, second);
            int dropped = schema.Length - CoreAI.Infrastructure.Llm.ToolExecutionPolicy.SchemaHintMaxChars;
            StringAssert.EndsWith("…[+" + dropped + " chars]", first);
            Assert.AreEqual(1, log.Lines.Count(l => l.Contains("Tool 'big_schema_tool' schema clipped")));
        }

        [Test]
        public void VersioningFormatters_LongSnapshots_ClipInsideTheFence_WithCount_LoggedOncePerSnapshot()
        {
            TruncationMarker.ResetLogOnce();
            using ContractLogCapture log = new();
            string lua = "-- start\n" + new string('l', 7000);
            string data = "{\"k\":\"" + new string('j', 9000) + "\"}";
            LuaScriptVersionRecord luaRecord = new("script_a", lua, lua, null);
            DataOverlayVersionRecord dataRecord = new("overlay_a", data, data, null);

            string luaPrompt = LuaScriptVersionPromptFormatter.Format("script_a", luaRecord);
            string luaPromptAgain = LuaScriptVersionPromptFormatter.Format("script_a", luaRecord);
            string dataPrompt = DataOverlayVersionPromptFormatter.Format("overlay_a", dataRecord);
            string mutationPrompt = MutationStatePromptFormatter.Format(
                "script_a", luaRecord, new[] { "overlay_a" }, new[] { dataRecord });

            Assert.AreEqual(luaPrompt, luaPromptAgain);
            StringAssert.Contains(lua.Substring(0, 6000) + "\n…[+" + (lua.Length - 6000) + " chars]\n```", luaPrompt);
            StringAssert.Contains(data.Substring(0, 8000) + "\n…[+" + (data.Length - 8000) + " chars]\n```", dataPrompt);
            StringAssert.Contains(lua.Substring(0, 5000) + "\n…[+" + (lua.Length - 5000) + " chars]\n```", mutationPrompt);
            StringAssert.Contains(data.Substring(0, 5000) + "\n…[+" + (data.Length - 5000) + " chars]\n```", mutationPrompt);
            Assert.AreEqual(2, log.Lines.Count(l => l.Contains("[LuaScriptVersionPromptFormatter] 'script_a'")),
                "original and current each log once, and the second Format call adds nothing");
            StringAssert.Contains($"{lua.Length} chars total -> 6000 shown, {lua.Length - 6000} dropped",
                log.Lines.First(l => l.Contains("[LuaScriptVersionPromptFormatter]")));
            Assert.AreEqual(2, log.Lines.Count(l => l.Contains("[DataOverlayVersionPromptFormatter] 'overlay_a'")));
            Assert.AreEqual(4, log.Lines.Count(l => l.Contains("[MutationStatePromptFormatter]")));
        }

        [Test]
        public void FailedToolRetryDetail_LongPlainText_ClippedWithCount_AndLogged()
        {
            using ContractLogCapture log = new();

            string detail = CoreAI.Infrastructure.Llm.MeaiLlmClient.ClipFailedToolDetail(
                "flaky_tool", new string('f', 300));

            Assert.AreEqual(new string('f', 240) + "…[+60 chars]", detail);
            StringAssert.Contains("Failed tool 'flaky_tool' detail clipped in the retry instruction: 300 chars total -> 240 kept, 60 dropped.",
                log.Lines.Single(l => l.Contains("flaky_tool")));
        }

        /// <summary>
        /// A JSON <c>error</c> went to the model whole before 7.46.0; the release about cuts must not add one.
        /// </summary>
        [Test]
        public void FailedToolRetryDetail_LongJsonError_GoesWhole_AndIsNotLogged()
        {
            using ContractLogCapture log = new();
            string error = new string('j', 600);

            string detail = CoreAI.Infrastructure.Llm.MeaiLlmClient.ClipFailedToolDetail(
                "json_tool", "{\"error\":\"" + error + "\"}");

            Assert.AreEqual(error, detail);
            Assert.IsFalse(log.Lines.Any(l => l.Contains("json_tool")));
            Assert.AreEqual(error, AiOrchestrator.ExtractToolTraceMessage("{\"message\":\"" + error + "\"}"));
        }

        private sealed class DescribedTool : ILlmTool
        {
            public DescribedTool(string name, string description)
            {
                Name = name;
                Description = description;
            }

            public string Name { get; }
            public string Description { get; }
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        /// <summary>Captures CoreAI log lines for one test and restores the previous log.</summary>
        private sealed class ContractLogCapture : ILog, IDisposable
        {
            private readonly ILog _previous = Log.Instance;

            public ContractLogCapture()
            {
                Log.Instance = this;
            }

            public List<string> Lines { get; } = new();

            public void Debug(string message, string tag = null) => Lines.Add(message);
            public void Info(string message, string tag = null) => Lines.Add(message);
            public void Warn(string message, string tag = null) => Lines.Add(message);
            public void Error(string message, string tag = null) => Lines.Add(message);

            public void Dispose()
            {
                Log.Instance = _previous;
            }
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name, string schema = "{}")
            {
                Name = name;
                ParametersSchema = schema;
            }

            public string Name { get; }
            public string Description => "stub tool";
            public string ParametersSchema { get; }
            public bool AllowDuplicates => false;
        }

        private sealed class MultiFunctionStubTool : ILlmTool, IAIFunctionsLlmTool
        {
            public string Name => "camera";
            public string Description => "camera functions";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public IEnumerable<Microsoft.Extensions.AI.AIFunction> CreateAIFunctions()
            {
                yield return Microsoft.Extensions.AI.AIFunctionFactory.Create((Func<string>)(() => "looked"),
                    new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "camera_look" });
                yield return Microsoft.Extensions.AI.AIFunctionFactory.Create((Func<string>)(() => "listed"),
                    new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "camera_list" });
            }
        }

        /// <summary>
        /// A <c>camera</c> wrapper that counts how often its functions are built; while
        /// <see cref="FailuresLeft"/> is positive an enumeration throws after the first function.
        /// </summary>
        private sealed class CountingWrapperTool : ILlmTool, IAIFunctionsLlmTool
        {
            public int Enumerations;
            public int FailuresLeft;
            public int? TimeoutOverride;
            public string Name => "camera";
            public string Description => "camera functions";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;
            public int? ToolTimeoutMsOverride => TimeoutOverride;
            public bool EndsTurn => true;
            public bool IsMutating => true;

            public IEnumerable<Microsoft.Extensions.AI.AIFunction> CreateAIFunctions()
            {
                Enumerations++;
                yield return Microsoft.Extensions.AI.AIFunctionFactory.Create((Func<string>)(() => "looked"),
                    new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "camera_look" });
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                    throw new InvalidOperationException("the second function cannot be built yet");
                }

                yield return Microsoft.Extensions.AI.AIFunctionFactory.Create((Func<string>)(() => "listed"),
                    new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "camera_list" });
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        private sealed class StaticSystemPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "Stable teacher prompt.";
                return true;
            }
        }

        private sealed class NullUserPromptProvider : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private sealed class TraceEchoRuntimeContextProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return "trace=" + traceId;
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLlmRequestRetries => 1;
            public int MaxContextOverflowRetries => 3;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string UniversalSystemPromptPrefix => "";
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public int MaxLuaRepairRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}

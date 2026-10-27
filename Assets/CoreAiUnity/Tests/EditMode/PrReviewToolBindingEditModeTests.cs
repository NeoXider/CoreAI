#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Exercises request-local discovery and disabled tools through the production MEAI client.</summary>
    public sealed class PrReviewToolBindingEditModeTests
    {
        [TestCase(false, false, "")]
        [TestCase(false, true, "")]
        [TestCase(true, false, "")]
        [TestCase(true, true, "")]
        [TestCase(false, false, "Teacher")]
        [TestCase(false, true, "Teacher")]
        [TestCase(true, false, "Teacher")]
        [TestCase(true, true, "Teacher")]
        public async Task LLM001_NoneModeDoesNotBindUnusedSkillFactories(bool streaming, bool native, string role)
        {
            CountingBindingTool nested = new CountingBindingTool();
            SkillSet[] catalog = { new SkillSet("unused", "Unused skill", "Do not call this skill.", nested) };
            ILlmTool proxy = CallSkillToolLlmTool.Create(catalog);
            nested.BindingAttempts = 0;
            EchoChatClient provider = new EchoChatClient();
            MeaiLlmClient client = new MeaiLlmClient(provider, GameLoggerUnscopedFallback.Instance,
                QuietSettings(), native, new InMemoryAgentMemoryStore());
            LlmCompletionRequest request = new LlmCompletionRequest
            {
                AgentRoleId = role,
                UserPayload = "Explain a fact without tools.",
                Tools = new[] { proxy },
                ForcedToolMode = LlmToolChoiceMode.None
            };
            string text = "";
            Exception failure = await Capture(async () =>
            {
                if (streaming)
                {
                    await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                    {
                        Assert.AreEqual(LlmErrorCode.None, chunk.ErrorCode, chunk.Error);
                        Assert.IsTrue(chunk.ExecutedToolCalls == null || chunk.ExecutedToolCalls.Count == 0);
                        text += chunk.Text;
                    }
                }
                else
                {
                    LlmCompletionResult result = await client.CompleteAsync(request);
                    Assert.IsTrue(result.Ok, result.Error);
                    Assert.IsTrue(result.ExecutedToolCalls == null || result.ExecutedToolCalls.Count == 0);
                    text = result.Content;
                }
            });
            Assert.IsNull(failure, "Explicit no-tool mode must not require a role binding: " + failure);
            Assert.AreEqual(1, provider.Calls);
            Assert.IsTrue(provider.ToolsWereAbsent);
            Assert.AreEqual(0, nested.BindingAttempts, "A disabled skill must not reconstruct nested factories.");
            Assert.AreEqual(0, nested.Invocations);
            Assert.AreEqual("tool-free answer", text);
        }

        [Test]
        public async Task MEM002_DiscoveryAndInvocationAgreeForIndependentRequestBindings()
        {
            MemoryLlmTool declaration = new MemoryLlmTool();
            SkillSet[] catalog = { new SkillSet("notes", "Memory notes", "Use memory to keep notes.", declaration) };
            ILlmTool originalRead = ReadSkillLlmTool.Create(catalog);
            ILlmTool originalCall = CallSkillToolLlmTool.Create(catalog);
            ILlmTool[] tools = { originalRead, originalCall };
            InMemoryAgentMemoryStore firstStore = new InMemoryAgentMemoryStore();
            InMemoryAgentMemoryStore secondStore = new InMemoryAgentMemoryStore();
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>();
            DiscoveryChatClient firstProvider = new DiscoveryChatClient("first fact", release.Task);
            DiscoveryChatClient secondProvider = new DiscoveryChatClient("second fact", release.Task);
            MeaiLlmClient first = new MeaiLlmClient(firstProvider, GameLoggerUnscopedFallback.Instance,
                QuietSettings(), true, firstStore);
            MeaiLlmClient second = new MeaiLlmClient(secondProvider, GameLoggerUnscopedFallback.Instance,
                QuietSettings(), true, secondStore);
            Task<LlmCompletionResult> firstTurn = first.CompleteAsync(Request("first", "actor-a", tools));
            Task<LlmCompletionResult> secondTurn = second.CompleteAsync(Request("second", "actor-b", tools));
            release.TrySetResult(true);
            LlmCompletionResult[] completed = await Task.WhenAll(firstTurn, secondTurn);
            Assert.IsTrue(completed.All(result => result.Ok));
            Assert.AreEqual(3, firstProvider.Calls);
            Assert.AreEqual(3, secondProvider.Calls);

            AssertBoundDiscovery(firstProvider.Discovery, firstStore, "first");
            AssertBoundDiscovery(secondProvider.Discovery, secondStore, "second");
            Assert.IsTrue(firstStore.TryLoad("first", out AgentMemoryState firstState));
            Assert.IsTrue(secondStore.TryLoad("second", out AgentMemoryState secondState));
            StringAssert.Contains("first fact", firstState.Memory);
            StringAssert.DoesNotContain("second fact", firstState.Memory);
            StringAssert.Contains("second fact", secondState.Memory);
            StringAssert.DoesNotContain("first fact", secondState.Memory);
            Assert.IsFalse(firstStore.TryLoad("second", out _));
            Assert.IsFalse(secondStore.TryLoad("first", out _));
            Assert.IsTrue(firstProvider.InvocationResult.Value<bool>("Success"), firstProvider.InvocationResult.ToString());
            Assert.IsTrue(secondProvider.InvocationResult.Value<bool>("Success"), secondProvider.InvocationResult.ToString());

            JObject raw = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(catalog, "notes"));
            Assert.IsFalse(raw["tools"].Single().Value<bool>("invocable"),
                "A request binding must not mutate the original unbound discovery catalog.");
            Assert.IsFalse(((IResolvedLlmToolCallProvider)originalCall).TryResolveInvocation(
                Call("memory", "{\"action\":\"read\"}"), out _, out _));
        }

        [Test]
        public async Task MEM002_DeniedTargetRemainsUnavailableForDiscoveryAndInvocation()
        {
            SkillSet[] catalog = { new SkillSet("notes", "Memory notes", "Use memory.", new MemoryLlmTool()) };
            ILlmTool read = ((ISkillSetMetaLlmTool)ReadSkillLlmTool.Create(catalog)).RestrictTo(new[] { "other" });
            ILlmTool call = ((ISkillSetMetaLlmTool)CallSkillToolLlmTool.Create(catalog)).RestrictTo(new[] { "other" });
            InMemoryAgentMemoryStore store = new InMemoryAgentMemoryStore();
            ILlmTool boundRead = CallSkillToolLlmTool.BindForRole(read, store, "denied");
            object payload = await ((IAIFunctionLlmTool)boundRead).CreateAIFunction().InvokeAsync(
                new MEAI.AIFunctionArguments(new Dictionary<string, object> { ["skill_name"] = "notes" }),
                CancellationToken.None);
            JObject discovery = DiscoveryChatClient.ParseFeedback(payload);
            Assert.IsFalse(discovery.Value<bool>("success"));
            Assert.IsFalse(store.TryLoad("denied", out _));
            ILlmTool boundCall = CallSkillToolLlmTool.BindForRole(call, store, "denied");
            Assert.IsFalse(((IResolvedLlmToolCallProvider)boundCall).TryResolveInvocation(
                Call("memory", "{\"action\":\"append\",\"content\":\"denied\"}"), out _, out _));
        }

        private static void AssertBoundDiscovery(JObject discovery, IAgentMemoryStore store, string role)
        {
            Assert.IsNotNull(discovery, "The real read_skill roundtrip must return a payload.");
            Assert.IsTrue(discovery.Value<bool>("success"));
            JToken memory = discovery["tools"].Single(tool => tool.Value<string>("tool_name") == "memory");
            Assert.IsTrue(memory.Value<bool>("invocable"),
                "The discovery result must describe the memory target that the same request can execute.");
            JToken expected = JToken.Parse(new MemoryTool(store, role).CreateAIFunction().JsonSchema.GetRawText());
            Assert.IsTrue(JToken.DeepEquals(expected, memory["parameters_schema"]),
                "The admitted callable schema must be the schema taught by read_skill.");
        }

        private static CoreAISettingsOptions QuietSettings() => new CoreAISettingsOptions
        {
            LogToolCalls = false, LogToolCallArguments = false, LogToolCallResults = false,
            LogMeaiToolCallingSteps = false, EnableMeaiDebugLogging = false,
            DefaultToolTimeoutMs = 0, MaxToolCallRetries = 0
        };

        private static LlmCompletionRequest Request(string role, string actor, IReadOnlyList<ILlmTool> tools) =>
            new LlmCompletionRequest { AgentRoleId = role, ActorId = actor, TraceId = "discovery-" + actor,
                UserPayload = "Read the notes skill and append one fact.", Tools = tools };

        private static IDictionary<string, object> Call(string name, string json) =>
            new Dictionary<string, object> { ["tool_name"] = name, ["arguments_json"] = json };

        private static async Task<Exception> Capture(Func<Task> action)
        {
            try { await action(); return null; }
            catch (Exception exception) { return exception; }
        }

        private sealed class CountingBindingTool : LlmToolBase, IAIFunctionLlmTool
        {
            internal int BindingAttempts;
            internal int Invocations;
            public override string Name => "unused_factory";
            public override string Description => "A binding that must remain untouched in disabled mode.";
            public override string ParametersSchema => "{}";
            public MEAI.AIFunction CreateAIFunction()
            {
                BindingAttempts++;
                return MEAI.AIFunctionFactory.Create((Func<string>)Invoke,
                    new MEAI.AIFunctionFactoryOptions { Name = Name });
            }
            private string Invoke() { Invocations++; return "executed"; }
        }

        private sealed class EchoChatClient : MEAI.IChatClient
        {
            internal int Calls;
            internal bool ToolsWereAbsent = true;
            private void Observe(MEAI.ChatOptions options)
            {
                Calls++;
                ToolsWereAbsent &= options?.Tools == null || options.Tools.Count == 0;
            }
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Observe(options);
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "tool-free answer")));
            }
            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Observe(options);
                await Task.CompletedTask;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "tool-free answer");
            }
            public object GetService(Type type, object key = null) => null;
            public void Dispose() { }
        }

        private sealed class DiscoveryChatClient : MEAI.IChatClient
        {
            private readonly string _fact;
            private readonly Task _start;
            internal int Calls;
            internal JObject Discovery;
            internal JObject InvocationResult;
            internal DiscoveryChatClient(string fact, Task start) { _fact = fact; _start = start; }
            public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (Calls == 1)
                {
                    await _start;
                    cancellationToken.ThrowIfCancellationRequested();
                    return ToolCall("read-call", "read_skill", new Dictionary<string, object> { ["skill_name"] = "notes" });
                }
                MEAI.FunctionResultContent feedback = messages.SelectMany(message => message.Contents)
                    .OfType<MEAI.FunctionResultContent>().Last();
                if (Calls == 2)
                {
                    Discovery = ParseFeedback(feedback.Result);
                    return ToolCall("memory-call", "call_skill_tool", Call("memory",
                        new JObject { ["action"] = "append", ["content"] = _fact }.ToString()));
                }
                InvocationResult = ParseFeedback(feedback.Result);
                return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done"));
            }
            internal static JObject ParseFeedback(object value)
            {
                JToken parsed = JToken.Parse(value.ToString());
                return parsed.Type == JTokenType.String ? JObject.Parse(parsed.Value<string>()) : (JObject)parsed;
            }
            private static MEAI.ChatResponse ToolCall(string id, string name, IDictionary<string, object> arguments) =>
                new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                    new List<MEAI.AIContent> { new MEAI.FunctionCallContent(id, name, arguments) }));
            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("This fixture drives the real buffered tool loop.");
            public object GetService(Type type, object key = null) => null;
            public void Dispose() { }
        }
    }
}
#endif

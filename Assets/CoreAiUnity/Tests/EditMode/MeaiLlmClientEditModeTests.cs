using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
#if COREAI_LLM
    public sealed class MeaiLlmClientEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: <see cref="CompleteAsync_ProviderCancellation_PropagatesOperationCanceledException"/> below
        /// is a synchronous [Test] that blocks on Assert.CatchAsync; detaching the context sends the
        /// awaited delegate's continuation to the thread pool instead of back onto this same blocked
        /// thread, which would deadlock.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task NoneToolMode_NeverExecutesOrBuildsBindingsAcrossChannels(
            [Values(false, true)] bool streaming, [Values(false, true)] bool native,
            [Values(false, true)] bool explicitFallback, [Values(false, true)] bool unsolicitedNativeCall)
        {
            int invocations = 0;
            string prose = "Example: {\"name\":\"save\",\"arguments\":{}}";
            DelegateLlmTool tool = new("save", "save", (Func<string>)(() =>
            {
                invocations++;
                return "saved";
            }));
            ThrowingBindingTool invalid = new();
            CapturingChatClient inner = new() { ResponseText = prose,
                FunctionCallName = unsolicitedNativeCall ? tool.Name : null };
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(),
                supportsNativeToolCalling: native);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher", UserPayload = "Explain this JSON",
                Tools = new ILlmTool[] { tool, invalid }, ForcedToolMode = LlmToolChoiceMode.None,
                AllowTextShapedToolCallsOnNativeEndpoint = explicitFallback
            };
            List<string> text = new();
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                {
                    text.Add(chunk.Text ?? "");
                    Assert.IsTrue(chunk.ExecutedToolCalls == null || chunk.ExecutedToolCalls.Count == 0);
                }
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.IsTrue(result.Ok, result.Error);
                text.Add(result.Content);
                Assert.IsTrue(result.ExecutedToolCalls == null || result.ExecutedToolCalls.Count == 0);
            }
            Assert.AreEqual(0, invocations);
            Assert.AreEqual(0, invalid.BindingAttempts, "Disabled tools must not construct unused factories.");
            Assert.AreEqual(1, inner.Calls);
            Assert.AreEqual(prose, string.Concat(text));
        }

        [Test]
        public async Task NoneToolMode_EmptyStreamingReplyDoesNotStartRescueTurn()
        {
            CapturingChatClient inner = new() { ResponseText = "", FunctionCallName = "save" };
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(),
                supportsNativeToolCalling: true);
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
                { UserPayload = "hi", ForcedToolMode = LlmToolChoiceMode.None })) { }
            Assert.AreEqual(1, inner.Calls);
        }

        private sealed class ThrowingBindingTool : ILlmTool, IAIFunctionLlmTool
        {
            public string Name => "invalid";
            public string Description => "Unused factory must remain unused.";
            public bool AllowDuplicates => false;
            public string ParametersSchema => "{}";
            public int BindingAttempts { get; private set; }
            public MEAI.AIFunction CreateAIFunction()
            {
                BindingAttempts++;
                throw new InvalidOperationException("Unused binding was constructed.");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeEndpoint_UnboundOptionalTool_PreservesEducationalJson(bool streaming)
        {
            string lesson = "Example: {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"lesson\"}}";
            CapturingChatClient inner = new() { ResponseText = lesson };
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(),
                supportsNativeToolCalling: true);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher", UserPayload = "Explain this JSON",
                Tools = new ILlmTool[] { new MemoryLlmTool() }
            };

            string visible;
            IReadOnlyList<LlmToolCallTrace> traces;
            if (streaming)
            {
                List<string> chunks = new();
                LlmStreamChunk terminal = null;
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                {
                    chunks.Add(chunk.Text ?? "");
                    if (chunk.IsDone) terminal = chunk;
                }
                Assert.IsNotNull(terminal);
                Assert.IsTrue(string.IsNullOrEmpty(terminal.Error));
                visible = string.Concat(chunks);
                traces = terminal.ExecutedToolCalls;
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.IsTrue(result.Ok, result.Error);
                visible = result.Content;
                traces = result.ExecutedToolCalls;
            }

            Assert.AreEqual(lesson, visible);
            Assert.IsTrue(traces == null || traces.Count == 0, "Teaching JSON must not become a tool invocation.");
            Assert.AreEqual(1, inner.Calls, "Missing optional bindings must not trigger a prose tool loop.");
        }

        [TestCase(false, LlmToolChoiceMode.RequireAny, false)]
        [TestCase(true, LlmToolChoiceMode.RequireAny, false)]
        [TestCase(false, LlmToolChoiceMode.RequireSpecific, true)]
        [TestCase(true, LlmToolChoiceMode.RequireSpecific, true)]
        public async Task RequiredToolWithoutBinding_FailsBeforeProvider(
            bool streaming, LlmToolChoiceMode mode, bool anotherToolBound)
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(),
                supportsNativeToolCalling: true);
            List<ILlmTool> tools = new() { new LegacyDuckTypedFunctionTool("unbound") };
            if (anotherToolBound) tools.Add(new ExplicitFunctionTool("available"));
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher", UserPayload = "Run required tool", Tools = tools,
                ForcedToolMode = mode, RequiredToolName = "unbound"
            };

            LlmErrorCode errorCode = LlmErrorCode.None;
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                {
                    Assert.IsTrue(chunk.IsDone);
                    Assert.IsFalse(string.IsNullOrEmpty(chunk.Error));
                    errorCode = chunk.ErrorCode;
                }
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.IsFalse(result.Ok);
                Assert.IsFalse(string.IsNullOrEmpty(result.Error));
                errorCode = result.ErrorCode;
            }

            Assert.AreEqual(LlmErrorCode.InvalidRequest, errorCode);
            Assert.AreEqual(0, inner.Calls, "A required unavailable tool is a local configuration failure.");
        }

        [Test]
        public void CreateHttp_WithOpenAiSettings_ShouldNotThrow()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(true, "http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings,
                ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                logger,
                supportsNativeToolCalling: true);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CreateHttp_CoreSettingsProviderParameters_ReachRequestBody()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            string body = null;
            settings.ConfigureHttpApi("http://127.0.0.1:9/v1", "", "model");
            settings.SetProviderBodyParameter("top_k", new Newtonsoft.Json.Linq.JValue(37));
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () =>
                new System.Net.Http.HttpClient(new RequestCaptureHandler(value => body = value));
            try
            {
                MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, GameLoggerUnscopedFallback.Instance, supportsNativeToolCalling: true);
                LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest { UserPayload = "hi" });
                Assert.IsTrue(result.Ok, result.Error);
                Assert.AreEqual(37, (int)Newtonsoft.Json.Linq.JObject.Parse(body)["top_k"]);
            }
            finally
            {
                MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TextChannel_WithProviderOverrides_ExecutesToolWithoutNativeWireFields(bool streaming)
        {
            TextChannelHttpHandler handler = new();
            OpenAiHttpOptions settings = new()
            {
                ApiBaseUrl = "http://127.0.0.1:9/v1", Model = "test",
                ExtraBodyJson = "{\"tools\":[{\"type\":\"function\"}],\"tool_choice\":\"required\",\"parallel_tool_calls\":true}"
            };
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new System.Net.Http.HttpClient(handler);
            try
            {
                int executions = 0;
                DelegateLlmTool tool = new("save", "Save progress", (Func<string>)(() => { executions++; return "saved"; }));
                MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, new CoreAISettingsOptions(),
                    GameLoggerUnscopedFallback.Instance, supportsNativeToolCalling: false);
                LlmCompletionRequest request = new() { UserPayload = "save", Tools = new List<ILlmTool> { tool } };
                if (streaming)
                {
                    LlmStreamChunk terminal = null;
                    await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request)) terminal = chunk;
                    Assert.IsNotNull(terminal);
                    Assert.IsTrue(terminal.IsDone);
                    Assert.IsTrue(string.IsNullOrEmpty(terminal.Error), terminal.Error);
                    Assert.IsTrue(terminal.ExecutedToolCalls.Any(call => call.Name == "save" && call.Success));
                }
                else
                {
                    LlmCompletionResult result = await client.CompleteAsync(request);
                    Assert.IsTrue(result.Ok, result.Error);
                    Assert.IsTrue(result.ExecutedToolCalls.Any(call => call.Name == "save" && call.Success));
                }
                Assert.AreEqual(1, executions);
                Assert.AreEqual(2, handler.Bodies.Count, "The tool result must reach the follow-up completion.");
                foreach (Newtonsoft.Json.Linq.JObject body in handler.Bodies)
                {
                    Assert.IsNull(body["tools"]);
                    Assert.IsNull(body["tool_choice"]);
                    Assert.IsNull(body["parallel_tool_calls"]);
                }
            }
            finally { MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TextChannel_ArbitraryChatClient_ReceivesNoNativeOptionsButLocalToolStillRuns(bool streaming)
        {
            TextChannelChatClient provider = new();
            int executions = 0;
            DelegateLlmTool tool = new("save", "Save progress", (Func<string>)(() => { executions++; return "saved"; }));
            MeaiLlmClient client = new(provider, GameLoggerUnscopedFallback.Instance,
                new CoreAISettingsOptions(), supportsNativeToolCalling: false);
            LlmCompletionRequest request = new() { UserPayload = "save", Tools = new List<ILlmTool> { tool } };
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                    Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.IsTrue(result.Ok, result.Error);
            }
            Assert.AreEqual(1, executions);
            Assert.AreEqual(2, provider.Options.Count);
            foreach (MEAI.ChatOptions options in provider.Options)
            {
                Assert.IsNull(options.Tools);
                Assert.IsNull(options.ToolMode);
                Assert.IsNull(options.AllowMultipleToolCalls);
            }
        }

        /// <summary>
        /// The text-channel wrapper is the native <c>ConfigureOptionsChatClient</c>, so the endpoint
        /// gets a CLONE with the tool channel stripped - including the raw passthrough copies in
        /// <c>AdditionalProperties</c> - while the caller's own options object is left intact for the
        /// invocation layer above.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public async Task TextChannel_StripsRawToolPassthrough_WithoutTouchingCallerOptions(bool streaming)
        {
            OptionsCapturingChatClient provider = new();
            MEAI.ChatOptions callerOptions = new()
            {
                Tools = new List<MEAI.AITool>
                {
                    MEAI.AIFunctionFactory.Create((Func<string>)(() => "ok"),
                        new MEAI.AIFunctionFactoryOptions { Name = "save" })
                },
                ToolMode = MEAI.ChatToolMode.Auto,
                AllowMultipleToolCalls = true,
                AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                {
                    ["tools"] = "raw", ["tool_choice"] = "required", ["parallel_tool_calls"] = true,
                    ["temperature_override"] = 0.3f
                }
            };
            MEAI.IChatClient wrapped = MeaiLlmClient.StripNativeToolChannel(provider);
            if (streaming)
            {
                await foreach (MEAI.ChatResponseUpdate _ in wrapped.GetStreamingResponseAsync(
                    Array.Empty<MEAI.ChatMessage>(), callerOptions)) { }
            }
            else
            {
                await wrapped.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), callerOptions);
            }

            MEAI.ChatOptions seen = provider.Options.Single();
            Assert.AreNotSame(callerOptions, seen);
            Assert.IsNull(seen.Tools);
            Assert.IsNull(seen.ToolMode);
            Assert.IsNull(seen.AllowMultipleToolCalls);
            Assert.IsFalse(seen.AdditionalProperties.ContainsKey("tools"));
            Assert.IsFalse(seen.AdditionalProperties.ContainsKey("tool_choice"));
            Assert.IsFalse(seen.AdditionalProperties.ContainsKey("parallel_tool_calls"));
            Assert.IsTrue(seen.AdditionalProperties.ContainsKey("temperature_override"));

            Assert.AreEqual(1, callerOptions.Tools.Count, "The caller's options must survive untouched.");
            Assert.IsNotNull(callerOptions.ToolMode);
            Assert.IsTrue(callerOptions.AdditionalProperties.ContainsKey("tools"));
        }

        private sealed class OptionsCapturingChatClient : MEAI.IChatClient
        {
            public List<MEAI.ChatOptions> Options { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Options.Add(options);
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Options.Add(options);
                await Task.CompletedTask;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok");
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class TextChannelChatClient : MEAI.IChatClient
        {
            public List<MEAI.ChatOptions> Options { get; } = new();
            private string Next(MEAI.ChatOptions options)
            {
                Options.Add(options);
                return Options.Count == 1 ? "{\"name\":\"save\",\"arguments\":{}}" : "done";
            }
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            { return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, Next(options)))); }
            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, Next(options));
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class TextChannelHttpHandler : System.Net.Http.HttpMessageHandler
        {
            public List<Newtonsoft.Json.Linq.JObject> Bodies { get; } = new();
            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Newtonsoft.Json.Linq.JObject body = Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync());
                Bodies.Add(body);
                string content = Bodies.Count == 1 ? "{\"name\":\"save\",\"arguments\":{}}" : "done";
                bool streaming = body["stream"]?.Value<bool>() == true;
                Newtonsoft.Json.Linq.JObject message = new() { ["role"] = "assistant", ["content"] = content };
                Newtonsoft.Json.Linq.JObject choice = new()
                {
                    [streaming ? "delta" : "message"] = message, ["finish_reason"] = "stop"
                };
                string json = new Newtonsoft.Json.Linq.JObject { ["choices"] = new Newtonsoft.Json.Linq.JArray(choice) }.ToString();
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(streaming ? "data: " + json.Replace("\r", "").Replace("\n", "") +
                        "\n\ndata: [DONE]\n\n" : json, System.Text.Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
                };
            }
        }

        private sealed class RequestCaptureHandler : System.Net.Http.HttpMessageHandler
        {
            private readonly Action<string> _capture;
            public RequestCaptureHandler(Action<string> capture) { _capture = capture; }
            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _capture(await request.Content.ReadAsStringAsync());
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}")
                };
            }
        }

        [Test]
        public void CreateHttp_WithCoreAiSettings_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            OpenAiChatLlmClient client = new(settings, supportsNativeToolCalling: true);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CompleteAsync_ProviderCancellation_PropagatesOperationCanceledException()
        {
            MeaiLlmClient client = new(new CancellingChatClient(),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await client.CompleteAsync(new LlmCompletionRequest
                {
                    AgentRoleId = "Role",
                    SystemPrompt = "sys",
                    UserPayload = "hi"
                }, cancellation.Token));

            cancellation.Dispose();
        }

        [Test]
        public void BuildAIFunctions_ShouldCreateMemoryTool()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(true, "http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings,
                ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                logger,
                supportsNativeToolCalling: true,
                memoryStore: memoryStore);

            List<ILlmTool> tools = new() { new MemoryLlmTool() };
            client.SetTools(tools);

            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_BindsExplicitAIFunctionToolContract()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("explicit_tool") }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastOptions);
            Assert.IsNotNull(inner.LastOptions.Tools);
            Assert.That(inner.LastOptions.Tools.Select(t => t.Name), Does.Contain("explicit_tool"));
        }

        [Test]
        public async Task CompleteAsync_RequireSpecific_MapsToRequiredModeAndNarrowsTools()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool>
                {
                    new ExplicitFunctionTool("memory"),
                    new ExplicitFunctionTool("execute_lua")
                },
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "memory"
            }, CancellationToken.None);

            // Local llama.cpp / LM Studio servers 400 on a specific-function tool_choice, so
            // RequireSpecific must map to "required" (RequireAny) with the tools narrowed to the single
            // forced tool - never a RequiredChatToolMode carrying a specific RequiredFunctionName.
            Assert.IsInstanceOf<MEAI.RequiredChatToolMode>(inner.LastOptions?.ToolMode,
                "RequireSpecific must serialize as tool_choice=required, not a specific function.");
            Assert.IsNull(((MEAI.RequiredChatToolMode)inner.LastOptions.ToolMode).RequiredFunctionName,
                "A specific-function tool_choice is rejected (HTTP 400) by local OpenAI-compatible servers.");
            Assert.IsNotNull(inner.LastOptions.Tools);
            Assert.AreEqual(1, inner.LastOptions.Tools.Count,
                "The tools list must be narrowed to just the forced tool so 'required' forces exactly it.");
            Assert.AreEqual("memory", inner.LastOptions.Tools[0].Name);
        }

        [Test]
        public async Task CompleteAsync_NativeTools_AreCanonicalOrdinalByName()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool>
                {
                    new ExplicitFunctionTool("z_tool"),
                    new ExplicitFunctionTool("a_tool")
                }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastOptions?.Tools);
            CollectionAssert.AreEqual(
                new[] { "a_tool", "z_tool" },
                inner.LastOptions.Tools.Select(t => t.Name).ToArray());
        }

        [Test]
        public async Task CompleteAsync_DoesNotBindLegacyDuckTypedCreateAIFunctionTool()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new LegacyDuckTypedFunctionTool("legacy_tool") }
            }, CancellationToken.None);

            Assert.IsTrue(inner.LastOptions == null ||
                          inner.LastOptions.Tools == null ||
                          inner.LastOptions.Tools.All(t => t.Name != "legacy_tool"),
                "Tools must opt into IAIFunctionLlmTool/IAIFunctionsLlmTool; CreateAIFunction duck typing should not bind.");
        }

        [Test]
        public async Task CompleteAsync_ReusesIdempotencyKey_OnSameRequestInstance()
        {
            HelloOnceChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi"
            };

            await client.CompleteAsync(request, CancellationToken.None);
            string firstKey = request.IdempotencyKey;
            Assert.IsNotEmpty(firstKey);

            await client.CompleteAsync(request, CancellationToken.None);
            Assert.AreEqual(firstKey, request.IdempotencyKey);
        }

        [Test]
        public async Task CompleteAsync_KeepsCallerProvidedIdempotencyKey()
        {
            HelloOnceChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);
            const string preset = "deadbeefcafebabe1122334455667788";
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                IdempotencyKey = preset
            };

            await client.CompleteAsync(request, CancellationToken.None);
            Assert.AreEqual(preset, request.IdempotencyKey);
        }

        [Test]
        public async Task CompleteAsync_NormalizesTailSystemMessages_ForProviderCompatibility()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "root system",
                UserPayload = "current user",
                ChatHistory = new List<MEAI.ChatMessage>
                {
                    new(MEAI.ChatRole.User, "previous user"),
                    new(MEAI.ChatRole.System, "## Memory\nstable facts"),
                    new(MEAI.ChatRole.Assistant, "previous assistant")
                }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastMessages);
            Assert.AreEqual(MEAI.ChatRole.System, inner.LastMessages[0].Role);
            Assert.AreEqual("root system", inner.LastMessages[0].Text);
            Assert.IsFalse(inner.LastMessages.Skip(1).Any(m => m.Role == MEAI.ChatRole.System),
                "OpenAI-compatible chat templates may reject system messages outside the first position.");
            Assert.AreEqual(MEAI.ChatRole.User, inner.LastMessages[2].Role);
            StringAssert.Contains("System context update:", inner.LastMessages[2].Text);
            StringAssert.Contains("## Memory", inner.LastMessages[2].Text);
            Assert.AreEqual("current user", inner.LastMessages[^1].Text);
        }

        [Test]
        public async Task CompleteStreamingAsync_NormalizesTailSystemMessages_ForProviderCompatibility()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "root system",
                               UserPayload = "current user",
                               ChatHistory = new List<MEAI.ChatMessage>
                               {
                                   new(MEAI.ChatRole.System, "## World State\nnear shop")
                               }
                           }, CancellationToken.None))
            {
            }

            Assert.IsNotNull(inner.LastMessages);
            Assert.AreEqual(MEAI.ChatRole.System, inner.LastMessages[0].Role);
            Assert.IsFalse(inner.LastMessages.Skip(1).Any(m => m.Role == MEAI.ChatRole.System),
                "Streaming must keep the same provider-safe message contract as CompleteAsync.");
            Assert.AreEqual(MEAI.ChatRole.User, inner.LastMessages[1].Role);
            StringAssert.Contains("## World State", inner.LastMessages[1].Text);
            Assert.AreEqual("current user", inner.LastMessages[^1].Text);
        }

        [Test]
        public async Task CompleteAndStreaming_MapSharedPrefixAndPersonalTailIdenticallyOffline()
        {
            LlmCompletionRequest studentA = BuildCacheLayeredTransportRequest(
                "Student A",
                "tool_alpha",
                "world-a");
            LlmCompletionRequest studentB = BuildCacheLayeredTransportRequest(
                "Student B",
                "tool_beta",
                "world-b");

            (List<MEAI.ChatMessage> aSync, string aSyncOutput) = await MapNonStreamingAsync(studentA);
            (List<MEAI.ChatMessage> aStream, string aStreamOutput) = await MapStreamingAsync(studentA);
            (List<MEAI.ChatMessage> bSync, string bSyncOutput) = await MapNonStreamingAsync(studentB);
            (List<MEAI.ChatMessage> bStream, string bStreamOutput) = await MapStreamingAsync(studentB);

            CollectionAssert.AreEqual(MessageSignatures(aSync), MessageSignatures(aStream));
            CollectionAssert.AreEqual(MessageSignatures(bSync), MessageSignatures(bStream));
            Assert.AreEqual("ok", aSyncOutput);
            Assert.AreEqual("ok", aStreamOutput);
            Assert.AreEqual("ok", bSyncOutput);
            Assert.AreEqual("ok", bStreamOutput);

            Assert.AreEqual(MEAI.ChatRole.System, aSync[0].Role);
            Assert.AreEqual(MEAI.ChatRole.System, bSync[0].Role);
            Assert.AreEqual(aSync[0].Text, bSync[0].Text,
                "The first provider system message is shared by stable role/prompt version, not by student.");
            Assert.AreEqual("shared role prefix", aSync[0].Text);
            StringAssert.DoesNotContain("Student A", aSync[0].Text);
            StringAssert.DoesNotContain("Student B", bSync[0].Text);
            Assert.IsFalse(aSync.Skip(1).Any(message => message.Role == MEAI.ChatRole.System));
            Assert.IsFalse(bSync.Skip(1).Any(message => message.Role == MEAI.ChatRole.System));

            string aTail = string.Join("\n", aSync.Skip(1).Select(message => message.Text));
            string bTail = string.Join("\n", bSync.Skip(1).Select(message => message.Text));
            StringAssert.Contains("Student A", aTail);
            StringAssert.Contains("tool_alpha", aTail);
            StringAssert.Contains("world-a", aTail);
            StringAssert.Contains("Student B", bTail);
            StringAssert.Contains("tool_beta", bTail);
            StringAssert.Contains("world-b", bTail);
            Assert.AreNotEqual(aTail, bTail);
            StringAssert.StartsWith("System context update:", aSync[1].Text);
            StringAssert.StartsWith("System context update:", bSync[1].Text);
            Assert.AreEqual("current user for Student A", aSync[^1].Text);
            Assert.AreEqual("current user for Student B", bSync[^1].Text);
            Assert.AreEqual(MEAI.ChatRole.User, aSync[^1].Role);
            Assert.AreEqual(MEAI.ChatRole.User, bSync[^1].Role);
        }

        [Test]
        public void ExtractCacheTokenCounts_MatchesProviderKeyVariants()
        {
            (int nullRead, int nullWrite) = MeaiLlmClient.ExtractCacheTokenCounts(null);
            Assert.AreEqual(0, nullRead);
            Assert.AreEqual(0, nullWrite);

            MEAI.AdditionalPropertiesDictionary<long> counts = new()
            {
                ["cache_read_input_tokens"] = 11,
                ["CachedTokens"] = 7,
                ["cache_creation_input_tokens"] = 13,
                ["cache_create_tokens"] = 3,
                ["cache_write_tokens"] = 5,
                ["input_tokens"] = 999,
                ["prompt_cache_miss_tokens"] = 17
            };

            (int cacheRead, int cacheWrite) = MeaiLlmClient.ExtractCacheTokenCounts(counts);

            Assert.AreEqual(18, cacheRead);
            Assert.AreEqual(21, cacheWrite);
        }

        [Test]
        public async Task CompleteAsync_MapsAdditionalCountsCacheTokens()
        {
            UsageChatClient inner = new(
                100,
                12,
                112,
                80,
                20);
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi"
            }, CancellationToken.None);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(100, result.PromptTokens);
            Assert.AreEqual(12, result.CompletionTokens);
            Assert.AreEqual(112, result.TotalTokens);
            Assert.AreEqual(80, result.CacheReadTokens);
            Assert.AreEqual(20, result.CacheWriteTokens);
        }

        /// <summary>
        /// Cache counters have ONE carrier: <c>AdditionalCounts</c>. The OpenAI wire alias
        /// <c>prompt_tokens_details.cached_tokens</c> is therefore counted exactly once as a read, and
        /// a vendor <c>cache_creation_*</c> key exactly once as a write. There is no second typed
        /// carrier to double-count against or take precedence over (the typed
        /// <c>UsageDetails.CachedInputTokenCount</c> arrives only in Microsoft.Extensions.AI 10.x,
        /// above the consumer's 9.10.2 floor, and has no counterpart for cache WRITES in any version).
        /// </summary>
        [Test]
        public async Task CompleteAsync_CacheCountersComeFromAdditionalCountsExactlyOnce()
        {
            MEAI.ChatResponse response = ScriptedUsageChatClient.TextResponse("answer", 100);
            response.Usage.AdditionalCounts = new MEAI.AdditionalPropertiesDictionary<long>
            {
                ["prompt_tokens_details.cached_tokens"] = 17,
                ["cache_creation_input_tokens"] = 9
            };
            MeaiLlmClient client = new(new ScriptedUsageChatClient(response),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest { UserPayload = "hi" });
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(17, result.CacheReadTokens);
            Assert.AreEqual(9, result.CacheWriteTokens);
        }

        [Test]
        public async Task CompleteAsync_MultipleMessages_OnlyFinalAssistantTextIsVisible()
        {
            MEAI.ChatResponse response = new(new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.Assistant, "intermediate plan"),
                new(MEAI.ChatRole.Tool, "internal tool result"),
                new(MEAI.ChatRole.Assistant, "final answer")
            });
            MeaiLlmClient client = new(new ScriptedUsageChatClient(response),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest { UserPayload = "hi" });
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("final answer", result.Content);
        }

        /// <summary>
        /// The visible reply of one message is the native <c>ChatMessage.Text</c>: every
        /// <c>TextContent</c> of that message concatenated, with tool calls and reasoning left out.
        /// Guards the hand-rolled concatenation that used to sit here from coming back.
        /// </summary>
        [Test]
        public async Task CompleteAsync_FinalAssistantMessage_ConcatenatesTextPartsAndDropsNonText()
        {
            MEAI.ChatResponse response = new(new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.TextContent("visible one. "),
                    new MEAI.TextReasoningContent("private chain of thought"),
                    new MEAI.FunctionCallContent("call-1", "lookup", new Dictionary<string, object>()),
                    new MEAI.TextContent("visible two.")
                })
            });
            MeaiLlmClient client = new(new ScriptedUsageChatClient(response),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest { UserPayload = "hi" });
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("visible one. visible two.", result.Content);
            Assert.AreEqual("private chain of thought", result.ReasoningContent);
        }

        /// <summary>
        /// Streaming counterpart: a single update carrying several <c>TextContent</c> parts yields all
        /// of them, not just the first, because the text comes from the native
        /// <c>ChatResponseUpdate.Text</c>. Reasoning content is not <c>TextContent</c> and stays out of
        /// the visible stream.
        /// </summary>
        [Test]
        public async Task CompleteStreamingAsync_UpdateWithSeveralTextParts_EmitsAllOfThem()
        {
            MultiPartStreamingChatClient provider = new();
            MeaiLlmClient client = new(provider, GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);
            System.Text.StringBuilder visible = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(
                new LlmCompletionRequest { UserPayload = "hi" }))
            {
                Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
                visible.Append(chunk.Text ?? "");
            }

            Assert.AreEqual("alpha beta", visible.ToString());
        }

        private sealed class MultiPartStreamingChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "")
                {
                    Contents = new List<MEAI.AIContent>
                    {
                        new MEAI.TextContent("alpha "),
                        new MEAI.TextReasoningContent("private chain of thought"),
                        new MEAI.TextContent("beta")
                    }
                };
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Tool, "tool chatter never shown");
                await Task.CompletedTask;
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        [Test]
        public async Task CompleteAsync_SuccessfulTurnEndingTool_DoesNotRequireVisibleText()
        {
            MEAI.ChatResponse response = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent("finish-1", "finish", new Dictionary<string, object>()) }));
            MeaiLlmClient client = new(new ScriptedUsageChatClient(response),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                UserPayload = "finish",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("finish", endsTurn: true) }
            });
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsEmpty(result.Content);
            Assert.AreEqual(1, result.ExecutedToolCalls.Count);
            Assert.IsTrue(result.ExecutedToolCalls[0].Success);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OverlappingRequests_KeepToolNotificationsInTheirOwnRole(bool streaming)
        {
            TaskCompletionSource<bool> firstBindingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseFirstBinding = new(false);
            System.Collections.Concurrent.ConcurrentDictionary<string, string> notifiedRoles = new();
            CoreAi.ToolExecutedHandler handler = (role, tool, arguments, result) => notifiedRoles[tool] = role;
            CoreAi.OnToolExecuted += handler;
            MeaiLlmClient client = new(new RoleToolChatClient(),
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            LlmCompletionRequest firstRequest = new()
            {
                AgentRoleId = "first-role", UserPayload = "finish",
                Tools = new List<ILlmTool> { new BindingBarrierTool(firstBindingEntered, releaseFirstBinding) }
            };
            LlmCompletionRequest secondRequest = new()
            {
                AgentRoleId = "second-role", UserPayload = "finish",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("second_tool", endsTurn: true) }
            };

            async Task CompleteRequestAsync(LlmCompletionRequest request)
            {
                if (streaming)
                {
                    await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                    {
                        Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
                    }
                }
                else
                {
                    LlmCompletionResult result = await client.CompleteAsync(request);
                    Assert.IsTrue(result.Ok, result.Error);
                }
            }

            Task first = Task.Run(() => CompleteRequestAsync(firstRequest));
            try
            {
                Assert.AreSame(firstBindingEntered.Task, await Task.WhenAny(firstBindingEntered.Task, Task.Delay(10000)),
                    "The first request must pause at its binding boundary before starting the second.");
                await CompleteRequestAsync(secondRequest);
                releaseFirstBinding.Set();
                await first;
                Assert.AreEqual("first-role", notifiedRoles["first_tool"]);
                Assert.AreEqual("second-role", notifiedRoles["second_tool"]);
            }
            finally
            {
                releaseFirstBinding.Set();
                try { await first; }
                finally { CoreAi.OnToolExecuted -= handler; }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompleteStreamingAsync_TeachingJsonIsPreservedWhenUnknownOrQuoted(bool quoted)
        {
            string example = "{\"name\":\"" + (quoted ? "available_tool" : "unknown_tool") + "\",\"arguments\":{}}";
            string prefix = quoted ? "Example: `" : "Example: ";
            string suffix = quoted ? "` end" : " end";
            StreamingScriptedChatClient inner = new(new[] { prefix, example, suffix });
            MeaiLlmClient client = new(inner,
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: false,
                memoryStore: null);
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest
            {
                UserPayload = "explain",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("available_tool") }
            })) chunks.Add(chunk);

            Assert.AreEqual(prefix + example + suffix, string.Concat(chunks.Select(chunk => chunk.Text)));
            Assert.IsFalse(chunks.SelectMany(chunk => chunk.ExecutedToolCalls ?? Array.Empty<LlmToolCallTrace>()).Any());
            Assert.AreEqual(1, inner.StreamCalls);
        }

        [Test]
        public async Task CompleteStreamingAsync_MessageIdsSeparateVisibleMessagesAndHideInternalRoles()
        {
            IdentityStreamingChatClient inner = new();
            MeaiLlmClient client = new(inner,
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest { UserPayload = "hi" }))
            {
                if (!string.IsNullOrEmpty(chunk.Text)) chunks.Add(chunk);
            }

            Assert.AreEqual("first second", string.Concat(chunks.Select(chunk => chunk.Text)));
            Assert.AreEqual(1, chunks.Count(chunk => chunk.StartsNewMessage));
            Assert.AreEqual("second", chunks.Single(chunk => chunk.StartsNewMessage).Text);
        }

        [Test]
        public async Task CompleteAsync_EmptyTerminalResponseAfterToolUse_PreservesLastRoundtripPromptTokens()
        {
            ScriptedUsageChatClient inner = new(
                ScriptedUsageChatClient.TextResponse(
                    "{\"name\":\"explicit_tool\",\"arguments\":{}}", 50),
                ScriptedUsageChatClient.TextResponse("", null),
                ScriptedUsageChatClient.TextResponse("", null),
                ScriptedUsageChatClient.TextResponse("", null),
                ScriptedUsageChatClient.TextResponse("", null));
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: false, memoryStore: null);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("explicit_tool") }
            }, CancellationToken.None);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.EmptyResponse, result.ErrorCode);
            Assert.AreEqual(50, result.LastRoundtripPromptTokens,
                "Terminal paths without usage must still carry the last roundtrip's prompt size.");
        }

        [Test]
        public async Task CompleteStreamingAsync_NoTools_YieldsOneChunkPerInnerUpdateBeforeTerminal()
        {
            StreamingScriptedChatClient inner = new(new[] { "a", "bb", "ccc" });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "PlainChat",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = null
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            CollectionAssert.AreEqual(new[] { "a", "bb", "ccc" }, texts);
        }

        [Test]
        public async Task CompleteAsync_ReasoningContent_RemainsSeparateFromAnswer()
        {
            ReasoningChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "sys",
                UserPayload = "briefing"
            }, CancellationToken.None);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("final note", result.Content);
            Assert.AreEqual("draft reasoning", result.ReasoningContent);
        }

        [Test]
        public async Task CompleteStreamingAsync_ReasoningDelta_NeverJoinsConsumerText()
        {
            // WHY: in RedoSchool the streaming consumer stored the reasoning as a note; the public Text
            // must be assembled from content only, and reasoning stays a separate diagnostic channel.
            ReasoningChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: true, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "sys",
                UserPayload = "briefing"
            };
            List<LlmStreamChunk> chunks = new();

            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual("final note", string.Concat(chunks.Select(chunk => chunk.Text ?? "")));
            Assert.AreEqual("draft reasoning", string.Concat(chunks.Select(chunk => chunk.ReasoningText ?? "")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompleteStreamingAsync_SingleLargeInnerDelta_PreservesText(bool native)
        {
            const int len = 150;
            string blob = new('z', len);
            StreamingScriptedChatClient inner = new(new[] { blob });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: native, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "PlainChat",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = null
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            Assert.AreEqual(blob, string.Concat(texts));
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText()
        {
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Saved from stream\"}}"
                },
                new[] { "Quiz created successfully." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, supportsNativeToolCalling: false, memoryStore: memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Saved from stream"));
            Assert.That(full, Does.Contain("Quiz created successfully."));
            // Live streaming may surface the raw tool JSON in intermediate chunks before extraction finishes.
            Assert.GreaterOrEqual(inner.StreamCalls, 2, "Tool cycle should trigger second stream call.");
        }

        [Test]
        public async Task CompleteAsync_ToolExecutedThenFinalTextEmpty_StillReturnsExecutedToolCalls()
        {
            // Regression: MeaiLlmClient.CompleteAsync used to return early on an empty final assistant
            // response BEFORE copying functionClient.LastExecutedToolCalls into the result - so a turn
            // that successfully ran a tool but then trailed off into an empty response silently lost all
            // evidence that the tool ran (Ok=false, ExecutedToolCalls empty). Every terminal streaming
            // chunk and the success non-streaming path already carry these traces; the empty-response
            // non-streaming path must too.
            ScriptedNonStreamChatClient inner = new(
                new[]
                {
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Saved before going blank\"}}",
                    ""
                });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, supportsNativeToolCalling: false, memoryStore: memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmCompletionResult result = await client.CompleteAsync(request, CancellationToken.None);

            Assert.IsFalse(result.Ok, "Final empty assistant text should still surface as a failed turn.");
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Saved before going blank"),
                "The tool call must have actually executed despite the empty final response.");
            Assert.IsNotNull(result.ExecutedToolCalls);
            Assert.IsTrue(result.ExecutedToolCalls.Any(t => t.Name == "memory" && t.Success),
                "ExecutedToolCalls must carry the successful tool trace even when the wrapping turn errors.");
        }

        [Test]
        public async Task CompleteAsync_ToolExecutedThenProviderThrows_ReturnsFailureWithExecutedToolCalls()
        {
            ToolThenThrowChatClient inner = new();
            MeaiLlmClient client = new(inner,
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: false,
                memoryStore: new StatefulMemoryStore());

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "sys",
                UserPayload = "save",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
            Assert.IsTrue(result.ExecutedToolCalls.Any(t => t.Name == "memory" && t.Success));
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson()
        {
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "Working... {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Prefix persisted\"}}"
                },
                new[] { "Done." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, supportsNativeToolCalling: false, memoryStore: memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.That(full, Does.Contain("Working..."));
            Assert.That(full, Does.Contain("Done."));
            // Intermediate chunks may still include the tool JSON shape before the text-only pass completes.
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Prefix persisted"));
        }

        [Test]
        public async Task CompleteStreamingAsync_UnboundToolsRequested_ChunkedInner_YieldsPrefixThenStripsJson()
        {
            // Two prose deltas before the JSON so hybrid streaming yields two Text chunks; a single "Saved! "
            // delta would already be a full safe prefix and the final strip step dedupes to one chunk.
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "Saved", "! ", "{\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}}"
                });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), supportsNativeToolCalling: false, memoryStore: null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            Assert.GreaterOrEqual(texts.Count, 2,
                "Multiple inner prose deltas should surface as multiple streamed chunks before JSON.");
            string full = string.Concat(texts);
            Assert.AreEqual("Saved! ", full);
            Assert.That(full, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public async Task CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError()
        {
            string toolJson = "{\"name\":\"unavailable\",\"arguments\":{}}";
            StreamingScriptedChatClient inner = new(
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson });

            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, supportsNativeToolCalling: false);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool>
                {
                    new DelegateLlmTool("unavailable", "Unavailable resource",
                        (Func<string>)(() => "{\"Success\":false,\"Error\":\"Resource unavailable\"}"))
                },
                MaxToolCallRoundtrips = 2
            };

            LlmStreamChunk last = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                last = chunk;
            }

            Assert.IsNotNull(last);
            Assert.IsTrue(last.IsDone);
            Assert.IsFalse(string.IsNullOrWhiteSpace(last.Error), "An exhausted budget with no usable summary must report an error.");
            Assert.AreEqual(request.MaxToolCallRoundtrips.Value + 1, inner.StreamCalls,
                "Only the configured tool rounds and one tools-disabled summary may reach the provider.");
            Assert.AreNotEqual(LlmErrorCode.None, last.ErrorCode);
            Assert.IsTrue(last.ExecutedToolCalls.Any(call => call.Name == "unavailable" && !call.Success));
        }

        [Test]
        public async Task
            CompleteStreamingAsync_TooManySuccessfulToolIterations_WithVisibleText_CompletesWithoutUserError()
        {
            StreamingScriptedChatClient inner = new(
                new[] { "Saved. ", MemoryToolJson("append", "loop-1") },
                new[] { "Still saved. ", MemoryToolJson("append", "loop-2") },
                new[] { "Progress saved. ", MemoryToolJson("append", "loop-3") },
                new[] { "Summary saved. ", MemoryToolJson("append", "loop-4") },
                new[] { "Done saved. ", MemoryToolJson("append", "loop-5") },
                new[] { "Final saved. ", MemoryToolJson("append", "loop-6") });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, supportsNativeToolCalling: false, memoryStore: memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Save memory and summarize",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> texts = new();
            LlmStreamChunk last = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }

                last = chunk;
            }

            Assert.IsNotNull(last);
            Assert.IsTrue(last.IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(last.Error), $"Unexpected user-visible error: {last.Error}");
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Success));
            Assert.That(string.Concat(texts), Does.Contain("Saved."));
        }

        private static string MemoryToolJson(string action, string content)
        {
            return "{\"name\":\"memory\",\"arguments\":{\"action\":\"" + action +
                   "\",\"content\":\"" + content + "\"}}";
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState { Memory = "" };
                return true;
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

        private sealed class StatefulMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, AgentMemoryState> _states = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return _states.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                _states[roleId] = state;
            }

            public void Clear(string roleId)
            {
                _states.Remove(roleId);
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

        private sealed class StubCoreSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

        private sealed class ExplicitFunctionTool : ILlmTool, IAIFunctionLlmTool
        {
            public ExplicitFunctionTool(string name, bool endsTurn = false)
            {
                Name = name;
                EndsTurn = endsTurn;
            }

            public string Name { get; }
            public bool EndsTurn { get; }
            public string Description => "Explicit MEAI function test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => "{\"Success\":true}"),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }
        }

        private sealed class LegacyDuckTypedFunctionTool : ILlmTool
        {
            public LegacyDuckTypedFunctionTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "Legacy duck-typed MEAI function test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => "{\"Success\":true}"),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }
        }

        private static LlmCompletionRequest BuildCacheLayeredTransportRequest(
            string student,
            string availableTool,
            string worldState)
        {
            return new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "shared role prefix",
                UserPayload = "current user for " + student,
                ChatHistory = new List<MEAI.ChatMessage>
                {
                    new(MEAI.ChatRole.System, "## Request System Instructions\nGuidance for " + student),
                    new(MEAI.ChatRole.System, "## Memory\nFacts for " + student),
                    new(MEAI.ChatRole.System,
                        "## Tool Availability (current request)\nAvailable tools:\n- " + availableTool),
                    new(MEAI.ChatRole.System, "## World State\n" + worldState)
                }
            };
        }

        private static async Task<(List<MEAI.ChatMessage> Messages, string Output)> MapNonStreamingAsync(
            LlmCompletionRequest request)
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner,
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);

            LlmCompletionResult result = await client.CompleteAsync(request, CancellationToken.None);
            return (inner.LastMessages, result.Content);
        }

        private static async Task<(List<MEAI.ChatMessage> Messages, string Output)> MapStreamingAsync(
            LlmCompletionRequest request)
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner,
                GameLoggerUnscopedFallback.Instance,
                new StubCoreSettings(),
                supportsNativeToolCalling: true,
                memoryStore: null);
            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            return (inner.LastMessages, string.Concat(textChunks));
        }

        private static string[] MessageSignatures(IEnumerable<MEAI.ChatMessage> messages)
        {
            return messages.Select(message => message.Role + ":" + message.Text).ToArray();
        }

        private sealed class CapturingChatClient : MEAI.IChatClient
        {
            public string ResponseText { get; set; } = "ok";
            public string FunctionCallName { get; set; }
            public int Calls { get; private set; }
            public MEAI.ChatOptions LastOptions { get; private set; }
            public List<MEAI.ChatMessage> LastMessages { get; private set; }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Calls++;
                LastOptions = options;
                LastMessages = chatMessages.ToList();
                MEAI.ChatMessage message = new(MEAI.ChatRole.Assistant, ResponseText);
                if (FunctionCallName != null) message.Contents.Add(new MEAI.FunctionCallContent(
                    "unsolicited", FunctionCallName, new Dictionary<string, object>()));
                return Task.FromResult(new MEAI.ChatResponse(message));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Calls++;
                LastOptions = options;
                LastMessages = chatMessages.ToList();
                MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, ResponseText);
                if (FunctionCallName != null) update.Contents.Add(new MEAI.FunctionCallContent(
                    "unsolicited", FunctionCallName, new Dictionary<string, object>()));
                yield return update;
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ReasoningChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                List<MEAI.AIContent> contents = new()
                {
                    new MEAI.TextReasoningContent("draft reasoning"),
                    new MEAI.TextContent("final note")
                };
                return Task.FromResult(
                    new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, contents)));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                MEAI.ChatResponseUpdate reasoning = new(MEAI.ChatRole.Assistant, "")
                {
                    Contents = new List<MEAI.AIContent>
                    {
                        new MEAI.TextReasoningContent("draft reasoning")
                    }
                };
                yield return reasoning;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "final note");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Minimal MEAI client for non-streaming completion tests.</summary>
        private sealed class HelloOnceChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "x");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Scripted non-streaming client with per-response usage for terminal-path tests.</summary>
        private sealed class ScriptedUsageChatClient : MEAI.IChatClient
        {
            private readonly Queue<MEAI.ChatResponse> _responses;

            public ScriptedUsageChatClient(params MEAI.ChatResponse[] responses)
            {
                _responses = new Queue<MEAI.ChatResponse>(responses ?? Array.Empty<MEAI.ChatResponse>());
            }

            public static MEAI.ChatResponse TextResponse(string text, int? inputTokens)
            {
                MEAI.ChatResponse response =
                    new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text ?? string.Empty));
                if (inputTokens.HasValue)
                {
                    response.Usage = new MEAI.UsageDetails
                    {
                        InputTokenCount = inputTokens.Value,
                        OutputTokenCount = 5,
                        TotalTokenCount = inputTokens.Value + 5
                    };
                }

                return response;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("ScriptedUsageChatClient ran out of responses.");
                }

                return Task.FromResult(_responses.Dequeue());
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Minimal MEAI client that returns usage details with provider-specific cache counts.</summary>
        private sealed class UsageChatClient : MEAI.IChatClient
        {
            private readonly int _promptTokens;
            private readonly int _completionTokens;
            private readonly int _totalTokens;
            private readonly int _cacheReadTokens;
            private readonly int _cacheWriteTokens;

            public UsageChatClient(
                int promptTokens,
                int completionTokens,
                int totalTokens,
                int cacheReadTokens,
                int cacheWriteTokens)
            {
                _promptTokens = promptTokens;
                _completionTokens = completionTokens;
                _totalTokens = totalTokens;
                _cacheReadTokens = cacheReadTokens;
                _cacheWriteTokens = cacheWriteTokens;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                MEAI.ChatResponse response = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok"))
                {
                    Usage = new MEAI.UsageDetails
                    {
                        InputTokenCount = _promptTokens,
                        OutputTokenCount = _completionTokens,
                        TotalTokenCount = _totalTokens,
                        AdditionalCounts = new MEAI.AdditionalPropertiesDictionary<long>
                        {
                            ["cache_read_input_tokens"] = _cacheReadTokens,
                            ["cache_creation_input_tokens"] = _cacheWriteTokens
                        }
                    }
                };
                return Task.FromResult(response);
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "x");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Non-streaming scripted client: returns each queued text response in order, one per call.</summary>
        private sealed class ScriptedNonStreamChatClient : MEAI.IChatClient
        {
            private readonly Queue<string> _responses;

            public ScriptedNonStreamChatClient(params string[] responses)
            {
                _responses = new Queue<string>(responses ?? Array.Empty<string>());
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                string text = _responses.Count > 0 ? _responses.Dequeue() : "";
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text)));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class CancellingChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ToolThenThrowChatClient : MEAI.IChatClient
        {
            private int _calls;

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                _calls++;
                if (_calls == 1)
                {
                    string toolJson = MemoryToolJson("append", "saved-once");
                    return Task.FromResult(new MEAI.ChatResponse(
                        new MEAI.ChatMessage(MEAI.ChatRole.Assistant, toolJson)));
                }

                throw new LlmClientException(
                    "provider unavailable after tool",
                    LlmErrorCode.BackendUnavailable,
                    503);
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class BindingBarrierTool : ILlmTool, IAIFunctionLlmTool
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly ManualResetEventSlim _release;
            public BindingBarrierTool(TaskCompletionSource<bool> entered, ManualResetEventSlim release)
            {
                _entered = entered;
                _release = release;
            }
            public string Name => "first_tool";
            public string Description => "Completes the first request.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
            public bool EndsTurn => true;
            public MEAI.AIFunction CreateAIFunction()
            {
                _entered.TrySetResult(true);
                if (!_release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Binding barrier was not released.");
                return MEAI.AIFunctionFactory.Create((Func<string>)(() => "ok"), Name);
            }
        }

        private sealed class RoleToolChatClient : MEAI.IChatClient
        {
            private static MEAI.FunctionCallContent Call(MEAI.ChatOptions options)
            {
                string name = options.Tools.OfType<MEAI.AIFunction>().First().Name;
                return new MEAI.FunctionCallContent(name + "-call", name, new Dictionary<string, object>());
            }
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                    new List<MEAI.AIContent> { Call(options) })));
            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "")
                { Contents = new List<MEAI.AIContent> { Call(options) } };
                await Task.CompletedTask;
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class IdentityStreamingChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.System, "hidden system") { MessageId = "system" };
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "first ") { MessageId = "a" };
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Tool, "hidden tool") { MessageId = "tool" };
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "second") { MessageId = "b" };
                await Task.CompletedTask;
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class StreamingScriptedChatClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _streamScripts;
            public int StreamCalls { get; private set; }

            public StreamingScriptedChatClient(params string[][] streamScripts)
            {
                _streamScripts = new Queue<string[]>(streamScripts ?? Array.Empty<string[]>());
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                if (_streamScripts.Count == 0)
                {
                    yield break;
                }

                string[] chunks = _streamScripts.Dequeue();
                foreach (string chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Tests for the hardened TryExtractToolCallsFromText parser.
    /// Covers: multi-tool, code block false-positives, partial JSON, edge cases.
    /// </summary>
    [TestFixture]
    public sealed class TryExtractToolCallsFromTextTests
    {
        [Test]
        public void SingleToolCall_ExtractedCorrectly()
        {
            string text =
                "Here is the result: {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"hello\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("memory", calls[0].Name);
            Assert.That(cleaned, Does.Contain("Here is the result:"));
            Assert.That(cleaned, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void PseudoActionWrite_QwenStyle_ExtractedAsMemory()
        {
            string text =
                "Action=write content=\"Final exam is on June 15th.\" memory_type=\"text\" action=\"write\"";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("memory", calls[0].Name);
            Assert.AreEqual("write", calls[0].Arguments["action"]?.ToString());
            Assert.That(calls[0].Arguments["content"]?.ToString(),
                Does.Contain("June 15"));
            Assert.That(cleaned, Does.Not.Contain("Action=write"));
        }

        [Test]
        public void PseudoActionWrite_WithProsePrefix_StripsPseudoTailOnly()
        {
            string text = "Okay. Action=write content=\"hello\"";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.That(cleaned, Does.Contain("Okay."));
            Assert.That(cleaned, Does.Not.Contain("Action=write"));
        }

        [Test]
        public void MultipleToolCalls_AllExtracted()
        {
            string text =
                "{\"name\":\"tool_a\",\"arguments\":{\"x\":1}} some text {\"name\":\"tool_b\",\"arguments\":{\"y\":2}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("tool_a", calls[0].Name);
            Assert.AreEqual("tool_b", calls[1].Name);
            Assert.That(cleaned, Does.Contain("some text"));
        }

        [Test]
        public void JsonInCodeBlock_NotExtracted()
        {
            string text =
                "Here is an example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}\n```\nDone.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "JSON inside code blocks should be ignored");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void MalformedJson_GracefullySkipped()
        {
            string text = "Partial: {\"name\":\"tool\",\"arguments\":{\"broken";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "Unclosed JSON should not produce tool calls");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void JsonWithoutNameAndArguments_NotExtracted()
        {
            string text = "Config: {\"key\":\"value\",\"count\":42}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "Regular JSON without name+arguments keys should be ignored");
        }

        [Test]
        public void EmptyText_ReturnsFalse()
        {
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("", out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText(null, out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("   ", out _, out _));
        }

        [Test]
        public void NestedBracesInArguments_HandledCorrectly()
        {
            string text = "{\"name\":\"config\",\"arguments\":{\"data\":{\"nested\":true}}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("config", calls[0].Name);
        }

        [Test]
        public void StripCodeBlocks_PreservesPositions()
        {
            string text = "Before ```code``` After";
            string stripped = MeaiLlmClient.StripCodeBlocks(text);

            Assert.AreEqual(text.Length, stripped.Length, "Stripped text should have same length");
            Assert.That(stripped, Does.StartWith("Before "));
            Assert.That(stripped, Does.EndWith(" After"));
        }

        [Test]
        public void IsValidToolCallJson_RequiresBothKeys()
        {
            Assert.IsTrue(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\",\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\"}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson(""));
        }

        [Test]
        public void FindToolCallJsonSpans_MultipleSpans()
        {
            string text = "A {\"name\":\"a\",\"arguments\":{}} B {\"name\":\"b\",\"arguments\":{\"x\":1}}";
            List<MeaiLlmClient.JsonSpan> spans = MeaiLlmClient.FindToolCallJsonSpans(text);

            Assert.AreEqual(2, spans.Count);
        }

        [Test]
        public void ToolCallWithStringContainingBraces_HandledCorrectly()
        {
            string text = "{\"name\":\"tool\",\"arguments\":{\"code\":\"function() { return {}; }\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("tool", calls[0].Name);
        }

        [Test]
        public void ToolCallInMiddleOfLongText_PrefixAndSuffixPreserved()
        {
            // Real-world pattern: model writes text, then tool call JSON, then nothing
            string prefix = "I will save this to memory now. ";
            string json = "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"save me\"}}";
            string suffix = " Done processing.";
            string text = prefix + json + suffix;

            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.That(cleaned, Does.Contain("I will save this to memory now."));
            Assert.That(cleaned, Does.Contain("Done processing."));
            Assert.That(cleaned, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void CodeBlockFollowedByRealToolCall_OnlyRealCallExtracted()
        {
            // Model shows an example in code block, then makes a real call
            string text =
                "Here is an example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}\n```\n" +
                "Now I will actually call it:\n" +
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"real\"}}";

            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found, "Should extract the real call outside the code block");
            Assert.AreEqual(1, calls.Count, "Only the non-code-block call should be extracted");
            Assert.AreEqual("write", calls[0].Arguments?["action"]?.ToString());
        }

        [Test]
        public void ToolCallWithArrayArguments_ExtractedCorrectly()
        {
            string text = "{\"name\":\"batch_tool\",\"arguments\":{\"items\":[1,2,3],\"mode\":\"sync\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("batch_tool", calls[0].Name);
        }

        [Test]
        public void BacktickCitedSchemaExample_NotExtracted()
        {
            // Parity with the portable extractor: inline-code JSON is a citation, not a command.
            string text =
                "Use `{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}` to clear your memory.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string _);

            Assert.IsFalse(found, "Backtick-cited JSON must not execute.");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void QuoteWrappedSchemaExample_NotExtracted()
        {
            string text =
                "The docs show '{\"name\":\"memory\",\"arguments\":{\"action\":\"clear\"}}' as the format.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string _);

            Assert.IsFalse(found, "JSON wrapped in matching quotes is a citation, not a command.");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void PlaceholderToolName_NotExtracted()
        {
            string text = "Reply with {\"name\":\"<tool_name>\",\"arguments\":{}} to call a tool.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string _);

            Assert.IsFalse(found, "Placeholder tool names are schema examples, not commands.");
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"<tool_name>\",\"arguments\":{}}"),
                "IsValidToolCallJson must require the exact tool-call shape (identifier-like name).");
        }

        [Test]
        public void CitedExamplePlusRealCall_OnlyRealCallExtracted()
        {
            string text =
                "Example: `{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}` and now " +
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"real\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count, "Only the non-cited call should be extracted.");
            Assert.AreEqual("write", calls[0].Arguments?["action"]?.ToString());
            Assert.That(cleaned, Does.Contain("`"), "The cited example must stay in the cleaned text.");
        }

        [Test]
        public void CleanedText_IsTrimmable_NoLeadingTrailingJson()
        {
            // Tool call at very start of text — cleaned should not start with JSON
            string text =
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"x\"}} Here is my answer.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.That(cleaned.TrimStart(), Does.Not.StartWith("{\"name\""),
                "Cleaned text should not start with JSON tool call");
            Assert.That(cleaned, Does.Contain("Here is my answer."));
        }
    }

    /// <summary>
    /// Unit coverage for the Kilo/Cline-style on-the-fly hybrid hold helpers
    /// (<see cref="MeaiLlmClient.GetHybridSafeSegments"/> / <see cref="MeaiLlmClient.GetHybridUnemittedSuffix"/>),
    /// which keep prose streaming live before AND after a tool call instead of buffering the whole turn.
    /// </summary>
    public sealed class HybridSafeSegmentsTests
    {
        private const string ToolJson = "{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}";

        [Test]
        public void GetHybridSafeSegments_ProseToolProse_SplitsIntoThreeSegmentsAndStreamsTrailingProse()
        {
            string text = "P " + ToolJson + " Q";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(3, segments.Count, "Expected prose / tool-json / prose.");
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual("P ", text.Substring(segments[0].Start, segments[0].Length));
            Assert.IsTrue(segments[1].IsToolJson, "Middle span is the hidden tool-call JSON.");
            Assert.IsFalse(segments[2].IsToolJson, "Trailing prose must resume live after the tool call.");
            Assert.AreEqual(" Q", text.Substring(segments[2].Start, segments[2].Length));
            Assert.AreEqual(text.Length, safeEnd, "A fully-closed turn has no pending hold.");
        }

        [Test]
        public void GetHybridSafeSegments_IncompleteBraceAtEnd_HoldsFromOpenBrace()
        {
            string text = "Hi {\"name\":\"x\",\"argu";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(1, segments.Count);
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual("Hi ", text.Substring(segments[0].Start, segments[0].Length));
            Assert.AreEqual(text.IndexOf('{'), safeEnd, "Output is held from the first still-open brace.");
        }

        [Test]
        public void GetHybridSafeSegments_NonToolClosedObject_StreamsWholeTextAsProse()
        {
            string text = "Use { \"a\": 1 } now";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(1, segments.Count, "A non-tool {...} must not be hidden.");
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual(text, text.Substring(segments[0].Start, segments[0].Length));
            Assert.AreEqual(text.Length, safeEnd);
        }

        [Test]
        public void GetHybridSafeSegments_TwoToolCalls_PreservesProseBetweenThem()
        {
            string text = "A " + ToolJson + " B " + ToolJson + " C";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            int toolSpans = segments.Count(s => s.IsToolJson);
            Assert.AreEqual(2, toolSpans, "Both text-shaped tool calls are hidden.");

            string prose = string.Concat(segments
                .Where(s => !s.IsToolJson)
                .Select(s => text.Substring(s.Start, s.Length)));
            Assert.AreEqual("A  B  C", prose, "Prose between two tool calls must not be lost.");
            Assert.AreEqual(text.Length, safeEnd);
        }

        [Test]
        public void GetHybridSafeSegments_Empty_ReturnsNoSegments()
        {
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments("", out int safeEnd);
            Assert.IsEmpty(segments);
            Assert.AreEqual(0, safeEnd);
        }

        [Test]
        public void GetHybridUnemittedSuffix_StripsHeldToolJson_ReturnsTrailingProse()
        {
            string visible = "ok " + ToolJson + " bye";
            string? suffix = MeaiLlmClient.GetHybridUnemittedSuffix(visible, 3);

            Assert.IsNotNull(suffix);
            Assert.That(suffix, Does.Contain("bye"));
            Assert.That(suffix, Does.Not.Contain("\"name\""), "Held tool-call JSON must be stripped.");
        }

        [Test]
        public void GetHybridUnemittedSuffix_CursorAtOrPastEnd_ReturnsNull()
        {
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("abc", 3));
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("abc", 9));
        }

        [Test]
        public void GetHybridUnemittedSuffix_WhitespaceOnlyTail_ReturnsNull()
        {
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("ok    ", 2));
        }
    }
#endif
}

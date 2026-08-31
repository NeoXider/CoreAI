#if COREAI_LLM
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using Cysharp.Threading.Tasks;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Exercises reasoning isolation through the provider adapter, orchestration, chat consumer, and history store.
    /// </summary>
    public sealed class ReasoningIsolationPlayModeTests
    {
        private const string RoleId = "ReasoningIsolationTeacher";

        [UnityTest]
        public IEnumerator BufferedChat_ContentAndReasoning_ConsumerAndHistoryReceiveOnlyContent()
        {
            return UniTask.ToCoroutine(async () =>
            {
                const string responseJson =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"final answer\",\"reasoning_content\":\"private chain\"}}]}";
                using (TestContext context = CreateContext(false, responseJson, ""))
                {
                    StringBuilder displayed = new();
                    string result = await context.ChatService.SendMessageSmartAsync(
                        "question",
                        RoleId,
                        chunk => displayed.Append(chunk.Text ?? ""));

                    Assert.AreEqual("final answer", result);
                    Assert.AreEqual("final answer", displayed.ToString());
                    Assert.IsNotNull(context.Client.LastCompletion);
                    Assert.AreEqual("private chain", context.Client.LastCompletion.ReasoningContent);
                    Assert.AreEqual("final answer", context.MemoryStore.AssistantText);
                    Assert.AreEqual("final answer", context.CommandSink.LastPayload);
                }
            });
        }

        [UnityTest]
        public IEnumerator BufferedChat_ReasoningOnly_NeverBecomesAnswerOrAutomaticRecord()
        {
            return UniTask.ToCoroutine(async () =>
            {
                const string responseJson =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"private chain\"}}]}";
                using (TestContext context = CreateContext(false, responseJson, ""))
                {
                    StringBuilder displayed = new();
                    string result = await context.ChatService.SendMessageSmartAsync(
                        "question",
                        RoleId,
                        chunk => displayed.Append(chunk.Text ?? ""));

                    Assert.That(result, Does.Not.Contain("private chain"));
                    Assert.That(displayed.ToString(), Does.Not.Contain("private chain"));
                    Assert.IsNotNull(context.Client.LastCompletion);
                    Assert.IsFalse(context.Client.LastCompletion.Ok);
                    Assert.AreEqual(LlmErrorCode.EmptyResponse, context.Client.LastCompletion.ErrorCode);
                    Assert.AreEqual("private chain", context.Client.LastCompletion.ReasoningContent);
                    Assert.AreEqual("", context.MemoryStore.AssistantText);
                    Assert.AreEqual("", context.CommandSink.LastPayload);
                }
            });
        }

        [UnityTest]
        public IEnumerator StreamingChat_ReasoningDeltas_StayOutOfIncrementalVisibleText()
        {
            return UniTask.ToCoroutine(async () =>
            {
                const string sse =
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private \"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"content\":\"final \"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"chain\"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
                    "data: [DONE]\n\n";
                using (TestContext context = CreateContext(true, "", sse))
                {
                    StringBuilder displayed = new();
                    StringBuilder diagnostics = new();
                    string result = await context.ChatService.SendMessageSmartAsync(
                        "question",
                        RoleId,
                        chunk =>
                        {
                            displayed.Append(chunk.Text ?? "");
                            diagnostics.Append(chunk.ReasoningText ?? "");
                        });

                    Assert.AreEqual("final answer", result);
                    Assert.AreEqual("final answer", displayed.ToString());
                    Assert.AreEqual("private chain", diagnostics.ToString());
                    Assert.AreEqual("final answer", context.MemoryStore.AssistantText);
                    Assert.AreEqual("final answer", context.CommandSink.LastPayload);
                }
            });
        }

        [UnityTest]
        public IEnumerator StreamingChat_ReasoningOnly_KeepsDiagnosticsButNoVisibleOrStoredAnswer()
        {
            return UniTask.ToCoroutine(async () =>
            {
                const string sse =
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private \"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"chain\"}}]}\n\n" +
                    "data: [DONE]\n\n";
                using (TestContext context = CreateContext(true, "", sse))
                {
                    StringBuilder displayed = new();
                    StringBuilder diagnostics = new();
                    LlmErrorCode terminalError = LlmErrorCode.None;
                    string result = await context.ChatService.SendMessageSmartAsync(
                        "question",
                        RoleId,
                        chunk =>
                        {
                            displayed.Append(chunk.Text ?? "");
                            diagnostics.Append(chunk.ReasoningText ?? "");
                            if (chunk.IsDone)
                            {
                                terminalError = chunk.ErrorCode;
                            }
                        });

                    Assert.AreEqual("", result);
                    Assert.AreEqual("", displayed.ToString());

                    // WHY: exact-equality here would assert the number of attempts, not the contract.
                    // A reasoning-only stream never commits (IsCommittingChunk looks at Text and tool
                    // calls, not ReasoningText), so it terminates as EmptyResponse — a retryable code.
                    // The retry decorator therefore re-runs it, and this fake transport replays the
                    // identical SSE, so diagnostics legitimately carry the reasoning once per attempt.
                    // What must hold is that the reasoning reached the diagnostics channel at all and
                    // stayed out of every visible and persisted surface.
                    Assert.That(diagnostics.ToString(), Does.Contain("private chain"));
                    Assert.AreEqual(LlmErrorCode.EmptyResponse, terminalError);
                    Assert.AreEqual("", context.MemoryStore.AssistantText);
                    Assert.AreEqual("", context.CommandSink.LastPayload);
                }
            });
        }

        private static TestContext CreateContext(bool enableStreaming, string responseJson, string sse)
        {
            TestSettings settings = new(enableStreaming);
            FakeOpenAiTransport transport = new(responseJson, sse);
            MEAI.IChatClient providerClient = new MeaiOpenAiChatClient(settings, transport);
            MeaiLlmClient meaiClient = new(
                providerClient,
                GameLoggerUnscopedFallback.Instance,
                settings);
            CapturingLlmClient capturingClient = new(meaiClient);
            CapturingMemoryStore memoryStore = new();
            CapturingCommandSink commandSink = new();
            AgentMemoryPolicy memoryPolicy = new();
            memoryPolicy.ConfigureChatHistory(RoleId, true, 4096, true);
            memoryPolicy.DisableMemoryTool(RoleId);
            memoryPolicy.SetStreamingEnabled(RoleId, enableStreaming);
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            AiOrchestrator orchestrator = new(
                new SoloAuthorityHost(),
                capturingClient,
                commandSink,
                new SessionTelemetryCollector(),
                composer,
                memoryStore,
                memoryPolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                new LocalActorIdentityProvider("reasoning-isolation-test"));
            CoreAiChatService chatService = new(
                orchestrator,
                memoryPolicy,
                settings,
                memoryStore);

            return new TestContext(
                providerClient,
                chatService,
                capturingClient,
                memoryStore,
                commandSink);
        }

        private sealed class TestContext : IDisposable
        {
            private readonly MEAI.IChatClient _providerClient;

            public TestContext(
                MEAI.IChatClient providerClient,
                CoreAiChatService chatService,
                CapturingLlmClient client,
                CapturingMemoryStore memoryStore,
                CapturingCommandSink commandSink)
            {
                _providerClient = providerClient;
                ChatService = chatService;
                Client = client;
                MemoryStore = memoryStore;
                CommandSink = commandSink;
            }

            public CoreAiChatService ChatService { get; }
            public CapturingLlmClient Client { get; }
            public CapturingMemoryStore MemoryStore { get; }
            public CapturingCommandSink CommandSink { get; }

            public void Dispose()
            {
                _providerClient.Dispose();
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public LlmCompletionResult LastCompletion { get; private set; }

            public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return CaptureCompletionAsync(request, cancellationToken);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }

            private async Task<LlmCompletionResult> CaptureCompletionAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken)
            {
                LastCompletion = await _inner.CompleteAsync(request, cancellationToken);
                return LastCompletion;
            }
        }

        private sealed class FakeOpenAiTransport : IOpenAiHttpTransport
        {
            private readonly string _responseJson;
            private readonly string _sse;

            public FakeOpenAiTransport(string responseJson, string sse)
            {
                _responseJson = responseJson ?? "";
                _sse = sse ?? "";
            }

            public string DebugLabel => "ReasoningIsolationFake";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(
                OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = _responseJson,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(
                OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };
                MemoryStream stream = new(Encoding.UTF8.GetBytes(_sse), false);
                return Task.FromResult(result.WithRawStream(stream));
            }
        }

        private sealed class CapturingMemoryStore : IAgentMemoryStore
        {
            private readonly List<StoredMessage> _messages = new();

            public string AssistantText
            {
                get
                {
                    StringBuilder text = new();
                    for (int i = 0; i < _messages.Count; i++)
                    {
                        StoredMessage message = _messages[i];
                        if (string.Equals(message.Role, "assistant", StringComparison.Ordinal))
                        {
                            text.Append(message.Content);
                        }
                    }

                    return text.ToString();
                }
            }

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
                _messages.Clear();
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                _messages.Add(new StoredMessage(role, content));
            }

            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<CoreAI.Ai.ChatMessage>();
            }

            private sealed class StoredMessage
            {
                public StoredMessage(string role, string content)
                {
                    Role = role ?? "";
                    Content = content ?? "";
                }

                public string Role { get; }
                public string Content { get; }
            }
        }

        private sealed class CapturingCommandSink : IAiGameCommandSink
        {
            public string LastPayload { get; private set; } = "";

            public void Publish(ApplyAiGameCommand command)
            {
                LastPayload = command?.JsonPayload ?? "";
            }
        }

        private sealed class TestSettings : ICoreAISettings, IOpenAiHttpSettings
        {
            public TestSettings(bool enableStreaming)
            {
                EnableStreaming = enableStreaming;
            }

            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "reasoning-test";
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public IRequestHeaderProvider HeaderProvider => null;
            public string UniversalSystemPromptPrefix => "";
            public LlmBackendType BackendType => LlmBackendType.OpenAiHttp;
            public int ContextWindowTokens => 8192;
            public int MaxContextTokens => 4096;
            public int MaxLuaRepairRetries => 1;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string ModelName => Model;
            public string CustomBaseUrl => ApiBaseUrl;
            public float Temperature => 0f;
            public string DeveloperInstructions => "";
            public string ApplicationName => "";
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 0f;
            public int MaxLlmRequestRetries => 1;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; }
        }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Flagship product scenario: an agent switches to another API endpoint mid-conversation and
    /// keeps its history, while routing metadata (profile id, context window) follows the new
    /// endpoint. Uses the real registry + routing client + orchestrator, with fake endpoint clients.
    /// </summary>
    [TestFixture]
    public sealed class LlmEndpointSwitchFlagshipEditModeTests
    {
        private sealed class CapturingClient : ILlmClient
        {
            private readonly string _reply;

            public CapturingClient(string reply, bool nativeTools)
            {
                _reply = reply;
                SupportsNativeToolCalling = nativeTools;
            }

            public bool SupportsNativeToolCalling { get; }
            public List<LlmCompletionRequest> Requests { get; } = new();
            public LlmCompletionRequest LastRequest => Requests.Count > 0 ? Requests[^1] : null;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _reply });
            }
        }

        private sealed class PerEndpointFactory : ILlmEndpointClientFactory
        {
            private readonly Dictionary<string, ILlmClient> _clients;

            public PerEndpointFactory(Dictionary<string, ILlmClient> clients)
            {
                _clients = clients;
            }

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = _clients[descriptor.EndpointId],
                    Mode = LlmExecutionMode.ClientOwnedApi
                });
            }
        }

        private sealed class MemoryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState Load()
            {
                return new LlmEndpointRegistryState();
            }

            public void Save(LlmEndpointRegistryState state)
            {
            }
        }

        private sealed class HistoryMemoryStore : IAgentMemoryStore
        {
            private readonly List<ChatMessage> _history = new();

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
                _history.Clear();
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                _history.Add(new ChatMessage { Role = role, Content = content });
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return _history.ToArray();
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

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.7f;
            public int ContextWindowTokens => 32768;
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

        private sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private CoreAISettingsAsset _registrySettings;

        [SetUp]
        public void SetUp()
        {
            _registrySettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_registrySettings);
        }

        [Test]
        public async Task MidConversationEndpointSwitch_PreservesHistoryAndFollowsTheNewEndpoint()
        {
            CapturingClient endpointA = new("Understood: velvet.", false);
            CapturingClient endpointB = new("The code word is velvet.", true);
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance,
                _registrySettings,
                null,
                new MemoryStore(),
                new PerEndpointFactory(new Dictionary<string, ILlmClient>
                {
                    ["endpoint-a"] = endpointA,
                    ["endpoint-b"] = endpointB
                }));
            await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a", 128000));
            await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-b", 8192));
            registry.AssignRoleProfile(BuiltInAgentRoleIds.Programmer, "endpoint-a");

            RoutingLlmClient routing = new(registry);
            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            HistoryMemoryStore memory = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), routing, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Remember the code word: velvet.",
                SourceTag = "Chat"
            });
            Assert.AreEqual(1, endpointA.Requests.Count, "Turn 1 must reach endpoint A.");
            Assert.AreEqual(0, endpointB.Requests.Count);

            // The switch: same agent, same conversation, new endpoint.
            registry.AssignRoleProfile(BuiltInAgentRoleIds.Programmer, "endpoint-b");

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "What was the code word?",
                SourceTag = "Chat"
            });

            Assert.AreEqual(1, endpointA.Requests.Count, "Turn 2 must not reach the old endpoint.");
            Assert.AreEqual(1, endpointB.Requests.Count, "Turn 2 must reach the newly routed endpoint.");
            LlmCompletionRequest turn2 = endpointB.LastRequest;
            Assert.IsNotNull(turn2.ChatHistory, "History must survive the endpoint switch.");
            string history = string.Join("\n", turn2.ChatHistory.Select(m => m.Text ?? ""));
            StringAssert.Contains("velvet", history,
                "The new endpoint must see the conversation held on the old endpoint.");
            StringAssert.Contains("Understood: velvet.", history,
                "The old endpoint's reply must be part of the carried-over history.");
            Assert.AreEqual("endpoint-b", turn2.RoutingProfileId,
                "The request must be annotated with the effective new profile.");
            Assert.AreEqual(8192, turn2.ContextWindowTokens,
                "Routing metadata must report the new endpoint's context window.");
            Assert.IsTrue(routing.SupportsNativeToolCallingForRole(BuiltInAgentRoleIds.Programmer, ""),
                "Tool strategy must follow the new endpoint after the switch.");

            registry.Dispose();
        }

        private static LlmEndpointDescriptor Descriptor(string id, int contextWindowTokens)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "https://example.test/v1",
                Model = "test",
                Active = true,
                ContextWindowTokens = contextWindowTokens
            };
        }
    }
}

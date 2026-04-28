using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmExecutionModesEditModeTests
    {
        [Test]
        public async Task ClientLimitedDecorator_RejectsAfterRequestLimit()
        {
            CountingClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            LlmCompletionResult first = await limited.CompleteAsync(new LlmCompletionRequest { UserPayload = "one" });
            LlmCompletionResult second = await limited.CompleteAsync(new LlmCompletionRequest { UserPayload = "two" });

            Assert.IsTrue(first.Ok);
            Assert.IsFalse(second.Ok);
            StringAssert.Contains("request limit", second.Error);
            Assert.AreEqual(LlmErrorCode.QuotaExceeded, second.ErrorCode);
            Assert.AreEqual(1, inner.Calls);
        }

        [Test]
        public async Task ClientLimitedDecorator_RejectsPromptOverCharacterLimit()
        {
            CountingClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 0, 3);

            LlmCompletionResult result = await limited.CompleteAsync(new LlmCompletionRequest { UserPayload = "abcd" });

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("prompt character limit", result.Error);
            Assert.AreEqual(LlmErrorCode.QuotaExceeded, result.ErrorCode);
            Assert.AreEqual(0, inner.Calls);
        }

        [Test]
        public void RoutingManifest_CanKeepMultipleModesActive()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureOffline();
            LlmRoutingManifest manifest = ScriptableObject.CreateInstance<LlmRoutingManifest>();
            OpenAiHttpLlmSettings http = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            http.SetRuntimeConfiguration(
                true,
                "https://game.example.com/v1",
                "",
                "proxy-model",
                executionMode: LlmExecutionMode.ServerManagedApi);

            SetList(manifest, "profiles", new List<LlmBackendProfileEntry>
            {
                new()
                {
                    profileId = "offline",
                    kind = LlmBackendKind.Stub,
                    executionMode = LlmExecutionMode.Offline
                },
                new()
                {
                    profileId = "server",
                    kind = LlmBackendKind.ServerManagedApi,
                    executionMode = LlmExecutionMode.ServerManagedApi,
                    httpSettings = http
                }
            });
            SetList(manifest, "routes", new List<LlmRoleRouteEntry>
            {
                new() { rolePattern = "Analyzer", profileId = "offline" },
                new() { rolePattern = "PlayerChat", profileId = "server" }
            });

            LlmClientRegistry registry = new(GameLoggerUnscopedFallback.Instance, settings);
            registry.SetLegacyFallback(new StubLlmClient());
            registry.ApplyManifest(manifest);

            Assert.AreEqual("offline", registry.ResolveProfileIdForRole("Analyzer"));
            Assert.AreEqual(LlmExecutionMode.Offline, registry.ResolveExecutionModeForRole("Analyzer"));
            Assert.AreEqual("server", registry.ResolveProfileIdForRole("PlayerChat"));
            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, registry.ResolveExecutionModeForRole("PlayerChat"));

            Object.DestroyImmediate(http);
            Object.DestroyImmediate(manifest);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ServerManagedAuthorization_UsesDynamicHeader()
        {
            ServerManagedAuthorization.SetProvider(() => "Bearer runtime-token");

            Assert.AreEqual("Bearer runtime-token", ServerManagedAuthorization.GetAuthorizationHeader());

            ServerManagedAuthorization.ClearProvider();
            Assert.AreEqual("", ServerManagedAuthorization.GetAuthorizationHeader());
        }

        [Test]
        public void ScopedMemoryStore_IsolatesByScope()
        {
            InMemoryAgentMemoryStore inner = new();
            ScopedAgentMemoryStoreDecorator scopedA = new(inner, new FixedScopeProvider("user-a"));
            ScopedAgentMemoryStoreDecorator scopedB = new(inner, new FixedScopeProvider("user-b"));

            scopedA.AppendChatMessage("Teacher", "user", "hello-a");
            scopedB.AppendChatMessage("Teacher", "user", "hello-b");

            ChatMessage[] aHistory = scopedA.GetChatHistory("Teacher");
            ChatMessage[] bHistory = scopedB.GetChatHistory("Teacher");

            Assert.AreEqual("hello-a", aHistory[0].Content);
            Assert.AreEqual("hello-b", bHistory[0].Content);
        }

        private static void SetList<T>(LlmRoutingManifest manifest, string fieldName, List<T> value)
        {
            typeof(LlmRoutingManifest)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(manifest, value);
        }

        private sealed class CountingClient : ILlmClient
        {
            public int Calls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class FixedScopeProvider : IAgentMemoryScopeProvider
        {
            private readonly string _userId;

            public FixedScopeProvider(string userId)
            {
                _userId = userId;
            }

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("", _userId, "session-1", "topic-1");
            }
        }

        private sealed class InMemoryAgentMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, List<ChatMessage>> _history = new();
            private readonly Dictionary<string, AgentMemoryState> _memory = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return _memory.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                _memory[roleId] = state;
            }

            public void Clear(string roleId)
            {
                _memory.Remove(roleId);
                _history.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                if (!_history.TryGetValue(roleId, out List<ChatMessage> list))
                {
                    list = new List<ChatMessage>();
                    _history[roleId] = list;
                }

                list.Add(new ChatMessage(role, content));
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (!_history.TryGetValue(roleId, out List<ChatMessage> list))
                {
                    return null;
                }

                return list.ToArray();
            }
        }
    }
}

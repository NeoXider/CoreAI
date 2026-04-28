using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
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
        public void RouteResolver_ChoosesExactRoleBeforeWildcard()
        {
            LlmRouteTable table = new()
            {
                Profiles = new[]
                {
                    new LlmRouteProfile { ProfileId = "fallback", Mode = LlmExecutionMode.Offline },
                    new LlmRouteProfile { ProfileId = "teacher", Mode = LlmExecutionMode.ServerManagedApi }
                },
                Rules = new[]
                {
                    new LlmRouteRule { RolePattern = "*", ProfileId = "fallback", SortOrder = 0 },
                    new LlmRouteRule { RolePattern = "Teacher", ProfileId = "teacher", SortOrder = 0 }
                }
            };

            LlmRouteResolution teacher = new LlmRouteResolver(table).Resolve("Teacher");
            LlmRouteResolution other = new LlmRouteResolver(table).Resolve("Other");

            Assert.AreEqual("teacher", teacher.Profile.ProfileId);
            Assert.AreEqual("fallback", other.Profile.ProfileId);
        }

        [Test]
        public void RouteTable_ValidatesDuplicateAndMissingProfiles()
        {
            LlmRouteTable table = new()
            {
                Profiles = new[]
                {
                    new LlmRouteProfile { ProfileId = "same" },
                    new LlmRouteProfile { ProfileId = "same" }
                },
                Rules = new[]
                {
                    new LlmRouteRule { RolePattern = "Teacher", ProfileId = "missing" }
                }
            };

            IReadOnlyList<string> errors = table.Validate();

            Assert.AreEqual(2, errors.Count);
            StringAssert.Contains("Duplicate", errors[0]);
            StringAssert.Contains("missing", errors[1]);
        }

        [Test]
        public void RoutingManifest_ConvertsToPortableRouteTable()
        {
            LlmRoutingManifest manifest = ScriptableObject.CreateInstance<LlmRoutingManifest>();
            SetList(manifest, "profiles", new List<LlmBackendProfileEntry>
            {
                new()
                {
                    profileId = "server",
                    kind = LlmBackendKind.ServerManagedApi,
                    executionMode = LlmExecutionMode.ServerManagedApi,
                    contextWindowTokens = 12000
                }
            });
            SetList(manifest, "routes", new List<LlmRoleRouteEntry>
            {
                new() { rolePattern = "Teacher", profileId = "server", sortOrder = 5 }
            });

            LlmRouteTable table = manifest.ToRouteTable();

            Assert.AreEqual(1, table.Profiles.Count);
            Assert.AreEqual("server", table.Profiles[0].ProfileId);
            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, table.Profiles[0].Mode);
            Assert.AreEqual(12000, table.Profiles[0].ContextWindowTokens);
            Assert.AreEqual("Teacher", table.Rules[0].RolePattern);
            Object.DestroyImmediate(manifest);
        }

        [Test]
        public void LlmProviderError_MapsStableCodes()
        {
            Assert.AreEqual(LlmErrorCode.QuotaExceeded, LlmProviderError.MapCode("quota_exceeded"));
            Assert.AreEqual(LlmErrorCode.AuthExpired, LlmProviderError.MapCode("subscription_required"));
            Assert.AreEqual(LlmErrorCode.InvalidRequest, LlmProviderError.MapCode("model_not_allowed"));
            Assert.AreEqual(LlmErrorCode.RateLimited, LlmProviderError.MapCode("rate_limited"));
        }

        [Test]
        public void UsageRecord_Add_AggregatesTokenCounts()
        {
            LlmUsageRecord total = new() { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
            total.Add(new LlmUsageRecord { PromptTokens = 3, CompletionTokens = 2, TotalTokens = 5 });

            Assert.AreEqual(13, total.PromptTokens);
            Assert.AreEqual(7, total.CompletionTokens);
            Assert.AreEqual(20, total.TotalTokens);
        }

        [Test]
        public void ToolCallHistory_RecordsLifecycleWithCallId()
        {
            InMemoryLlmToolCallHistory history = new();
            LlmToolCallInfo info = new("trace", "Teacher", "call-1", "spawn_quiz", "{}");

            history.RecordStarted(new LlmToolCallStarted(info));
            history.RecordCompleted(new LlmToolCallCompleted(info, "{\"ok\":true}", 12d));

            IReadOnlyList<LlmToolCallRecord> records = history.Snapshot();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("call-1", records[0].Info.CallId);
            Assert.AreEqual("completed", records[1].Status);
            Assert.AreEqual(12d, records[1].DurationMs);
        }

        [Test]
        public async Task ScriptedLlmClient_ReturnsRuleByContextMarker()
        {
            ScriptedLlmClient client = new ScriptedLlmClient()
                .WhenContextContains("slot=practice", "practice-response")
                .OtherwiseReply("fallback");

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                SystemPrompt = "## Runtime Context\nslot=practice",
                UserPayload = "hello"
            });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("practice-response", result.Content);
            Assert.AreEqual("scripted", result.Model);
        }

        [Test]
        public void ToolResultEnvelope_RoundTripsJson()
        {
            LlmToolResultEnvelope envelope = new()
            {
                ToolName = "spawn_quiz",
                Action = "feedback_only",
                Success = false,
                Score = 0.5f,
                Summary = "Needs retry",
                PayloadJson = "{\"mistakes\":1}"
            };

            Assert.IsTrue(LlmToolResultEnvelope.TryParse(envelope.ToJson(), out LlmToolResultEnvelope parsed));
            Assert.AreEqual("spawn_quiz", parsed.ToolName);
            Assert.AreEqual("feedback_only", parsed.Action);
            Assert.AreEqual(0.5f, parsed.Score);
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

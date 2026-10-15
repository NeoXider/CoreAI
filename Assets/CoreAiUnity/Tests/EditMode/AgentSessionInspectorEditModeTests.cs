using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Diagnostics;
using CoreAI.Editor.Diagnostics;
using CoreAI.Infrastructure.Prompts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class AgentSessionInspectorEditModeTests
    {
        [Test]
        public void InspectSerializedInputs_BuildsEditModeSnapshot_ReadOnly()
        {
            CoreAISettingsOptions settings = new()
            {
                ContextWindowTokens = 8192,
                UniversalSystemPromptPrefix = "Universal prefix.",
                ToolContractAdditionalInstructions = "Use tools carefully.",
                EnableConversationHistorySummarization = true
            };

            AgentPromptsDefinition prompts = new();
            prompts.CustomAgents.Add(new AgentPromptEntryDefinition
            {
                RoleId = "Teacher",
                SystemPrompt = "Base teacher prompt.",
                UserPromptTemplate = "Teach {hint}.",
                OverrideUniversalPrefix = false
            });

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("Teacher", true, 4096, true, 12);
            policy.SetMaxOutputTokens("Teacher", 512);
            policy.SetAdditionalSystemPrompt("Teacher", "Additional teacher prompt.");

            ReadOnlyMemoryStore memoryStore = new("Remember the student likes examples.");

            AgentSessionSnapshot snapshot = AgentSessionInspector.InspectSerializedInputs(
                "Teacher",
                settings,
                prompts,
                policy,
                memoryStore);

            Assert.AreEqual("edit-mode (serialized scene)", snapshot.SnapshotSource);
            Assert.AreEqual("Teacher", snapshot.RoleId);
            Assert.IsTrue(snapshot.RoleIsExplicitlyConfigured);
            Assert.AreEqual("Universal prefix.", snapshot.UniversalSystemPromptPrefix);
            Assert.AreEqual("Base teacher prompt.", snapshot.BaseSystemPrompt);
            Assert.AreEqual("Additional teacher prompt.", snapshot.AdditionalSystemPrompt);
            StringAssert.Contains("Universal prefix.", snapshot.ResolvedSystemPrompt);
            StringAssert.Contains("Base teacher prompt.", snapshot.ResolvedSystemPrompt);
            StringAssert.Contains("Additional teacher prompt.", snapshot.ResolvedSystemPrompt);
            StringAssert.Contains("Remember the student likes examples.", snapshot.MemoryText);

            Assert.IsTrue(snapshot.RoleConfig.WithChatHistory);
            Assert.IsTrue(snapshot.RoleConfig.PersistChatHistory);
            Assert.AreEqual(4096, snapshot.RoleConfig.ContextTokens);
            Assert.AreEqual(12, snapshot.RoleConfig.MaxChatHistoryMessages);
            Assert.AreEqual(512, snapshot.RoleConfig.MaxOutputTokens);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary, snapshot.RoleConfig.ToolResultMemory);
            Assert.AreEqual(4096, snapshot.Budget.ContextWindowTokens);
            Assert.AreEqual(512, snapshot.Budget.ReservedForCompletionTokens);
            Assert.Greater(snapshot.Budget.EstimatedSystemTokens, 0);
            Assert.Greater(snapshot.Budget.HistoryTokenBudget, 0);

            Assert.AreEqual(AgentSessionSnapshot.UnavailableInEditMode,
                snapshot.ResolvedSystemPromptWithRuntimeContext);
            Assert.AreEqual(AgentSessionSnapshot.UnavailableInEditMode,
                snapshot.UserPayloadEstimate);
            Assert.AreEqual(1, snapshot.EstimatedRequestChatHistory.Count);
            Assert.AreEqual(AgentSessionSnapshot.UnavailableInEditMode,
                snapshot.EstimatedRequestChatHistory[0].Content);

            StringAssert.Contains("Source: edit-mode (serialized scene)", snapshot.ToStatsText());
            StringAssert.Contains(AgentSessionSnapshot.UnavailableInEditMode, snapshot.ToSessionText());
        }

        [Test]
        public void SerializeSnapshotToJson_ExportsValidIndentedJsonWithKnownField()
        {
            AgentSessionSnapshot snapshot = new()
            {
                SnapshotSource = "test",
                RoleId = "Teacher",
                RoleIsExplicitlyConfigured = true,
                UniversalSystemPromptPrefix = "Universal prefix.",
                BaseSystemPrompt = "Base teacher prompt.",
                AdditionalSystemPrompt = "Additional teacher prompt.",
                ResolvedSystemPrompt = "Resolved teacher prompt.",
                MemoryText = "Remember the student likes examples.",
                ConversationSummary = "Prior lesson summary.",
                RoleConfig = new AgentSessionRoleConfigSnapshot
                {
                    UseMemoryTool = true,
                    DefaultAction = MemoryToolAction.Append,
                    AllowDuplicateToolCalls = null,
                    WithChatHistory = true,
                    PersistChatHistory = true,
                    ContextTokens = 4096,
                    MaxChatHistoryMessages = 12,
                    MaxOutputTokens = 512,
                    ToolResultMemory = ToolResultMemoryPolicy.CompactSummary,
                    Temperature = 0.1f,
                    UseLlmContextCompaction = true
                },
                Budget = new AgentSessionBudgetSnapshot
                {
                    ContextWindowTokens = 4096,
                    ReservedForCompletionTokens = 512,
                    EstimatedSystemTokens = 128,
                    EstimatedUserTokens = 16
                }
            };
            snapshot.Tools.Add(new AgentSessionToolSnapshot
            {
                Name = "memory",
                Description = "Memory tool",
                ParametersSchema = "{}",
                AllowDuplicates = false
            });
            snapshot.ChatHistory.Add(new AgentSessionChatMessageSnapshot
            {
                Role = "user",
                Content = "Can you explain loops?",
                Timestamp = 123
            });

            string json = AgentSessionInspectorWindow.SerializeSnapshotToJson(snapshot);
            JObject parsed = JObject.Parse(json);

            Assert.IsNotEmpty(json);
            StringAssert.Contains("\n  \"RoleId\"", json);
            Assert.AreEqual("Teacher", parsed.Value<string>("RoleId"));
            Assert.AreEqual("Append", parsed["RoleConfig"]?.Value<string>("DefaultAction"));
        }

        [Test]
        public void SnapshotTextViews_SplitSystemAndHistory()
        {
            AgentSessionSnapshot snapshot = new()
            {
                SnapshotSource = "test",
                RoleId = "Teacher",
                RoleIsExplicitlyConfigured = true,
                ResolvedSystemPrompt = "Resolved teacher prompt.",
                ResolvedSystemPromptFinalEstimate = "Resolved teacher prompt with tool contract.",
                RoleConfig = new AgentSessionRoleConfigSnapshot(),
                Budget = new AgentSessionBudgetSnapshot()
            };
            snapshot.ChatHistory.Add(new AgentSessionChatMessageSnapshot
            {
                Role = "user",
                Content = "Can you explain loops?",
                Timestamp = 123
            });
            snapshot.EstimatedRequestChatHistory.Add(new AgentSessionChatMessageSnapshot
            {
                Role = "system",
                Content = "## Memory\nStudent likes examples.",
                Timestamp = 0
            });
            snapshot.EstimatedRequestChatHistory.Add(new AgentSessionChatMessageSnapshot
            {
                Role = "assistant",
                Content = "Loops repeat a block.",
                Timestamp = 124
            });

            string systemText = snapshot.ToSystemPromptText();
            string historyText = snapshot.ToHistoryText();

            StringAssert.Contains("Resolved teacher prompt.", systemText);
            StringAssert.DoesNotContain("Can you explain loops?", systemText);

            StringAssert.Contains("Can you explain loops?", historyText);
            StringAssert.Contains("Loops repeat a block.", historyText);
            StringAssert.DoesNotContain("Resolved teacher prompt.", historyText);
            StringAssert.DoesNotContain("Student likes examples.", historyText);
        }

        [Test]
        public void LiveKnownRoleIds_IncludeManifestPromptRoles()
        {
            CoreAISettingsOptions settings = new()
            {
                UniversalSystemPromptPrefix = "Universal prefix."
            };
            AgentPromptsDefinition prompts = new();
            prompts.CustomAgents.Add(new AgentPromptEntryDefinition
            {
                RoleId = "Teacher",
                SystemPrompt = "Teacher prompt."
            });

            IAgentSystemPromptProvider systemProvider = new ChainedAgentSystemPromptProvider(
                new IAgentSystemPromptProvider[]
                {
                    new ManifestAgentSystemPromptProvider(prompts),
                    new BuiltInDefaultAgentSystemPromptProvider()
                });
            AgentMemoryPolicy policy = new();
            AgentSessionInspector inspector = new(
                new AiPromptComposer(
                    systemProvider,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore(),
                    null,
                    policy,
                    settings),
                systemProvider,
                null,
                null,
                policy,
                settings);

            CollectionAssert.Contains(inspector.GetKnownRoleIds(), "Teacher");
        }

        [Test]
        public void InspectorCandidateScore_PrefersRicherProjectScope()
        {
            AgentSessionInspector coreInspector = CreateInspectorWithPromptRole(null);
            AgentSessionInspector gameInspector = CreateInspectorWithPromptRole("Teacher");

            int coreScore = InvokeCandidateScore(coreInspector, 2);
            int gameScore = InvokeCandidateScore(gameInspector, 3);
            int deeperCoreScore = InvokeCandidateScore(coreInspector, 3);

            Assert.Greater(gameScore, coreScore);
            Assert.Greater(deeperCoreScore, coreScore);
        }

        [Test]
        public void Inspect_PassesRequestScopedFieldsToRuntimeContextProvider()
        {
            CapturingRuntimeContextProvider provider = new();
            AgentMemoryPolicy policy = new();
            policy.SetRuntimeContextProvider("Teacher", provider);

            AiPromptComposer composer;
            AgentSessionInspector inspector = CreateInspector(policy, out composer);

            ActorContext actor = CreateActorContext("teacher-actor", "teacher-session");
            AiTaskRequest request = new()
            {
                RoleId = "Teacher",
                RoutingProfileId = "school-local",
                SystemPrompt = "Request-scoped teacher prompt.",
                RequestSystemInstructions = "Teacher runtime context captured.",
                Hint = "Explain loops.",
                ActorContext = actor,
                MaxToolCallRoundtrips = 3,
                AllowTextShapedToolCallsOnNativeEndpoint = true
            };

            AgentSessionSnapshot snapshot = inspector.Inspect("Teacher", request);

            Assert.IsTrue(provider.WasCalled, "The role runtime-context provider must run during inspection.");
            Assert.AreEqual("teacher-session", provider.SessionId);
            Assert.AreEqual("school-local", provider.RoutingProfileId);
            Assert.AreEqual("Request-scoped teacher prompt.", provider.SystemPrompt);
            Assert.AreEqual("Teacher runtime context captured.", provider.RequestSystemInstructions);
            Assert.AreEqual(3, provider.MaxToolCallRoundtrips);
            Assert.AreEqual(true, provider.AllowTextShapedToolCallsOnNativeEndpoint);
            Assert.AreEqual("Teacher", snapshot.RoleId);
        }

        [Test]
        public void Inspect_ResolvedSystemPrompt_MatchesRequestScopedSystemPromptOverride()
        {
            AgentMemoryPolicy policy = new();
            AiPromptComposer composer;
            AgentSessionInspector inspector = CreateInspector(policy, out composer);

            AiTaskRequest request = new()
            {
                RoleId = "Teacher",
                SystemPrompt = "Request-scoped teacher prompt."
            };

            AgentSessionSnapshot snapshot = inspector.Inspect("Teacher", request);

            Assert.AreEqual(
                composer.GetSystemPrompt("Teacher", request.SystemPrompt),
                snapshot.ResolvedSystemPrompt);
            StringAssert.Contains("Request-scoped teacher prompt.", snapshot.ResolvedSystemPrompt);
            StringAssert.DoesNotContain("Base teacher prompt.", snapshot.ResolvedSystemPrompt);
        }

        [Test]
        public void AiTaskRequestClone_CopiesEveryPublicSettableProperty()
        {
            using CancellationTokenSource cts = new();

            PropertyInfo[] writable = Array.FindAll(
                typeof(AiTaskRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance),
                static property => property.CanWrite && property.GetIndexParameters().Length == 0);

            Assert.IsNotEmpty(writable, "AiTaskRequest must expose writable request-scoped properties.");

            List<string> uncopied = new();
            for (int i = 0; i < writable.Length; i++)
            {
                PropertyInfo property = writable[i];
                object probe = CreateProbeValue(property, i, cts);
                AiTaskRequest source = new();
                property.SetValue(source, probe);

                AiTaskRequest clone = source.Clone();
                object copied = property.GetValue(clone);

                if (!ProbeMatches(property, probe, copied))
                {
                    uncopied.Add(property.Name);
                }
            }

            CollectionAssert.IsEmpty(
                uncopied,
                "AiTaskRequest.Clone() must copy every public settable property; add the new field to it, " +
                "or inspection and retries will silently lose it: " + string.Join(", ", uncopied));
        }

        private static AgentSessionInspector CreateInspector(AgentMemoryPolicy policy, out AiPromptComposer composer)
        {
            CoreAISettingsOptions settings = new()
            {
                UniversalSystemPromptPrefix = "Universal prefix.",
                ContextWindowTokens = 8192
            };

            AgentPromptsDefinition prompts = new();
            prompts.CustomAgents.Add(new AgentPromptEntryDefinition
            {
                RoleId = "Teacher",
                SystemPrompt = "Base teacher prompt."
            });

            IAgentSystemPromptProvider systemProvider = new ChainedAgentSystemPromptProvider(
                new IAgentSystemPromptProvider[]
                {
                    new ManifestAgentSystemPromptProvider(prompts),
                    new BuiltInDefaultAgentSystemPromptProvider()
                });

            composer = new AiPromptComposer(
                systemProvider,
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(),
                null,
                policy,
                settings);

            return new AgentSessionInspector(
                composer,
                systemProvider,
                null,
                null,
                policy,
                settings);
        }

        private static ActorContext CreateActorContext(string actorId, string sessionId)
        {
            return new LocalActorIdentityProvider(
                    actorId,
                    sessionId,
                    "world",
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty)
                .GetActorContext("Teacher");
        }

        private static object CreateProbeValue(PropertyInfo property, int index, CancellationTokenSource cts)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(string))
            {
                return "probe-" + property.Name;
            }

            if (type == typeof(int))
            {
                return 4242 + index;
            }

            if (type == typeof(float))
            {
                return 0.375f;
            }

            if (type == typeof(LlmReasoningOptions))
            {
                return new LlmReasoningOptions(LlmReasoningEffort.High, 2048 + index);
            }

            if (type == typeof(LlmSamplingOptions))
            {
                return new LlmSamplingOptions
                {
                    TopP = 0.75f, Seed = 42, Stop = new[] { "probe-stop" },
                    FrequencyPenalty = 0.25f, PresencePenalty = 0.5f
                };
            }

            if (type == typeof(bool))
            {
                return true;
            }

            if (type.IsEnum)
            {
                object defaultValue = Activator.CreateInstance(type);
                foreach (object candidate in Enum.GetValues(type))
                {
                    if (!Equals(candidate, defaultValue))
                    {
                        return candidate;
                    }
                }
            }

            if (type == typeof(CancellationToken))
            {
                return cts.Token;
            }

            if (type == typeof(string[]))
            {
                return new[] { "probe-" + property.Name };
            }

            if (type == typeof(ActorContext))
            {
                return CreateActorContext("probe-actor", "probe-session");
            }

            if (type == typeof(IReadOnlyList<AiAttachment>))
            {
                return new List<AiAttachment>();
            }

            Assert.Fail("AiTaskRequest." + property.Name + " has type " + type.FullName +
                        ", which the clone guard does not know how to probe. Extend CreateProbeValue.");
            return null;
        }

        private static bool ProbeMatches(PropertyInfo property, object probe, object copied)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(string) || type.IsValueType)
            {
                return Equals(probe, copied);
            }

            return ReferenceEquals(probe, copied);
        }

        /// <summary>
        /// Mirrors a production runtime-context provider that validates request-scoped instructions
        /// (a captured session snapshot) and rejects a request that does not carry them.
        /// </summary>
        private sealed class CapturingRuntimeContextProvider : IAgentRuntimeContextProvider
        {
            public bool WasCalled { get; private set; }

            public string SessionId { get; private set; } = "";

            public string RoutingProfileId { get; private set; } = "";

            public string SystemPrompt { get; private set; } = "";

            public string RequestSystemInstructions { get; private set; } = "";

            public int? MaxToolCallRoundtrips { get; private set; }

            public bool? AllowTextShapedToolCallsOnNativeEndpoint { get; private set; }

            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.RequestSystemInstructions))
                {
                    throw new InvalidOperationException(
                        "Teacher runtime context must be captured before orchestration starts");
                }

                WasCalled = true;
                SessionId = request.ActorContext?.SessionId ?? "";
                RoutingProfileId = request.RoutingProfileId ?? "";
                SystemPrompt = request.SystemPrompt ?? "";
                RequestSystemInstructions = request.RequestSystemInstructions;
                MaxToolCallRoundtrips = request.MaxToolCallRoundtrips;
                AllowTextShapedToolCallsOnNativeEndpoint = request.AllowTextShapedToolCallsOnNativeEndpoint;
                return "## Teacher Runtime Context\nCaptured for " + roleId + ".";
            }
        }

        private static AgentSessionInspector CreateInspectorWithPromptRole(string roleId)
        {
            CoreAISettingsOptions settings = new()
            {
                UniversalSystemPromptPrefix = "Universal prefix."
            };
            AgentPromptsDefinition prompts = new();
            if (!string.IsNullOrWhiteSpace(roleId))
            {
                prompts.CustomAgents.Add(new AgentPromptEntryDefinition
                {
                    RoleId = roleId,
                    SystemPrompt = roleId + " prompt."
                });
            }

            IAgentSystemPromptProvider systemProvider = new ChainedAgentSystemPromptProvider(
                new IAgentSystemPromptProvider[]
                {
                    new ManifestAgentSystemPromptProvider(prompts),
                    new BuiltInDefaultAgentSystemPromptProvider()
                });
            AgentMemoryPolicy policy = new();
            return new AgentSessionInspector(
                new AiPromptComposer(
                    systemProvider,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore(),
                    null,
                    policy,
                    settings),
                systemProvider,
                null,
                null,
                policy,
                settings);
        }

        private static int InvokeCandidateScore(AgentSessionInspector inspector, int scopeDepth)
        {
            System.Reflection.MethodInfo method = typeof(AgentSessionInspectorWindow).GetMethod(
                "ComputeInspectorCandidateScore",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (int)method.Invoke(null, new object[] { inspector, scopeDepth });
        }

        private sealed class ReadOnlyMemoryStore : IAgentMemoryStore
        {
            private readonly string _memory;

            public ReadOnlyMemoryStore(string memory)
            {
                _memory = memory;
            }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState
                {
                    LastSystemPrompt = "",
                    Memory = _memory
                };
                return true;
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return new[]
                {
                    new ChatMessage("user", "Can you explain loops?")
                };
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                throw new AssertionException("Inspector must not save memory.");
            }

            public void Clear(string roleId)
            {
                throw new AssertionException("Inspector must not clear memory.");
            }

            public void ClearChatHistory(string roleId)
            {
                throw new AssertionException("Inspector must not clear chat history.");
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                throw new AssertionException("Inspector must not append chat history.");
            }
        }
    }
}

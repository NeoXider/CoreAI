using CoreAI.Ai;
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
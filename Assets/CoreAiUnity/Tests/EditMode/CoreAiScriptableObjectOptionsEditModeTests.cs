using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;
using CoreAI.Presentation.AiDashboard;
using CoreAI.Unity;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAiScriptableObjectOptionsEditModeTests
    {
        [Test]
        public void CoreAiChatConfig_ToOptionsAndApplyOptions_PreservePortableValues()
        {
            CoreAiChatConfig asset = ScriptableObject.CreateInstance<CoreAiChatConfig>();
            try
            {
                asset.ApplyOptions(new CoreAiChatOptions
                {
                    RoleId = "RuntimeChat",
                    HeaderTitle = "Runtime title",
                    WelcomeMessage = "Welcome",
                    LoadPersistedChatOnStartup = false,
                    MaxPersistedMessagesForUi = 12,
                    EnableStreaming = false,
                    EnableStopGeneration = false,
                    ShowToolCallsInChat = true,
                    ShowClearButton = false,
                    TypingIndicatorText = "Thinking",
                    StreamingToolProgressHint = "Tool",
                    LongRequestHintFormat = "Wait {elapsed}",
                    UseFullscreenChat = true,
                    ChatWidth = 700,
                    ChatHeight = 800,
                    SendOnShiftEnter = true,
                    MaxMessageLength = 123,
                    EnableOpenChatKeyboardShortcut = false,
                    EnableEscapeChatShortcuts = false,
                    ErrorMessagePrefix = "Err: ",
                    TimeoutMessage = "Timeout",
                    NoResponseMessage = "Empty"
                });

                CoreAiChatOptions options = asset.ToOptions();

                Assert.AreEqual("RuntimeChat", options.RoleId);
                Assert.AreEqual("Runtime title", options.HeaderTitle);
                Assert.IsTrue(options.ShowToolCallsInChat);
                Assert.IsFalse(options.EnableStopGeneration);
                Assert.IsFalse(options.ShowClearButton);
                Assert.IsTrue(options.UseFullscreenChat);
                Assert.AreEqual(123, options.MaxMessageLength);
                Assert.IsFalse(options.EnableOpenChatKeyboardShortcut);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CoreAISettingsAsset_ToOptionsAndApplyOptions_PreservePortableValues()
        {
            CoreAISettingsAsset asset = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                asset.ApplyOptions(new CoreAISettingsOptions
                {
                    MaxLuaRepairRetries = 5,
                    EnableMeaiDebugLogging = true,
                    LlmRequestTimeoutSeconds = 77f,
                    MaxLlmRequestRetries = 3,
                    MaxContextOverflowRetries = 2,
                    EnableHttpDebugLogging = true,
                    LogTokenUsage = false,
                    ContextWindowTokens = 16384,
                    UniversalSystemPromptPrefix = "Prefix",
                    ToolContractAdditionalInstructions = "Contract",
                    Temperature = 1.1f,
                    OverrideTemperature = true,
                    MaxToolCallRetries = 4,
                    EnableStreaming = false,
                    MaxTokens = 4096,
                    MaxToolResultChars = 99,
                    DefaultToolTimeoutMs = 88,
                    MaxResponseChars = 77,
                    MaxToolCallRoundtrips = 6,
                    MaxToolCallHistoryMessages = 5
                });

                CoreAISettingsOptions options = asset.ToOptions();

                Assert.AreEqual(5, options.MaxLuaRepairRetries);
                Assert.IsTrue(options.EnableMeaiDebugLogging);
                Assert.AreEqual(77f, options.LlmRequestTimeoutSeconds);
                Assert.AreEqual(2, options.MaxContextOverflowRetries);
                Assert.AreEqual(16384, options.ContextWindowTokens);
                Assert.AreEqual("Prefix", options.UniversalSystemPromptPrefix);
                Assert.AreEqual("Contract", options.ToolContractAdditionalInstructions);
                Assert.AreEqual(1.1f, options.Temperature);
                Assert.IsTrue(options.OverrideTemperature);
                Assert.IsFalse(options.EnableStreaming);
                Assert.AreEqual(4096, options.MaxTokens);
                Assert.AreEqual(6, options.MaxToolCallRoundtrips);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void OpenAiHttpLlmSettings_ToOptionsAndApplyOptions_PreservePortableValues()
        {
            OpenAiHttpLlmSettings asset = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            try
            {
                asset.ApplyOptions(new OpenAiHttpOptions
                {
                    UseOpenAiCompatibleHttp = true,
                    ExecutionMode = LlmExecutionMode.ClientLimited,
                    ApiBaseUrl = "https://api.example.com/v1/",
                    ApiKey = "sk-test",
                    Model = "model-a",
                    Temperature = 0.7f,
                    RequestTimeoutSeconds = 44,
                    MaxTokens = 512,
                    MaxRequestsPerSession = 3,
                    MaxPromptChars = 900,
                    LogLlmInput = false,
                    LogLlmOutput = false,
                    EnableHttpDebugLogging = true
                });

                OpenAiHttpOptions options = asset.ToOptions();

                Assert.IsTrue(options.UseOpenAiCompatibleHttp);
                Assert.AreEqual(LlmExecutionMode.ClientLimited, options.ExecutionMode);
                Assert.AreEqual("https://api.example.com/v1", options.ApiBaseUrl);
                Assert.AreEqual("sk-test", options.ApiKey);
                Assert.AreEqual("model-a", options.Model);
                Assert.AreEqual(3, options.MaxRequestsPerSession);
                Assert.AreEqual(900, options.MaxPromptChars);
                Assert.IsTrue(options.EnableHttpDebugLogging);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void GameLogSettingsAsset_ToOptions_PreservesLoggingRules()
        {
            GameLogSettingsAsset asset = ScriptableObject.CreateInstance<GameLogSettingsAsset>();
            try
            {
                asset.ApplyOptions(new GameLogSettingsOptions
                {
                    EnabledFeatures = GameLogFeature.Core,
                    MinimumLevel = GameLogLevel.Warning
                });

                GameLogSettingsOptions options = asset.ToOptions();

                Assert.IsFalse(asset.ShouldLog(GameLogFeature.Core, GameLogLevel.Info));
                Assert.IsTrue(asset.ShouldLog(GameLogFeature.Core, GameLogLevel.Warning));
                Assert.IsFalse(options.ShouldLog(GameLogFeature.Llm, GameLogLevel.Error));
                Assert.IsTrue(options.ShouldLog(GameLogFeature.Core, GameLogLevel.Error));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AiPermissionsAsset_ToOptionsAndApplyOptions_PreserveFlags()
        {
            AiPermissionsAsset asset = ScriptableObject.CreateInstance<AiPermissionsAsset>();
            try
            {
                asset.ApplyOptions(new AiPermissionsOptions
                {
                    AllowCreator = false,
                    AllowAnalyzer = true,
                    AllowCoreMechanic = false
                });

                AiPermissionsOptions options = asset.ToOptions();

                Assert.IsFalse(options.AllowCreator);
                Assert.IsTrue(options.AllowAnalyzer);
                Assert.IsFalse(options.AllowCoreMechanic);
                Assert.IsInstanceOf<IAiPermissions>(asset);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void LlmRoutingManifest_ToOptions_BuildsPortableRouteTable()
        {
            LlmRoutingManifest manifest = ScriptableObject.CreateInstance<LlmRoutingManifest>();
            OpenAiHttpLlmSettings http = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            try
            {
                http.ApplyOptions(new OpenAiHttpOptions
                {
                    UseOpenAiCompatibleHttp = true,
                    ExecutionMode = LlmExecutionMode.ServerManagedApi,
                    Model = "route-model"
                });
                SetField(manifest, "profiles", new List<LlmBackendProfileEntry>
                {
                    new()
                    {
                        profileId = "chat",
                        kind = LlmBackendKind.ServerManagedApi,
                        httpSettings = http,
                        contextWindowTokens = 4096
                    }
                });
                SetField(manifest, "routes", new List<LlmRoleRouteEntry>
                {
                    new() { rolePattern = "SmartChat", profileId = "chat", sortOrder = 1 }
                });

                LlmRouteTable table = manifest.ToOptions();

                Assert.AreEqual(1, table.Profiles.Count);
                Assert.AreEqual("chat", table.Profiles[0].ProfileId);
                Assert.AreEqual(LlmExecutionMode.ServerManagedApi, table.Profiles[0].Mode);
                Assert.AreEqual("route-model", table.Profiles[0].Model);
                Assert.AreEqual(1, table.Rules.Count);
                Assert.AreEqual("SmartChat", table.Rules[0].RolePattern);
            }
            finally
            {
                Object.DestroyImmediate(http);
                Object.DestroyImmediate(manifest);
            }
        }

        [Test]
        public void AgentPromptsManifest_ToDefinition_ReadsTextAssetContents()
        {
            AgentPromptsManifest manifest = ScriptableObject.CreateInstance<AgentPromptsManifest>();
            try
            {
                manifest.roleOverrides.Add(new AgentPromptsManifest.Entry
                {
                    roleId = "Teacher",
                    systemPrompt = new TextAsset("System prompt"),
                    userPromptTemplate = new TextAsset("User {hint}"),
                    overrideUniversalPrefix = true
                });

                AgentPromptsDefinition definition = manifest.ToDefinition();

                Assert.AreEqual(1, definition.RoleOverrides.Count);
                Assert.AreEqual("Teacher", definition.RoleOverrides[0].RoleId);
                Assert.AreEqual("System prompt", definition.RoleOverrides[0].SystemPrompt);
                Assert.AreEqual("User {hint}", definition.RoleOverrides[0].UserPromptTemplate);
                Assert.IsTrue(definition.RoleOverrides[0].OverrideUniversalPrefix);
            }
            finally
            {
                Object.DestroyImmediate(manifest);
            }
        }

        [Test]
        public void SkillSetAsset_ToSkillDefinition_BuildsPortableSkill()
        {
            SkillSetAsset asset = ScriptableObject.CreateInstance<SkillSetAsset>();
            try
            {
                SetField(asset, "skillName", "Crafting");
                SetField(asset, "description", "Craft items");
                SetField(asset, "inlineInstructions", "Use crafting tools");

                SkillSetDefinition definition = asset.ToSkillDefinition();
                SkillSet skill = definition.BuildSkillSet();

                Assert.AreEqual("Crafting", definition.Name);
                Assert.AreEqual("Craft items", definition.Description);
                Assert.AreEqual("Use crafting tools", definition.Instructions);
                Assert.AreEqual("Crafting", skill.Name);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CoreAiPrefabRegistryAsset_ImplementsRegistryContract()
        {
            CoreAiPrefabRegistryAsset asset = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                Assert.IsInstanceOf<ICoreAiPrefabRegistry>(asset);
                Assert.IsFalse(((ICoreAiPrefabRegistry)asset).TryResolve("missing", out GameObject prefab));
                Assert.IsNull(prefab);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, fieldName);
            field.SetValue(target, value);
        }
    }
}

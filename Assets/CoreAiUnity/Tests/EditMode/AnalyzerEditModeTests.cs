using NUnit.Framework;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Ai;
using CoreAI.Session;
using CoreAI.Authority;
using CoreAI.Messaging;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для Analyzer роли: проверка промптов, телеметрии, формата ответов.
    /// </summary>
    [TestFixture]
    public class AnalyzerEditModeTests
    {
        private AiPromptComposer _promptComposer;
        private StubLlmClient _stubLlm;
        private SessionTelemetryCollector _telemetry;

        [SetUp]
        public void SetUp()
        {
            _promptComposer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new StubUserTemplateProvider(),
                new NullLuaScriptVersionStore(),
                null);
            _stubLlm = new StubLlmClient();
            _telemetry = new SessionTelemetryCollector();
        }

        #region System Prompt Tests

        [Test]
        public void Analyzer_SystemPrompt_IsNotEmpty()
        {
            string prompt = _promptComposer.GetSystemPrompt("Analyzer");
            Assert.IsNotNull(prompt);
            Assert.Greater(prompt.Length, 50, "Analyzer system prompt should be substantial");
        }

        [Test]
        public void Analyzer_SystemPrompt_ContainsAnalysisKeywords()
        {
            string prompt = _promptComposer.GetSystemPrompt("Analyzer").ToLowerInvariant();
            StringAssert.Contains("analy", prompt); // analyze/analysis
            StringAssert.Contains("telemetry", prompt); // reads telemetry
        }

        [Test]
        public void Analyzer_SystemPrompt_DifferentFromCreator()
        {
            string analyzerPrompt = _promptComposer.GetSystemPrompt("Analyzer");
            string creatorPrompt = _promptComposer.GetSystemPrompt("Creator");
            Assert.AreNotEqual(analyzerPrompt, creatorPrompt);
        }

        #endregion

        #region Telemetry Tests

        [Test]
        public void Analyzer_ReceivesTelemetry_InUserPayload()
        {
            _telemetry.SetTelemetry("wave", 3);
            GameSessionSnapshot snapshot = _telemetry.BuildSnapshot();

            string userPayload = _promptComposer.BuildUserPayload(snapshot, new AiTaskRequest
            {
                RoleId = "Analyzer",
                Hint = "Analyze player death rate"
            });

            Assert.IsNotNull(userPayload);
            Assert.Greater(userPayload.Length, 10);
        }

        [Test]
        public void Analyzer_EmptyTelemetry_HandlesGracefully()
        {
            GameSessionSnapshot snapshot = _telemetry.BuildSnapshot();
            string userPayload = _promptComposer.BuildUserPayload(snapshot, new AiTaskRequest
            {
                RoleId = "Analyzer",
                Hint = ""
            });

            Assert.IsNotNull(userPayload);
        }

        #endregion

        #region Response Validation Tests

        [Test]
        public void Analyzer_ResponsePolicy_ValidJson_ReturnsTrue()
        {
            AnalyzerResponsePolicy policy = new();
            string content = @"{""metric"": ""player_death_rate"", ""value"": 0.35, ""status"": ""balanced""}";

            Assert.IsTrue(policy.ShouldValidate("Analyzer"));
            Assert.IsTrue(policy.TryValidate("Analyzer", content, out _));
        }

        [Test]
        public void Analyzer_ResponsePolicy_InvalidText_ReturnsFalse()
        {
            AnalyzerResponsePolicy policy = new();
            string content = "The game seems balanced enough.";

            Assert.IsFalse(policy.TryValidate("Analyzer", content, out string reason));
            StringAssert.Contains("Expected JSON", reason);
        }

        [Test]
        public void Analyzer_ResponsePolicy_RecommendationsJson_ReturnsTrue()
        {
            AnalyzerResponsePolicy policy = new();
            string content =
                @"{""recommendation"": ""increase enemy HP by 10%"", ""analysis"": ""players die too fast""}";

            Assert.IsTrue(policy.TryValidate("Analyzer", content, out _));
        }

        #endregion

        #region Stub LLM Tests

        [Test]
        public void Analyzer_StubLlm_ReturnsJsonResponse()
        {
            LlmCompletionResult result = _stubLlm.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Analyzer",
                SystemPrompt = _promptComposer.GetSystemPrompt("Analyzer"),
                UserPayload = "Analyze wave 3",
                TraceId = "test123"
            }).Result;

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(result.Content);
        }

        #endregion

        #region Orchestrator Integration Tests

        [Test]
        public void Analyzer_Orchestrator_PublishesEnvelope()
        {
            // Arrange
            TestCommandSink commandSink = new();
            TestAuthorityHost authority = new() { CanRunAiTasks = true };
            NullAgentMemoryStore memoryStore = new();
            AgentMemoryPolicy memoryPolicy = new();
            NoOpRoleStructuredResponsePolicy structuredPolicy = new();
            NullAiOrchestrationMetrics metrics = new();

            AiOrchestrator orchestrator = new(
                authority,
                _stubLlm,
                commandSink,
                _telemetry,
                _promptComposer,
                memoryStore,
                memoryPolicy,
                structuredPolicy,
                metrics, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());

            // Act
            orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Analyzer",
                Hint = "Analyze current session balance",
                TraceId = "test_analyzer"
            }).Wait();

            // Assert
            Assert.IsTrue(commandSink.PublishedCommands.Count > 0, "Should publish at least one command");
            ApplyAiGameCommand envelope = commandSink.PublishedCommands[0];
            Assert.AreEqual("Analyzer", envelope.SourceRoleId);
            Assert.IsNotNull(envelope.JsonPayload);
        }

        #endregion

        #region Helper Classes

        private sealed class TestCommandSink : IAiGameCommandSink
        {
            public List<ApplyAiGameCommand> PublishedCommands { get; } = new();

            public void Publish(ApplyAiGameCommand command)
            {
                PublishedCommands.Add(command);
            }
        }

        private sealed class TestAuthorityHost : IAuthorityHost
        {
            public bool CanRunAiTasks { get; set; } = true;
        }

        private sealed class StubUserTemplateProvider : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}\n\nTelemetry:\n{telemetry}";
                return true;
            }
        }

        private sealed class StubSessionTelemetryProvider : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        #endregion
    }
}

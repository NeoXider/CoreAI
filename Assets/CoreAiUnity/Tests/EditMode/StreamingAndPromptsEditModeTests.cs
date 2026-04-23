using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.AgentMemory;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Тесты для 3-слойной архитектуры промптов, streaming API и AgentMemoryPolicy.
    /// </summary>
    public class StreamingAndPromptsEditModeTests
    {
        // ===================== 3-Layer Prompt Architecture =====================

        [Test]
        public void AiPromptComposer_GetSystemPrompt_Combines3Layers()
        {
            // Arrange
            var provider = new TestSystemPromptProvider("Teacher", "Ты учитель.");
            var policy = new AgentMemoryPolicy();
            policy.SetAdditionalSystemPrompt("Teacher", "Используй аналогии из игр.");

            var settings = new TestCoreAISettings { UniversalSystemPromptPrefix = "Отвечай кратко." };

            var composer = new AiPromptComposer(provider, new NullUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(), null, policy, settings);

            // Act
            string result = composer.GetSystemPrompt("Teacher");

            // Assert
            Assert.That(result, Does.Contain("Отвечай кратко."), "Layer 1: universalPrefix");
            Assert.That(result, Does.Contain("Ты учитель."), "Layer 2: base from provider");
            Assert.That(result, Does.Contain("Используй аналогии из игр."), "Layer 3: additional from AgentBuilder");

            // Порядок: prefix → base → additional
            int prefixIdx = result.IndexOf("Отвечай кратко.");
            int baseIdx = result.IndexOf("Ты учитель.");
            int additionalIdx = result.IndexOf("Используй аналогии из игр.");
            Assert.Less(prefixIdx, baseIdx, "Prefix should come before base");
            Assert.Less(baseIdx, additionalIdx, "Base should come before additional");
        }

        [Test]
        public void AiPromptComposer_GetSystemPrompt_WorksWithoutPolicy()
        {
            // No policy, no settings — should still return base prompt
            var provider = new TestSystemPromptProvider("Creator", "Ты создатель.");
            var composer = new AiPromptComposer(provider, new NullUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            string result = composer.GetSystemPrompt("Creator");
            Assert.That(result, Does.Contain("Ты создатель."));
        }

        [Test]
        public void AiPromptComposer_GetSystemPrompt_FallbackForUnknownRole()
        {
            var provider = new TestSystemPromptProvider("Known", "Known prompt.");
            var composer = new AiPromptComposer(provider, new NullUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            string result = composer.GetSystemPrompt("UnknownRole");
            Assert.That(result, Does.Contain("You are agent \"UnknownRole\""));
        }

        [Test]
        public void AiPromptComposer_GetSystemPrompt_UniversalPrefixAppliedToAllRoles()
        {
            var provider = new TestSystemPromptProvider("RoleA", "Base A.");
            var settings = new TestCoreAISettings { UniversalSystemPromptPrefix = "GLOBAL RULES" };
            var composer = new AiPromptComposer(provider, new NullUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(), null, null, settings);

            string resultA = composer.GetSystemPrompt("RoleA");
            string resultB = composer.GetSystemPrompt("RoleB"); // unknown role

            Assert.That(resultA, Does.Contain("GLOBAL RULES"), "Prefix applied to known role");
            Assert.That(resultB, Does.Contain("GLOBAL RULES"), "Prefix applied to unknown role too");
        }

        // ===================== AgentMemoryPolicy — Additional Prompts =====================

        [Test]
        public void AgentMemoryPolicy_SetAndGetAdditionalSystemPrompt()
        {
            var policy = new AgentMemoryPolicy();

            // Initially empty
            Assert.False(policy.TryGetAdditionalSystemPrompt("Teacher", out _));

            // Set
            policy.SetAdditionalSystemPrompt("Teacher", "Be creative.");
            Assert.True(policy.TryGetAdditionalSystemPrompt("Teacher", out string prompt));
            Assert.AreEqual("Be creative.", prompt);

            // Clear
            policy.SetAdditionalSystemPrompt("Teacher", null);
            Assert.False(policy.TryGetAdditionalSystemPrompt("Teacher", out _));
        }

        [Test]
        public void AgentMemoryPolicy_AdditionalPrompts_TrimsWhitespace()
        {
            var policy = new AgentMemoryPolicy();
            policy.SetAdditionalSystemPrompt("  Teacher  ", "  Trimmed prompt  ");

            Assert.True(policy.TryGetAdditionalSystemPrompt("Teacher", out string prompt));
            Assert.AreEqual("Trimmed prompt", prompt);
        }

        [Test]
        public void AgentMemoryPolicy_AdditionalPrompts_EmptyStringClears()
        {
            var policy = new AgentMemoryPolicy();
            policy.SetAdditionalSystemPrompt("Teacher", "Something");
            policy.SetAdditionalSystemPrompt("Teacher", "   ");

            Assert.False(policy.TryGetAdditionalSystemPrompt("Teacher", out _));
        }

        // ===================== AgentConfig.ApplyToPolicy — Registers Prompt =====================

        [Test]
        public void AgentConfig_ApplyToPolicy_RegistersAdditionalPrompt()
        {
            var builder = new AgentBuilder("TestRole")
                .WithSystemPrompt("My custom prompt.")
                .WithMode(AgentMode.ChatOnly);

            var config = builder.Build();
            var policy = new AgentMemoryPolicy();

            config.ApplyToPolicy(policy);

            Assert.True(policy.TryGetAdditionalSystemPrompt("TestRole", out string prompt));
            Assert.AreEqual("My custom prompt.", prompt);
        }

        [Test]
        public void AgentConfig_ApplyToPolicy_NoPrompt_DoesNotRegister()
        {
            var builder = new AgentBuilder("NoPromptRole")
                .WithMode(AgentMode.ChatOnly);

            var config = builder.Build();
            var policy = new AgentMemoryPolicy();

            config.ApplyToPolicy(policy);

            Assert.False(policy.TryGetAdditionalSystemPrompt("NoPromptRole", out _));
        }

        [Test]
        public void AgentBuilder_Build_DoesNotPrependUniversalPrefix()
        {
            // AgentBuilder no longer prepends universalPrefix — that's AiPromptComposer's job
            CoreAISettings.ResetOverrides();

            var settings = new TestCoreAISettings { UniversalSystemPromptPrefix = "PREFIX:" };
            var builder = new AgentBuilder("TestRole", settings)
                .WithSystemPrompt("My prompt");

            var config = builder.Build();

            Assert.AreEqual("My prompt", config.SystemPrompt,
                "Build should NOT prepend universalPrefix — composer handles it");
        }

        // ===================== LlmStreamChunk =====================

        [Test]
        public void LlmStreamChunk_DefaultValues()
        {
            var chunk = new LlmStreamChunk();
            Assert.AreEqual("", chunk.Text, "Default Text should be empty string");
            Assert.IsFalse(chunk.IsDone);
            Assert.IsNull(chunk.Error);
        }

        [Test]
        public void LlmStreamChunk_FinalChunk()
        {
            var chunk = new LlmStreamChunk { IsDone = true, Text = "" };
            Assert.IsTrue(chunk.IsDone);
            Assert.AreEqual("", chunk.Text);
        }

        // ===================== ILlmClient.CompleteStreamingAsync — Default Fallback =====================

        [Test]
        public async Task ILlmClient_DefaultStreamingFallback_ReturnsSingleChunk()
        {
            ILlmClient client = new FakeLlmClient("Hello, world!");

            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in client.CompleteStreamingAsync(
                new LlmCompletionRequest { UserPayload = "test" }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            // Default fallback: first chunk with text, then done chunk
            Assert.GreaterOrEqual(chunks.Count, 1);
            Assert.That(chunks[0].Text, Does.Contain("Hello, world!"));

            // Last chunk should be done
            Assert.IsTrue(chunks[chunks.Count - 1].IsDone);
        }

        // ===================== Streaming Toggle =====================

        [Test]
        public void AgentMemoryPolicy_Streaming_GlobalFallback_True()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = true };

            Assert.IsTrue(policy.IsStreamingEnabled("AnyRole", settings));
        }

        [Test]
        public void AgentMemoryPolicy_Streaming_GlobalFallback_False()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = false };

            Assert.IsFalse(policy.IsStreamingEnabled("AnyRole", settings));
        }

        [Test]
        public void AgentMemoryPolicy_Streaming_PerRoleOverride_WinsOverGlobal()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = false };

            policy.SetStreamingEnabled("FastChat", true);

            Assert.IsTrue(policy.IsStreamingEnabled("FastChat", settings),
                "Per-role override must win over global settings");
            Assert.IsFalse(policy.IsStreamingEnabled("OtherRole", settings),
                "Other roles should follow global");
        }

        [Test]
        public void AgentMemoryPolicy_Streaming_PerRoleDisable_WinsOverGlobal()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = true };

            policy.SetStreamingEnabled("StrictJsonRole", false);

            Assert.IsFalse(policy.IsStreamingEnabled("StrictJsonRole", settings));
            Assert.IsTrue(policy.IsStreamingEnabled("AnyOther", settings));
        }

        [Test]
        public void AgentMemoryPolicy_Streaming_ClearOverride_FallsBackToGlobal()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = true };

            policy.SetStreamingEnabled("Role", false);
            Assert.IsFalse(policy.IsStreamingEnabled("Role", settings));

            policy.SetStreamingEnabled("Role", null);
            Assert.IsTrue(policy.IsStreamingEnabled("Role", settings),
                "null override should clear the per-role value");
        }

        [Test]
        public void AgentBuilder_WithStreaming_RegistersInPolicy()
        {
            CoreAISettings.ResetOverrides();
            var policy = new AgentMemoryPolicy();
            var settings = new TestCoreAISettings { EnableStreaming = true };

            new AgentBuilder("SilentRole").WithStreaming(false).Build().ApplyToPolicy(policy);
            new AgentBuilder("NoisyRole").WithStreaming(true).Build().ApplyToPolicy(policy);

            Assert.IsFalse(policy.IsStreamingEnabled("SilentRole", settings));
            Assert.IsTrue(policy.IsStreamingEnabled("NoisyRole", settings));
        }

        [Test]
        public void CoreAISettings_Static_EnableStreaming_Default_True()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;

            Assert.IsTrue(CoreAISettings.EnableStreaming, "Default should be true");

            CoreAISettings.EnableStreaming = false;
            Assert.IsFalse(CoreAISettings.EnableStreaming);

            CoreAISettings.ResetOverrides();
            Assert.IsTrue(CoreAISettings.EnableStreaming, "ResetOverrides should restore default");
        }

        [Test]
        public async Task ILlmClient_DefaultStreamingFallback_Error_ReturnsErrorChunk()
        {
            ILlmClient client = new FakeLlmClient(null, "Connection failed");

            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in client.CompleteStreamingAsync(
                new LlmCompletionRequest { UserPayload = "test" }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual("Connection failed", chunks[0].Error);
        }

        // ===================== Test Helpers =====================

        private class TestSystemPromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _roleId;
            private readonly string _prompt;

            public TestSystemPromptProvider(string roleId, string prompt)
            {
                _roleId = roleId;
                _prompt = prompt;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                if (roleId == _roleId)
                {
                    prompt = _prompt;
                    return true;
                }
                prompt = null;
                return false;
            }
        }

        private class NullUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private class TestCoreAISettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix { get; set; } = "";
            public float Temperature { get; set; } = 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }

        private class FakeLlmClient : ILlmClient
        {
            private readonly string _response;
            private readonly string _error;

            public FakeLlmClient(string response = "OK", string error = null)
            {
                _response = response;
                _error = error;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                if (_error != null)
                    return Task.FromResult(new LlmCompletionResult { Ok = false, Error = _error });

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _response });
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }
        }
    }
}

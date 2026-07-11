using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="AgentBuilder"/> custom-agent configuration.
    /// </summary>
    [TestFixture]
    public sealed class AgentBuilderEditModeTests
    {
        private string _savedUniversalPrefix;

        [SetUp]
        public void SetUp()
        {
            _savedUniversalPrefix = CoreAISettings.UniversalSystemPromptPrefix;
            CoreAISettings.UniversalSystemPromptPrefix = string.Empty;
            CoreAIAgent.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.UniversalSystemPromptPrefix = _savedUniversalPrefix;
            CoreAIAgent.Reset();
        }

        [Test]
        public void Build_AppliesToPolicyByDefault_WhenPolicyIsRegistered()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);

            AgentConfig config = new AgentBuilder("PolicyAwareAgent")
                .WithSystemPrompt("You are a test agent.")
                .WithMemory()
                .Build();

            Assert.IsTrue(policy.HasRole("PolicyAwareAgent"),
                "Build() should register role config on CoreAIAgent.Policy when it exists.");
            Assert.AreEqual(1, policy.GetToolsForRole("PolicyAwareAgent").Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void BuildDetached_DoesNotMutateGlobalPolicy()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);

            _ = new AgentBuilder("DetachedAgent")
                .WithSystemPrompt("You are a test agent.")
                .WithMemory()
                .BuildDetached();

            Assert.IsFalse(policy.HasRole("DetachedAgent"),
                "BuildDetached() should not add role config to CoreAIAgent.Policy.");
        }

        [Test]
        public async Task AskAsync_Fails_WhenCoreAiNotInitialized()
        {
            // With auto-registration, an unregistered role no longer fails on "not registered" — the first
            // Ask registers it into the global policy. It still fails when CoreAI itself was never
            // initialized (no policy), with a message that points at the missing lifetime scope.
            CoreAIAgent.Reset();
            AgentConfig config = new AgentBuilder("UnregisteredAgent")
                .WithSystemPrompt("You are unregistered.")
                .BuildDetached();

            InvalidOperationException ex = null;
            try
            {
                await config.AskAsync("Hello");
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }

            Assert.NotNull(ex, "expected InvalidOperationException");
            StringAssert.Contains("Initialize CoreAI", ex.Message);
        }

        [Test]
        public void AskWithCallback_DoesNotThrow_AndLegacyAskIsObsolete()
        {
            CoreAIAgent.Reset();
            AgentConfig config = new AgentBuilder("CallbackAgent")
                .WithSystemPrompt("You are a callback agent.")
                .BuildDetached();

            // Fire-and-forget convenience must never throw into the caller; failures are logged.
            // Silence the logger so the expected "not initialized" error does not trip Unity's
            // log assertions in EditMode.
            Logging.ILog savedLog = Logging.Log.Instance;
            Logging.Log.Instance = Logging.NullLog.Instance;
            try
            {
                Assert.DoesNotThrow(() => config.AskWithCallback("Hello", _ => { }));
            }
            finally
            {
                Logging.Log.Instance = savedLog;
            }

            MethodInfo askMethod = typeof(AgentConfigExtensions).GetMethod(nameof(AgentConfigExtensions.Ask));
            Assert.IsNotNull(askMethod);
            Assert.IsTrue(askMethod.IsDefined(typeof(ObsoleteAttribute), false),
                "Ask(callback) is a legacy alias and must carry [Obsolete] pointing to AskAsync/AskWithCallback.");
        }

        [Test]
        public void Builder_CreatesBasicAgent_WithDefaults()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("You are a test agent.")
                .Build();

            Assert.AreEqual("TestAgent", config.RoleId);
            Assert.AreEqual("You are a test agent.", config.SystemPrompt);
            Assert.AreEqual(0, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
            Assert.IsTrue(config.UseLlmContextCompaction,
                "Smart compaction should default on for AgentBuilder agents.");
        }

        [Test]
        public void Builder_WithLlmContextCompaction_OverridesDefault()
        {
            AgentConfig config = new AgentBuilder("NoSmart")
                .WithSystemPrompt("x")
                .WithLlmContextCompaction(false)
                .Build();
            Assert.IsFalse(config.UseLlmContextCompaction);
        }

        [Test]
        public void Builder_AddsTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("ToolAgent")
                .WithSystemPrompt("You use tools.")
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_AddsMultipleTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("MultiToolAgent")
                .WithSystemPrompt("You use many tools.")
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(3, config.Tools.Count);
        }

        [Test]
        public void Builder_WithMemory_AddsMemoryTool()
        {
            AgentConfig config = new AgentBuilder("MemoryAgent")
                .WithSystemPrompt("You remember things.")
                .WithMemory()
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_WithWaitTool_AddsWaitTool()
        {
            AgentConfig config = new AgentBuilder("WaitAgent")
                .WithSystemPrompt("You can wait for external state.")
                .WithWaitTool(5d)
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("wait", config.Tools[0].Name);
            Assert.IsTrue(config.Tools[0].AllowDuplicates);
        }

        [Test]
        public void Builder_WithMode_SetsCorrectMode()
        {
            AgentConfig toolsOnly = new AgentBuilder("ToolsOnly")
                .WithMode(AgentMode.ToolsOnly)
                .Build();

            AgentConfig toolsAndChat = new AgentBuilder("ToolsAndChat")
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            AgentConfig chatOnly = new AgentBuilder("ChatOnly")
                .WithMode(AgentMode.ChatOnly)
                .Build();

            Assert.AreEqual(AgentMode.ToolsOnly, toolsOnly.Mode);
            Assert.AreEqual(AgentMode.ToolsAndChat, toolsAndChat.Mode);
            Assert.AreEqual(AgentMode.ChatOnly, chatOnly.Mode);
        }

        [Test]
        public void Builder_FullConfiguration_AllFieldsSet()
        {
            AgentConfig config = new AgentBuilder("FullAgent")
                .WithSystemPrompt("You are a full configured agent.")
                .WithMemory(MemoryToolAction.Append)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            Assert.AreEqual("FullAgent", config.RoleId);
            Assert.AreEqual("You are a full configured agent.", config.SystemPrompt);
            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
        }

        [Test]
        public void Config_ApplyToPolicy_SetsToolsOnPolicy()
        {
            AgentMemoryPolicy policy = new();

            AgentConfig config = new AgentBuilder("PolicyAgent")
                .WithSystemPrompt("Test")
                .WithMemory()
                .Build();

            config.ApplyToPolicy(policy);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("PolicyAgent");
            Assert.Greater(tools.Count, 0, "Should have tools after ApplyToPolicy");
        }

        [Test]
        public void Builder_ChainCalls_ReturnsSameBuilder()
        {
            AgentBuilder builder = new("ChainAgent");

            AgentBuilder result1 = builder.WithSystemPrompt("Test");
            AgentBuilder result2 = builder.WithMemory();
            AgentBuilder result3 = builder.WithMode(AgentMode.ChatOnly);

            Assert.AreSame(builder, result1);
            Assert.AreSame(builder, result2);
            Assert.AreSame(builder, result3);
        }

        [Test]
        public void ApplyToPolicy_ToolsAndChat_DefaultsStreamingOverrideToTrue()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ChatWithToolsRole")
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsTrue(policy.TryGetStreamingOverride("ChatWithToolsRole", out bool enabled));
            Assert.IsTrue(enabled);
        }

        [Test]
        public void ApplyToPolicy_ChatOnly_LeavesStreamingOnGlobalFallback()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ChatOnlyRole")
                .WithMode(AgentMode.ChatOnly)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsFalse(policy.TryGetStreamingOverride("ChatOnlyRole", out _));
        }

        [Test]
        public void ApplyToPolicy_ExplicitWithStreamingFalse_WinsOverModeDefault()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ExplicitOffRole")
                .WithMode(AgentMode.ToolsAndChat)
                .WithStreaming(false)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsTrue(policy.TryGetStreamingOverride("ExplicitOffRole", out bool enabled));
            Assert.IsFalse(enabled);
        }

        [Test]
        public void ValidateOnBuild_CustomRole_WithoutSystemPrompt_ReportsMissingPrompt()
        {
            AgentBuilder builder = new("CustomNpc")
            {
                SuppressBuildWarnings = true
            };
            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.MissingSystemPrompt), Is.True);
        }

        [Test]
        public void ValidateOnBuild_BuiltInRole_WithoutSystemPrompt_SkipsMissingPrompt()
        {
            AgentBuilder builder = new(BuiltInAgentRoleIds.Creator)
            {
                SuppressBuildWarnings = true
            };
            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.MissingSystemPrompt), Is.False);
        }

        [Test]
        public void ValidateOnBuild_ToolsOnlyWithoutTools_ReportsNoTools()
        {
            AgentBuilder builder = new("Npc")
            {
                SuppressBuildWarnings = true
            };
            builder.WithMode(AgentMode.ToolsOnly);

            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.NoToolsForToolMode), Is.True);
        }

        [Test]
        public void ValidateOnBuild_CompactionTrue_GlobalGateOff_ReportsCompactionGate()
        {
            try
            {
                // Ensure static override is deterministic for the test body
                CoreAISettings.ResetOverrides();
                CoreAISettings.EnableLlmContextCompaction = false;

                AgentBuilder builder = new("CompactionRole")
                {
                    SuppressBuildWarnings = true
                };
                builder.WithSystemPrompt("x");
                builder.WithMode(AgentMode.ChatOnly);
                builder.WithLlmContextCompaction(true);

                IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

                Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.CompactionGateDisabled), Is.True);
            }
            finally
            {
                CoreAISettings.ResetOverrides();
            }
        }
    }
}
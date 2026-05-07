using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Play Mode tests for <see cref="SkillSet"/> — self-service skill pattern.
    /// Exercises the full pipeline: AgentBuilder → ApplyToPolicy → AiOrchestrator.RunTaskAsync.
    /// Validates: catalog injection, read_skill tool registration, all skill tools available.
    /// </summary>
    public sealed class SkillSetPlayModeTests
    {
        // Captures the LlmCompletionRequest sent to the LLM
        private sealed class CaptureLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }
            public int CompleteCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken ct = default)
            {
                LastRequest = request;
                CompleteCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand c) { }
        }

        private sealed class Telemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot() => new();
        }

        private sealed class PromptSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "";
                return true;
            }
        }

        private sealed class PromptUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string t)
            {
                t = "{hint}";
                return true;
            }
        }

        private sealed class StubMem : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState s) { s = null; return false; }
            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persist = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int max = 0) => Array.Empty<ChatMessage>();
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public bool EnableLlmContextCompaction => false;
            public int MaxLuaRepairRetries => 0;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 1;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => false;
        }

        private static DelegateLlmTool MakeTool(string name) =>
            new(name, $"Test tool: {name}", new Action(() => { }));

        private AiOrchestrator BuildOrch(string roleId, CaptureLlmClient llm,
            AgentMemoryPolicy policy, StubSettings settings, StubMem mem)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                llm,
                new Sink(),
                new Telemetry(),
                new AiPromptComposer(new PromptSys(), new PromptUsr(), null, null, policy, settings),
                mem,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                null, null, null);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Self-Service: catalog + read_skill + all tools available
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Self-service: system prompt contains catalog (name + description) but NOT full instructions.
        /// read_skill tool is registered. All skill tools are available.
        /// </summary>
        [UnityTest]
        public IEnumerator SelfService_SystemPromptContainsCatalog_NotFullInstructions()
        {
            SkillSet quizSkill = new("Quiz",
                "Generate quiz questions and verify answers",
                "Detailed: call spawn_quiz with topic and difficulty. Then check_answer.",
                MakeTool("spawn_quiz"), MakeTool("check_answer"));
            SkillSet lessonSkill = new("Lesson",
                "Explain topics step by step",
                "Detailed: call advance_lesson when done. Use show_example for code.",
                MakeTool("advance_lesson"), MakeTool("show_example"));

            const string roleId = "pm_selfservice_catalog";
            AgentConfig config = new AgentBuilder(roleId)
            {
                SuppressBuildWarnings = true
            }
                .WithSystemPrompt("You are a teacher.")
                .WithSkill(quizSkill)
                .WithSkill(lessonSkill)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            StubSettings settings = new();
            CaptureLlmClient llm = new();
            StubMem mem = new();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);
            AiOrchestrator orch = BuildOrch(roleId, llm, policy, settings, mem);

            // Act: no AllowedToolNames — model sees everything + catalog
            Task t = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = roleId,
                Hint = "quiz me on variables"
            });
            yield return PlayModeTestAwait.WaitTask(t, 10f, "self-service catalog");

            // Assert: catalog in prompt
            Assert.IsNotNull(llm.LastRequest);
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("Available Skills"),
                "Catalog header should be in system prompt.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("Quiz"),
                "Quiz should appear in catalog.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("Lesson"),
                "Lesson should appear in catalog.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("read_skill"),
                "Catalog should mention read_skill.");

            // Assert: full instructions NOT in prompt
            Assert.That(llm.LastRequest.SystemPrompt, Does.Not.Contain("Detailed: call spawn_quiz"),
                "Full quiz instructions should NOT be in system prompt.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Not.Contain("Detailed: call advance_lesson"),
                "Full lesson instructions should NOT be in system prompt.");

            // Assert: descriptions ARE in prompt
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("Generate quiz questions"),
                "Quiz description should be in catalog.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("Explain topics step by step"),
                "Lesson description should be in catalog.");
        }

        /// <summary>
        /// Self-service: only meta-tools (read_skill + call_skill_tool) are registered.
        /// Individual skill tools are NOT in the tool list — they go through call_skill_tool proxy.
        /// </summary>
        [UnityTest]
        public IEnumerator SelfService_OnlyMetaTools_NotSkillToolsDirectly()
        {
            SkillSet a = new("SkillA", "Skill A desc", "A instructions.", MakeTool("tool_a1"), MakeTool("tool_a2"));
            SkillSet b = new("SkillB", "Skill B desc", "B instructions.", MakeTool("tool_b1"));

            const string roleId = "pm_selfservice_tools";
            AgentConfig config = new AgentBuilder(roleId)
            {
                SuppressBuildWarnings = true
            }
                .WithSkills(a, b)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            StubSettings settings = new();
            CaptureLlmClient llm = new();
            StubMem mem = new();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);
            AiOrchestrator orch = BuildOrch(roleId, llm, policy, settings, mem);

            Task t = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = roleId,
                Hint = "test"
            });
            yield return PlayModeTestAwait.WaitTask(t, 10f, "self-service meta tools only");

            // Assert: ONLY meta-tools, not individual skill tools
            Assert.IsNotNull(llm.LastRequest?.Tools);
            HashSet<string> names = new();
            foreach (ILlmTool tool in llm.LastRequest.Tools)
            {
                names.Add(tool.Name);
            }

            Assert.IsTrue(names.Contains("read_skill"), "read_skill meta-tool should be registered.");
            Assert.IsTrue(names.Contains("call_skill_tool"), "call_skill_tool proxy should be registered.");
            Assert.AreEqual(2, names.Count, "Only 2 meta-tools should be sent to the model.");

            // Skill tools should NOT be in the tool list
            Assert.IsFalse(names.Contains("tool_a1"), "Skill tool_a1 should NOT be sent directly.");
            Assert.IsFalse(names.Contains("tool_a2"), "Skill tool_a2 should NOT be sent directly.");
            Assert.IsFalse(names.Contains("tool_b1"), "Skill tool_b1 should NOT be sent directly.");
        }

        /// <summary>
        /// System prompt should reference call_skill_tool in the catalog.
        /// </summary>
        [UnityTest]
        public IEnumerator SelfService_CatalogMentionsCallSkillTool()
        {
            SkillSet quiz = new("Quiz", "Quiz desc", "Quiz inst.", MakeTool("spawn_quiz"));

            const string roleId = "pm_selfservice_callref";
            AgentConfig config = new AgentBuilder(roleId)
            {
                SuppressBuildWarnings = true
            }
                .WithSkill(quiz)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            StubSettings settings = new();
            CaptureLlmClient llm = new();
            StubMem mem = new();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);
            AiOrchestrator orch = BuildOrch(roleId, llm, policy, settings, mem);

            Task t = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = roleId,
                Hint = "quiz me"
            });
            yield return PlayModeTestAwait.WaitTask(t, 10f, "catalog references");

            Assert.IsNotNull(llm.LastRequest);
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("call_skill_tool"),
                "Catalog should mention call_skill_tool.");
            Assert.That(llm.LastRequest.SystemPrompt, Does.Contain("read_skill"),
                "Catalog should mention read_skill.");

            // Individual tool names NOT in system prompt
            Assert.That(llm.LastRequest.SystemPrompt, Does.Not.Contain("spawn_quiz"),
                "Individual tool names should NOT be in catalog.");
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI;
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
    /// Play Mode mirror of compaction per-role gates (runs under Unity lifecycle; uses stub LLM only).
    /// </summary>
    public sealed class LlmCompactionPerRolePlayModeTests
    {
        private sealed class FlatTok : ITokenEstimator
        {
            private readonly int _n;

            public FlatTok(int n)
            {
                _n = Math.Max(1, n);
            }

            public int EstimateText(string text)
            {
                return _n;
            }
        }

        private sealed class FixedHistoryBudgetPolicy : IContextBudgetPolicy
        {
            private readonly int _h;

            public FixedHistoryBudgetPolicy(int h)
            {
                _h = Math.Max(1, h);
            }

            public ContextBudget Compute(ContextBudgetRequest req, ITokenEstimator e)
            {
                return new ContextBudget(8192, 256, 50, _h, 0);
            }
        }

        private sealed class SplitCountingLlm : ILlmClient
        {
            private readonly ILlmClient _inner;

            public int CompactionCompletes;

            /// <summary>Last auxiliary compaction request only (role <see cref="BuiltInAgentRoleIds.ContextCompactionAux"/>).</summary>
            public LlmCompletionRequest LastCompactionRequest { get; private set; }

            public SplitCountingLlm(ILlmClient inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken ct = default)
            {
                if (string.Equals(
                        request.AgentRoleId,
                        BuiltInAgentRoleIds.ContextCompactionAux,
                        StringComparison.Ordinal))
                {
                    CompactionCompletes++;
                    LastCompactionRequest = request;
                    return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "rollup" });
                }

                return _inner.CompleteAsync(request, ct);
            }
        }

        private sealed class CaptureLlm : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken ct = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class Mem : IAgentMemoryStore
        {
            public readonly List<ChatMessage> Rows = new();
            public readonly List<(string Role, string Content, bool Persist)> Appended = new();

            public bool TryLoad(string roleId, out AgentMemoryState s)
            {
                s = null;
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
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                Appended.Add((role, content, persistToDisk));
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return maxMessages > 0 && Rows.Count > maxMessages
                    ? Rows.GetRange(Rows.Count - maxMessages, maxMessages).ToArray()
                    : Rows.ToArray();
            }
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand c)
            {
            }
        }

        private sealed class Telemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
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

        private sealed class StubSet : ICoreAISettings
        {
            public bool EnableLlmContextCompaction => true;
            public int MaxLuaRepairRetries => 1;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 60f;
            public int MaxLlmRequestRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 2;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => false;
        }

        private static AgentMemoryPolicy MakePolicy(string roleId)
        {
            AgentMemoryPolicy p = new();
            p.DisableMemoryTool(roleId);
            p.SetToolsForRole(roleId, Array.Empty<ILlmTool>());
            p.ConfigureChatHistory(roleId, true, 8192, false, 50);
            return p;
        }

        private static void Seed(Mem mem, int count)
        {
            for (int i = 0; i < count; i++)
            {
                mem.Rows.Add(new ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"{i}: ".PadRight(36, '-')
                });
            }
        }

        [UnityTest]
        public IEnumerator Orchestrator_PlayMode_ChatSource_EnablesProgrammerShortTermHistory()
        {
            StubSet settings = new();
            CaptureLlm llm = new();
            Mem mem = new();
            mem.Rows.Add(new ChatMessage
            {
                Role = "user",
                Content = "{\"hint\":\"отвечай на русском\",\"ai_task_source\":\"Chat\"}"
            });
            mem.Rows.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Понял, буду отвечать на русском языке."
            });

            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool(BuiltInAgentRoleIds.Programmer);
            policy.SetToolsForRole(BuiltInAgentRoleIds.Programmer, Array.Empty<ILlmTool>());
            AiOrchestrator orchestrator = new(
                new SoloAuthorityHost(),
                llm,
                new Sink(),
                new Telemetry(),
                new AiPromptComposer(new PromptSys(), new PromptUsr(), null, null, policy, settings),
                mem,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings);

            Task task = orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "какие моды есть",
                SourceTag = "Chat"
            });
            yield return PlayModeTestAwait.WaitTask(task, 60f, "programmer chat history");

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Assert.AreEqual(2, llm.LastRequest.ChatHistory.Count);
            StringAssert.Contains("отвечай на русском", llm.LastRequest.ChatHistory[0].Text);
            Assert.AreEqual(2, mem.Appended.Count);
            Assert.IsFalse(mem.Appended[0].Persist);
            Assert.IsFalse(mem.Appended[1].Persist);
        }

        [UnityTest]
        public IEnumerator Orchestrator_PlayMode_PerRole_CompactionGate()
        {
            const string smartRole = "pm_smart_compact";
            StubSet settings = new();
            ITokenEstimator compactEst = new FlatTok(10);

            StubLlmClient stubMain = new();
            SplitCountingLlm counting1 = new(stubMain);
            Mem mem1 = new();
            Seed(mem1, 9);
            AgentMemoryPolicy policy1 = MakePolicy(smartRole);
            AiOrchestrator o1 = new(
                new SoloAuthorityHost(),
                counting1,
                new Sink(),
                new Telemetry(),
                new AiPromptComposer(new PromptSys(), new PromptUsr(), null, null, policy1, settings),
                mem1,
                policy1,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                ConversationContextManagerFactories.Create(
                    true,
                    new InMemoryConversationSummaryStore(),
                    compactEst,
                    counting1,
                    null),
                null,
                new FixedHistoryBudgetPolicy(28));

            Task t1 = o1.RunTaskAsync(new AiTaskRequest { RoleId = smartRole, Hint = "playmode" });
            yield return PlayModeTestAwait.WaitTask(t1, 120f, "smart role compaction");

            Assert.GreaterOrEqual(counting1.CompactionCompletes, 1,
                "Compaction LLM expected for roles with smart compaction.");
            Assert.IsNotNull(counting1.LastCompactionRequest);
            Assert.IsNull(counting1.LastCompactionRequest.ChatHistory,
                "Compaction auxiliary call must not replay MEAI chat tail.");
            Assert.AreEqual(
                LlmContextCompactionOptions.DefaultSystemPrompt,
                counting1.LastCompactionRequest.SystemPrompt,
                "Orchestrator main-role system must not be substituted for compaction system.");
            StringAssert.StartsWith("## Prior rolling summary",
                counting1.LastCompactionRequest.UserPayload.TrimStart());

            StubLlmClient stub2 = new();
            SplitCountingLlm counting2 = new(stub2);
            Mem mem2 = new();
            Seed(mem2, 9);
            string prog = BuiltInAgentRoleIds.Programmer;
            AgentMemoryPolicy policy2 = MakePolicy(prog);
            AiOrchestrator o2 = new(
                new SoloAuthorityHost(),
                counting2,
                new Sink(),
                new Telemetry(),
                new AiPromptComposer(new PromptSys(), new PromptUsr(), null, null, policy2, settings),
                mem2,
                policy2,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                ConversationContextManagerFactories.Create(
                    true,
                    new InMemoryConversationSummaryStore(),
                    compactEst,
                    counting2,
                    null),
                null,
                new FixedHistoryBudgetPolicy(28));

            Task t2 = o2.RunTaskAsync(new AiTaskRequest { RoleId = prog, Hint = "playmode" });
            yield return PlayModeTestAwait.WaitTask(t2, 120f, "programmer no compaction");

            Assert.AreEqual(0, counting2.CompactionCompletes, "Programmer should not invoke auxiliary compaction.");
        }
    }
}
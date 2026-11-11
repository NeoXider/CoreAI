using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Engine-free composition for role, tool-profile and prompt tests: a real <see cref="AiOrchestrator"/> over a
    /// real policy and prompt composer, with an LLM client that records every request it is sent.
    /// </summary>
    internal static class AgentRoleTestHarness
    {
        internal static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            AgentMemoryPolicy policy,
            IAgentMemoryStore memory = null,
            IAgentSystemPromptProvider systemPrompts = null,
            ICoreAISettings settings = null)
        {
            ICoreAISettings effectiveSettings = settings ?? new CoreAISettingsOptions { UniversalSystemPromptPrefix = "" };
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                llm,
                new NullCommandSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(systemPrompts ?? new NoSystemPrompts(), new NoUserTemplates(), null, null,
                    policy, effectiveSettings),
                memory ?? new NullAgentMemoryStore(),
                policy,
                null,
                null,
                effectiveSettings,
                new LocalActorIdentityProvider("agent-role-tests"));
        }

        internal static string[] ToolNames(IReadOnlyList<ILlmTool> tools)
        {
            return (tools ?? Array.Empty<ILlmTool>()).Select(tool => tool?.Name ?? "<null>").ToArray();
        }

        internal static string[] ToolNames(LlmCompletionRequest request)
        {
            return ToolNames(request?.Tools);
        }

        /// <summary>User and assistant turns the request carries from earlier turns (system tail excluded).</summary>
        internal static int HistoryTurnCount(LlmCompletionRequest request)
        {
            return request?.ChatHistory?.Count(message => message.Role != MEAI.ChatRole.System) ?? 0;
        }

        internal static ILlmTool Stub(string name)
        {
            return new DelegateLlmTool(name, "Stand-in for " + name + ".", (Func<string, string>)(input => name));
        }

        internal sealed class CapturingLlmClient : ILlmClient
        {
            public List<LlmCompletionRequest> Requests { get; } = new();

            public LlmCompletionRequest LastRequest => Requests.Count == 0 ? null : Requests[Requests.Count - 1];

            public string Reply { get; set; } = "ok";

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = Reply });
            }
        }

        internal sealed class RecordingLog : ILog
        {
            public List<string> Warnings { get; } = new();
            public List<string> Errors { get; } = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
                Warnings.Add(message);
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message);
            }
        }

        private sealed class NullCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class NoSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = null;
                return false;
            }
        }

        private sealed class NoUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }
    }
}

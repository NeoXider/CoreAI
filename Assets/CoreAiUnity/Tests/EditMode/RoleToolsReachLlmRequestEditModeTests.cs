using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the path from <see cref="AgentMemoryPolicy"/> registration to <see cref="LlmCompletionRequest.Tools"/>:
    /// a role composed like the production Programmer (custom tools plus skills) reaches the LLM client with
    /// every tool, a request allowlist still restricts, and a memory tool the client cannot bind does not
    /// take the other tools with it.
    /// </summary>
    [TestFixture]
    public sealed class RoleToolsReachLlmRequestEditModeTests
    {
        private const string Role = "CastleProgrammer";

        [Test]
        public async Task RoleWithCustomToolsAndSkills_CarriesEveryToolIntoTheRequest()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, BuildProgrammerLikePolicy());

            await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = Role, Hint = "build a castle" },
                CancellationToken.None);

            Assert.IsNotNull(llm.LastRequest, "the orchestrator must reach the LLM client");
            CollectionAssert.AreEquivalent(
                new[] { "memory", "execute_lua", "manage_mods", "read_skill", "call_skill_tool" },
                ToolNames(llm.LastRequest));
        }

        [Test]
        public async Task AllowedToolNames_RestrictsTheRequestToTheAllowlist()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, BuildProgrammerLikePolicy());

            await orchestrator.RunTaskAsync(
                new AiTaskRequest
                {
                    RoleId = Role,
                    Hint = "build a castle",
                    AllowedToolNames = new[] { "execute_lua" }
                },
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "execute_lua" }, ToolNames(llm.LastRequest));
        }

        [Test]
        public async Task AllowedToolNames_NamingASkillTool_KeepsOnlyTheSkillEntryPoints()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, BuildProgrammerLikePolicy());

            await orchestrator.RunTaskAsync(
                new AiTaskRequest
                {
                    RoleId = Role,
                    Hint = "build a tower",
                    AllowedToolNames = new[] { "build_tower" }
                },
                CancellationToken.None);

            CollectionAssert.AreEquivalent(
                new[] { "read_skill", "call_skill_tool" },
                ToolNames(llm.LastRequest));
        }

#if COREAI_LLM
        [Test]
        public async Task UnbindableMemoryTool_DoesNotRemoveTheOtherToolsFromTheRequest()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            List<ILlmTool> tools = new()
            {
                new MemoryLlmTool(),
                new ExplicitFunctionTool("execute_lua"),
                new DelegateLlmTool("manage_mods", "Manages mods.", (Func<string, string>)(action => "ok"))
            };
            LogAssert.Expect(LogType.Warning, new Regex("IAgentMemoryStore is null"));

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = Role,
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = tools
            }, CancellationToken.None);

            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(inner.LastOptions?.Tools, "the other tools must still be sent to the provider");
            CollectionAssert.AreEquivalent(
                new[] { "execute_lua", "manage_mods" },
                inner.LastOptions.Tools.Select(tool => tool.Name).ToArray());
        }
#endif

        private static string[] ToolNames(LlmCompletionRequest request)
        {
            return (request?.Tools ?? Array.Empty<ILlmTool>()).Select(tool => tool.Name).ToArray();
        }

        /// <summary>Custom tools plus a skill, the same shape CoreAiModsInstaller gives the Programmer.</summary>
        private static AgentMemoryPolicy BuildProgrammerLikePolicy()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(Role,
                new DelegateLlmTool("execute_lua", "Runs Lua.", (Func<string, string>)(code => "ran")));
            policy.AddToolForRole(Role,
                new DelegateLlmTool("manage_mods", "Manages mods.", (Func<string, string>)(action => "ok")));
            policy.AddSkillForRole(Role, new SkillSet(
                "Tower Building",
                "Builds towers.",
                "Call build_tower.",
                new DelegateLlmTool("build_tower", "Builds a tower.", (Func<string, string>)(name => "built " + name))));
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(ILlmClient llm, AgentMemoryPolicy policy)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                llm,
                new NullSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new NullSystemPrompts(), new NullUserTemplates(), null),
                new NullAgentMemoryStore(),
                policy,
                null,
                null,
                new StubCoreSettings(),
                new LocalActorIdentityProvider("role-tools-reach-request-test"));
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class NullSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        private sealed class NullUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private sealed class StubCoreSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 1;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 1;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

#if COREAI_LLM
        private sealed class ExplicitFunctionTool : ILlmTool, IAIFunctionLlmTool
        {
            public ExplicitFunctionTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "Explicit MEAI function test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => "{\"Success\":true}"),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }
        }

        private sealed class CapturingChatClient : MEAI.IChatClient
        {
            public MEAI.ChatOptions LastOptions { get; private set; }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
#endif
    }
}

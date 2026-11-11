using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Messaging;
using CoreAI.Session;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Orchestrator mode through the chat service the chat panel and the overlay share, in the player loop, with
    /// a scripted model in place of a provider: a chat turn plans, declares three tasks and delegates them, and
    /// finishes; and the same plan stopped mid-way (Stop as both chat surfaces send it) ends, empties the queue,
    /// cancels the instruction waiting behind it, and runs nothing further.
    /// </summary>
    public sealed class OrchestratorModeChatPlayModeTests
    {
        private const int FrameBudget = 1200;

        [UnityTest]
        public IEnumerator ChatTurn_InOrchestratorMode_RunsAThreeTaskPlanToTheEnd()
        {
            Harness harness = new(holdTask: 0);

            Task<string> turn = harness.Chat.SendMessageAsync(ChatTurn("build a castle"));
            yield return AwaitTask(turn);

            Assert.IsTrue(turn.Status == TaskStatus.RanToCompletion, turn.Exception?.ToString());
            Assert.AreEqual("castle built", turn.Result);
            OrchestrationPlanSnapshot plan = harness.SingleSession().Current.Plan;
            Assert.AreEqual(OrchestrationPlanState.Completed, plan.State);
            Assert.AreEqual(3, plan.CompletedTasks);
            Assert.AreEqual(3, harness.Model.SubAgentRequests.Count);
            Assert.AreEqual("done 3/3", OrchestrationStatusText.FormatLine(harness.SingleSession().Current));
        }

        [UnityTest]
        public IEnumerator StopMidPlan_EndsThePlan_EmptiesTheQueue_AndNothingFurtherRuns()
        {
            Harness harness = new(holdTask: 2);

            Task<string> lead = harness.Chat.SendMessageAsync(ChatTurn("build a castle"));
            yield return WaitUntil(() => harness.Model.SubAgentRequests.Count == 2);
            Task<string> queued = harness.Chat.SendMessageAsync(ChatTurn("then add a moat"));
            yield return WaitUntil(() => harness.Driver.WaitingTurns == 1);
            OrchestratorSession session = harness.SingleSession();
            Assert.AreEqual(1, session.Queue.Snapshot().Count, "The second instruction waits behind the plan.");

            harness.Chat.StopAgent(BuiltInAgentRoleIds.Creator, Harness.Actor());
            yield return AwaitTask(lead);
            yield return AwaitTask(queued);
            for (int i = 0; i < 30; i++)
            {
                yield return null;
            }

            Assert.AreNotEqual(TaskStatus.RanToCompletion, queued.Status, "The waiting instruction never runs.");
            Assert.IsEmpty(session.Queue.Snapshot());
            Assert.AreEqual(0, harness.Driver.WaitingTurns);
            Assert.AreEqual(OrchestrationPlanState.Cancelled, session.LastPlan.Snapshot().State);
            Assert.AreEqual(2, harness.Model.SubAgentRequests.Count, "The third task never starts.");
            Assert.AreEqual(1, harness.Model.LeadRequests.Count, "No queued plan starts after Stop.");
        }

        private static AiTaskRequest ChatTurn(string text)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = text,
                SourceTag = OrchestratorChatDriver.ChatSourceTag,
                ActorContext = Harness.Actor()
            };
        }

        private static IEnumerator AwaitTask(Task task)
        {
            int frames = 0;
            while (!task.IsCompleted && frames++ < FrameBudget)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, "Task did not complete within the test frame budget.");
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            int frames = 0;
            while (!condition() && frames++ < FrameBudget)
            {
                yield return null;
            }

            Assert.IsTrue(condition(), "Condition not reached within the test frame budget.");
        }

        private sealed class Harness
        {
            private const string ActorId = "player-1";

            public Harness(int holdTask)
            {
                Model = new ScriptedModel { HoldTask = holdTask };
                AgentMemoryPolicy policy = new();
                InMemoryAgentMemoryStore memory = new();
                AiOrchestrator orchestrator = new(
                    new SoloAuthorityHost(),
                    Model,
                    new NoOpCommandSink(),
                    new SessionTelemetryCollector(),
                    new AiPromptComposer(new StubSystemPrompts(), new StubUserTemplates(), null),
                    memory,
                    policy,
                    null,
                    null,
                    new CoreAISettingsOptions(),
                    Identity);
                QueuedAiOrchestrator queue = new(orchestrator, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });
                Driver = new OrchestratorChatDriver(new OrchestratorSessionRegistry(
                    new OrchestratorModeOptions { Enabled = true },
                    new SubAgentRegistry(new[]
                    {
                        new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder, "Builds structures.", null),
                        new SubAgentDefinition("scripter", BuiltInAgentRoleIds.Programmer, "Writes game logic.", null)
                    }),
                    orchestrator));
                Chat = new CoreAiChatService(queue, policy, null, memory, null, null, null, Identity)
                {
                    OrchestratorMode = Driver
                };
            }

            public ScriptedModel Model { get; }

            public OrchestratorChatDriver Driver { get; }

            public CoreAiChatService Chat { get; }

            private static readonly LocalActorIdentityProvider Identity = new(ActorId);

            /// <summary>WHY one provider: its session id is part of the conversation key, as a host's identity is stable.</summary>
            public static ActorContext Actor()
            {
                return Identity.GetActorContext(BuiltInAgentRoleIds.Creator);
            }

            public OrchestratorSession SingleSession()
            {
                Assert.AreEqual(1, Driver.Sessions.Sessions.Count);
                return Driver.Sessions.Sessions[0];
            }
        }

        /// <summary>
        /// The scripted model: on a lead request it calls the request's own tools as a provider's tool loop would
        /// (declare three tasks, then delegate them in order) and reports; on a sub-agent request it answers at
        /// once, or holds task <see cref="HoldTask"/> until its run is cancelled.
        /// </summary>
        private sealed class ScriptedModel : ILlmClient
        {
            private readonly object _lock = new();

            public int HoldTask { get; set; }

            public List<LlmCompletionRequest> LeadRequests { get; } = new();

            public List<LlmCompletionRequest> SubAgentRequests { get; } = new();

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                ILlmTool delegateTool = Find(request, DelegateTaskTool.DefaultName);
                if (delegateTool == null)
                {
                    int ordinal;
                    lock (_lock)
                    {
                        SubAgentRequests.Add(request);
                        ordinal = SubAgentRequests.Count;
                    }

                    if (ordinal == HoldTask)
                    {
                        TaskCompletionSource<bool> never = new();
                        using (cancellationToken.Register(() => never.TrySetCanceled()))
                        {
                            await never.Task;
                        }
                    }

                    return new LlmCompletionResult { Ok = true, Content = "done " + ordinal };
                }

                lock (_lock)
                {
                    LeadRequests.Add(request);
                }

                AIFunction declare =
                    ((IAIFunctionLlmTool)Find(request, DeclareTasksTool.DefaultName)).CreateAIFunction();
                await declare.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tasks"] = "builder: Build four walls\nbuilder: Add two towers\nscripter: Open the gate on touch"
                }), cancellationToken);
                AIFunction delegation = ((IAIFunctionLlmTool)delegateTool).CreateAIFunction();
                string[] subAgents = { "builder", "builder", "scripter" };
                string[] tasks = { "Build four walls", "Add two towers", "Open the gate on touch" };
                for (int i = 0; i < tasks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await delegation.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                    {
                        ["subagent"] = subAgents[i],
                        ["task"] = tasks[i],
                        ["task_id"] = "t" + (i + 1)
                    }), cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new LlmCompletionResult { Ok = true, Content = "castle built" };
            }

            private static ILlmTool Find(LlmCompletionRequest request, string name)
            {
                foreach (ILlmTool tool in request.Tools ?? Array.Empty<ILlmTool>())
                {
                    if (tool.Name == name)
                    {
                        return tool;
                    }
                }

                return null;
            }
        }

        private sealed class StubSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "You are " + roleId + ".";
                return true;
            }
        }

        private sealed class StubUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class NoOpCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}

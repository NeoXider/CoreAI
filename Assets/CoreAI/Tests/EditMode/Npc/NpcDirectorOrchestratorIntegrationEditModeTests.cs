using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// The NPC director over the real <see cref="AiOrchestrator"/> and <see cref="QueuedAiOrchestrator"/> with a
    /// scripted provider: one agent role keeps a separate history per NPC, and NPC chatter never holds the
    /// player's build turn beyond its queue slot.
    /// </summary>
    [TestFixture]
    public sealed class NpcDirectorOrchestratorIntegrationEditModeTests
    {
        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public async Task OneNpcRole_KeepsASeparateHistoryPerNpc()
        {
            ProviderScript provider = new();
            provider.Reply = haystack => haystack.Contains("APPLE") && haystack.Contains("remember")
                ? "I will remember APPLE."
                : "Hello, traveller.";
            using QueuedAiOrchestrator queue = CreateQueue(provider, 2);
            using NpcDirector director = new(queue, null, new NpcDirectorOptions(), new ManualNpcClock());
            List<NpcSaid> said = new();
            director.NpcSaid += s => said.Add(s);
            director.Add(NpcTestWorld.Npc("bob", "Bob"));
            director.Add(NpcTestWorld.Npc("alice", "Alice"));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, remember the word APPLE", null));
            await PumpUntil(director, () => said.Count == 1, "Bob's first answer");
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Alice, hello", null));
            await PumpUntil(director, () => said.Count == 2, "Alice's answer");
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, what was the word?", null));
            await PumpUntil(director, () => said.Count == 3, "Bob's second answer");

            Assert.AreEqual(3, provider.Requests.Count);
            StringAssert.DoesNotContain("APPLE", provider.HistoryOf(1), "Alice never sees Bob's conversation.");
            StringAssert.Contains("APPLE", provider.HistoryOf(2), "Bob remembers his own conversation.");
            StringAssert.DoesNotContain("Alice, hello", provider.HistoryOf(2), "Bob never sees Alice's.");
        }

        [Test]
        public async Task NpcChatter_NeverHoldsTheBuildTurnBeyondItsQueueSlot()
        {
            ProviderScript provider = new() { HoldNpcTurns = true };
            using QueuedAiOrchestrator queue = CreateQueue(provider, 2);
            using NpcDirector director = new(queue, null, new NpcDirectorOptions { MaxConcurrentTurns = 1 },
                new ManualNpcClock());
            AddThreeNpcs(director);

            AddressAll(director);
            await PumpUntil(director, () => provider.NpcCallsStarted == 1, "the first NPC call");
            Task<LlmCompletionResult> build = queue.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "build a house",
                Priority = 0
            });
            Task finished = await Task.WhenAny(build, Task.Delay(5000));

            Assert.AreSame(build, finished, "The build turn ran while NPC calls were still held.");
            Assert.IsTrue((await build).Ok);
            Assert.AreEqual(1, provider.NpcCallsStarted, "Only one NPC call ever occupied a queue slot.");
            provider.ReleaseNpcTurns();
        }

        [Test]
        public async Task WithoutTheNpcTurnLimit_NpcChatterFillsTheQueue_AndTheBuildTurnWaits()
        {
            ProviderScript provider = new() { HoldNpcTurns = true };
            using QueuedAiOrchestrator queue = CreateQueue(provider, 2);
            using NpcDirector director = new(queue, null, new NpcDirectorOptions { MaxConcurrentTurns = 0 },
                new ManualNpcClock());
            AddThreeNpcs(director);

            AddressAll(director);
            await PumpUntil(director, () => provider.NpcCallsStarted == 2, "two NPC calls");
            Task<LlmCompletionResult> build = queue.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "build a house",
                Priority = 0
            });
            Task early = await Task.WhenAny(build, Task.Delay(300));

            Assert.AreNotSame(build, early, "Both slots are held by NPC chatter, so the build turn waits.");
            provider.ReleaseNpcTurns();
            Task finished = await Task.WhenAny(build, Task.Delay(5000));
            Assert.AreSame(build, finished);
            await PumpUntil(director, () => provider.NpcCallsStarted == 3, "the third NPC call");
        }

        /// <summary>
        /// WHY this twin exists: the queue picks the next ACTOR round-robin and orders by priority only inside one
        /// actor, so the NPC priority keeps chatter behind the build turn when both run as the same player (the
        /// single-player case) and the turn limit above is what protects the build turn across actors.
        /// </summary>
        [Test]
        public async Task WithinThePlayersOwnActor_TheBuildTurnRanksAheadOfAWaitingNpcTurn()
        {
            ProviderScript provider = new() { HoldNpcTurns = true };
            using QueuedAiOrchestrator queue = CreateQueue(provider, 2);
            using NpcDirector director = new(queue, null, new NpcDirectorOptions { MaxConcurrentTurns = 0 },
                new ManualNpcClock());
            AddThreeNpcs(director);
            ActorContext player = new LocalActorIdentityProvider("player-1").GetActorContext(BuiltInAgentRoleIds.AiNpc);
            foreach (string name in new[] { "Bob", "Alice", "Carl" })
            {
                NpcHeardLine line = NpcHeardLine.Chat("player-1", "Steve", name + ", hi", null);
                line.SpeakerActor = player;
                director.Hear(line);
            }

            await PumpUntil(director, () => provider.NpcCallsStarted == 2, "two NPC calls");
            Task<LlmCompletionResult> build = queue.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "build a house",
                Priority = 0
            });
            provider.ReleaseOneNpcTurn();
            Task finished = await Task.WhenAny(build, Task.Delay(5000));

            Assert.AreSame(build, finished);
            provider.ReleaseNpcTurns();
            await PumpUntil(director, () => provider.NpcCallsStarted == 3, "the third NPC call");
            CollectionAssert.AreEqual(
                new[]
                {
                    BuiltInAgentRoleIds.AiNpc, BuiltInAgentRoleIds.AiNpc, BuiltInAgentRoleIds.Creator,
                    BuiltInAgentRoleIds.AiNpc
                },
                provider.CallOrder(),
                "The freed slot went to the build turn, not to the waiting NPC turn of the same player.");
        }

        private static void AddThreeNpcs(NpcDirector director)
        {
            director.Add(NpcTestWorld.Npc("bob", "Bob"));
            director.Add(NpcTestWorld.Npc("alice", "Alice"));
            director.Add(NpcTestWorld.Npc("carl", "Carl"));
        }

        private static void AddressAll(NpcDirector director)
        {
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, hi", null));
            director.Hear(NpcHeardLine.Chat("p2", "Anna", "Alice, hi", null));
            director.Hear(NpcHeardLine.Chat("p3", "Ivan", "Carl, hi", null));
        }

        private static async Task PumpUntil(NpcDirector director, Func<bool> done, string what)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!done())
            {
                director.Tick();
                if (watch.ElapsedMilliseconds > 5000)
                {
                    Assert.Fail("Timed out waiting for " + what + ".");
                }

                await Task.Delay(5);
            }
        }

        private static QueuedAiOrchestrator CreateQueue(ILlmClient provider, int maxConcurrent)
        {
            AgentMemoryPolicy policy = new();
            IAgentMemoryStore memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(),
                new DefaultAgentMemoryScopeProvider());
            AiPromptComposer composer = new(new StubSystemPrompts(), new StubUserTemplates(), null);
            AiOrchestrator inner = new(
                new SoloAuthorityHost(),
                provider,
                new NoOpCommandSink(),
                new SessionTelemetryCollector(),
                composer,
                memory,
                policy,
                null,
                null,
                new CoreAISettingsOptions(),
                new LocalActorIdentityProvider("player-1"));
            return new QueuedAiOrchestrator(inner, new AiOrchestrationQueueOptions { MaxConcurrent = maxConcurrent });
        }

        /// <summary>A provider that answers by a script over everything it was sent, and can hold NPC turns.</summary>
        private sealed class ProviderScript : ILlmClient
        {
            private readonly object _gate = new();
            private readonly List<TaskCompletionSource<bool>> _held = new();
            private int _npcCallsStarted;

            public readonly List<LlmCompletionRequest> Requests = new();

            public Func<string, string> Reply = _ => "Hello.";

            public bool HoldNpcTurns;

            public int NpcCallsStarted => Volatile.Read(ref _npcCallsStarted);

            public string[] CallOrder()
            {
                lock (_gate)
                {
                    string[] order = new string[Requests.Count];
                    for (int index = 0; index < Requests.Count; index++)
                    {
                        order[index] = Requests[index].AgentRoleId;
                    }

                    return order;
                }
            }

            public string HistoryOf(int index)
            {
                lock (_gate)
                {
                    StringBuilder sb = new();
                    foreach (Microsoft.Extensions.AI.ChatMessage message in Requests[index].ChatHistory ??
                                                                            new List<Microsoft.Extensions.AI.ChatMessage>())
                    {
                        sb.Append(message.Role).Append(": ").Append(message.Text).Append('\n');
                    }

                    return sb.ToString();
                }
            }

            public void ReleaseOneNpcTurn()
            {
                lock (_gate)
                {
                    if (_held.Count > 0)
                    {
                        _held[0].TrySetResult(true);
                        _held.RemoveAt(0);
                    }
                }
            }

            public void ReleaseNpcTurns()
            {
                lock (_gate)
                {
                    foreach (TaskCompletionSource<bool> held in _held)
                    {
                        held.TrySetResult(true);
                    }

                    _held.Clear();
                    HoldNpcTurns = false;
                }
            }

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                string reply = await Answer(request, cancellationToken);
                return new LlmCompletionResult { Ok = true, Content = reply, Model = "scripted" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                string reply = await Answer(request, cancellationToken);
                yield return new LlmStreamChunk { Text = reply, Model = "scripted" };
                yield return new LlmStreamChunk { IsDone = true, Model = "scripted" };
            }

            private async Task<string> Answer(LlmCompletionRequest request, CancellationToken token)
            {
                TaskCompletionSource<bool> hold = null;
                string haystack;
                lock (_gate)
                {
                    Requests.Add(request);
                    StringBuilder sb = new();
                    sb.Append(request.SystemPrompt).Append('\n').Append(request.UserPayload).Append('\n');
                    foreach (Microsoft.Extensions.AI.ChatMessage message in request.ChatHistory ??
                                                                            new List<Microsoft.Extensions.AI.ChatMessage>())
                    {
                        sb.Append(message.Text).Append('\n');
                    }

                    haystack = sb.ToString();
                    if (request.AgentRoleId == BuiltInAgentRoleIds.AiNpc)
                    {
                        Interlocked.Increment(ref _npcCallsStarted);
                        if (HoldNpcTurns)
                        {
                            hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            _held.Add(hold);
                        }
                    }
                }

                if (hold != null)
                {
                    using (token.Register(() => hold.TrySetCanceled()))
                    {
                        await hold.Task;
                    }
                }

                return Reply(haystack);
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

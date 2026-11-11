using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.PlayMode.FastNoLlm.Npc
{
    /// <summary>
    /// Three villagers in a running scene, over the real orchestrator and queue with a scripted provider: each answers
    /// the player who named it, within the world's call budget, in the chat sink and in a UI Toolkit bubble placed
    /// over its head; a line naming nobody, or naming a villager out of range, gets no answer.
    /// </summary>
    public sealed class NpcVillagersPlayModeTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.Destroy(created);
                }
            }

            _created.Clear();
        }

        [UnityTest]
        public IEnumerator ThreeVillagers_AnswerTheRightSpeaker_InTheChatAndInBubbles()
        {
            Camera camera = new GameObject("NpcTestCamera").AddComponent<Camera>();
            _created.Add(camera.gameObject);
            camera.transform.position = new Vector3(0f, 1.6f, -8f);
            camera.transform.LookAt(new Vector3(0f, 1.6f, 0f));
            Transform bob = Villager("Bob", new Vector3(-3f, 0f, 0f));
            Transform alice = Villager("Alice", new Vector3(0f, 0f, 0f));
            Transform carl = Villager("Carl", new Vector3(3f, 0f, 0f));

            EchoProvider provider = new();
            using QueuedAiOrchestrator queue = CreateQueue(provider);
            NpcSpeechBubbleBoard board = new(null, new UnityNpcClock());
            NpcSpeechAnchorRegistry anchors = new();
            RecordingChat chat = new();
            CompositeNpcSpeechSink sinks = new(new INpcSpeechSink[] { new ChatOverlayNpcSpeechSink(chat), board });
            using NpcDirector director = new(queue, sinks, new NpcDirectorOptions { MaxCallsPerMinute = 3 },
                new UnityNpcClock());
            List<NpcSaid> said = new();
            director.NpcSaid += s => said.Add(s);
            AddVillager(director, anchors, "bob", "Bob", bob);
            AddVillager(director, anchors, "alice", "Alice", alice);
            AddVillager(director, anchors, "carl", "Carl", carl);
            CoreAiNpcSettings settings = new() { HearChat = false, Camera = camera };
            using CoreAiNpcEntryPoint entry = new(director, board, new NpcSpeechBubbleView(board, anchors), settings);
            entry.Start();
            yield return null;

            NpcPosition near = new(0d, 0d, -2d);
            director.Hear(NpcHeardLine.Chat("p-bob", "Steve", "Bob, any bread?", near));
            director.Hear(NpcHeardLine.Chat("p-alice", "Anna", "Alice, how is the farm?", near));
            director.Hear(NpcHeardLine.Chat("p-carl", "Ivan", "Carl, sharpen my axe", near));
            director.Hear(NpcHeardLine.Chat("p-none", "Zoe", "what a lovely day", near));
            director.Hear(NpcHeardLine.Chat("p-far", "Max", "Bob, can you hear me?", new NpcPosition(0d, 0d, 500d)));

            int maxVisible = 0;
            for (int frame = 0; frame < 600 && said.Count < 3; frame++)
            {
                entry.Tick();
                maxVisible = Mathf.Max(maxVisible, entry.View.VisibleCount);
                yield return null;
            }

            for (int frame = 0; frame < 5; frame++)
            {
                entry.Tick();
                maxVisible = Mathf.Max(maxVisible, entry.View.VisibleCount);
                yield return null;
            }

            Assert.AreEqual(3, said.Count, "Exactly the three named, in-range villagers answered.");
            Assert.AreEqual(3, provider.NpcCalls, "One model call per answered line, within the budget of 3.");
            AssertAnswered(said, "bob", "p-bob");
            AssertAnswered(said, "alice", "p-alice");
            AssertAnswered(said, "carl", "p-carl");
            Assert.AreEqual(3, chat.Lines.Count);
            Assert.AreEqual(3, maxVisible, "Each villager showed its bubble.");
            Assert.IsNotNull(entry.Surface?.Root?.panel, "The bubbles live on a runtime UI Toolkit panel.");
            Assert.IsTrue(entry.View.TryGetElement("alice", out VisualElement aliceBubble));
            Assert.Greater(aliceBubble.resolvedStyle.left, 0f, "The bubble was placed over its villager.");
        }

        private Transform Villager(string name, Vector3 position)
        {
            GameObject villager = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            villager.name = name;
            villager.transform.position = position + Vector3.up;
            _created.Add(villager);
            return villager.transform;
        }

        private static void AddVillager(NpcDirector director, NpcSpeechAnchorRegistry anchors, string id, string name,
            Transform body)
        {
            director.Add(new NpcAgentDefinition
            {
                Persona = new NpcPersona { Id = id, DisplayName = name, PromptFragment = name + " lives in the village." },
                Budget = new NpcBudget { CooldownSeconds = 0d },
                PositionSource = () => new NpcPosition(body.position.x, 0d, body.position.z)
            });
            anchors.Register(id, () =>
            {
                Vector3 head = body.position + Vector3.up * 1.4f;
                return new NpcPosition(head.x, head.y, head.z);
            });
        }

        private static void AssertAnswered(List<NpcSaid> said, string npcId, string speakerId)
        {
            foreach (NpcSaid line in said)
            {
                if (line.NpcId == npcId)
                {
                    Assert.AreEqual(speakerId, line.SpeakerId, npcId + " answered the wrong speaker.");
                    Assert.IsFalse(line.IsFallback);
                    return;
                }
            }

            Assert.Fail(npcId + " did not answer.");
        }

        private static QueuedAiOrchestrator CreateQueue(ILlmClient provider)
        {
            AiOrchestrator inner = new(
                new SoloAuthorityHost(),
                provider,
                new NullCommandSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new Prompts(), new Templates(), null),
                new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), new DefaultAgentMemoryScopeProvider()),
                new AgentMemoryPolicy(),
                null,
                null,
                new CoreAISettingsOptions(),
                new LocalActorIdentityProvider("player-1"));
            return new QueuedAiOrchestrator(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });
        }

        private sealed class EchoProvider : ILlmClient
        {
            private int _npcCalls;

            public int NpcCalls => Volatile.Read(ref _npcCalls);

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = Answer(request) });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                string answer = Answer(request);
                await Task.Yield();
                yield return new LlmStreamChunk { Text = answer.Substring(0, answer.Length / 2) };
                await Task.Yield();
                yield return new LlmStreamChunk { Text = answer.Substring(answer.Length / 2) };
                yield return new LlmStreamChunk { IsDone = true };
            }

            private string Answer(LlmCompletionRequest request)
            {
                if (request.AgentRoleId == BuiltInAgentRoleIds.AiNpc)
                {
                    Interlocked.Increment(ref _npcCalls);
                }

                return "Good day to you, friend.";
            }
        }

        private sealed class RecordingChat : CoreAI.ChatOverlay.IChatOverlayMessages
        {
            public readonly List<string> Lines = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add(text);
            }

            public void PostMessage(string speaker, string text,
                CoreAI.ChatOverlay.ChatFeedLineKind kind = CoreAI.ChatOverlay.ChatFeedLineKind.System)
            {
                lock (Lines)
                {
                    Lines.Add(speaker + ": " + text);
                }
            }
        }

        private sealed class Prompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "You are " + roleId + ".";
                return true;
            }
        }

        private sealed class Templates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class NullCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}

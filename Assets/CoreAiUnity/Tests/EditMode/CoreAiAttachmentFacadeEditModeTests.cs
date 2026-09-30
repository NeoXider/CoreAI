using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The attachment overloads of the Unity facade, the chat service and the chat panel bind the caller's list to
    /// <see cref="AiTaskRequest.Attachments"/> as-is (no copy), a single attachment becomes a one-element list, and a
    /// null attachment or list falls back to the prompt-only path. The panel also accepts an attachments-only
    /// external message (blank text) and still rejects a message with neither.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiAttachmentFacadeEditModeTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY detached: the facade's buffered paths are awaited from the test thread, and Unity's context would post
        /// their continuations back to that same thread.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            CoreAi.Invalidate();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAi.Invalidate();
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task FacadeAskAsync_BindsTheAttachments_AndNullFallsBackToThePromptOnlyTurn()
        {
            CapturingOrchestrator orchestrator = UseFacadeOrchestrator();
            AiAttachment image = AiAttachment.Image(Png);
            AiAttachment[] several = { image, AiAttachment.FromText("level.lua", "return 1") };

            await CoreAi.AskAsync("one", image, "Creator");
            await CoreAi.AskAsync("many", several);
            await CoreAi.AskAsync("none", (AiAttachment)null);
            await CoreAi.AskAsync("empty list", (IReadOnlyList<AiAttachment>)null);
            await CoreAi.AskAsync("plain");

            Assert.AreEqual(5, orchestrator.Requests.Count);
            Assert.AreEqual("Creator", orchestrator.Requests[0].RoleId);
            Assert.AreSame(image, Single(orchestrator.Requests[0].Attachments));
            Assert.AreSame(several, orchestrator.Requests[1].Attachments, "The caller's list is passed as-is.");
            Assert.IsNull(orchestrator.Requests[2].Attachments);
            Assert.IsNull(orchestrator.Requests[3].Attachments);
            Assert.IsNull(orchestrator.Requests[4].Attachments);
            Assert.AreEqual("plain", orchestrator.Requests[4].Hint);
        }

        [Test]
        public async Task FacadeStreamAsync_AndStreamChunksAsync_BindTheAttachments()
        {
            CapturingOrchestrator orchestrator = UseFacadeOrchestrator();
            AiAttachment image = AiAttachment.Image(Png);
            AiAttachment[] several = { image };

            List<string> text = new();
            await foreach (string chunk in CoreAi.StreamAsync("stream one", image))
            {
                text.Add(chunk);
            }

            await foreach (string _ in CoreAi.StreamAsync("stream many", several))
            {
            }

            await foreach (LlmStreamChunk _ in CoreAi.StreamChunksAsync("chunks", image))
            {
            }

            await foreach (LlmStreamChunk _ in CoreAi.StreamChunksAsync("chunks none", (AiAttachment)null))
            {
            }

            CollectionAssert.AreEqual(new[] { "ok" }, text);
            Assert.AreEqual(4, orchestrator.StreamRequests.Count);
            Assert.AreSame(image, Single(orchestrator.StreamRequests[0].Attachments));
            Assert.AreSame(several, orchestrator.StreamRequests[1].Attachments);
            Assert.AreSame(image, Single(orchestrator.StreamRequests[2].Attachments));
            Assert.IsNull(orchestrator.StreamRequests[3].Attachments);
        }

        [Test]
        public async Task FacadeSmartAskAsync_BindsTheAttachments_OnBothPaths()
        {
            CapturingOrchestrator orchestrator = UseFacadeOrchestrator();
            AiAttachment image = AiAttachment.Image(Png);
            AiAttachment[] several = { image };

            string buffered = await CoreAi.SmartAskAsync("buffered", image, uiStreamingOverride: false);
            string streamed = await CoreAi.SmartAskAsync("streamed", several, uiStreamingOverride: true);

            Assert.AreEqual("ok", buffered);
            Assert.AreEqual("ok", streamed);
            Assert.AreSame(image, Single(orchestrator.Requests[0].Attachments));
            Assert.AreSame(several, orchestrator.StreamRequests[0].Attachments);
        }

        [Test]
        public async Task ChatServiceOverloads_BindTheCallersList()
        {
            CapturingOrchestrator orchestrator = new();
            CoreAiChatService service = new(orchestrator, settings: new StubSettings(),
                actorIdentityProvider: new LocalActorIdentityProvider("attachment-test"));
            AiAttachment[] several = { AiAttachment.Image(Png), AiAttachment.FromText("a.md", "# A") };

            await service.SendMessageAsync("send", several, "SmartChat");
            await foreach (LlmStreamChunk _ in service.SendMessageStreamingAsync("stream", several, "SmartChat"))
            {
            }

            await service.SendMessageSmartAsync("smart", several, "SmartChat", uiStreamingOverride: false);
            await service.SendMessageAsync("none", (IReadOnlyList<AiAttachment>)null, "SmartChat");

            Assert.AreSame(several, orchestrator.Requests[0].Attachments);
            Assert.AreSame(several, orchestrator.StreamRequests[0].Attachments);
            Assert.AreSame(several, orchestrator.Requests[1].Attachments);
            Assert.IsNull(orchestrator.Requests[2].Attachments);
            Assert.AreEqual("Chat", orchestrator.Requests[0].SourceTag);
        }

        [Test]
        public async Task ChatPanelExternalSubmit_CarriesAttachments_AndAcceptsAnAttachmentsOnlyMessage()
        {
            GameObject go = new("CoreAiAttachmentFacade_Panel");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                panel.SetActorIdentityProvider(new LocalActorIdentityProvider("attachment-panel-test"));
                typeof(CoreAiChatPanel).GetField("_lifecycleActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(panel, true);
                CapturingOrchestrator orchestrator = new();
                panel.ChatService = new CoreAiChatService(orchestrator,
                    settings: new StubSettings { EnableStreaming = false });
                AiAttachment[] image = { AiAttachment.Image(Png, "image/png", "shot.png") };
                List<string> sent = new();
                panel.OnUserMessageSent += sent.Add;

                string withText = await panel.SubmitMessageFromExternalAsync("What is this?",
                    new CoreAiChatExternalSubmitOptions { Attachments = image, AppendUserMessageToChat = false });
                string imageOnly = await panel.SubmitMessageFromExternalAsync("   ",
                    new CoreAiChatExternalSubmitOptions { Attachments = image });
                CoreAiChatExternalSubmitResult nothing = await panel.SubmitMessageFromExternalResultAsync("  ",
                    new CoreAiChatExternalSubmitOptions { Attachments = new AiAttachment[] { null } });

                Assert.AreEqual("ok", withText);
                Assert.AreEqual("ok", imageOnly, "Blank text with an image is an image-only message.");
                Assert.AreEqual(2, orchestrator.Requests.Count);
                Assert.AreSame(image, orchestrator.Requests[0].Attachments);
                Assert.AreEqual("What is this?", orchestrator.Requests[0].Hint);
                Assert.AreSame(image, orchestrator.Requests[1].Attachments);
                Assert.AreEqual("", orchestrator.Requests[1].Hint);
                CollectionAssert.AreEqual(new[] { "[attachment: shot.png image/png 8 B]" }, sent,
                    "An attachments-only bubble shows the history placeholder, never an empty bubble.");
                Assert.AreEqual(CoreAiChatExternalSubmitRejection.EmptyInput, nothing.Rejection,
                    "Blank text with no real attachment stays EmptyInput.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static CapturingOrchestrator UseFacadeOrchestrator()
        {
            CapturingOrchestrator orchestrator = new();
            CoreAi.SetResolver(() => orchestrator);
            // WHY: without a CoreAILifetimeScope the resolver supplies only the orchestrator, so the chat service the attachment overloads run through is installed over it the way a scene scope would provide one.
            typeof(CoreAi).GetField("_chatService", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, new CoreAiChatService(orchestrator, settings: new StubSettings(),
                    actorIdentityProvider: new LocalActorIdentityProvider("attachment-facade-test")));
            return orchestrator;
        }

        private static AiAttachment Single(IReadOnlyList<AiAttachment> attachments)
        {
            Assert.IsNotNull(attachments);
            Assert.AreEqual(1, attachments.Count);
            return attachments[0];
        }

        private sealed class CapturingOrchestrator : IAiOrchestrationService
        {
            public List<AiTaskRequest> Requests { get; } = new();
            public List<AiTaskRequest> StreamRequests { get; } = new();

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                return Task.FromResult("ok");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamRequests.Add(request);
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "ok" };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        private sealed class StubSettings : ICoreAISettings
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
            public float LlmRequestTimeoutSeconds => 0f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }
    }
}

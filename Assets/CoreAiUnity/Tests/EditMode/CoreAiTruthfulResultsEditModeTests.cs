using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Newcomer audit A5 and A6. A provider failure used to come back from <c>CoreAi.AskAsync</c> as the reply
    /// (<c>AiOrchestrator</c> returns the error text for <c>Chat</c>-tagged tasks), so <c>"HTTP 503 ..."</c> became
    /// an NPC line, and <c>SmartAskAsync</c> joined the streamed text and never read the error chunk. Now every
    /// string entry point throws <see cref="LlmRequestFailedException"/>, the <c>*ResultAsync</c> twins return
    /// <c>Ok = false</c>, and the failure text is never a reply value.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiTruthfulResultsEditModeTests
    {
        private const string ProviderErrorText = "HTTP 503: upstream unavailable";

        private SynchronizationContext _previousContext;

        /// <summary>
        /// WHY detached: the facade's buffered paths are awaited from the test thread, and Unity's context would post
        /// their continuations back to that same thread.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            CoreAi.Invalidate();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAi.Invalidate();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        private static readonly AiAttachment Text = AiAttachment.FromText("notes.md", "# notes");

        private static readonly Dictionary<string, Func<Task<string>>> StringEntryPoints = new()
        {
            ["AskAsync"] = () => CoreAi.AskAsync("q"),
            ["AskAsync+attachment"] = () => CoreAi.AskAsync("q", Text),
            ["AskAsync+attachments"] = () => CoreAi.AskAsync("q", new[] { Text }),
            ["SmartAskAsync buffered"] = () => CoreAi.SmartAskAsync("q", uiStreamingOverride: false),
            ["SmartAskAsync streamed"] = () => CoreAi.SmartAskAsync("q", uiStreamingOverride: true),
            ["SmartAskAsync+attachment streamed"] = () => CoreAi.SmartAskAsync("q", Text, uiStreamingOverride: true),
            ["SmartAskAsync+attachments buffered"] = () =>
                CoreAi.SmartAskAsync("q", new[] { Text }, uiStreamingOverride: false),
            ["StreamAsync"] = () => CollectAsync(CoreAi.StreamAsync("q")),
            ["StreamAsync+attachment"] = () => CollectAsync(CoreAi.StreamAsync("q", Text)),
            ["StreamAsync+attachments"] = () => CollectAsync(CoreAi.StreamAsync("q", new[] { Text })),
            ["OrchestrateAsync"] = () => CoreAi.OrchestrateAsync(new AiTaskRequest { RoleId = "Creator", Hint = "q" }),
            ["OrchestrateStreamCollectAsync"] = () =>
                CoreAi.OrchestrateStreamCollectAsync(new AiTaskRequest { RoleId = "Creator", Hint = "q" })
        };

        private static IEnumerable<string> EntryPointNames => StringEntryPoints.Keys;

        [TestCaseSource(nameof(EntryPointNames))]
        public async Task ProviderFailure_ThroughEveryStringEntryPoint_ThrowsAndNeverBecomesTheReply(string entryPoint)
        {
            ScriptedOrchestrator orchestrator = UseFacade(ScriptedOrchestrator.Failing());

            Exception failure = await CaptureAsync(StringEntryPoints[entryPoint]);

            Assert.IsInstanceOf<LlmRequestFailedException>(failure,
                $"{entryPoint} must surface the failure instead of returning it as the reply.");
            Assert.IsInstanceOf<InvalidOperationException>(failure, "Existing StreamAsync handlers keep catching it.");
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, ((LlmRequestFailedException)failure).ErrorCode);
            Assert.AreEqual(503, ((LlmRequestFailedException)failure).HttpStatus);
            Assert.AreEqual(0, orchestrator.LegacyCalls,
                "The legacy string path returns the error text for chat tasks; a typed orchestrator is never asked it.");
        }

        [TestCaseSource(nameof(EntryPointNames))]
        public async Task Success_ThroughEveryStringEntryPoint_ReturnsTheReply(string entryPoint)
        {
            UseFacade(ScriptedOrchestrator.Succeeding("the reply"));

            string reply = await StringEntryPoints[entryPoint]();

            Assert.AreEqual("the reply", reply);
        }

        [Test]
        public async Task AskResultAsync_ReturnsTheTypedFailure_WithoutThrowing()
        {
            UseFacade(ScriptedOrchestrator.Failing());

            LlmCompletionResult result = await CoreAi.AskResultAsync("q");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
            Assert.AreEqual(503, result.HttpStatus);
            Assert.AreNotEqual(ProviderErrorText, result.Content);
        }

        [Test]
        public async Task AskResultAsync_ReturnsTheReply_OnSuccess()
        {
            UseFacade(ScriptedOrchestrator.Succeeding("hello"));

            LlmCompletionResult result = await CoreAi.AskResultAsync("q", Text);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("hello", result.Content);
        }

        [Test]
        public async Task SmartAskResultAsync_Streamed_ReturnsTheFailure_WithThePartialText()
        {
            UseFacade(ScriptedOrchestrator.Failing());
            List<string> fragments = new();

            LlmCompletionResult result = await CoreAi.SmartAskResultAsync("q", onChunk: fragments.Add,
                uiStreamingOverride: true);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
            Assert.AreEqual("half an ans", result.Content, "The text streamed before the failure is reported as partial.");
            CollectionAssert.AreEqual(new[] { "half an ans" }, fragments);
        }

        [Test]
        public async Task SmartAskResultAsync_BothPaths_ReturnTheReply_OnSuccess()
        {
            UseFacade(ScriptedOrchestrator.Succeeding("all of it"));

            LlmCompletionResult streamed = await CoreAi.SmartAskResultAsync("q", uiStreamingOverride: true);
            LlmCompletionResult buffered = await CoreAi.SmartAskResultAsync("q", new[] { Text },
                uiStreamingOverride: false);

            Assert.IsTrue(streamed.Ok);
            Assert.AreEqual("all of it", streamed.Content);
            Assert.IsTrue(buffered.Ok);
            Assert.AreEqual("all of it", buffered.Content);
        }

        [Test]
        public async Task SmartAskAsync_Streamed_DeliversTheFragmentsBeforeItThrows()
        {
            UseFacade(ScriptedOrchestrator.Failing());
            List<string> fragments = new();

            Exception failure = await CaptureAsync(() =>
                CoreAi.SmartAskAsync("q", onChunk: fragments.Add, uiStreamingOverride: true));

            Assert.IsInstanceOf<LlmRequestFailedException>(failure);
            CollectionAssert.AreEqual(new[] { "half an ans" }, fragments);
        }

        [Test]
        public async Task OrchestrateResultAsync_ReturnsTheTypedFailure()
        {
            UseFacade(ScriptedOrchestrator.Failing());

            LlmCompletionResult result = await CoreAi.OrchestrateResultAsync(
                new AiTaskRequest { RoleId = "Creator", Hint = "q" });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
        }

        [Test]
        public async Task AuthFailure_ThroughAskAsync_KeepsTheProviderTextOutOfTheException()
        {
            UseFacade(new ScriptedOrchestrator(new LlmCompletionResult
            {
                Ok = false, Error = "HTTP 401: key sk-live-secret rejected", ErrorCode = LlmErrorCode.AuthExpired,
                HttpStatus = 401
            }, "HTTP 401: key sk-live-secret rejected", Array.Empty<LlmStreamChunk>()));

            Exception failure = await CaptureAsync(() => CoreAi.AskAsync("q"));

            Assert.IsInstanceOf<LlmRequestFailedException>(failure);
            Assert.That(failure.ToString(), Does.Not.Contain("sk-live-secret"));
            Assert.AreEqual(LlmErrorCode.AuthExpired, ((LlmRequestFailedException)failure).ErrorCode);
        }

        [Test]
        public async Task LegacyStringOrchestrator_KeepsItsOwnString()
        {
            LegacyStringOrchestrator orchestrator = new("whatever the host says");
            CoreAi.SetResolver(() => orchestrator);
            InstallChatService(orchestrator);

            Assert.AreEqual("whatever the host says", await CoreAi.AskAsync("q"),
                "Without typed results nothing else is known about the turn: the documented boundary of the rule.");
        }

        [Test]
        public async Task ChatService_SmartSend_ThrowsOnAnErrorChunk_AndReturnsTheTextOfACleanStream()
        {
            ScriptedOrchestrator failing = ScriptedOrchestrator.Failing();
            ScriptedOrchestrator clean = ScriptedOrchestrator.Succeeding("abc");
            CoreAiChatService failingService = new(failing, settings: new StubSettings(),
                actorIdentityProvider: new LocalActorIdentityProvider("smart-send"));
            CoreAiChatService cleanService = new(clean, settings: new StubSettings(),
                actorIdentityProvider: new LocalActorIdentityProvider("smart-send"));

            Exception failure = await CaptureAsync(() =>
                failingService.SendMessageSmartAsync("hi", "Role", null, true));
            string text = await cleanService.SendMessageSmartAsync("hi", "Role", null, true);

            Assert.IsInstanceOf<LlmRequestFailedException>(failure,
                "An {IsDone, Error} chunk used to be joined away, leaving half an answer as the reply.");
            Assert.AreEqual("abc", text);
        }

        private static async Task<string> CollectAsync(IAsyncEnumerable<string> stream)
        {
            string text = "";
            await foreach (string chunk in stream)
            {
                text += chunk;
            }

            return text;
        }

        private static async Task<Exception> CaptureAsync(Func<Task> run)
        {
            try
            {
                await run();
            }
            catch (Exception ex)
            {
                return ex;
            }

            Assert.Fail("Expected the call to throw.");
            return null;
        }

        private static ScriptedOrchestrator UseFacade(ScriptedOrchestrator orchestrator)
        {
            CoreAi.SetResolver(() => orchestrator);
            InstallChatService(orchestrator);
            return orchestrator;
        }

        private static void InstallChatService(IAiOrchestrationService orchestrator)
        {
            // WHY: without a CoreAILifetimeScope the resolver supplies only the orchestrator, so the chat service the
            // string overloads run through is installed over it the way a scene scope would provide one.
            typeof(CoreAi).GetField("_chatService", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, new CoreAiChatService(orchestrator, settings: new StubSettings(),
                    actorIdentityProvider: new LocalActorIdentityProvider("truthful-results")));
        }

        /// <summary>
        /// A typed orchestrator whose legacy string path behaves like <c>AiOrchestrator</c> for chat tasks: it
        /// returns the failure text as if it were the reply.
        /// </summary>
        private sealed class ScriptedOrchestrator : IAiOrchestrationService, IAiTaskResultService,
            IAiActorContextResolver
        {
            private readonly LlmCompletionResult _result;
            private readonly string _legacyText;
            private readonly LlmStreamChunk[] _stream;
            private readonly IActorIdentityProvider _identity = new LocalActorIdentityProvider("truthful-results");

            public ScriptedOrchestrator(LlmCompletionResult result, string legacyText, LlmStreamChunk[] stream)
            {
                _result = result;
                _legacyText = legacyText;
                _stream = stream;
            }

            public int LegacyCalls { get; private set; }

            public static ScriptedOrchestrator Failing()
            {
                return new ScriptedOrchestrator(
                    new LlmCompletionResult
                    {
                        Ok = false, Error = ProviderErrorText, ErrorCode = LlmErrorCode.BackendUnavailable,
                        HttpStatus = 503
                    },
                    ProviderErrorText,
                    new[]
                    {
                        new LlmStreamChunk { Text = "half an ans" },
                        new LlmStreamChunk
                        {
                            IsDone = true, Error = ProviderErrorText, ErrorCode = LlmErrorCode.BackendUnavailable,
                            HttpStatus = 503
                        }
                    });
            }

            public static ScriptedOrchestrator Succeeding(string reply)
            {
                return new ScriptedOrchestrator(
                    new LlmCompletionResult { Ok = true, Content = reply },
                    reply,
                    new[]
                    {
                        new LlmStreamChunk { Text = reply.Substring(0, 1) },
                        new LlmStreamChunk { Text = reply.Substring(1) },
                        new LlmStreamChunk { IsDone = true }
                    });
            }

            public ActorContext ResolveActorContext(AiTaskRequest request)
            {
                request.ActorContext ??= _identity.GetActorContext(request.RoleId);
                return request.ActorContext.Value;
            }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                LegacyCalls++;
                return Task.FromResult(_legacyText);
            }

            public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                foreach (LlmStreamChunk chunk in _stream)
                {
                    await Task.Yield();
                    yield return chunk;
                }
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class LegacyStringOrchestrator : IAiOrchestrationService
        {
            private readonly string _text;

            public LegacyStringOrchestrator(string text)
            {
                _text = text;
            }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_text);
            }

            public void CancelTasks(string cancellationScope)
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

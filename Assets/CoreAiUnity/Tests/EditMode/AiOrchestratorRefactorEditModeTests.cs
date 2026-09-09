using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Logging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// v1.5.5 tests for ARCH-3 (SanitizeAndPublish), ARCH-6 (BuildCompletionRequest),
    /// and ARCH-7 (#if UNITY removal).
    /// </summary>
    [TestFixture]
    public sealed class AiOrchestratorRefactorEditModeTests
    {
        #region Test doubles

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }
            public int CallCount { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                CallCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class CapturingSink : IAiGameCommandSink
        {
            public ApplyAiGameCommand LastCommand { get; private set; }
            public int PublishCount { get; private set; }

            public void Publish(ApplyAiGameCommand command)
            {
                LastCommand = command;
                PublishCount++;
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class DenyAiAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => false;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class FailingLlmClient : ILlmClient
        {
            public string ErrorMessage { get; set; } = "llm failed";

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = ErrorMessage
                });
            }
        }

        private static AiOrchestrator BuildWithAuth(IAuthorityHost auth, ILlmClient llm, CapturingSink sink = null)
        {
            return new AiOrchestrator(
                auth, llm, sink ?? new CapturingSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null),
                null, new AgentMemoryPolicy(),
                null, null, new TestSettings(),
                new LocalActorIdentityProvider("orchestrator-refactor-test"));
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLlmRequestRetries => 1;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string UniversalSystemPromptPrefix => "";
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public int MaxLuaRepairRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

        private sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private static AiOrchestrator Build(CapturingLlmClient llm, CapturingSink sink = null)
        {
            return BuildWithAuth(new TestAuthority(), llm, sink);
        }

        #endregion

        private sealed class ExpectedSummaryFailureLog : ILog, IDisposable
        {
            private readonly ILog _previous = Log.Instance;
            private readonly string _failure;
            private int _observed;

            internal ExpectedSummaryFailureLog(string failure)
            {
                _failure = failure;
                Log.Instance = this;
            }

            public void Debug(string message, string tag = null) => _previous.Debug(message, tag);
            public void Info(string message, string tag = null) => _previous.Info(message, tag);
            public void Warn(string message, string tag = null) => _previous.Warn(message, tag);
            public void Error(string message, string tag = null)
            {
                if (message != null && message.Contains(_failure)) _observed++;
                else _previous.Error(message, tag);
            }

            public void Dispose()
            {
                Log.Instance = _previous;
                Assert.AreEqual(1, _observed, "The deliberately injected summary failure must be reported once.");
            }
        }

        /// <summary>Shared real-orchestrator scenario for ordinary and streaming durability regressions.</summary>
        internal sealed class SummaryPreflightScenario
        {
            internal readonly BoundedHistory Memory = new();
            internal readonly ControlledSummary Summary = new();
            internal readonly CapturingProvider Provider = new();
            private readonly CapturingSink _sink = new();
            internal readonly AiOrchestrator Orchestrator;
            internal int Publications => _sink.PublishCount;
            internal readonly AiTaskRequest Request = new() { RoleId = "summary-preflight", Hint = "new intent" };

            internal SummaryPreflightScenario(bool denyAuthority = false)
            {
                AgentMemoryPolicy policy = new();
                policy.ConfigureChatHistory(Request.RoleId, true, 8192, true, 1);
                Orchestrator = new AiOrchestrator(
                    denyAuthority ? new DenyAiAuthority() : new TestAuthority(), Provider, _sink,
                    new TestTelemetry(), new AiPromptComposer(new NullSys(), new NullUsr(), null), Memory,
                    policy, null, null, new TestSettings(), new LocalActorIdentityProvider("summary-owner"),
                    new DeterministicConversationContextManager(Summary));
            }

            internal void AssertOldSourceRetained()
            {
                Assert.AreEqual(0, Memory.Appends.Count, "Failed preflight must not append and evict old source.");
                CollectionAssert.AreEqual(Memory.Original, Memory.GetChatHistory(Request.RoleId));
                Assert.AreEqual(0, Provider.Calls, "The main provider must wait for durable summary confirmation.");
                Assert.AreEqual(0, Publications, "An unprepared request cannot publish an answer.");
            }

            internal void AssertPublishedOnce()
            {
                Assert.AreEqual(1, Provider.Calls);
                Assert.AreEqual(1, Publications);
                Assert.AreEqual(2, Memory.Appends.Count);
                Assert.AreEqual("user", Memory.Appends[0].Role);
                Assert.AreEqual(Request.Hint, Memory.Appends[0].Content);
                Assert.AreEqual("assistant", Memory.Appends[1].Role);
                Assert.AreEqual("ok", Memory.Appends[1].Content);
                Assert.AreEqual(0, Summary.SyncCalls, "Async orchestration must not use sync summary storage.");
                StringAssert.Contains(Memory.Original[0].Content, Summary.Stored,
                    "The source evicted by bounded appends must already exist in the committed summary.");
            }

            internal static async Task<Exception> CaptureFailure(Task operation)
            {
                try { await operation; return null; }
                catch (Exception failure) { return failure; }
            }

            internal static async Task AwaitEntered(Task signal)
            {
                Assert.AreSame(signal, await Task.WhenAny(signal, Task.Delay(5000)),
                    "The operation must reach the controlled async boundary without hanging.");
                await signal;
            }

            internal sealed class BoundedHistory : IAgentMemoryStore
            {
                internal readonly ChatMessage[] Original =
                    { new("user", "source that must survive eviction"), new("assistant", "recent answer") };
                private readonly List<ChatMessage> _history;
                internal readonly List<ChatMessage> Appends = new();
                internal BoundedHistory() { _history = new List<ChatMessage>(Original); }
                public bool TryLoad(string roleId, out AgentMemoryState state) { state = null; return false; }
                public void Save(string roleId, AgentMemoryState state) { }
                public void Clear(string roleId) { }
                public void ClearChatHistory(string roleId) => _history.Clear();
                public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => _history.ToArray();
                public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
                {
                    ChatMessage message = new(role, content);
                    Appends.Add(message);
                    _history.Add(message);
                    while (_history.Count > Original.Length) _history.RemoveAt(0);
                }
            }

            internal sealed class ControlledSummary : IConversationSummaryStore, IAsyncConversationSummaryStore
            {
                internal readonly TaskCompletionSource<bool> LoadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                internal readonly TaskCompletionSource<bool> SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                internal TaskCompletionSource<bool> LoadGate;
                internal TaskCompletionSource<bool> SaveGate;
                internal const string LoadFailure = "Summary read failed.";
                internal const string SaveFailure = "Summary confirmation failed.";
                internal bool FailLoad;
                internal bool FailSave;
                internal bool SaveIgnoresCallerCancellation;
                internal string Stored = "";
                internal int SyncCalls;
                public string LoadSummary(string roleId) { SyncCalls++; throw new InvalidOperationException("Sync load forbidden."); }
                public void SaveSummary(string roleId, string summary) { SyncCalls++; throw new InvalidOperationException("Sync save forbidden."); }
                public void ClearSummary(string roleId) { SyncCalls++; throw new InvalidOperationException("Sync clear forbidden."); }
                public async Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
                {
                    LoadEntered.TrySetResult(true);
                    if (LoadGate != null)
                    {
                        using CancellationTokenRegistration registration = cancellationToken.Register(() => LoadGate.TrySetCanceled());
                        await LoadGate.Task;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (FailLoad) throw new IOException(LoadFailure);
                    return Stored;
                }
                public async Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
                {
                    SaveEntered.TrySetResult(true);
                    if (SaveGate != null) await SaveGate.Task;
                    if (!SaveIgnoresCallerCancellation) cancellationToken.ThrowIfCancellationRequested();
                    if (FailSave) throw new IOException(SaveFailure);
                    Stored = summary;
                }
                public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
                { Stored = ""; return Task.CompletedTask; }
            }

            internal sealed class CapturingProvider : ILlmClient
            {
                internal int Calls;
                internal bool Fail;
                public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                    CancellationToken cancellationToken = default)
                {
                    Calls++;
                    return Task.FromResult(new LlmCompletionResult { Ok = !Fail, Content = Fail ? null : "ok", Error = Fail ? "HTTP 503" : null });
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreflight_ConfirmationControlsProviderAndBoundedHistory(bool failConfirmation)
        {
            SummaryPreflightScenario scenario = new();
            scenario.Summary.SaveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.Summary.FailSave = failConfirmation;
            using ExpectedSummaryFailureLog failureLog = failConfirmation
                ? new ExpectedSummaryFailureLog(SummaryPreflightScenario.ControlledSummary.SaveFailure) : null;
            Task<string> turn = scenario.Orchestrator.RunTaskAsync(scenario.Request);
            try
            {
                await SummaryPreflightScenario.AwaitEntered(scenario.Summary.SaveEntered.Task);
                Assert.IsFalse(turn.IsCompleted);
                scenario.AssertOldSourceRetained();
            }
            finally { scenario.Summary.SaveGate.TrySetResult(true); }
            await turn;
            if (failConfirmation)
            {
                scenario.AssertOldSourceRetained();
                scenario.Summary.FailSave = false;
                await scenario.Orchestrator.RunTaskAsync(scenario.Request);
            }
            scenario.AssertPublishedOnce();
        }

        [Test]
        public async Task SummaryPreflight_FailedLoadPreservesSourceAndCanRetry()
        {
            SummaryPreflightScenario scenario = new();
            scenario.Summary.FailLoad = true;
            using ExpectedSummaryFailureLog failureLog = new(SummaryPreflightScenario.ControlledSummary.LoadFailure);
            await scenario.Orchestrator.RunTaskAsync(scenario.Request);
            scenario.AssertOldSourceRetained();
            scenario.Summary.FailLoad = false;
            await scenario.Orchestrator.RunTaskAsync(scenario.Request);
            scenario.AssertPublishedOnce();
        }

        [Test]
        public async Task SummaryPreflight_CancellationDuringLoadDoesNotAppendFromFinally()
        {
            SummaryPreflightScenario scenario = new();
            scenario.Summary.LoadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource cancellation = new();
            Task<string> turn = scenario.Orchestrator.RunTaskAsync(scenario.Request, cancellation.Token);
            await SummaryPreflightScenario.AwaitEntered(scenario.Summary.LoadEntered.Task);
            cancellation.Cancel();
            Assert.That(await SummaryPreflightScenario.CaptureFailure(turn), Is.InstanceOf<OperationCanceledException>());
            scenario.AssertOldSourceRetained();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreflight_CancellationAfterAcceptedWriteWaitsForConfirmationAndSkipsProvider(bool streaming)
        {
            SummaryPreflightScenario scenario = new();
            scenario.Summary.SaveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.Summary.SaveIgnoresCallerCancellation = true;
            using CancellationTokenSource cancellation = new();
            Task turn = streaming ? DrainSummaryTurn(scenario, cancellation.Token)
                : scenario.Orchestrator.RunTaskAsync(scenario.Request, cancellation.Token);
            try
            {
                await SummaryPreflightScenario.AwaitEntered(scenario.Summary.SaveEntered.Task);
                cancellation.Cancel();
                Assert.IsFalse(turn.IsCompleted, "An accepted write must finish host confirmation despite caller cancellation.");
                scenario.AssertOldSourceRetained();
            }
            finally { scenario.Summary.SaveGate.TrySetResult(true); }
            Assert.That(await SummaryPreflightScenario.CaptureFailure(turn), Is.InstanceOf<OperationCanceledException>());
            Assert.AreEqual(0, scenario.Provider.Calls, "Cancellation must stop the main request after durability finishes.");
            Assert.AreEqual(0, scenario.Publications);
            Assert.AreEqual(1, scenario.Memory.Appends.Count, "Confirmed preflight preserves cancellation's write-once user intent.");
            StringAssert.Contains(scenario.Memory.Original[0].Content, scenario.Summary.Stored);
        }

        private static async Task DrainSummaryTurn(SummaryPreflightScenario scenario, CancellationToken cancellationToken)
        {
            await foreach (LlmStreamChunk chunk in scenario.Orchestrator.RunStreamingAsync(scenario.Request, cancellationToken)) { }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreflight_ProviderFailureAndAuthorityDenialRetainUserIntentOnce(bool denyAuthority)
        {
            SummaryPreflightScenario scenario = new(denyAuthority);
            scenario.Provider.Fail = true;
            await scenario.Orchestrator.RunTaskAsync(scenario.Request);
            Assert.AreEqual(denyAuthority ? 0 : 1, scenario.Provider.Calls);
            Assert.AreEqual(0, scenario.Publications);
            Assert.AreEqual(1, scenario.Memory.Appends.Count);
            Assert.AreEqual(scenario.Request.Hint, scenario.Memory.Appends[0].Content);
            if (!denyAuthority) StringAssert.Contains(scenario.Memory.Original[0].Content, scenario.Summary.Stored);
        }

        // ─────────────────────────────────────────────────
        // ARCH-6: BuildCompletionRequest — all fields forwarded
        // ─────────────────────────────────────────────────

        [Test]
        public async Task BuildCompletionRequest_ForwardsAllFields_Sync()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = Build(llm);

            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Tester",
                Hint = "go",
                ForcedToolMode = LlmToolChoiceMode.RequireAny,
                RequiredToolName = "test_tool",
                MaxOutputTokens = 512,
                AllowedToolNames = new[] { "test_tool" }
            });

            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual("Tester", llm.LastRequest.AgentRoleId);
            Assert.AreEqual(LlmToolChoiceMode.RequireAny, llm.LastRequest.ForcedToolMode);
            Assert.AreEqual("test_tool", llm.LastRequest.RequiredToolName);
            Assert.AreEqual(512, llm.LastRequest.MaxOutputTokens);
        }

        [Test]
        public async Task BuildCompletionRequest_ForwardsAllFields_Streaming()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = Build(llm);

            // Streaming also uses BuildCompletionRequest — the same LLM client
            // captures the request that goes into CompleteStreamingAsync, which
            // uses the DIM fallback to call CompleteAsync.
            await foreach (LlmStreamChunk _ in orch.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "Tester",
                               Hint = "stream-go",
                               ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                               RequiredToolName = "quiz",
                               MaxOutputTokens = 256
                           }))
            {
                // consume all chunks
            }

            Assert.IsNotNull(llm.LastRequest, "Streaming path must call the LLM client.");
            Assert.AreEqual("Tester", llm.LastRequest.AgentRoleId);
            Assert.AreEqual(LlmToolChoiceMode.RequireSpecific, llm.LastRequest.ForcedToolMode);
            Assert.AreEqual("quiz", llm.LastRequest.RequiredToolName);
            Assert.AreEqual(256, llm.LastRequest.MaxOutputTokens);
        }

        // ─────────────────────────────────────────────────
        // ARCH-3: SanitizeAndPublish — publishes command
        // ─────────────────────────────────────────────────

        [Test]
        public async Task SanitizeAndPublish_Sync_PublishesCommandWithContent()
        {
            CapturingLlmClient llm = new();
            CapturingSink sink = new();
            AiOrchestrator orch = Build(llm, sink);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "R",
                Hint = "test-hint",
                SourceTag = "test-tag"
            });

            Assert.AreEqual("ok", result);
            Assert.AreEqual(1, sink.PublishCount, "Command must be published exactly once.");
            Assert.AreEqual("ok", sink.LastCommand.JsonPayload);
            Assert.AreEqual("R", sink.LastCommand.SourceRoleId);
            Assert.AreEqual("test-hint", sink.LastCommand.SourceTaskHint);
            Assert.AreEqual("test-tag", sink.LastCommand.SourceTag);
        }

        [Test]
        public async Task SanitizeAndPublish_Streaming_PublishesCommandWithContent()
        {
            CapturingLlmClient llm = new();
            CapturingSink sink = new();
            AiOrchestrator orch = Build(llm, sink);

            await foreach (LlmStreamChunk _ in orch.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "R",
                               Hint = "stream-hint",
                               SourceTag = "stream-tag"
                           }))
            {
                // consume
            }

            Assert.AreEqual(1, sink.PublishCount, "Streaming path must also publish exactly once.");
            Assert.AreEqual("ok", sink.LastCommand.JsonPayload);
            Assert.AreEqual("R", sink.LastCommand.SourceRoleId);
            Assert.AreEqual("stream-hint", sink.LastCommand.SourceTaskHint);
        }

        [Test]
        public async Task RunTaskAsync_ChatSource_OnLlmFailure_ReturnsErrorText()
        {
            FailingLlmClient llm = new() { ErrorMessage = "HTTP 503" };
            AiOrchestrator orch = BuildWithAuth(new TestAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "hi",
                SourceTag = "Chat"
            });

            Assert.IsNotNull(result);
            StringAssert.Contains("503", result);
        }

        [Test]
        public async Task RunTaskAsync_NonChatSource_OnLlmFailure_ReturnsNull()
        {
            FailingLlmClient llm = new() { ErrorMessage = "HTTP 503" };
            AiOrchestrator orch = BuildWithAuth(new TestAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "hi",
                SourceTag = "Lua"
            });

            Assert.IsNull(result);
        }

        [Test]
        public async Task RunTaskAsync_ChatSource_AuthorityDenied_ReturnsMessage()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = BuildWithAuth(new DenyAiAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "SmartChat",
                Hint = "x",
                SourceTag = "Chat"
            });

            Assert.IsNotNull(result);
            StringAssert.Contains("disabled", result.ToLowerInvariant());
            Assert.AreEqual(0, llm.CallCount, "LLM must not run when authority denies.");
        }

        // ─────────────────────────────────────────────────
        // ARCH-7: DIM availability without #if UNITY
        // ─────────────────────────────────────────────────

        [Test]
        public async Task ILlmClient_DimFallback_IsAvailable()
        {
            // This test compiles and runs only if the #if UNITY guard was removed.
            // If the guard were still present, ILlmClient would not have
            // CompleteStreamingAsync in a non-Unity test runner.
            ILlmClient client = new CapturingLlmClient();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(
                               new LlmCompletionRequest
                               {
                                   AgentRoleId = "Test",
                                   SystemPrompt = "sys",
                                   UserPayload = "hi"
                               }))
            {
                chunks++;
                if (chunk.IsDone)
                {
                    break;
                }
            }

            Assert.GreaterOrEqual(chunks, 1, "DIM fallback must produce at least one chunk.");
        }

        [Test]
        public async Task IAiOrchestrationService_DimFallback_IsAvailable()
        {
            // Same as above for the orchestration interface.
            IAiOrchestrationService svc = new FallbackOnlyService();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks++;
            }

            Assert.AreEqual(2, chunks, "DIM fallback: 1 text + 1 terminal.");
        }

        private sealed class FallbackOnlyService : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken ct = default)
            {
                return Task.FromResult("dim-result");
            }

            public void CancelTasks(string scope)
            {
            }
        }
    }
}

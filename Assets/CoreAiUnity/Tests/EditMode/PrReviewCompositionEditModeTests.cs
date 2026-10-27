using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Protects manual composition and the public host correlation/compatibility contracts.</summary>
    public sealed class PrReviewCompositionEditModeTests
    {
        private const string Role = "Teacher";

        [TearDown]
        public void TearDown()
        {
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = null;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EXEC001_ManualCore_OmittedScopeProviderRetainsScopedHistory(bool streaming)
        {
            await AssertManualCompositionAsync(streaming, false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EXEC001_ManualChat_OmittedScopeProviderRetainsStoreAndCoreScope(bool streaming)
        {
            await AssertManualCompositionAsync(streaming, true);
        }

        private static async Task AssertManualCompositionAsync(bool streaming, bool throughChat)
        {
            MutableScopeProvider scope = new MutableScopeProvider();
            InMemoryAgentMemoryStore backing = new InMemoryAgentMemoryStore();
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(backing, scope);
            AgentMemoryPolicy policy = new AgentMemoryPolicy();
            policy.ConfigureChatHistory(Role, true, 4096, false);
            RecordingClient provider = new RecordingClient();
            CoreAISettingsOptions settings = new CoreAISettingsOptions
            {
                EnableStreaming = streaming,
                LlmRequestTimeoutSeconds = 0,
                LogToolCalls = false,
                LogToolCallArguments = false,
                LogToolCallResults = false
            };
            AiPromptComposer prompts = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(), null);
            LocalActorIdentityProvider identity = new LocalActorIdentityProvider();
            // WHY: the core omits the new argument only in the legacy direct-composition case.
            AiOrchestrator core = throughChat
                ? new AiOrchestrator(new Authority(), provider, new Sink(), new Telemetry(), prompts,
                    memory, policy, null, null, settings, identity, null, null, null, null, null, scope)
                : new AiOrchestrator(new Authority(), provider, new Sink(), new Telemetry(), prompts,
                    memory, policy, null, null, settings, identity);
            CoreAiChatService chat = throughChat
                ? new CoreAiChatService(core, policy, settings, memory,
                    GameLoggerUnscopedFallback.Instance, actorIdentityProvider: identity)
                : null;

            for (int actor = 0; actor < 2; actor++)
            {
                string marker = actor == 0 ? "first-scope-secret" : "second-scope-secret";
                string foreign = actor == 0 ? "second-scope-secret" : "first-scope-secret";
                scope.Current = new AgentMemoryScope("tenant", "user-" + actor, "session", "topic");
                memory.AppendChatMessage(Role, "assistant", marker, false);
                AiTaskRequest task = new AiTaskRequest { RoleId = Role, Hint = "question-" + actor };
                if (streaming)
                {
                    IAsyncEnumerable<LlmStreamChunk> response = throughChat
                        ? chat.SendMessageStreamingAsync(task)
                        : core.RunStreamingAsync(task);
                    List<LlmStreamChunk> chunks = new List<LlmStreamChunk>();
                    await foreach (LlmStreamChunk chunk in response) chunks.Add(chunk);
                    Assert.IsTrue(chunks.Last().IsDone);
                    Assert.IsTrue(chunks.All(chunk => chunk.ErrorCode == LlmErrorCode.None),
                        "A scoped text completion must succeed.");
                }
                else
                {
                    string answer = throughChat
                        ? await chat.SendMessageAsync(task)
                        : await core.RunTaskAsync(task);
                    Assert.AreEqual("answer-" + (actor + 1), answer);
                }

                LlmCompletionRequest observed = provider.Requests[actor];
                Assert.IsNotNull(observed.ChatHistory,
                    "The selected nonempty store partition must produce a provider history window.");
                string history = string.Join("\n", observed.ChatHistory.Select(message => message.Text));
                StringAssert.Contains(marker, history, "The store's selected scope must reach the provider.");
                StringAssert.DoesNotContain(foreign, history, "Another selected user cannot enter this prompt.");
                ChatMessage[] persisted = memory.GetChatHistory(Role);
                Assert.IsTrue(persisted.Any(message => message.Content == "question-" + actor));
                Assert.IsTrue(persisted.Any(message => message.Content == "answer-" + (actor + 1)));
                Assert.IsFalse(persisted.Any(message => message.Content.Contains(foreign)));
                Assert.IsEmpty(backing.GetChatHistory(Role), "A nonempty host scope must not write the bare role key.");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EXEC001_QueueDiscoversInnerScopeBeforeWaiting(bool emptyAtAdmission)
        {
            MutableScopeProvider scope = new MutableScopeProvider
                { Current = new AgentMemoryScope("tenant", "", "blocker", "") };
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), scope);
            RecordingClient provider = new RecordingClient { HoldFirstRequest = true };
            LocalActorIdentityProvider identity = new LocalActorIdentityProvider(LocalActorIdentityProvider.DefaultActorId,
                "connection", "", ActorGrantSet.Create(new[] { "keep", "drop" }), AgentMemoryScope.Empty);
            AiOrchestrator core = CreateCore(provider, memory, identity);
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(core,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task<string> blocker = null;
            Task<string> queued = null;
            try
            {
                blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = Role, Hint = "blocker" });
                await RequireCompletion(provider.FirstRequestStarted.Task);
                AgentMemoryScope admitted = emptyAtAdmission ? AgentMemoryScope.Empty
                    : new AgentMemoryScope("tenant", "", "admitted", "");
                scope.Current = admitted;
                memory.AppendChatMessage(Role, "assistant", "admitted secret", false);
                ActorContext actor = identity.GetActorContext(Role)
                    .NarrowGrants(ActorGrantSet.Create(new[] { "keep" }));
                AiTaskRequest request = new AiTaskRequest { RoleId = Role, Hint = "queued question", ActorContext = actor };
                int callsBeforeAdmission = scope.Calls;
                queued = queue.RunTaskAsync(request);
                Assert.IsTrue(request.ActorContext.Value.IsTrusted);
                Assert.IsTrue(request.ActorContext.Value.HasBoundMemoryScope);
                Assert.AreEqual(actor.ActorId, request.ActorContext.Value.ActorId);
                Assert.AreEqual(actor.SessionId, request.ActorContext.Value.SessionId);
                Assert.AreEqual(admitted, request.ActorContext.Value.MemoryScope);
                Assert.IsTrue(request.ActorContext.Value.Grants.Contains("keep"));
                Assert.IsFalse(request.ActorContext.Value.Grants.Contains("drop"));
                Assert.IsFalse(request.ActorContext.Value.Grants.IsUnrestricted);
                Assert.AreEqual(callsBeforeAdmission + 1, scope.Calls);
                Assert.AreEqual(1, provider.Requests.Count, "The second turn must still be waiting for the first.");

                scope.Current = new AgentMemoryScope("tenant", "", "later", "");
                provider.ReleaseFirstRequest.TrySetResult(true);
                await RequireCompletion(blocker);
                await RequireCompletion(queued);
                Assert.AreEqual("answer-2", await queued);
                StringAssert.Contains("admitted secret", string.Join("\n", provider.Requests[1].ChatHistory.Select(message => message.Text)));
                Assert.AreEqual(callsBeforeAdmission + 1, scope.Calls,
                    "The real core must preserve the queue's discovered binding rather than recapture a later provider value.");
                using (AgentMemoryActorScope.Enter(request.ActorContext.Value))
                    Assert.IsTrue(memory.GetChatHistory(Role).Any(message => message.Content == "queued question"));
                Assert.IsEmpty(memory.GetChatHistory(Role), "A later provider partition must not receive the admitted turn.");
            }
            finally
            {
                provider.ReleaseFirstRequest.TrySetResult(true);
                queue.Dispose();
                if (blocker != null) await Capture(blocker);
                if (queued != null) await Capture(queued);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EXEC001_ChatWithoutStoreCapturesInnerBeforeDeferredStream(bool emptyAtAdmission)
        {
            MutableScopeProvider scope = new MutableScopeProvider
            {
                Current = emptyAtAdmission ? AgentMemoryScope.Empty : new AgentMemoryScope("tenant", "", "admitted", "")
            };
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), scope);
            memory.AppendChatMessage(Role, "assistant", "stream admission secret", false);
            RecordingClient provider = new RecordingClient();
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(CreateCore(provider, memory),
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            CoreAiChatService chat = new CoreAiChatService(queue,
                settings: new CoreAISettingsOptions { LlmRequestTimeoutSeconds = 0 });
            AiTaskRequest request = new AiTaskRequest { RoleId = Role, Hint = "deferred question" };
            int callsBeforeAdmission = scope.Calls;
            IAsyncEnumerable<LlmStreamChunk> stream = chat.SendMessageStreamingAsync(request);
            Assert.IsTrue(request.ActorContext.Value.HasBoundMemoryScope);
            Assert.AreEqual(scope.Current, request.ActorContext.Value.MemoryScope);
            Assert.AreEqual(callsBeforeAdmission + 1, scope.Calls);
            Assert.IsEmpty(provider.Requests, "Chat must capture the inner capability before starting enumeration.");

            scope.Current = new AgentMemoryScope("tenant", "", "later", "");
            Task<List<LlmStreamChunk>> reading = ReadStream(stream);
            await RequireCompletion(reading);
            List<LlmStreamChunk> chunks = await reading;
            Assert.IsTrue(chunks.Last().IsDone);
            Assert.IsTrue(chunks.All(chunk => chunk.ErrorCode == LlmErrorCode.None));
            StringAssert.Contains("stream admission secret", string.Join("\n", provider.Requests.Single().ChatHistory.Select(message => message.Text)));
            Assert.AreEqual(callsBeforeAdmission + 1, scope.Calls);
            using (AgentMemoryActorScope.Enter(request.ActorContext.Value))
                Assert.IsTrue(memory.GetChatHistory(Role).Any(message => message.Content == "deferred question"));
            Assert.IsEmpty(memory.GetChatHistory(Role));
        }

        [Test]
        public void EXEC001_OpaqueChatStoreSuppressesUnrelatedInnerProvider()
        {
            MutableScopeProvider own = new MutableScopeProvider
                { Current = new AgentMemoryScope("tenant", "", "opaque-store", "") };
            OpaqueMemoryStore memory = new OpaqueMemoryStore(
                new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), own));
            memory.AppendChatMessage(Role, "user", "opaque history", false);
            MutableScopeProvider unrelated = new MutableScopeProvider
                { Current = new AgentMemoryScope("tenant", "", "unrelated-inner", "") };
            AiOrchestrator core = CreateCore(new RecordingClient(),
                new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), unrelated));
            CoreAiChatService chat = new CoreAiChatService(core, memoryStore: memory);
            AiTaskRequest request = chat.CreateTaskRequest("question", Role);
            Assert.IsTrue(request.ActorContext.Value.IsTrusted);
            Assert.IsFalse(request.ActorContext.Value.HasBoundMemoryScope,
                "An opaque supplied store must retain its own legacy resolution rather than the inner core's unrelated scope.");
            Assert.AreEqual(0, unrelated.Calls);
            Assert.IsTrue(chat.TryGetPersistedChatHistory(Role, out ChatMessage[] history, default));
            Assert.AreEqual("opaque history", history.Single().Content);
            chat.ClearContext(Role, true, false);
            Assert.IsEmpty(memory.GetChatHistory(Role));
            Assert.AreEqual(0, unrelated.Calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EXEC001_ActorlessUnknownQueuePreservesLegacyMemoryResolution(bool hasAmbientScope)
        {
            MutableScopeProvider provider = new MutableScopeProvider
                { Current = new AgentMemoryScope("tenant", "", "legacy-provider", "") };
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), provider);
            ActorlessMemoryOrchestrator inner = new ActorlessMemoryOrchestrator(memory);
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            AgentMemoryScope ambient = new AgentMemoryScope("tenant", "", "ambient", "");
            using (hasAmbientScope ? AgentMemoryActorScope.Enter(ambient) : null)
            {
                Task<string> turn = queue.RunTaskAsync(new AiTaskRequest { RoleId = Role, Hint = "legacy question" });
                await RequireCompletion(turn);
                Assert.AreEqual("done", await turn);
                Assert.AreEqual(hasAmbientScope, inner.HasMemoryFrame,
                    "An actorless Unknown queue must not install a synthetic bound Empty frame.");
                if (hasAmbientScope) Assert.AreEqual(ambient, inner.MemoryScope);
                Assert.AreEqual("legacy question", memory.GetChatHistory(Role).Single().Content);
            }

            Assert.AreEqual(hasAmbientScope ? 0 : 2, provider.Calls,
                "Without an ambient scope, both legacy store operations must reach its own provider.");
        }

        [TestCase("StopAgent", true)]
        [TestCase("StopAgent", false)]
        [TestCase("ClearContext", true)]
        [TestCase("ClearContext", false)]
        public void HOST001_LegacyMethodsRemainExactAndDelegateBindable(string name, bool facade)
        {
            Type owner = facade ? typeof(CoreAi) : typeof(CoreAiChatService);
            Type[] parameters = name == "StopAgent"
                ? new[] { typeof(string) }
                : new[] { typeof(string), typeof(bool), typeof(bool) };
            BindingFlags flags = BindingFlags.Public | (facade ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo method = owner.GetMethod(name, flags, null, parameters, null);
            Assert.IsNotNull(method, "Adding optional actor arguments must preserve the old exact method signature.");
            if (facade && name == "ClearContext")
            {
                ParameterInfo[] metadata = method.GetParameters();
                Assert.IsTrue(metadata[1].IsOptional);
                Assert.AreEqual(true, metadata[1].DefaultValue);
                Assert.IsTrue(metadata[2].IsOptional);
                Assert.AreEqual(true, metadata[2].DefaultValue);
            }
            Type delegateType = name == "StopAgent" ? typeof(Action<string>) : typeof(Action<string, bool, bool>);
            CancellationRecorder recorder = new CancellationRecorder();
            InMemoryAgentMemoryStore memory = new InMemoryAgentMemoryStore();
            memory.AppendChatMessage(Role, "user", "retained", false);
            CoreAiChatService chat = new CoreAiChatService(recorder, memoryStore: memory);
            Delegate legacy = facade
                ? Delegate.CreateDelegate(delegateType, method)
                : Delegate.CreateDelegate(delegateType, chat, method);
            Assert.IsNotNull(legacy);
            Delegate compileVisible;
            if (name == "StopAgent")
                compileVisible = facade ? new Action<string>(CoreAi.StopAgent) : new Action<string>(chat.StopAgent);
            else
                compileVisible = facade ? new Action<string, bool, bool>(CoreAi.ClearContext)
                    : new Action<string, bool, bool>(chat.ClearContext);
            Assert.IsNotNull(compileVisible);
            if (!facade && name == "StopAgent")
            {
                legacy.DynamicInvoke(Role);
                Assert.AreEqual(Role, recorder.CancelledRole);
                Assert.AreEqual(1, recorder.ActorCancellations);
            }
            else if (!facade)
            {
                legacy.DynamicInvoke(Role, true, false);
                Assert.IsEmpty(memory.GetChatHistory(Role));
            }
        }

        [Test]
        public void HOST001_LegacyHistoryReflectionSignatureStillReturnsHistory()
        {
            MethodInfo method = typeof(CoreAiChatService).GetMethod("TryGetPersistedChatHistory", new[]
            {
                typeof(string), typeof(ChatMessage[]).MakeByRefType(), typeof(int)
            });
            Assert.IsNotNull(method, "An out-history consumer must retain its exact three-argument method.");
            InMemoryAgentMemoryStore memory = new InMemoryAgentMemoryStore();
            memory.AppendChatMessage(Role, "user", "saved fact", false);
            CoreAiChatService chat = new CoreAiChatService(new CancellationRecorder(), memoryStore: memory);
            ParameterInfo limit = method.GetParameters()[2];
            Assert.AreEqual("maxMessages", limit.Name);
            Assert.IsTrue(limit.IsOptional);
            Assert.AreEqual(0, limit.DefaultValue);
            LegacyHistoryReader legacy = chat.TryGetPersistedChatHistory;
            ActorHistoryReader actorAware = chat.TryGetPersistedChatHistory;
            Assert.IsTrue(chat.TryGetPersistedChatHistory(Role, out ChatMessage[] defaultLiteral, default));
            Assert.AreEqual("saved fact", defaultLiteral.Single().Content);
            Assert.IsTrue(chat.TryGetPersistedChatHistory(Role, out ChatMessage[] optionalLimit));
            Assert.AreEqual("saved fact", optionalLimit.Single().Content);
            Assert.IsTrue(legacy(Role, out ChatMessage[] integerLimit, 1));
            Assert.AreEqual("saved fact", integerLimit.Single().Content);
            ActorContext actor = new LocalActorIdentityProvider().GetActorContext(Role);
            Assert.IsTrue(actorAware(Role, out ChatMessage[] actorHistory, 0, actor));
            Assert.AreEqual("saved fact", actorHistory.Single().Content);
            object[] arguments = { Role, null, 0 };
            Assert.IsTrue((bool)method.Invoke(chat, arguments));
            Assert.AreEqual("saved fact", ((ChatMessage[])arguments[1]).Single().Content);
        }

        [TestCase("chat")]
        [TestCase("component")]
        [TestCase("orchestrator")]
        public void HOST001_LegacyConstructorAritiesRemainAvailable(string kind)
        {
            Type owner;
            Type[] parameters;
            if (kind == "chat")
            {
                owner = typeof(CoreAiChatService);
                parameters = new[] { typeof(IAiOrchestrationService), typeof(AgentMemoryPolicy),
                    typeof(ICoreAISettings), typeof(IAgentMemoryStore), typeof(IGameLogger), typeof(ILlmClient),
                    typeof(CoreAI.Vision.IAgentCameraService), typeof(IActorIdentityProvider) };
            }
            else if (kind == "component")
            {
                owner = typeof(CoreAiComponentCommandExecutor);
                parameters = new[] { typeof(IGameLogger) };
            }
            else
            {
                owner = typeof(AiOrchestrator);
                parameters = new[] { typeof(IAuthorityHost), typeof(ILlmClient), typeof(IAiGameCommandSink),
                    typeof(ISessionTelemetryProvider), typeof(AiPromptComposer), typeof(IAgentMemoryStore),
                    typeof(AgentMemoryPolicy), typeof(IRoleStructuredResponsePolicy), typeof(IAiOrchestrationMetrics),
                    typeof(ICoreAISettings), typeof(IActorIdentityProvider), typeof(IConversationContextManager),
                    typeof(IAgentTurnTraceSink), typeof(IContextBudgetPolicy), typeof(ITokenEstimator),
                    typeof(IConversationCompactionCoordinator) };
            }
            ConstructorInfo legacy = owner.GetConstructor(parameters);
            Assert.IsNotNull(legacy,
                "A new optional dependency must not remove an existing exact constructor signature.");
            int optionalStart = kind == "chat" ? 1 : kind == "orchestrator" ? 11 : parameters.Length;
            ParameterInfo[] metadata = legacy.GetParameters();
            for (int index = 0; index < metadata.Length; index++)
            {
                Assert.AreEqual(index >= optionalStart, metadata[index].IsOptional,
                    "The legacy constructor's optional suffix must retain its metadata.");
                if (index >= optionalStart) Assert.IsNull(metadata[index].DefaultValue);
            }

            Type extra = kind == "component" ? typeof(CoreAiComponentCommandOptions) : typeof(IAgentMemoryScopeProvider);
            ConstructorInfo extended = owner.GetConstructor(parameters.Concat(new[] { extra }).ToArray());
            Assert.IsNotNull(extended);
            Assert.IsTrue(extended.GetParameters().All(parameter => !parameter.IsOptional),
                "Extended constructors require full arity so legacy optional calls have one candidate.");
        }

        [TestCase("caller-trace", "started")]
        [TestCase(" caller-trace ", "started")]
        [TestCase(" caller-trace ", "completed")]
        [TestCase(" caller-trace ", "failed")]
        public async Task HOST002_ProducerForwardedTraceRearmsItsOwnTurn(string trace, string eventKind)
        {
            VirtualIdleClock clock = new VirtualIdleClock();
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = clock.Schedule;
            ProgressOrchestrator orchestrator = new ProgressOrchestrator();
            CoreAiChatService chat = ProgressChat(orchestrator);
            using CancellationTokenSource caller = new CancellationTokenSource();
            Task<string> turn = chat.SendMessageAsync(new AiTaskRequest
                { RoleId = Role, Hint = "active", TraceId = trace }, caller.Token);
            try
            {
                await orchestrator.Started.Task;
                clock.Advance(TimeSpan.FromMilliseconds(500));
                orchestrator.Publish(eventKind);
                Assert.AreEqual(TimeSpan.FromMilliseconds(1500), clock.PendingDeadline);
                clock.Advance(TimeSpan.FromMilliseconds(750));
                Assert.IsFalse(orchestrator.Token.IsCancellationRequested,
                    "Progress published from the received request must survive the original idle deadline.");
                orchestrator.Finish.TrySetResult("done");
                Assert.AreEqual("done", await turn);
            }
            finally
            {
                caller.Cancel();
                await Capture(turn);
            }
        }

        [TestCase("actor")]
        [TestCase("role")]
        [TestCase("trace")]
        public async Task HOST002_ForeignProgressCannotRearmTheTurn(string mismatch)
        {
            VirtualIdleClock clock = new VirtualIdleClock();
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = clock.Schedule;
            ProgressOrchestrator orchestrator = new ProgressOrchestrator();
            CoreAiChatService chat = ProgressChat(orchestrator);
            using CancellationTokenSource caller = new CancellationTokenSource();
            Task<string> turn = chat.SendMessageAsync(new AiTaskRequest
                { RoleId = Role, Hint = "stalled", TraceId = " caller-trace " }, caller.Token);
            try
            {
                await orchestrator.Started.Task;
                clock.Advance(TimeSpan.FromMilliseconds(500));
                orchestrator.Publish("started", mismatch);
                Assert.AreEqual(TimeSpan.FromSeconds(1), clock.PendingDeadline);
                clock.Advance(TimeSpan.FromMilliseconds(500));
                Assert.IsTrue(orchestrator.Token.IsCancellationRequested);
                Assert.IsInstanceOf<LlmOperationTimeoutException>(await Capture(turn));
                Assert.IsFalse(caller.IsCancellationRequested);
            }
            finally
            {
                caller.Cancel();
                await Capture(turn);
            }
        }

        private static CoreAiChatService ProgressChat(ProgressOrchestrator orchestrator)
        {
            return new CoreAiChatService(orchestrator,
                settings: new CoreAISettingsOptions { LlmRequestTimeoutSeconds = 1, EnableStreaming = false },
                actorIdentityProvider: new LocalActorIdentityProvider("actor-a", "connection-a", "",
                    ActorGrantSet.None, AgentMemoryScope.Empty));
        }

        private static async Task<Exception> Capture(Task task)
        {
            try { await task; return null; }
            catch (Exception exception) { return exception; }
        }

        private delegate bool LegacyHistoryReader(string roleId, out ChatMessage[] messages, int maxMessages);
        private delegate bool ActorHistoryReader(string roleId, out ChatMessage[] messages, int maxMessages, ActorContext? actorContext);

        private static AiOrchestrator CreateCore(RecordingClient provider, IAgentMemoryStore memory,
            IActorIdentityProvider identity = null)
        {
            AgentMemoryPolicy policy = new AgentMemoryPolicy();
            policy.ConfigureChatHistory(Role, true, 4096, false);
            CoreAISettingsOptions settings = new CoreAISettingsOptions
            {
                LlmRequestTimeoutSeconds = 0, LogToolCalls = false,
                LogToolCallArguments = false, LogToolCallResults = false
            };
            return new AiOrchestrator(new Authority(), provider, new Sink(), new Telemetry(),
                new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider(), null),
                memory, policy, null, null, settings, identity ?? new LocalActorIdentityProvider());
        }

        private static async Task RequireCompletion(Task task)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(task, finished, "The controlled operation must complete within five seconds.");
            await task;
        }

        private static async Task<List<LlmStreamChunk>> ReadStream(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            List<LlmStreamChunk> chunks = new List<LlmStreamChunk>();
            await foreach (LlmStreamChunk chunk in stream) chunks.Add(chunk);
            return chunks;
        }

        private sealed class OpaqueMemoryStore : IAgentMemoryStore
        {
            private readonly IAgentMemoryStore _inner;
            internal OpaqueMemoryStore(IAgentMemoryStore inner) { _inner = inner; }
            public bool TryLoad(string roleId, out AgentMemoryState state) => _inner.TryLoad(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => _inner.Save(roleId, state);
            public void Clear(string roleId) => _inner.Clear(roleId);
            public void ClearChatHistory(string roleId) => _inner.ClearChatHistory(roleId);
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) =>
                _inner.AppendChatMessage(roleId, role, content, persistToDisk);
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => _inner.GetChatHistory(roleId, maxMessages);
        }

        private sealed class ActorlessMemoryOrchestrator : IAiOrchestrationService
        {
            private readonly IAgentMemoryStore _memory;
            internal bool HasMemoryFrame;
            internal AgentMemoryScope MemoryScope;
            internal ActorlessMemoryOrchestrator(IAgentMemoryStore memory) { _memory = memory; }
            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
            {
                HasMemoryFrame = AgentMemoryScopeExecutionContext.TryGet(out AgentMemoryScope captured, out _);
                MemoryScope = captured;
                _memory.AppendChatMessage(request.RoleId, "user", request.Hint, false);
                return Task.FromResult("done");
            }
            public void CancelTasks(string cancellationScope) { }
        }

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            internal AgentMemoryScope Current;
            internal int Calls;
            public AgentMemoryScope GetScope(string roleId) { Calls++; return Current; }
        }

        private sealed class Authority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) { }
        }

        private sealed class Telemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot() => new GameSessionSnapshot();
        }

        private sealed class RecordingClient : ILlmClient
        {
            internal readonly List<LlmCompletionRequest> Requests = new List<LlmCompletionRequest>();
            internal bool HoldFirstRequest;
            internal readonly TaskCompletionSource<bool> FirstRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                int sequence = Requests.Count;
                if (HoldFirstRequest && sequence == 1)
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(() => ReleaseFirstRequest.TrySetCanceled(cancellationToken));
                    FirstRequestStarted.TrySetResult(true);
                    await ReleaseFirstRequest.Task;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmCompletionResult { Ok = true, Content = "answer-" + sequence };
            }
        }

        private sealed class CancellationRecorder : IAiOrchestrationService, IActorAiTaskCancellation
        {
            internal string CancelledRole;
            internal int ActorCancellations;
            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default) => Task.FromResult("done");
            public void CancelTasks(string cancellationScope) { }
            public void CancelActorTasks(ActorContext actorContext, string roleId = null)
            {
                ActorCancellations++;
                CancelledRole = roleId;
            }
        }

        private sealed class ProgressOrchestrator : IAiOrchestrationService
        {
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<string> Finish = new TaskCompletionSource<string>();
            internal AiTaskRequest Request;
            internal CancellationToken Token;
            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                Request = request;
                Token = ct;
                using CancellationTokenRegistration registration = ct.Register(() => Finish.TrySetCanceled(ct));
                Started.TrySetResult(true);
                return await Finish.Task;
            }
            public void CancelTasks(string cancellationScope) { }
            internal void Publish(string eventKind, string mismatch = null)
            {
                LlmToolCallInfo info = new LlmToolCallInfo(
                    mismatch == "trace" ? "foreign-trace" : Request.TraceId,
                    mismatch == "role" ? "OtherRole" : Request.RoleId,
                    "call", "tool", "{}", mismatch == "actor" ? "other-actor" : Request.ActorContext.Value.ActorId);
                if (eventKind == "started") CoreAi.NotifyToolCallStarted(new LlmToolCallStarted(info));
                else if (eventKind == "completed") CoreAi.NotifyToolCallCompleted(new LlmToolCallCompleted(info, "{}", 0));
                else CoreAi.NotifyToolCallFailed(new LlmToolCallFailed(info, "controlled refusal", 0));
            }
        }

        private sealed class VirtualIdleClock
        {
            private readonly List<Deadline> _deadlines = new List<Deadline>();
            internal TimeSpan Now { get; private set; }
            internal TimeSpan? PendingDeadline => _deadlines.Where(deadline => deadline.Active)
                .Select(deadline => (TimeSpan?)deadline.Due).OrderBy(due => due).FirstOrDefault();
            internal IDisposable Schedule(CancellationTokenSource source, TimeSpan window)
            {
                Deadline deadline = new Deadline(source, Now + window);
                _deadlines.Add(deadline);
                return deadline;
            }
            internal void Advance(TimeSpan delta)
            {
                Now += delta;
                foreach (Deadline deadline in _deadlines.Where(item => item.Active && item.Due <= Now).ToArray())
                    deadline.Fire();
            }
            private sealed class Deadline : IDisposable
            {
                private readonly CancellationTokenSource _source;
                internal readonly TimeSpan Due;
                internal bool Active = true;
                internal Deadline(CancellationTokenSource source, TimeSpan due) { _source = source; Due = due; }
                internal void Fire() { Active = false; _source.Cancel(); }
                public void Dispose() { Active = false; }
            }
        }
    }
}

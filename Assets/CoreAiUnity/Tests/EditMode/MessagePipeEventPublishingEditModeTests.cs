#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Logging;
using CoreAI.Messaging;
using MessagePipe;
using MessagePipe.VContainer;
using NUnit.Framework;
using VContainer;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Comprehensive tests verifying that all MessagePipe events are correctly
    /// published and subscribable through the CoreAI pipeline — covering both
    /// streaming and non-streaming paths, child VContainer scopes, and all 8
    /// broker types registered by <see cref="CoreServicesInstaller"/>.
    /// </summary>
    [TestFixture]
    public sealed class MessagePipeEventPublishingEditModeTests
    {
        // ────────── Group 1: Bootstrap & Broker Smoke ──────────

        [Test]
        public void AllSevenLlmBrokers_PublishAndSubscribe()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            // LlmBackendSelected
            AssertRoundTrip(
                new LlmBackendSelected("t", "r", "p", LlmExecutionMode.Auto, "Test"),
                e => e.RoleId == "r" && e.TraceId == "t");

            // LlmRequestStarted
            AssertRoundTrip(
                new LlmRequestStarted("t", "r", "p", LlmExecutionMode.Auto, true),
                e => e.RoleId == "r" && e.Streaming);

            // LlmRequestCompleted
            AssertRoundTrip(
                new LlmRequestCompleted("t", "r", "p", LlmExecutionMode.Auto, false, true, ""),
                e => e.RoleId == "r" && e.Success);

            // LlmUsageReported
            AssertRoundTrip(
                new LlmUsageReported("t", "r", "p", LlmExecutionMode.Auto, "m", 10, 20, 30, false, true),
                e => e.PromptTokens == 10 && e.CompletionTokens == 20);

            // LlmToolCallStarted
            AssertRoundTrip(
                new LlmToolCallStarted("t", "r", "memory", "{}"),
                e => e.ToolName == "memory");

            // LlmToolCallCompleted
            AssertRoundTrip(
                new LlmToolCallCompleted("t", "r", "memory", "{}", "{\"ok\":true}", 5d),
                e => e.ToolName == "memory" && e.DurationMs == 5d);

            // LlmToolCallFailed
            AssertRoundTrip(
                new LlmToolCallFailed("t", "r", "memory", "{}", "boom", 1d),
                e => e.ToolName == "memory" && e.Error == "boom");
        }

        [Test]
        public void BootstrapIsIdempotent_DoesNotThrowOrDuplicateProvider()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();
            Assert.DoesNotThrow(() =>
                GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics());
            Assert.That(GlobalMessagePipe.IsInitialized, Is.True);
        }

        // ────── Group 2: ToolExecutionPolicy → MessagePipe (non-streaming) ──────

        [Test]
        public async Task ExecuteSingleAsync_Success_PublishesStartedThenCompleted()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallCompleted> completedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()
                .Subscribe(e => completedEvents.Add(e));

            ToolExecutionPolicy policy = CreatePolicy("Teacher", "trace-1");
            MEAI.ChatOptions options = MakeChatOptions(("memory", "{\"Success\":true}"));
            MEAI.FunctionCallContent fc = MakeToolCall("memory");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, options, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(1, startedEvents.Count, "Should publish LlmToolCallStarted");
            Assert.AreEqual("memory", startedEvents[0].ToolName);
            Assert.AreEqual("Teacher", startedEvents[0].RoleId);
            Assert.AreEqual(1, completedEvents.Count, "Should publish LlmToolCallCompleted");
            Assert.AreEqual("memory", completedEvents[0].ToolName);
            Assert.That(completedEvents[0].DurationMs, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public async Task ExecuteSingleAsync_ToolFails_PublishesStartedThenFailed()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallFailed> failedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallFailed>()
                .Subscribe(e => failedEvents.Add(e));

            ToolExecutionPolicy policy = CreatePolicy("Teacher", "trace-2");
            MEAI.ChatOptions options = MakeChatOptions(("memory", "{\"Success\":false,\"Error\":\"oops\"}"));
            MEAI.FunctionCallContent fc = MakeToolCall("memory");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, options, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, startedEvents.Count);
            Assert.AreEqual(1, failedEvents.Count,
                "Should publish LlmToolCallFailed for unsuccessful result");
            Assert.AreEqual("memory", failedEvents[0].ToolName);
        }

        [Test]
        public async Task ExecuteSingleAsync_ToolThrows_PublishesStartedThenFailed()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallFailed> failedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallFailed>()
                .Subscribe(e => failedEvents.Add(e));

            ToolExecutionPolicy policy = CreatePolicyForTools("Teacher", "trace-3",
                new StubTool("boom_tool"));
            MEAI.ChatOptions options = MakeChatOptionsWithThrow("boom_tool");
            MEAI.FunctionCallContent fc = MakeToolCall("boom_tool");

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(fc, options, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, startedEvents.Count, "Should publish Started");
            Assert.AreEqual(1, failedEvents.Count, "Should publish Failed on exception");
            Assert.That(failedEvents[0].Error, Does.Contain("kaboom"));
        }

        [Test]
        public async Task ExecuteSingleAsync_ToolNotFound_PublishesFailedOnly()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallFailed> failedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallFailed>()
                .Subscribe(e => failedEvents.Add(e));

            ToolExecutionPolicy policy = CreatePolicy("Teacher", "trace-4");
            // No tools in ChatOptions → "not found"
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool>() };
            MEAI.FunctionCallContent fc = MakeToolCall("memory");

            await policy.ExecuteSingleAsync(fc, options, CancellationToken.None);

            Assert.AreEqual(0, startedEvents.Count, "Should NOT publish Started for missing tool");
            Assert.AreEqual(1, failedEvents.Count, "Should publish Failed for missing tool");
            Assert.AreEqual("memory", failedEvents[0].ToolName);
        }

        [Test]
        public async Task ExecuteBatchAsync_TwoTools_PublishesStartedCompletedForEach()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallCompleted> completedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()
                .Subscribe(e => completedEvents.Add(e));

            ToolExecutionPolicy policy = CreatePolicyForTools("Batch", "trace-5",
                new StubTool("tool_a"), new StubTool("tool_b"));
            MEAI.ChatOptions options = MakeChatOptions(
                ("tool_a", "{\"Success\":true}"),
                ("tool_b", "{\"Success\":true}"));

            List<MEAI.FunctionCallContent> calls = new()
            {
                MakeToolCall("tool_a"),
                MakeToolCall("tool_b")
            };

            ToolExecutionPolicy.BatchToolCallResult batch =
                await policy.ExecuteBatchAsync(calls, options, CancellationToken.None);

            Assert.IsFalse(batch.AnyFailed);
            Assert.AreEqual(2, startedEvents.Count, "Should fire Started for each tool");
            Assert.AreEqual(2, completedEvents.Count, "Should fire Completed for each tool");
            Assert.AreEqual("tool_a", startedEvents[0].ToolName);
            Assert.AreEqual("tool_b", startedEvents[1].ToolName);
        }

        // ────── Group 3: SmartToolCallingChatClient end-to-end ──────

        [Test]
        public async Task SmartToolCalling_NonStreaming_PublishesFullLifecycle()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            List<LlmToolCallStarted> startedEvents = new();
            List<LlmToolCallCompleted> completedEvents = new();

            using IDisposable s1 = GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                .Subscribe(e => startedEvents.Add(e));
            using IDisposable s2 = GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()
                .Subscribe(e => completedEvents.Add(e));

            int iter = 0;
            ScriptedChatClient inner = new(_ =>
            {
                iter++;
                return iter == 1
                    ? MakeToolCallResponse("memory")
                    : MakeTextResponse("Done.");
            });

            ICoreAISettings settings = new StubSettings();
            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true,
                new List<ILlmTool> { new StubTool("memory") },
                "Teacher", 3, "e2e-1",
                MessagePipeToolCallEventPublisher.Instance,
                NullToolExecutionNotifier.Instance);

            MEAI.AIFunction memFunc = MEAI.AIFunctionFactory.Create(
                (Func<string>)(() => "{\"Success\":true}"),
                new MEAI.AIFunctionFactoryOptions { Name = "memory" });
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { memFunc } };
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.User, "test")
            };

            await client.GetResponseAsync(messages, options);

            Assert.AreEqual(1, startedEvents.Count, "Should publish Started");
            Assert.AreEqual(1, completedEvents.Count, "Should publish Completed");
            Assert.AreEqual("memory", completedEvents[0].ToolName);
            Assert.AreEqual("Teacher", completedEvents[0].RoleId);
        }

        // ────── Group 4: Streaming parity ──────

        [Test]
        public async Task StreamingAndNonStreaming_ProduceSameEventCountAndToolName()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

            // Non-streaming
            List<LlmToolCallStarted> nsStarted = new();
            List<LlmToolCallCompleted> nsCompleted = new();
            using (GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                       .Subscribe(e => nsStarted.Add(e)))
            using (GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()
                       .Subscribe(e => nsCompleted.Add(e)))
            {
                ToolExecutionPolicy policy = CreatePolicy("Role", "ns");
                await policy.ExecuteSingleAsync(MakeToolCall("memory"),
                    MakeChatOptions(("memory", "{\"Success\":true}")),
                    CancellationToken.None);
            }

            // Streaming path (same ToolExecutionPolicy + MessagePipeToolCallEventPublisher)
            List<LlmToolCallStarted> sStarted = new();
            List<LlmToolCallCompleted> sCompleted = new();
            using (GlobalMessagePipe.GetSubscriber<LlmToolCallStarted>()
                       .Subscribe(e => sStarted.Add(e)))
            using (GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()
                       .Subscribe(e => sCompleted.Add(e)))
            {
                ToolExecutionPolicy policy = CreatePolicy("Role", "s");
                List<MEAI.FunctionCallContent> calls = new() { MakeToolCall("memory") };
                await policy.ExecuteBatchAsync(calls,
                    MakeChatOptions(("memory", "{\"Success\":true}")),
                    CancellationToken.None);
            }

            Assert.AreEqual(nsStarted.Count, sStarted.Count,
                "Started event count must match between streaming and non-streaming");
            Assert.AreEqual(nsCompleted.Count, sCompleted.Count,
                "Completed event count must match between streaming and non-streaming");
            Assert.AreEqual(nsStarted[0].ToolName, sStarted[0].ToolName);
            Assert.AreEqual(nsCompleted[0].ToolName, sCompleted[0].ToolName);
        }

        // ────── Group 5: VContainer Child Scope ──────

        [Test]
        public void ChildScope_CanSubscribeToParentBrokerEvents()
        {
            ContainerBuilder parentBuilder = new();
            MessagePipeOptions opts = parentBuilder.RegisterMessagePipe();
            parentBuilder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            parentBuilder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            parentBuilder.RegisterMessageBroker<LlmToolCallFailed>(opts);

            using IObjectResolver parent = parentBuilder.Build();
            using IObjectResolver child = parent.CreateScope(_ => { });

            List<LlmToolCallCompleted> received = new();
            using IDisposable d = child.Resolve<ISubscriber<LlmToolCallCompleted>>()
                .Subscribe(e => received.Add(e));

            parent.Resolve<IPublisher<LlmToolCallCompleted>>().Publish(
                new LlmToolCallCompleted("t", "Teacher", "memory", "{}", "{\"ok\":true}", 5d));

            Assert.AreEqual(1, received.Count, "Child scope should receive parent events");
            Assert.AreEqual("memory", received[0].ToolName);
            Assert.AreEqual("Teacher", received[0].RoleId);
        }

        [Test]
        public void ChildScope_ReceivesAllEightEventTypes()
        {
            ContainerBuilder parentBuilder = new();
            MessagePipeOptions opts = parentBuilder.RegisterMessagePipe();
            parentBuilder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            parentBuilder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            parentBuilder.RegisterMessageBroker<LlmToolCallFailed>(opts);
            parentBuilder.RegisterMessageBroker<LlmBackendSelected>(opts);
            parentBuilder.RegisterMessageBroker<LlmRequestStarted>(opts);
            parentBuilder.RegisterMessageBroker<LlmRequestCompleted>(opts);
            parentBuilder.RegisterMessageBroker<LlmUsageReported>(opts);
            parentBuilder.RegisterMessageBroker<ApplyAiGameCommand>(opts);

            using IObjectResolver parent = parentBuilder.Build();
            using IObjectResolver child = parent.CreateScope(_ => { });

            int total = 0;
            using IDisposable d1 = child.Resolve<ISubscriber<LlmToolCallStarted>>().Subscribe(_ => total++);
            using IDisposable d2 = child.Resolve<ISubscriber<LlmToolCallCompleted>>().Subscribe(_ => total++);
            using IDisposable d3 = child.Resolve<ISubscriber<LlmToolCallFailed>>().Subscribe(_ => total++);
            using IDisposable d4 = child.Resolve<ISubscriber<LlmBackendSelected>>().Subscribe(_ => total++);
            using IDisposable d5 = child.Resolve<ISubscriber<LlmRequestStarted>>().Subscribe(_ => total++);
            using IDisposable d6 = child.Resolve<ISubscriber<LlmRequestCompleted>>().Subscribe(_ => total++);
            using IDisposable d7 = child.Resolve<ISubscriber<LlmUsageReported>>().Subscribe(_ => total++);
            using IDisposable d8 = child.Resolve<ISubscriber<ApplyAiGameCommand>>().Subscribe(_ => total++);

            parent.Resolve<IPublisher<LlmToolCallStarted>>()
                .Publish(new LlmToolCallStarted("", "", "", ""));
            parent.Resolve<IPublisher<LlmToolCallCompleted>>()
                .Publish(new LlmToolCallCompleted("", "", "", "", "", 0));
            parent.Resolve<IPublisher<LlmToolCallFailed>>()
                .Publish(new LlmToolCallFailed("", "", "", "", "", 0));
            parent.Resolve<IPublisher<LlmBackendSelected>>()
                .Publish(new LlmBackendSelected("", "", "", LlmExecutionMode.Auto, ""));
            parent.Resolve<IPublisher<LlmRequestStarted>>()
                .Publish(new LlmRequestStarted("", "", "", LlmExecutionMode.Auto, false));
            parent.Resolve<IPublisher<LlmRequestCompleted>>()
                .Publish(new LlmRequestCompleted("", "", "", LlmExecutionMode.Auto, false, true, ""));
            parent.Resolve<IPublisher<LlmUsageReported>>()
                .Publish(new LlmUsageReported("", "", "", LlmExecutionMode.Auto, "", 0, 0, 0, false, true));
            parent.Resolve<IPublisher<ApplyAiGameCommand>>()
                .Publish(new ApplyAiGameCommand { CommandTypeId = "test" });

            Assert.AreEqual(8, total, "Child scope must receive all 8 event types from parent");
        }

        // ────── Group 6: ApplyAiGameCommand ──────

        [Test]
        public void ApplyAiGameCommand_PublishAndSubscribe_ViaVContainer()
        {
            ContainerBuilder builder = new();
            MessagePipeOptions opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<ApplyAiGameCommand>(opts);
            using IObjectResolver resolver = builder.Build();

            ApplyAiGameCommand received = null;
            using IDisposable d = resolver.Resolve<ISubscriber<ApplyAiGameCommand>>()
                .Subscribe(e => received = e);

            resolver.Resolve<IPublisher<ApplyAiGameCommand>>().Publish(new ApplyAiGameCommand
            {
                CommandTypeId = "lua",
                JsonPayload = "{\"action\":\"spawn\"}",
                SourceRoleId = "Teacher"
            });

            Assert.IsNotNull(received);
            Assert.AreEqual("lua", received.CommandTypeId);
            Assert.AreEqual("Teacher", received.SourceRoleId);
        }

        // ════════════════════ Helpers ════════════════════

        private static void AssertRoundTrip<T>(T evt, Func<T, bool> predicate)
        {
            int count = 0;
            T last = default;
            using (GlobalMessagePipe.GetSubscriber<T>().Subscribe(e =>
                   {
                       count++;
                       last = e;
                   }))
            {
                GlobalMessagePipe.GetPublisher<T>().Publish(evt);
            }

            Assert.AreEqual(1, count, $"Expected 1 {typeof(T).Name} event");
            Assert.IsTrue(predicate(last), $"{typeof(T).Name} predicate failed");
        }

        private static ToolExecutionPolicy CreatePolicy(string roleId, string traceId)
        {
            return new ToolExecutionPolicy(
                NullLog.Instance, new StubSettings(),
                new List<ILlmTool> { new StubTool("memory") },
                true, roleId, 3, traceId,
                MessagePipeToolCallEventPublisher.Instance,
                NullToolExecutionNotifier.Instance);
        }

        private static ToolExecutionPolicy CreatePolicyForTools(string roleId, string traceId,
            params ILlmTool[] tools)
        {
            return new ToolExecutionPolicy(
                NullLog.Instance, new StubSettings(), tools.ToList(),
                true, roleId, 3, traceId,
                MessagePipeToolCallEventPublisher.Instance,
                NullToolExecutionNotifier.Instance);
        }

        private static MEAI.FunctionCallContent MakeToolCall(string name)
        {
            return new MEAI.FunctionCallContent(
                $"call_{name}_{Guid.NewGuid():N}",
                name,
                new Dictionary<string, object?> { { "key", "value" } });
        }

        private static MEAI.ChatOptions MakeChatOptions(params (string name, string result)[] tools)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            foreach ((string name, string result) in tools)
            {
                opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => result),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = name,
                        Description = $"Test tool {name}"
                    }));
            }

            return opts;
        }

        private static MEAI.ChatOptions MakeChatOptionsWithThrow(string name)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            opts.Tools.Add(MEAI.AIFunctionFactory.Create(
                (Func<string>)(() => throw new InvalidOperationException("kaboom")),
                new MEAI.AIFunctionFactoryOptions { Name = name }));
            return opts;
        }

        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));
        }

        private static MEAI.ChatResponse MakeToolCallResponse(string toolName)
        {
            MEAI.FunctionCallContent fc = new(
                "call_" + Guid.NewGuid().ToString("N"), toolName,
                new Dictionary<string, object?> { { "action", "write" } });
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { fc }));
        }

        // ─── Stubs ───

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 60f;
            public int MaxLlmRequestRetries => 0;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => true;
            public int ContextWindowTokens => 4096;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => true;
            public bool LogToolCallArguments => true;
            public bool LogToolCallResults => true;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
        }

        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _fn;
            private int _i;

            public ScriptedChatClient(Func<int, MEAI.ChatResponse> fn)
            {
                _fn = fn;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null,
                CancellationToken ct = default)
            {
                return Task.FromResult(_fn(++_i));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions options = null,
                CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type t, object key = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}
#endif

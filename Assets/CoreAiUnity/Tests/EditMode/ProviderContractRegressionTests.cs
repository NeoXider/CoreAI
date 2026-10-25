using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using MessagePipe;
#endif

namespace CoreAI.Tests.EditMode
{
#if !UNITY_5_3_OR_NEWER
    // WHY: Portable NUnit supports this attribute; Unity's stripped NUnit runs EditMode tests sequentially.
    [NonParallelizable]
#endif
    public sealed class ProviderContractRegressionTests
    {
        private SynchronizationContext _previousContext;
#if UNITY_5_3_OR_NEWER
        private readonly List<UnityEngine.Object> _ownedUnityObjects = new List<UnityEngine.Object>();
#endif

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
#if UNITY_5_3_OR_NEWER
            try
            {
                foreach (UnityEngine.Object owned in _ownedUnityObjects)
                {
                    UnityEngine.Object.DestroyImmediate(owned);
                }
            }
            finally
            {
                _ownedUnityObjects.Clear();
            }
#endif
#if COREAI_LLM && UNITY_5_3_OR_NEWER
            ServerManagedAuthorization.ClearProvider();
#endif
        }

        [Test]
        public async Task LLM002_DefaultAdapter_PreservesFailureReceiptsAndMetadata()
        {
            LlmCompletionResult failure = Failure(LlmErrorCode.BackendUnavailable, true);
            failure.Content = "partial";
            failure.ReasoningContent = "hidden";
            failure.Model = "served-model";
            failure.PromptTokens = 11;
            failure.LastRoundtripPromptTokens = 7;
            failure.CompletionTokens = 5;
            failure.TotalTokens = 16;
            failure.ReasoningTokens = 3;
            failure.CacheReadTokens = 4;
            failure.CacheWriteTokens = 2;
            ILlmClient client = new CompletionOnlyClient(failure);
            List<LlmStreamChunk> chunks = await Drain(client);
            Assert.AreEqual(1, chunks.Count);
            LlmStreamChunk terminal = chunks[0];
            Assert.IsTrue(terminal.IsDone);
            Assert.AreEqual(failure.ErrorCode, terminal.ErrorCode);
            Assert.AreEqual(failure.HttpStatus, terminal.HttpStatus);
            Assert.AreEqual(failure.RetryAfterSeconds, terminal.RetryAfterSeconds);
            Assert.AreEqual("partial", terminal.Text);
            Assert.AreEqual("hidden", terminal.ReasoningText);
            Assert.AreEqual("served-model", terminal.Model);
            Assert.AreSame(failure.ExecutedToolCalls, terminal.ExecutedToolCalls);
            Assert.AreEqual(11, terminal.PromptTokens);
            Assert.AreEqual(7, terminal.LastRoundtripPromptTokens);
            Assert.AreEqual(5, terminal.CompletionTokens);
            Assert.AreEqual(16, terminal.TotalTokens);
            Assert.AreEqual(3, terminal.ReasoningTokens);
            Assert.AreEqual(4, terminal.CacheReadTokens);
            Assert.AreEqual(2, terminal.CacheWriteTokens);
        }

        [Test]
        public async Task LLM002_DefaultAdapter_FlagOnlyFailure_RemainsFailure()
        {
            List<LlmStreamChunk> chunks = await Drain(new CompletionOnlyClient(new LlmCompletionResult { Ok = false }));
            Assert.AreEqual(LlmErrorCode.ProviderError, chunks.Single().ErrorCode);
        }

#if COREAI_LLM
        [Test]
        public async Task LLM002_DefaultAdapter_ExecutedFailureCannotReplayThroughRetry()
        {
            CompletionOnlyClient primary = new CompletionOnlyClient(Failure(LlmErrorCode.BackendUnavailable, true));
            RetryingStreamingLlmClientDecorator client = new RetryingStreamingLlmClientDecorator(primary, 2);
            List<LlmStreamChunk> chunks = await Drain(client);
            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(0, client.RetryCount);
            Assert.AreEqual(1, chunks.Single().ExecutedToolCalls.Count);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks.Single().ErrorCode);
            Assert.AreEqual(503, chunks.Single().HttpStatus);
        }
#endif

        [TestCase(LlmErrorCode.AuthExpired)]
        [TestCase(LlmErrorCode.BackendUnavailable)]
        [TestCase(LlmErrorCode.PaymentRequired)]
        public async Task LLM003_Logging_CodeOnlyErrorIsFailure(LlmErrorCode code)
        {
            CapturingLog log = new CapturingLog();
            ILlmClient client = new LoggingLlmClientDecorator(new ChunkClient(
                new LlmStreamChunk { ErrorCode = code, IsDone = true },
                new LlmStreamChunk { TotalTokens = 9 }), log);
            await Drain(client);
            Assert.AreEqual(1, log.Warnings.Count(line => line.Contains("LLM x (stream)")));
            Assert.IsFalse(log.Information.Any(line => line.Contains("LLM < (stream)")));
            StringAssert.Contains(code.ToString(), log.Warnings.Single(line => line.Contains("LLM x (stream)")));
        }

#if UNITY_5_3_OR_NEWER
        private CoreAISettingsAsset CreateSettings()
        {
            CoreAISettingsAsset settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _ownedUnityObjects.Add(settings);
            return settings;
        }

        [TestCase(LlmErrorCode.PaymentRequired, false)]
        [TestCase(LlmErrorCode.AuthExpired, false)]
        [TestCase(LlmErrorCode.InvalidRequest, false)]
        [TestCase(LlmErrorCode.Cancelled, false)]
        [TestCase(LlmErrorCode.BackendUnavailable, true)]
        public async Task LLM003_CodeOnlyDefaultAdapter_UsesFailureClassification(LlmErrorCode code, bool shouldFallback)
        {
            CompletionOnlyClient primary = new CompletionOnlyClient(Failure(code, false));
            CompletionOnlyClient secondary = new CompletionOnlyClient(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator fallback = new FallbackLlmClientDecorator(primary, secondary);
            List<LlmStreamChunk> chunks = await Drain(fallback);
            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(shouldFallback ? 1 : 0, secondary.Calls);
            Assert.AreEqual(shouldFallback ? LlmErrorCode.None : code, chunks.Single().ErrorCode);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LLM002_DefaultAdapter_ExecutedFailureCannotReplayThroughFallback(bool withRetry)
        {
            CompletionOnlyClient primary = new CompletionOnlyClient(Failure(LlmErrorCode.BackendUnavailable, true));
            CompletionOnlyClient secondary = new CompletionOnlyClient(new LlmCompletionResult { Ok = true, Content = "secondary" });
            ILlmClient client = new FallbackLlmClientDecorator(primary, secondary);
#if COREAI_LLM
            if (withRetry) client = new RetryingStreamingLlmClientDecorator(client, 2);
#endif
            List<LlmStreamChunk> chunks = await Drain(client);
            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(0, secondary.Calls);
            Assert.AreEqual(1, chunks.Single().ExecutedToolCalls.Count);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks.Single().ErrorCode);
        }

#if COREAI_LLM
        [TestCase(false)]
        [TestCase(true)]
        public async Task LLM001_AuthRefresh_PreservesExecutedFailureWithoutReplay(bool streaming)
        {
            CountingRefresher refresher = new CountingRefresher();
            ServerManagedAuthorization.SetRefresher(refresher);
            LlmCompletionResult failure = Failure(LlmErrorCode.AuthExpired, true);
            failure.HttpStatus = 401;
            CompletionOnlyClient primary = new CompletionOnlyClient(failure);
            ILlmClient client = new RefreshOnUnauthorizedDecorator(primary);
            if (streaming)
            {
                LlmStreamChunk terminal = (await Drain(client)).Single();
                Assert.AreEqual(1, terminal.ExecutedToolCalls.Count);
                Assert.AreEqual(LlmErrorCode.AuthExpired, terminal.ErrorCode);
                Assert.AreEqual(401, terminal.HttpStatus);
                Assert.AreEqual(17, terminal.RetryAfterSeconds);
            }
            else
            {
                Assert.AreSame(failure, await client.CompleteAsync(Request()));
            }

            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(1, refresher.Calls, "Credentials may refresh for a future turn without replaying this one.");
        }

        [Test]
        public async Task LLM001_AuthRefresh_PriorReceiptOnlyChunkCommitsBeforeThrownFailure()
        {
            CountingRefresher refresher = new CountingRefresher();
            ServerManagedAuthorization.SetRefresher(refresher);
            ReceiptThenAuthThrowClient inner = new ReceiptThenAuthThrowClient();
            List<LlmStreamChunk> chunks = await Drain(new RefreshOnUnauthorizedDecorator(inner));
            Assert.AreEqual(1, inner.Calls);
            Assert.AreEqual(1, chunks[0].ExecutedToolCalls.Count);
            Assert.AreEqual(LlmErrorCode.AuthExpired, chunks.Last().ErrorCode);
            Assert.AreEqual(403, chunks.Last().HttpStatus);
            Assert.AreEqual(17, chunks.Last().RetryAfterSeconds);
        }
#endif

        [TestCase(false)]
        [TestCase(true)]
        public async Task LLM007_CodeOnlyRoutingFailure_IsStickyForHealthAndEvents(bool abandon)
        {
            CapturingRegistry registry = new CapturingRegistry(new ChunkClient(null,
                new LlmStreamChunk { ErrorCode = LlmErrorCode.BackendUnavailable, IsDone = true, TotalTokens = 9 },
                new LlmStreamChunk { TotalTokens = 9 }));
            CapturingPublisher<LlmRequestCompleted> completed = new CapturingPublisher<LlmRequestCompleted>();
            CapturingPublisher<LlmUsageReported> usage = new CapturingPublisher<LlmUsageReported>();
            RoutingLlmClient client = new RoutingLlmClient(registry,
                requestCompletedPublisher: completed, usageReportedPublisher: usage);
            if (abandon)
            {
                IAsyncEnumerator<LlmStreamChunk> stream = client.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
                try
                {
                    Assert.IsTrue(await stream.MoveNextAsync());
                    Assert.AreEqual(LlmErrorCode.BackendUnavailable, stream.Current.ErrorCode);
                    Assert.AreEqual(0, completed.Messages.Count,
                        "A yielded failure does not finish an iterator until its consumer closes it.");
                }
                finally
                {
                    await stream.DisposeAsync();
                    await stream.DisposeAsync();
                }
            }
            else
            {
                await Drain(client);
            }

            Assert.AreEqual(1, completed.Messages.Count);
            Assert.IsFalse(completed.Messages.Single().Success);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, completed.Messages.Single().ErrorCode);
            Assert.AreEqual(1, usage.Messages.Count);
            Assert.IsFalse(usage.Messages.Single().Success);
            Assert.AreEqual(9, usage.Messages.Single().TotalTokens);
            CollectionAssert.AreEqual(new[] { LlmErrorCode.BackendUnavailable }, registry.HealthReports);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LLM007_CodeOnlyRoutingFailure_WithoutMeasuredUsageDoesNotInventTokens(bool abandon)
        {
            CapturingRegistry registry = new CapturingRegistry(new ChunkClient(
                new LlmStreamChunk { ErrorCode = LlmErrorCode.BackendUnavailable, IsDone = true }));
            CapturingPublisher<LlmRequestCompleted> completed = new CapturingPublisher<LlmRequestCompleted>();
            CapturingPublisher<LlmUsageReported> usage = new CapturingPublisher<LlmUsageReported>();
            RoutingLlmClient client = new RoutingLlmClient(registry,
                requestCompletedPublisher: completed, usageReportedPublisher: usage);
            if (abandon)
            {
                IAsyncEnumerator<LlmStreamChunk> stream = client.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
                try
                {
                    Assert.IsTrue(await stream.MoveNextAsync());
                    Assert.AreEqual(LlmErrorCode.BackendUnavailable, stream.Current.ErrorCode);
                }
                finally
                {
                    await stream.DisposeAsync();
                    await stream.DisposeAsync();
                }
            }
            else
            {
                await Drain(client);
            }

            Assert.AreEqual(1, completed.Messages.Count);
            Assert.IsFalse(completed.Messages.Single().Success);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, completed.Messages.Single().ErrorCode);
            Assert.IsEmpty(usage.Messages, "A failed request without measured usage must not invent token charges.");
            CollectionAssert.AreEqual(new[] { LlmErrorCode.BackendUnavailable }, registry.HealthReports);
        }

        [Test]
        public void LLM004_Backlog_BoundsBytesChunksAndRetainsPartialChunkUntilAcknowledged()
        {
            FetchSseBacklog backlog = new FetchSseBacklog(4, 2);
            Assert.IsTrue(backlog.TryEnqueue("é"));
            Assert.IsTrue(backlog.TryEnqueue("ab"));
            Assert.IsFalse(backlog.TryEnqueue("x"));
            Assert.AreEqual(4, backlog.BufferedBytes);
            byte[] buffer = new byte[8];
            Assert.AreEqual(1, backlog.Read(buffer, 0, 1, out int partialAck));
            Assert.AreEqual(0, partialAck);
            Assert.AreEqual(4, backlog.BufferedBytes);
            Assert.IsFalse(backlog.TryEnqueue("x"));
            Assert.AreEqual(1, backlog.Read(buffer, 1, 1, out int fullAck));
            Assert.AreEqual(2, fullAck);
            Assert.AreEqual(2, backlog.BufferedBytes);
            Assert.IsTrue(backlog.TryEnqueue("x"));
            Assert.IsFalse(backlog.TryEnqueue("y"), "The fragment limit applies even below the byte limit.");
            backlog.Clear();
            Assert.AreEqual(0, backlog.BufferedBytes);
            Assert.AreEqual(0, backlog.BufferedChunks);
            Assert.AreEqual(0, backlog.Read(buffer, 0, buffer.Length, out int clearedAck));
            Assert.AreEqual(0, clearedAck);
            LlmClientException failure = (LlmClientException)FetchSseTransportProtocol.CreateTransportException("BacklogOverflow");
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failure.ErrorCode);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public async Task LLM005_ReusedRegistryHandle_ReleasesEveryConcurrentInvocation(bool firstStream, bool secondStream)
        {
            CoreAISettingsAsset settingsObject = CreateSettings();
            GateClient inner = new GateClient();
            LlmClientRegistry registry = CreateRegistry((CoreAISettingsAsset)settingsObject, inner);
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                ILlmClient handle = registry.ResolveClientForRole("role");
                ((IDisposable)handle).Dispose();
                Assert.AreEqual(0, InFlight(registry));
                Task first = firstStream ? ConsumeGateStream(handle, "a") : handle.CompleteAsync(Request("a"));
                Task second = secondStream ? ConsumeGateStream(handle, "b") : handle.CompleteAsync(Request("b"));
                Assert.AreEqual(2, InFlight(registry));
                ((IDisposable)handle).Dispose();
                Assert.AreEqual(2, InFlight(registry), "Disposing a reusable handle cannot release active operations.");
                inner.First.TrySetResult(true);
                await first;
                Assert.AreEqual(1, InFlight(registry));
                inner.Second.TrySetResult(true);
                await second;
                Assert.AreEqual(0, InFlight(registry));
                ((IDisposable)handle).Dispose();
                Assert.AreEqual(0, InFlight(registry));
            }
            finally
            {
                inner.First.TrySetResult(true);
                inner.Second.TrySetResult(true);
                registry.Dispose();
            }
        }

        [Test]
        public async Task LLM005_RegistryLease_EarlyStreamDisposalAndCancellationReleaseOnce()
        {
            CoreAISettingsAsset settings = CreateSettings();
            GateClient inner = new GateClient();
            LlmClientRegistry registry = CreateRegistry(settings, inner);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                ILlmClient handle = registry.ResolveClientForRole("role");
                IAsyncEnumerator<LlmStreamChunk> stream = handle.CompleteStreamingAsync(Request("a")).GetAsyncEnumerator();
                Assert.IsTrue(await stream.MoveNextAsync());
                Task<LlmCompletionResult> completion = handle.CompleteAsync(Request("b"), cancellation.Token);
                Assert.AreEqual(2, InFlight(registry));
                await stream.DisposeAsync();
                await stream.DisposeAsync();
                Assert.AreEqual(1, InFlight(registry));
                cancellation.Cancel();
                Assert.ThrowsAsync<TaskCanceledException>(async () => await completion);
                Assert.AreEqual(0, InFlight(registry));
            }
            finally
            {
                inner.First.TrySetResult(true);
                inner.Second.TrySetResult(true);
                registry.Dispose();
            }
        }

        [TestCase("deactivate", false)]
        [TestCase("deactivate", true)]
        [TestCase("remove", false)]
        [TestCase("remove", true)]
        [TestCase("replace", false)]
        [TestCase("replace", true)]
        [TestCase("dispose", false)]
        [TestCase("dispose", true)]
        public async Task RegistryRetirement_RefusesResolvedButUnadmittedHandles(string retirement, bool streaming)
        {
            CoreAISettingsAsset settings = CreateSettings();
            RetirementClient inner = new RetirementClient();
            int releases = 0;
            LlmClientRegistry registry = CreateRegistry(settings, inner, () =>
            {
                releases++;
                return Task.CompletedTask;
            });
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                ILlmClient handle = registry.ResolveClientForRole("role");
                IAsyncEnumerable<LlmStreamChunk> stream = handle.CompleteStreamingAsync(Request());
                handle.ResolveRequestSettings(Request());
                handle.SupportsNativeToolCallingForRole("role");
                Assert.AreEqual(0, InFlight(registry), "Metadata handles and unenumerated streams take no lease.");
                if (retirement == "deactivate") await registry.SetEndpointActiveAsync("endpoint", false);
                else if (retirement == "remove") await registry.RemoveEndpointAsync("endpoint");
                else if (retirement == "replace")
                {
                    LlmEndpointDescriptor replacement = Descriptor();
                    replacement.Model = "replacement";
                    await registry.AddOrUpdateEndpointAsync(replacement);
                }
                else registry.Dispose();
                Assert.AreEqual(1, releases, "An unused handle must not hold the old owned host alive.");

                Exception failure = null;
                try
                {
                    if (streaming) { await foreach (LlmStreamChunk chunk in stream) { } }
                    else await handle.CompleteAsync(Request());
                }
                catch (Exception ex) { failure = ex; }
                Assert.IsInstanceOf<LlmClientException>(failure, "A retired generation must refuse before invoking its old client.");
                Assert.AreEqual(LlmErrorCode.BackendUnavailable, ((LlmClientException)failure).ErrorCode);
                Assert.AreEqual(0, inner.Calls, "No request may enter a host after its release callback ran.");
            }
            finally
            {
                registry.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RegistryRetirement_ReactivationCannotReadmitAnOldGeneration(bool streaming)
        {
            CoreAISettingsAsset settings = CreateSettings();
            RetirementClient inner = new RetirementClient();
            LlmClientRegistry registry = CreateRegistry(settings, inner);
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                ILlmClient stale = registry.ResolveClientForRole("role");
                await registry.SetEndpointActiveAsync("endpoint", false);
                await registry.SetEndpointActiveAsync("endpoint", true);
                Exception failure = null;
                try
                {
                    if (streaming) { await foreach (LlmStreamChunk chunk in stale.CompleteStreamingAsync(Request())) { } }
                    else await stale.CompleteAsync(Request());
                }
                catch (Exception ex) { failure = ex; }
                Assert.IsInstanceOf<LlmClientException>(failure);
                Assert.AreEqual(LlmErrorCode.BackendUnavailable, ((LlmClientException)failure).ErrorCode);
                Assert.AreEqual(0, inner.Calls);
                Assert.IsTrue((await registry.ResolveClientForRole("role").CompleteAsync(Request())).Ok,
                    "Fresh handles may enter the newly activated generation.");
                Assert.AreEqual(1, inner.Calls);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RegistryRetirement_DrainsAnAdmittedOperationAndRefusesAnotherOnTheSameHandle(bool streaming)
        {
            CoreAISettingsAsset settings = CreateSettings();
            GateClient inner = new GateClient();
            inner.Second.TrySetResult(true);
            int releases = 0;
            LlmClientRegistry registry = CreateRegistry(settings, inner, () =>
            {
                Interlocked.Increment(ref releases);
                return Task.CompletedTask;
            });
            Task<LlmCompletionResult> activeTask = null;
            IAsyncEnumerator<LlmStreamChunk> activeStream = null;
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                ILlmClient handle = registry.ResolveClientForRole("role");
                if (streaming)
                {
                    activeStream = handle.CompleteStreamingAsync(Request("a")).GetAsyncEnumerator();
                    Assert.IsTrue(await activeStream.MoveNextAsync());
                }
                else activeTask = handle.CompleteAsync(Request("a"));
                Assert.AreEqual(1, InFlight(registry));
                await registry.SetEndpointActiveAsync("endpoint", false);
                Assert.AreEqual(0, Volatile.Read(ref releases), "An admitted operation must keep its owned host alive while draining.");
                Exception failure = null;
                try { await handle.CompleteAsync(Request("b")); }
                catch (Exception ex) { failure = ex; }
                Assert.IsInstanceOf<LlmClientException>(failure);
                Assert.AreEqual(LlmErrorCode.BackendUnavailable, ((LlmClientException)failure).ErrorCode);
                Assert.AreEqual(1, InFlight(registry), "Refused admission cannot alter the active operation's lease.");
                inner.First.TrySetResult(true);
                if (activeTask != null) Assert.IsTrue((await activeTask).Ok);
                else
                {
                    Assert.IsTrue(await activeStream.MoveNextAsync());
                    Assert.IsTrue(activeStream.Current.IsDone);
                    Assert.IsFalse(await activeStream.MoveNextAsync());
                }
                Assert.AreEqual(0, InFlight(registry));
            }
            finally
            {
                inner.First.TrySetResult(true);
                if (activeTask != null) await activeTask;
                if (activeStream != null) await activeStream.DisposeAsync();
                registry.Dispose();
            }
        }

        [Test]
        public async Task RegistryRetirement_ReentrantActivationWaitsForTheOwnedHostRelease()
        {
            CoreAISettingsAsset settings = CreateSettings();
            RetirementClient inner = new RetirementClient();
            TaskCompletionSource<bool> releaseGate = new TaskCompletionSource<bool>();
            Task<LlmEndpointSnapshot> reactivation = null;
            Task<LlmEndpointSnapshot> deactivation = null;
            LlmClientRegistry registry = null;
            bool reactivateOnRelease = true;
            EndpointFactory factory = new EndpointFactory(inner, () =>
            {
                if (reactivateOnRelease)
                {
                    reactivateOnRelease = false;
                    reactivation = registry.SetEndpointActiveAsync("endpoint", true);
                    return releaseGate.Task;
                }
                return Task.CompletedTask;
            });
            registry = new LlmClientRegistry(GameLoggerUnscopedFallback.Instance, settings, null,
                new RegistryStore(), factory);
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor());
                registry.AssignRoleProfile("role", "endpoint");
                Assert.AreEqual(1, factory.Activations);
                deactivation = registry.SetEndpointActiveAsync("endpoint", false);
                Assert.IsNotNull(reactivation, "The release hook must exercise synchronous registry reentry.");
                Assert.AreEqual(1, factory.Activations, "The successor host must wait until its predecessor release has settled.");
                Assert.IsFalse(reactivation.IsCompleted);
                releaseGate.TrySetResult(true);
                await reactivation;
                await deactivation;
                Assert.AreEqual(2, factory.Activations);
                Assert.IsTrue((await registry.ResolveClientForRole("role").CompleteAsync(Request())).Ok);
            }
            finally
            {
                releaseGate.TrySetResult(true);
                if (reactivation != null) await reactivation;
                if (deactivation != null) await deactivation;
                registry.Dispose();
            }
        }

        private sealed class RetirementClient : ILlmClient
        {
            internal int Calls;
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "done" });
            }
        }

        private static async Task ConsumeGateStream(ILlmClient handle, string gate)
        {
            await foreach (LlmStreamChunk chunk in handle.CompleteStreamingAsync(Request(gate))) { }
        }

        private static int InFlight(LlmClientRegistry registry) => registry.GetEndpoints().Single().InFlightRequests;

        private static LlmClientRegistry CreateRegistry(CoreAISettingsAsset settings, ILlmClient client,
            Func<Task> release = null)
        {
            return new LlmClientRegistry(GameLoggerUnscopedFallback.Instance, settings, null,
                new RegistryStore(), new EndpointFactory(client, release));
        }

        private static LlmEndpointDescriptor Descriptor() => new LlmEndpointDescriptor
        {
            EndpointId = "endpoint", DisplayName = "endpoint", Kind = LlmEndpointKind.HttpOpenAi,
            BaseUrl = "https://example.invalid/v1", Model = "test", Active = true, ContextWindowTokens = 8192
        };

        private sealed class RegistryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState Load() => new LlmEndpointRegistryState();
            public void Save(LlmEndpointRegistryState state) { }
        }

        private sealed class EndpointFactory : ILlmEndpointClientFactory
        {
            private readonly ILlmClient _client;
            private readonly Func<Task> _release;
            internal int Activations;
            public EndpointFactory(ILlmClient client, Func<Task> release = null) { _client = client; _release = release; }
            public Task<LlmEndpointClientActivation> ActivateAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Activations);
                return Task.FromResult(new LlmEndpointClientActivation { Client = _client, Mode = LlmExecutionMode.ClientOwnedApi,
                    ReleaseOwnedHostAsync = _release });
            }
        }

        private sealed class GateClient : ILlmClient
        {
            internal readonly TaskCompletionSource<bool> First = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<bool> Second = new TaskCompletionSource<bool>();

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<bool> gate = request.UserPayload == "a" ? First : Second;
                using CancellationTokenRegistration registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
                await gate.Task;
                return new LlmCompletionResult { Ok = true, Content = "done" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = "partial" };
                await CompleteAsync(request, cancellationToken);
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class CapturingRegistry : ILlmClientRegistry
        {
            private readonly ILlmClient _client;
            internal readonly List<LlmErrorCode> HealthReports = new List<LlmErrorCode>();
            internal CapturingRegistry(ILlmClient client) { _client = client; }
            public ILlmClient ResolveClientForRole(string roleId) => _client;
            public int ResolveContextWindowForRole(string roleId) => 8192;
            public LlmExecutionMode ResolveExecutionModeForRole(string roleId) => LlmExecutionMode.ClientOwnedApi;
            public string ResolveProfileIdForRole(string roleId) => "endpoint";
            public void ReportRouteFailure(string profileId, long generation, LlmErrorCode errorCode, string error) => HealthReports.Add(errorCode);
        }

        private sealed class CapturingPublisher<T> : IPublisher<T>
        {
            internal readonly List<T> Messages = new List<T>();
            public void Publish(T message) => Messages.Add(message);
        }

#if COREAI_LLM
        private sealed class CountingRefresher : IServerManagedAuthRefresher
        {
            internal int Calls;
            public Task<bool> RefreshAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                return Task.FromResult(true);
            }
        }

        private sealed class ReceiptThenAuthThrowClient : ILlmClient
        {
            internal int Calls;
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Calls++;
                yield return new LlmStreamChunk { ExecutedToolCalls = Failure(LlmErrorCode.AuthExpired, true).ExecutedToolCalls };
                await Task.CompletedTask;
                throw new LlmClientException("expired", LlmErrorCode.AuthExpired, 403, 17);
            }
        }
#endif
#endif

        private static LlmCompletionRequest Request(string payload = "hi") => new LlmCompletionRequest
        {
            AgentRoleId = "role", UserPayload = payload, TraceId = "provider-regression"
        };

        private static LlmCompletionResult Failure(LlmErrorCode code, bool executed) => new LlmCompletionResult
        {
            Ok = false, ErrorCode = code, HttpStatus = 503, RetryAfterSeconds = 17,
            ExecutedToolCalls = executed
                ? new[] { new LlmToolCallTrace("mutate", false, 1, "native", "failed after mutation") }
                : Array.Empty<LlmToolCallTrace>()
        };

        private static async Task<List<LlmStreamChunk>> Drain(ILlmClient client)
        {
            List<LlmStreamChunk> chunks = new List<LlmStreamChunk>();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(Request())) chunks.Add(chunk);
            return chunks;
        }

        private sealed class CompletionOnlyClient : ILlmClient
        {
            private readonly LlmCompletionResult _result;
            internal int Calls;
            internal CompletionOnlyClient(LlmCompletionResult result) { _result = result; }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                return Task.FromResult(_result);
            }
        }

        private sealed class ChunkClient : ILlmClient
        {
            private readonly LlmStreamChunk[] _chunks;
            internal ChunkClient(params LlmStreamChunk[] chunks) { _chunks = chunks; }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (LlmStreamChunk chunk in _chunks) yield return chunk;
                await Task.CompletedTask;
            }
        }

        private sealed class CapturingLog : ILog
        {
            internal readonly List<string> Warnings = new List<string>();
            internal readonly List<string> Information = new List<string>();
            public void Debug(string message, string tag = null) { }
            public void Info(string message, string tag = null) => Information.Add(message);
            public void Warn(string message, string tag = null) => Warnings.Add(message);
            public void Error(string message, string tag = null) => Warnings.Add(message);
        }
    }
}

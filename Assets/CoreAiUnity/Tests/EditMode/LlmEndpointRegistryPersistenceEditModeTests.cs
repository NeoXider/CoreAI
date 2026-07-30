using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmEndpointRegistryPersistenceEditModeTests
    {
        private sealed class MemoryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState State { get; set; } = new();
            public int Saves { get; private set; }

            public LlmEndpointRegistryState Load()
            {
                return State;
            }

            public void Save(LlmEndpointRegistryState state)
            {
                State = state;
                Saves++;
            }
        }

        private sealed class FakeFactory : ILlmEndpointClientFactory
        {
            public string LastSessionApiKey { get; private set; }
            public int Calls { get; private set; }
            public TaskCompletionSource<LlmEndpointClientActivation> Pending { get; set; }
            public TaskCompletionSource<bool> Activated { get; } = new();
            public Exception Failure { get; set; }
            public ILlmClient Client { get; set; } = new StubLlmClient();
            public LlmExecutionMode? ActivationMode { get; set; }
            public CancellationToken LastCancellationToken { get; private set; }
            public Func<Task> ReleaseOwnedHostAsync { get; set; }

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                Calls++;
                Activated.TrySetResult(true);
                LastSessionApiKey = sessionApiKey;
                LastCancellationToken = cancellationToken;
                if (Failure != null)
                {
                    return Task.FromException<LlmEndpointClientActivation>(Failure);
                }

                if (Pending != null)
                {
                    return Pending.Task;
                }

                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = Client,
                    Mode = ActivationMode ?? (descriptor.Kind == LlmEndpointKind.Offline
                        ? LlmExecutionMode.Offline
                        : LlmExecutionMode.ClientOwnedApi),
                    ReleaseOwnedHostAsync = ReleaseOwnedHostAsync
                });
            }
        }

        private sealed class FakeSecretProvider : ILlmEndpointSecretProvider
        {
            public string Secret { get; set; }

            public bool TryResolve(string secretReference, out string secret)
            {
                secret = Secret ?? "";
                return !string.IsNullOrEmpty(secret);
            }
        }

        private sealed class SupersedeFactory : ILlmEndpointClientFactory
        {
            public TaskCompletionSource<LlmEndpointClientActivation> First { get; } = new();
            public TaskCompletionSource<LlmEndpointClientActivation> Second { get; } = new();
            public CancellationToken FirstToken { get; private set; }
            private int _calls;

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    FirstToken = cancellationToken;
                    return First.Task;
                }

                return Second.Task;
            }
        }

        private sealed class OutOfOrderStore : ILlmEndpointRegistryStore, IDisposable
        {
            private int _calls;

            public ManualResetEventSlim FirstEntered { get; } = new(false);
            public ManualResetEventSlim ReleaseFirst { get; } = new(false);
            public LlmEndpointRegistryState State { get; private set; } = new();
            public int Calls => Volatile.Read(ref _calls);

            public LlmEndpointRegistryState Load()
            {
                return new LlmEndpointRegistryState();
            }

            public void Save(LlmEndpointRegistryState state)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    FirstEntered.Set();
                    ReleaseFirst.Wait(TimeSpan.FromSeconds(5));
                }

                State = state;
            }

            public void Dispose()
            {
                FirstEntered.Dispose();
                ReleaseFirst.Dispose();
            }
        }

        private sealed class NamedClient : ILlmClient
        {
            private readonly string _content;

            public NamedClient(string content)
            {
                _content = content;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _content });
            }
        }

        private sealed class BlockingClient : ILlmClient
        {
            public TaskCompletionSource<LlmCompletionResult> Completion { get; } = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Completion.Task;
            }
        }

        private CoreAISettingsAsset _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public async Task Mutation_PersistsDescriptorsProfilesAndRoleAssignmentsWithoutSessionSecret()
        {
            MemoryStore store = new();
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(store, factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.SecretReference = "player/openai";

            await registry.AddOrUpdateEndpointAsync(descriptor, "sk-session-only");
            registry.AddOrUpdateProfile(new LlmRuntimeProfile
            {
                ProfileId = "chat-fast",
                EndpointId = "cloud"
            });
            registry.AssignRoleProfile("Chat", "chat-fast");

            string persisted = JsonConvert.SerializeObject(store.State);
            Assert.AreEqual("sk-session-only", factory.LastSessionApiKey);
            Assert.That(persisted, Does.Contain("player/openai"));
            Assert.That(persisted, Does.Not.Contain("sk-session-only"));
            Assert.AreEqual("chat-fast", store.State.RoleProfiles.Single().ProfileId);
            Assert.GreaterOrEqual(store.Saves, 3);
        }

        [Test]
        public void Constructor_RestoresDisabledConfigurationWithoutStartingEndpoint()
        {
            LlmEndpointDescriptor disabled = Descriptor("local", "http://127.0.0.1:13333/v1");
            disabled.Active = false;
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState
                {
                    Endpoints = new[] { disabled },
                    Profiles = new[]
                    {
                        new LlmRuntimeProfile { ProfileId = "magic", EndpointId = "local" }
                    },
                    RoleProfiles = new[]
                    {
                        new LlmPersistedRoleProfile { RolePattern = "Magic", ProfileId = "magic" }
                    }
                }
            };
            FakeFactory factory = new();

            LlmClientRegistry registry = BuildRegistry(store, factory);

            Assert.AreEqual("magic", registry.GetRoleProfile("Magic"));
            Assert.AreEqual(LlmEndpointLifecycleState.Inactive, registry.GetEndpoints().Single().State);
            Assert.IsNull(factory.LastSessionApiKey);
        }

        [Test]
        public void Constructor_InOfflineMode_RestoresConfigurationWithoutAutoActivation()
        {
            // Regression: with LLMUnity + autostart persisted as Active, every scene load used to boot
            // a native llama.cpp host even when the settings said Offline — rapid Single-mode scene
            // cycling (the published-demo smoke) then crashed the editor mid-construction.
            _settings.ConfigureOffline();
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState
                {
                    Endpoints = new[] { Descriptor("cloud", "https://example.test/v1") },
                    Profiles = new[]
                    {
                        new LlmRuntimeProfile { ProfileId = "chat", EndpointId = "cloud" }
                    },
                    RoleProfiles = new[]
                    {
                        new LlmPersistedRoleProfile { RolePattern = "SmartChat", ProfileId = "chat" }
                    }
                }
            };
            FakeFactory factory = new();

            LlmClientRegistry registry = BuildRegistry(store, factory);

            Assert.AreEqual(0, factory.Calls, "Offline mode must not auto-activate persisted endpoints.");
            Assert.AreEqual(LlmEndpointLifecycleState.Inactive, registry.GetEndpoints().Single().State);
            Assert.AreEqual("chat", registry.GetRoleProfile("SmartChat"),
                "Configuration is still restored for display/explicit activation.");
        }

        [Test]
        public async Task Constructor_RestoresActiveEndpointAndAgentAssignmentReady()
        {
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState
                {
                    Endpoints = new[] { Descriptor("cloud", "https://example.test/v1") },
                    Profiles = new[]
                    {
                        new LlmRuntimeProfile { ProfileId = "chat", EndpointId = "cloud" }
                    },
                    RoleProfiles = new[]
                    {
                        new LlmPersistedRoleProfile { RolePattern = "SmartChat", ProfileId = "chat" }
                    }
                }
            };
            FakeFactory factory = new() { Client = new NamedClient("restored") };

            LlmClientRegistry registry = BuildRegistry(store, factory);
            LlmCompletionResult result = await registry.ResolveClientForRole("SmartChat")
                .CompleteAsync(new LlmCompletionRequest());

            Assert.AreEqual(1, factory.Calls);
            Assert.AreEqual("chat", registry.GetRoleProfile("SmartChat"));
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, registry.GetEndpoints().Single().State);
            Assert.AreEqual("restored", result.Content);
        }

        [Test]
        public async Task RestoredActiveEndpoint_FirstRequestWaitsForSharedReadiness()
        {
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState
                {
                    Endpoints = new[] { Descriptor("cloud", "https://example.test/v1") },
                    Profiles = new[]
                    {
                        new LlmRuntimeProfile { ProfileId = "chat", EndpointId = "cloud" }
                    },
                    RoleProfiles = new[]
                    {
                        new LlmPersistedRoleProfile { RolePattern = "SmartChat", ProfileId = "chat" }
                    }
                }
            };
            FakeFactory factory = new()
            {
                Pending = new TaskCompletionSource<LlmEndpointClientActivation>()
            };
            LlmClientRegistry registry = BuildRegistry(store, factory);

            Task<LlmCompletionResult> first = registry.ResolveClientForRole("SmartChat")
                .CompleteAsync(new LlmCompletionRequest());
            Task<LlmCompletionResult> second = registry.ResolveClientForRole("SmartChat")
                .CompleteAsync(new LlmCompletionRequest());
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(1, factory.Calls);

            factory.Pending.SetResult(new LlmEndpointClientActivation
            {
                Client = new NamedClient("ready"),
                Mode = LlmExecutionMode.ClientOwnedApi
            });

            Assert.AreEqual("ready", (await first).Content);
            Assert.AreEqual("ready", (await second).Content);
        }

        [Test]
        public async Task InactiveAdd_DoesNotCallFactory()
        {
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cold", "https://example.test/v1");
            descriptor.Active = false;

            LlmEndpointSnapshot snapshot = await registry.AddOrUpdateEndpointAsync(descriptor, "session");

            Assert.AreEqual(0, factory.Calls);
            Assert.AreEqual(LlmEndpointLifecycleState.Inactive, snapshot.State);
        }

        [Test]
        public async Task EditWithoutNewSessionKey_PreservesCurrentCredential()
        {
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.Active = false;
            await registry.AddOrUpdateEndpointAsync(descriptor, "keep-me");

            descriptor.Active = true;
            await registry.AddOrUpdateEndpointAsync(descriptor, null);

            Assert.AreEqual("keep-me", factory.LastSessionApiKey);
        }

        [Test]
        public async Task ExplicitEmptySessionKey_ClearsCurrentCredential()
        {
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.Active = false;
            await registry.AddOrUpdateEndpointAsync(descriptor, "clear-me");

            descriptor.Active = true;
            await registry.AddOrUpdateEndpointAsync(descriptor, "");

            Assert.AreEqual("", factory.LastSessionApiKey);
        }

        [Test]
        public async Task ChangedSecretReference_ResolvesReplacementInsteadOfPreservingOldSecret()
        {
            FakeFactory factory = new();
            FakeSecretProvider secrets = new() { Secret = "first-secret" };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory, secrets);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.SecretReference = "FIRST_KEY";
            await registry.AddOrUpdateEndpointAsync(descriptor);

            secrets.Secret = "second-secret";
            descriptor.SecretReference = "SECOND_KEY";
            await registry.AddOrUpdateEndpointAsync(descriptor);

            Assert.AreEqual(2, factory.Calls);
            Assert.AreEqual("second-secret", factory.LastSessionApiKey);
        }

        [Test]
        public async Task Restore_ResolvesPersistedSecretReferenceBeforeActivation()
        {
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.SecretReference = "COREAI_TEST_KEY";
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState { Endpoints = new[] { descriptor } }
            };
            FakeFactory factory = new();

            LlmClientRegistry registry = BuildRegistry(
                store, factory, new FakeSecretProvider { Secret = "resolved-secret" });
            await factory.Activated.Task;

            Assert.AreEqual("resolved-secret", factory.LastSessionApiKey);
        }

        [Test]
        public async Task RemoveAssignedEndpoint_ClearsRoleAssignment()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));
            registry.AssignRoleProfile("Chat", "cloud");

            Assert.IsTrue(await registry.RemoveEndpointAsync("cloud"));

            Assert.AreEqual("", registry.GetRoleProfile("Chat"));
            Assert.IsEmpty(registry.GetProfiles());
        }

        [Test]
        public async Task RemoveEndpoint_RejectsUnknownReplacementWithoutMutation()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));

            KeyNotFoundException caught = null;
            try
            {
                await registry.RemoveEndpointAsync("cloud", replacementEndpointId: "missing");
            }
            catch (KeyNotFoundException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "An unknown replacement endpoint must be rejected.");
            Assert.AreEqual("cloud", registry.GetEndpoints().Single().Descriptor.EndpointId);
        }

        [Test]
        public async Task ReadyFallback_ReportsEffectiveProfileContextAndMode()
        {
            FakeFactory factory = new() { Client = new NamedClient("fallback") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor primary = Descriptor("primary", "https://example.test/v1");
            primary.Active = false;
            primary.ContextWindowTokens = 1000;
            await registry.AddOrUpdateEndpointAsync(primary);
            LlmEndpointDescriptor fallback = Descriptor("fallback", "https://example.test/v1");
            fallback.Kind = LlmEndpointKind.Offline;
            fallback.ContextWindowTokens = 2048;
            await registry.AddOrUpdateEndpointAsync(fallback);
            registry.AddOrUpdateProfile(new LlmRuntimeProfile
            {
                ProfileId = "primary-profile",
                EndpointId = "primary",
                FallbackProfileIds = new[] { "fallback" }
            });
            registry.AssignRoleProfile("Chat", "primary-profile");

            Assert.AreEqual("fallback", registry.ResolveProfileIdForRole("Chat"));
            Assert.AreEqual(2048, registry.ResolveContextWindowForRole("Chat"));
            Assert.AreEqual(LlmExecutionMode.Offline, registry.ResolveExecutionModeForRole("Chat"));
            Assert.AreEqual("fallback", (await registry.ResolveClientForRole("Chat")
                .CompleteAsync(new LlmCompletionRequest())).Content);
        }

        [Test]
        public async Task Dispose_CancelsOwnedActivation()
        {
            FakeFactory factory = new()
            {
                Pending = new TaskCompletionSource<LlmEndpointClientActivation>()
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            _ = registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));

            registry.Dispose();

            Assert.IsTrue(factory.LastCancellationToken.IsCancellationRequested);
        }

        [Test]
        public async Task Deactivate_ReleasesOnlyFactoryOwnedHostLease()
        {
            int releases = 0;
            FakeFactory factory = new()
            {
                ReleaseOwnedHostAsync = () =>
                {
                    releases++;
                    return Task.CompletedTask;
                }
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            await registry.AddOrUpdateEndpointAsync(Descriptor("owned", "https://example.test/v1"));

            await registry.SetEndpointActiveAsync("owned", false);

            Assert.AreEqual(1, releases);
        }

        [Test]
        public async Task Dispose_ReleasesReadyOwnedHostLeaseOnce()
        {
            int releases = 0;
            FakeFactory factory = new()
            {
                ReleaseOwnedHostAsync = () =>
                {
                    releases++;
                    return Task.CompletedTask;
                }
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            await registry.AddOrUpdateEndpointAsync(Descriptor("owned", "https://example.test/v1"));

            registry.Dispose();
            registry.Dispose();

            Assert.AreEqual(1, releases);
        }

        /// <summary>
        /// A drained removal must keep the owned host alive until the last tracked request finishes.
        /// <para>
        /// WHY <see cref="UnityTestAttribute"/>: the production drain loop paces itself with
        /// <c>UniTask.Delay</c>, whose continuations only run when the editor loop ticks. A plain
        /// <c>async Task</c> test awaits without ever giving the editor a frame, so the loop could not
        /// observe the request finishing and the test stalled until the runner killed it — the same trap
        /// <see cref="CoreAiChatServiceEditModeTests"/> documents for <c>CancelAfterSlim</c>.
        /// </para>
        /// </summary>
        [UnityTest]
        [Timeout(30000)]
        public IEnumerator DrainRemoval_DefersOwnedHostReleaseUntilTrackedRequestCompletes()
        {
            int releases = 0;
            TaskCompletionSource<bool> released = new();
            BlockingClient client = new();
            FakeFactory factory = new()
            {
                Client = client,
                ReleaseOwnedHostAsync = () =>
                {
                    releases++;
                    released.TrySetResult(true);
                    return Task.CompletedTask;
                }
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            yield return WaitForTask(
                registry.AddOrUpdateEndpointAsync(Descriptor("owned", "https://example.test/v1")),
                "endpoint registration");

            Task<LlmCompletionResult> request = registry.ResolveClientForRole("Chat", "owned")
                .CompleteAsync(new LlmCompletionRequest());

            Task<bool> removal = registry.RemoveEndpointAsync("owned");
            yield return WaitForTask(removal, "drained endpoint removal");
            Assert.IsTrue(removal.Result);
            Assert.AreEqual(0, releases, "the owned host must survive while a request is still tracked");

            client.Completion.SetResult(new LlmCompletionResult { Ok = true });
            yield return WaitForTask(request, "the tracked request");
            yield return WaitForTask(released.Task, "the deferred owned-host release");

            Assert.AreEqual(1, releases);
        }

        [Test]
        public async Task RestoredEndpoint_ActivationAwaitsFactoryAndBecomesReady()
        {
            LlmEndpointDescriptor descriptor = Descriptor("restored", "https://example.test/v1");
            descriptor.Active = false;
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState { Endpoints = new[] { descriptor } }
            };
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(store, factory);

            LlmEndpointSnapshot snapshot = await registry.SetEndpointActiveAsync("restored", true);

            Assert.AreEqual(1, factory.Calls);
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, snapshot.State);
        }

        [Test]
        public async Task ConcurrentActivation_IsCoalescedPerEndpoint()
        {
            FakeFactory factory = new()
            {
                Pending = new TaskCompletionSource<LlmEndpointClientActivation>()
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cold", "https://example.test/v1");
            descriptor.Active = false;
            await registry.AddOrUpdateEndpointAsync(descriptor);

            Task<LlmEndpointSnapshot> first = registry.SetEndpointActiveAsync("cold", true);
            Task<LlmEndpointSnapshot> second = registry.SetEndpointActiveAsync("cold", true);
            Assert.AreNotSame(first, second);
            Assert.AreEqual(1, factory.Calls);
            Assert.AreEqual(LlmEndpointLifecycleState.WaitingForHttp,
                registry.GetEndpoints().Single().State);

            factory.Pending.SetResult(new LlmEndpointClientActivation
            {
                Client = new StubLlmClient(),
                Mode = LlmExecutionMode.ClientOwnedApi
            });
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, (await first).State);
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, (await second).State);
        }

        [Test]
        public async Task SharedActivation_CallerCancellationOnlyCancelsThatCallerWait()
        {
            FakeFactory factory = new()
            {
                Pending = new TaskCompletionSource<LlmEndpointClientActivation>()
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            using CancellationTokenSource cancelledCaller = new();

            Task<LlmEndpointSnapshot> first = registry.AddOrUpdateEndpointAsync(
                Descriptor("cloud", "https://example.test/v1"), cancellationToken: cancelledCaller.Token);
            Task<LlmEndpointSnapshot> second = registry.AddOrUpdateEndpointAsync(
                Descriptor("cloud", "https://example.test/v1"));
            cancelledCaller.Cancel();

            OperationCanceledException caught = null;
            try
            {
                await first;
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "The canceled caller must observe cancellation.");
            Assert.IsFalse(factory.LastCancellationToken.IsCancellationRequested);
            factory.Pending.SetResult(new LlmEndpointClientActivation
            {
                Client = new StubLlmClient(),
                Mode = LlmExecutionMode.ClientOwnedApi
            });

            Assert.AreEqual(LlmEndpointLifecycleState.Ready, (await second).State);
            Assert.AreEqual(1, factory.Calls);
        }

        [Test]
        public async Task ActivationMode_IsRetainedForRuntimeRoutingDiagnostics()
        {
            FakeFactory factory = new() { ActivationMode = LlmExecutionMode.ServerManagedApi };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            await registry.AddOrUpdateEndpointAsync(Descriptor("gateway", "https://example.test/v1"));

            Assert.AreEqual(
                LlmExecutionMode.ServerManagedApi,
                registry.ResolveExecutionModeForRole("Chat", "gateway"));
        }

        [Test]
        public async Task ConcurrentPersistenceWrites_CannotFinishWithOlderSnapshot()
        {
            using OutOfOrderStore store = new();
            LlmClientRegistry registry = BuildRegistry(store, new FakeFactory());
            LlmEndpointDescriptor first = Descriptor("cloud", "https://example.test/v1");
            first.Active = false;
            first.DisplayName = "first";

            Task firstSave = Task.Run(async () => await registry.AddOrUpdateEndpointAsync(first));
            Assert.IsTrue(store.FirstEntered.Wait(TimeSpan.FromSeconds(2)));
            LlmEndpointDescriptor second = Descriptor("cloud", "https://example.test/v1");
            second.Active = false;
            second.DisplayName = "second";
            using ManualResetEventSlim secondStarted = new(false);
            Task secondSave = Task.Run(async () =>
            {
                secondStarted.Set();
                await registry.AddOrUpdateEndpointAsync(second);
            });
            // WHY: prove the second worker is actually scheduled and running before the negative
            // check — otherwise a saturated worker pool lets the assertion pass vacuously even with
            // the persistence gate removed.
            Assert.IsTrue(secondStarted.Wait(TimeSpan.FromSeconds(2)), "The second save task must start.");
            bool concurrentSaveEntered = await WaitForConditionAsync(
                () => store.Calls > 1,
                TimeSpan.FromMilliseconds(250));
            Assert.IsFalse(concurrentSaveEntered, "The second save must wait for the first save to finish.");
            Assert.AreEqual(1, store.Calls);

            store.ReleaseFirst.Set();
            await Task.WhenAll(firstSave, secondSave);

            Assert.AreEqual("second", store.State.Endpoints.Single().DisplayName);
        }

        [Test]
        public async Task SupersedingStartup_CancelsOldGenerationAndPublishesOnlyLatest()
        {
            SupersedeFactory factory = new();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, _settings, null, new MemoryStore(), factory);
            LlmEndpointDescriptor firstDescriptor = Descriptor("cloud", "https://example.test/v1");
            firstDescriptor.Model = "first";
            Task<LlmEndpointSnapshot> first = registry.AddOrUpdateEndpointAsync(firstDescriptor);
            LlmEndpointDescriptor secondDescriptor = Descriptor("cloud", "https://example.test/v1");
            secondDescriptor.Model = "second";
            Task<LlmEndpointSnapshot> second = registry.AddOrUpdateEndpointAsync(secondDescriptor);

            Assert.IsTrue(factory.FirstToken.IsCancellationRequested);
            factory.Second.SetResult(new LlmEndpointClientActivation
            {
                Client = new NamedClient("second"),
                Mode = LlmExecutionMode.ClientOwnedApi
            });
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, (await second).State);
            factory.First.SetResult(new LlmEndpointClientActivation
            {
                Client = new NamedClient("first"),
                Mode = LlmExecutionMode.ClientOwnedApi
            });
            await first;

            Assert.AreEqual("second", registry.GetEndpoints().Single().Descriptor.Model);
            Assert.AreEqual("second", (await registry.ResolveClientForRole("Any", "cloud")
                .CompleteAsync(new LlmCompletionRequest())).Content);
        }

        [Test]
        public async Task ReadinessFailure_TransitionsToFailed()
        {
            FakeFactory factory = new() { Failure = new InvalidOperationException("not ready") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);

            LlmEndpointSnapshot snapshot = await registry.AddOrUpdateEndpointAsync(
                Descriptor("broken", "https://example.test/v1"));

            Assert.AreEqual(LlmEndpointLifecycleState.Failed, snapshot.State);
            Assert.That(snapshot.Error, Does.Contain("not ready"));
        }

        [Test]
        public async Task Snapshots_AreDefensiveCopies()
        {
            MemoryStore store = new();
            LlmClientRegistry registry = BuildRegistry(store, new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));

            LlmEndpointSnapshot snapshot = registry.GetEndpoints().Single();
            snapshot.Descriptor.Active = false;

            Assert.IsTrue(registry.GetEndpoints().Single().Descriptor.Active);
        }

        [Test]
        public void FileStore_RoundTripsSchemaAndSecretReference()
        {
            string path = Path.Combine(Path.GetTempPath(), "coreai-routing-" + Guid.NewGuid() + ".json");
            try
            {
                FileLlmEndpointRegistryStore store = new(path);
                LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
                descriptor.SecretReference = "vault/openai";
                store.Save(new LlmEndpointRegistryState { Endpoints = new[] { descriptor } });

                string json = File.ReadAllText(path);
                LlmEndpointRegistryState loaded = store.Load();

                Assert.That(json, Does.Contain("\"SchemaVersion\": 1"));
                Assert.That(json, Does.Contain("vault/openai"));
                Assert.AreEqual("cloud", loaded.Endpoints.Single().EndpointId);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private LlmClientRegistry BuildRegistry(
            ILlmEndpointRegistryStore store,
            FakeFactory factory,
            ILlmEndpointSecretProvider secretProvider = null)
        {
            return new LlmClientRegistry(
                GameLoggerUnscopedFallback.Instance,
                _settings,
                null,
                store,
                factory,
                secretProvider);
        }

        private static LlmEndpointDescriptor Descriptor(string id, string url)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = url,
                Model = "test",
                Active = true,
                ContextWindowTokens = 4096
            };
        }

        /// <summary>
        /// Awaits <paramref name="task"/> by yielding editor frames, so UniTask timers keep running, and
        /// turns a stuck task into a named failure within <paramref name="timeoutSeconds"/> instead of a
        /// silent multi-minute stall. Faults are rethrown exactly as <c>await</c> would.
        /// </summary>
        private static IEnumerator WaitForTask(Task task, string what, float timeoutSeconds = 10f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, $"{what} did not complete within {timeoutSeconds}s.");
            task.GetAwaiter().GetResult();
        }

        private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!condition() && stopwatch.Elapsed < timeout)
            {
                await Task.Yield();
            }

            return condition();
        }
    }
}

#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm.Gateway;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using VContainer;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    public sealed class GatewayClientCompositionEditModeTests
    {
        private SynchronizationContext _previous;
        [SetUp] public void SetUp()
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }
        [TearDown] public void TearDown()
        {
            ServerManagedAuthorization.ClearProvider();
            ServerManagedAuthorization.ClearRequestHeaderProvider();
            SynchronizationContext.SetSynchronizationContext(_previous);
        }

        [Test]
        public void Installer_PreservesHostReplacement()
        {
            ProbeBinding binding = new ProbeBinding();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IUnityGatewayClientBinding>(binding);
            builder.RegisterCoreAiGatewayClient(new GatewayClientOptions());
            using IObjectResolver container = builder.Build();
            Assert.That(container.Resolve<IUnityGatewayClientBinding>(), Is.SameAs(binding));
        }

        [Test]
        public async Task PrimaryServerRoute_UsesScopedBinding_WithoutSecondLegacyRefresh()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ConfigureServerManagedApi("https://gateway.test/v1", "");
                ProbeBinding binding = new ProbeBinding();
                LegacyRefresher legacy = new LegacyRefresher();
                ServerManagedAuthorization.SetRefresher(legacy);
                ILlmClient client = LlmPipelineInstaller.BuildHttpClient(settings,
                    LlmExecutionMode.ServerManagedApi, null, binding);
                LlmCompletionResult result = await client.CompleteAsync(
                    new LlmCompletionRequest { AgentRoleId = "role", UserPayload = "hello" });
                Assert.That(binding.Creates, Is.EqualTo(1));
                Assert.That(binding.Client.Calls, Is.EqualTo(1));
                Assert.That(result.ErrorCode, Is.EqualTo(LlmErrorCode.AuthExpired));
                Assert.That(legacy.Calls, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public async Task ManifestServerRoute_UsesSameBindingAndResolvedProfile()
        {
            OpenAiHttpLlmSettings http = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            try
            {
                http.SetRuntimeConfiguration(true, "https://gateway.test/v1", "", "",
                    executionMode: LlmExecutionMode.ServerManagedApi);
                ProbeBinding binding = new ProbeBinding();
                using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                    new CoreAISettingsOptions(), null, null, null, null, binding);
                registry.ApplyRouteTable(new LlmRouteTable
                {
                    Profiles = new[]
                    {
                        new LlmRouteProfile { ProfileId = "gateway", Mode = LlmExecutionMode.ServerManagedApi }
                    },
                    Rules = new[] { new LlmRouteRule { ProfileId = "gateway", RolePattern = "*" } }
                }, new[]
                {
                    new LlmBackendProfileEntry
                    {
                        profileId = "gateway", executionMode = LlmExecutionMode.ServerManagedApi,
                        kind = LlmBackendKind.ServerManagedApi, httpSettings = http
                    }
                });
                await registry.ResolveClientForRole("role").CompleteAsync(new LlmCompletionRequest
                { AgentRoleId = "role", UserPayload = "hello" });
                Assert.That(binding.Creates, Is.EqualTo(1));
                Assert.That(binding.Profile, Is.EqualTo("gateway"));
                Assert.That(binding.Client.Calls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(http); }
        }

        [Test]
        public async Task PrimaryRebuild_AfterSettingsChangeKeepsScopeBinding()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                ProbeBinding binding = new ProbeBinding();
                SilentLogger logger = new SilentLogger();
                using LlmClientRegistry registry = new LlmClientRegistry(logger, settings,
                    null, null, null, null, binding);
                settings.ConfigureServerManagedApi("https://first.test/v1", "");
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, logger, null, null, NullLog.Instance, binding));
                settings.ConfigureServerManagedApi("https://second.test/v1", "");
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, logger, null, null, NullLog.Instance, binding));
                await registry.ResolveClientForRole("role").CompleteAsync(new LlmCompletionRequest
                    { AgentRoleId = "role", UserPayload = "hello" });
                Assert.That(binding.Endpoints, Is.EqualTo(new[] { "https://first.test/v1", "https://second.test/v1" }));
                Assert.That(binding.Creates, Is.EqualTo(2));
                Assert.That(binding.Client.Calls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase("rate_limited")]
        [TestCase("backend_unavailable")]
        [TestCase("unmapped_host_denial")]
        public async Task ValidGatewayDenial_CannotDispatchFallbackProvider(string reason)
        {
            using UnityGatewayClientBinding binding = new UnityGatewayClientBinding(
                new GatewayClientOptions { EnableClientPreflight = true }, policy: new DenialPolicy(reason));
            ILlmClient primary = new ServerManagedLlmClient(new OpenAiHttpOptions
                { ApiBaseUrl = "https://gateway.test/v1" }, new CoreAISettingsOptions(),
                new SilentLogger(), null, binding);
            ProbeClient secondary = new ProbeClient();
            ILlmClient fallback = new FallbackLlmClientDecorator(primary, secondary, NullLog.Instance);
            LlmCompletionResult result = await fallback.CompleteAsync(new LlmCompletionRequest
                { AgentRoleId = "role", UserPayload = "hello" });
            Assert.That(result.ErrorCode, Is.EqualTo(LlmErrorCode.PermanentProviderError));
            Assert.That((string)JObject.Parse(result.ProviderErrorBody)["reason_code"], Is.EqualTo(reason));
            Assert.That(secondary.Calls, Is.Zero);
        }

        [Test]
        public async Task ScopedBindingDisposal_CancelsPendingPreflightBeforeHttp()
        {
            PendingPolicy policy = new PendingPolicy();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterCoreAiGatewayClient(new GatewayClientOptions
                { EnableClientPreflight = true }, policy: policy);
            IObjectResolver scope = builder.Build();
            IUnityGatewayClientBinding binding = scope.Resolve<IUnityGatewayClientBinding>();
            ILlmClient client = binding.CreateClient(new OpenAiHttpOptions
                { ApiBaseUrl = "https://gateway.test/v1" }, new CoreAISettingsOptions(),
                new SilentLogger(), null);
            Task<LlmCompletionResult> operation = client.CompleteAsync(new LlmCompletionRequest
                { AgentRoleId = "role", UserPayload = "hello" });
            await policy.Entered.Task;
            scope.Dispose();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation);
            Assert.That(policy.Token.IsCancellationRequested, Is.True);
            Assert.Throws<ObjectDisposedException>(() => binding.CreateClient(new OpenAiHttpOptions(),
                new CoreAISettingsOptions(), new SilentLogger(), null));
        }


        [TestCase(false)]
        [TestCase(true)]
        public async Task PrimaryReplacement_RetiresOwnedLeafAcrossOptionalFallback(bool enableFallback)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            try
            {
                settings.ConfigureServerManagedApi("https://first.test/v1", "");
                settings.ConfigureFallbackBackend(enableFallback, "https://unused-secondary.test/v1", "unused");
                using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(), settings,
                    null, null, null, null, binding);
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, new SilentLogger(), null, null, NullLog.Instance, binding));
                ILlmClient stale = registry.ResolveClientForRole("role");
                settings.ConfigureServerManagedApi("https://second.test/v1", "");
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, new SilentLogger(), null, null, NullLog.Instance, binding));
                Assert.That(binding.Leaves[0].Disposals, Is.EqualTo(1));
                Assert.That(binding.Leaves[1].Disposals, Is.Zero);
                Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await binding.Leaves[0].Gateway.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "old") }));
                Assert.ThrowsAsync<LlmClientException>(async () => await stale.CompleteAsync(Request()));
                LlmCompletionResult result = await registry.ResolveClientForRole("role").CompleteAsync(Request());
                Assert.That(result.Ok, Is.True);
                registry.Dispose();
                registry.Dispose();
                Assert.That(binding.Leaves[1].Disposals, Is.EqualTo(1));
                Assert.That(binding.Wire.Disposals, Is.Zero);
                Assert.That(binding.Auth.Disposals, Is.Zero);
                Assert.That(binding.Headers.Disposals, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Retirement_CancelsAdmittedOwnedRequestWithoutSendingSecondary(bool streaming)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            PendingPolicy pendingPolicy = new PendingPolicy();
            binding.Policy = pendingPolicy;
            using CancellationTokenSource guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task pending = null;
            try
            {
                settings.ConfigureServerManagedApi("https://first.test/v1", "");
                settings.ConfigureFallbackBackend(true, "https://unused-secondary.test/v1", "unused");
                using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(), settings,
                    null, null, null, null, binding);
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, new SilentLogger(), null, null, NullLog.Instance, binding));
                ILlmClient stale = registry.ResolveClientForRole("role");
                pending = InvokeSource(stale, streaming, guard.Token);
                await pendingPolicy.Entered.Task;
                binding.Policy = null;
                registry.SetLegacyFallback(LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, new SilentLogger(), null, null, NullLog.Instance, binding));
                Assert.That(pendingPolicy.Token.IsCancellationRequested, Is.True);
                Assert.That(binding.Leaves[0].Disposals, Is.EqualTo(1));
                Assert.CatchAsync<OperationCanceledException>(async () => await pending);
                Assert.That(guard.IsCancellationRequested, Is.False);
                Assert.That(binding.Wire.Sends, Is.Zero);
                Assert.ThrowsAsync<LlmClientException>(async () => await stale.CompleteAsync(Request()));
                Assert.That((await registry.ResolveClientForRole("role").CompleteAsync(Request())).Ok, Is.True);
            }
            finally
            {
                guard.Cancel();
                if (pending != null) { try { await pending; } catch (OperationCanceledException) { } }
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public async Task ManifestReplacementDisableAndScopeDisposal_RetireEachOwnedLeafOnce()
        {
            OpenAiHttpLlmSettings http = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            BorrowedClient borrowed = new BorrowedClient();
            try
            {
                http.SetRuntimeConfiguration(true, "https://gateway.test/v1", "", "",
                    executionMode: LlmExecutionMode.ServerManagedApi);
                using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                    new CoreAISettingsOptions(), null, null, null, null, binding);
                registry.SetLegacyFallback(borrowed);
                LlmRouteTable routes = Routes();
                LlmBackendProfileEntry[] profiles = Profiles(http);
                registry.ApplyRouteTable(routes, profiles);
                ILlmClient old = registry.ResolveClientForRole("role");
                registry.ApplyRouteTable(routes, profiles);
                Assert.That(binding.Leaves[0].Disposals, Is.EqualTo(1));
                Assert.ThrowsAsync<LlmClientException>(async () => await old.CompleteAsync(Request()));
                registry.ApplyRouteTable(null, null, false);
                Assert.That(binding.Leaves[1].Disposals, Is.EqualTo(1));
                Assert.That((await registry.ResolveClientForRole("role").CompleteAsync(Request())).Content,
                    Is.EqualTo("borrowed"));
                registry.ApplyRouteTable(routes, profiles);
                registry.Dispose();
                Assert.That(binding.Leaves.Select(leaf => leaf.Disposals), Is.EqualTo(new[] { 1, 1, 1 }));
                Assert.That(borrowed.Disposals, Is.Zero);
                Assert.That((await borrowed.CompleteAsync(Request())).Ok, Is.True);
                Assert.That(binding.Wire.Disposals, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(http); }
        }

        [TestCase("deactivate")]
        [TestCase("remove")]
        [TestCase("replace")]
        public async Task RuntimeEndpointRetirement_DisposesOnlyExplicitGatewayOwnership(string action)
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                new CoreAISettingsOptions(), null, null, new OwnedEndpointFactory(binding), null, binding);
            LlmEndpointDescriptor descriptor = new LlmEndpointDescriptor
            {
                EndpointId = "gateway", DisplayName = "gateway", Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "https://gateway.test/v1", Model = "configured", Active = true
            };
            await registry.AddOrUpdateEndpointAsync(descriptor);
            ILlmClient stale = registry.ResolveClientForRole("role", "gateway");
            if (action == "deactivate") await registry.SetEndpointActiveAsync("gateway", false);
            else if (action == "remove") await registry.RemoveEndpointAsync("gateway");
            else
            {
                descriptor.BaseUrl = "https://replacement.test/v1";
                await registry.AddOrUpdateEndpointAsync(descriptor);
            }
            Assert.That(binding.Leaves[0].Disposals, Is.EqualTo(1));
            Assert.ThrowsAsync<LlmClientException>(async () => await stale.CompleteAsync(Request()));
            if (action == "replace")
                Assert.That((await registry.ResolveClientForRole("role", "gateway").CompleteAsync(Request())).Ok, Is.True);
            registry.Dispose();
            Assert.That(binding.Leaves.All(leaf => leaf.Disposals == 1), Is.True);
            Assert.That(binding.Wire.Disposals, Is.Zero);
        }

        [Test]
        public async Task LegacyMeaiConstructorAndBorrowedRegistryClient_DoNotTransferOwnership()
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            GatewayChatClient gateway = GatewayClientFactory.Create(new OpenAiHttpOptions
                { ApiBaseUrl = "https://gateway.test/v1" }, binding.Wire);
            using CountingOwnedChat borrowedChat = new CountingOwnedChat(gateway);
            MeaiLlmClient legacy = new MeaiLlmClient(borrowedChat, new SilentLogger(),
                new CoreAISettingsOptions(), true, null);
            BorrowedClient borrowedClient = new BorrowedClient();
            using (LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(), new CoreAISettingsOptions()))
            {
                registry.SetLegacyFallback(legacy);
                registry.SetLegacyFallback(borrowedClient);
            }
            legacy.Dispose();
            Assert.That(borrowedChat.Disposals, Is.Zero);
            Assert.That(borrowedClient.Disposals, Is.Zero);
            Assert.That((await borrowedClient.CompleteAsync(Request())).Ok, Is.True);
            Assert.That(binding.Wire.Disposals, Is.Zero);
        }

        private static LlmCompletionRequest Request() => new LlmCompletionRequest
            { AgentRoleId = "role", UserPayload = "hello" };

        private static async Task InvokeSource(ILlmClient client, bool streaming, CancellationToken token)
        {
            if (!streaming) { await client.CompleteAsync(Request(), token); return; }
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(Request(), token)) { }
        }

        private static LlmRouteTable Routes() => new LlmRouteTable
        {
            Profiles = new[] { new LlmRouteProfile { ProfileId = "gateway", Mode = LlmExecutionMode.ServerManagedApi } },
            Rules = new[] { new LlmRouteRule { RolePattern = "*", ProfileId = "gateway" } }
        };

        private static LlmBackendProfileEntry[] Profiles(OpenAiHttpLlmSettings settings) => new[]
        {
            new LlmBackendProfileEntry
            {
                profileId = "gateway", executionMode = LlmExecutionMode.ServerManagedApi,
                kind = LlmBackendKind.ServerManagedApi, httpSettings = settings
            }
        };



        [TestCase(false)]
        [TestCase(true)]
        public async Task DeactivationOwnedCallback_CanQueryRegistryBeforeReturning(bool pendingReplacement)
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            ControlledEndpointFactory factory = new ControlledEndpointFactory(binding)
                { HoldCall = pendingReplacement ? 2 : int.MaxValue };
            using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                new CoreAISettingsOptions(), null, new EmptyRegistryStore(), factory, null, binding);
            await registry.AddOrUpdateEndpointAsync(Endpoint("https://first.test/v1"));
            ILlmClient stale = registry.ResolveClientForRole("role", "gateway");
            Task<LlmEndpointSnapshot> pending = pendingReplacement
                ? registry.AddOrUpdateEndpointAsync(Endpoint("https://pending.test/v1")) : null;
            if (pendingReplacement) await factory.Entered.Task;
            bool queryCompletedBeforeReturn = false;
            Task query = null;
            binding.Leaves[0].OnDispose = () =>
            {
                query = Task.Run(() => registry.GetEndpoints());
                queryCompletedBeforeReturn = query.Wait(TimeSpan.FromSeconds(2));
            };
            await registry.SetEndpointActiveAsync("gateway", false);
            if (query != null) await query;
            Assert.That(queryCompletedBeforeReturn, Is.True, "Owned cleanup must execute after the mutation lock exits.");
            Assert.That(binding.Leaves[0].Disposals, Is.EqualTo(1));
            Assert.ThrowsAsync<LlmClientException>(async () => await stale.CompleteAsync(Request()));
            if (pending != null)
            {
                factory.CompletePending(true);
                await AwaitBounded(pending);
                Assert.That(factory.ProducedOwnedLeaf.Disposals, Is.EqualTo(1));
            }
            await AwaitBounded(registry.SetEndpointActiveAsync("gateway", true));
            Assert.That((await registry.ResolveClientForRole("role", "gateway").CompleteAsync(Request())).Ok, Is.True);
            Assert.That(binding.Wire.Disposals, Is.Zero);
        }

        [Test]
        public async Task FailedPipelineResolution_RetiresUnpublishedPrimaryAndProfilesThenCanRetry()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OpenAiHttpLlmSettings http = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            LlmRoutingManifest manifest = ScriptableObject.CreateInstance<LlmRoutingManifest>();
            ILlmAsyncMarshaler previousMarshaler = MeaiOpenAiChatClient.DefaultAsyncMarshaler;
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            InvalidOperationException original = new InvalidOperationException("Initialization sentinel.");
            try
            {
                settings.ConfigureServerManagedApi("https://gateway.test/v1", "");
                http.SetRuntimeConfiguration(true, "https://gateway.test/v1", "", "",
                    executionMode: LlmExecutionMode.ServerManagedApi);
                ConfigureTwoGatewayProfiles(manifest, http);
                binding.FailAtCreate = 3;
                binding.CreationFailure = original;
                binding.DisposalCallback = () => throw new InvalidOperationException("Cleanup sentinel.");
                Exception failure = Assert.Catch<Exception>(() =>
                {
                    using IObjectResolver unexpected = BuildPipelineContainer(settings, manifest, binding);
                });
                Assert.That(ContainsException(failure, original), Is.True, "Cleanup must preserve the initialization cause.");
                Assert.That(binding.Leaves.Count, Is.EqualTo(2));
                Assert.That(binding.Leaves.Select(leaf => leaf.Disposals), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(binding.Wire.Disposals, Is.Zero);
                Assert.That(binding.Auth.Disposals, Is.Zero);
                Assert.That(binding.Headers.Disposals, Is.Zero);
                binding.FailAtCreate = 0;
                binding.DisposalCallback = null;
                using (IObjectResolver repaired = BuildPipelineContainer(settings, manifest, binding))
                {
                    LlmClientRegistry registry = repaired.Resolve<LlmClientRegistry>();
                    Assert.That((await registry.ResolveClientForRole("role").CompleteAsync(Request())).Ok, Is.True);
                    Assert.That(binding.Leaves.Count, Is.EqualTo(5));
                    Assert.That(binding.Leaves.Skip(2).All(leaf => leaf.Disposals == 0), Is.True);
                }
                Assert.That(binding.Leaves.All(leaf => leaf.Disposals == 1), Is.True);
            }
            finally
            {
                MeaiOpenAiChatClient.DefaultAsyncMarshaler = previousMarshaler;
                UnityEngine.Object.DestroyImmediate(manifest);
                UnityEngine.Object.DestroyImmediate(http);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase("deactivate", true)]
        [TestCase("remove", true)]
        [TestCase("replace", true)]
        [TestCase("deactivate", false)]
        [TestCase("remove", false)]
        [TestCase("replace", false)]
        public async Task LateActivationResult_ClosesUnpublishedOwnedLeafButKeepsBorrowedResult(string action, bool owned)
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            ControlledEndpointFactory factory = new ControlledEndpointFactory(binding) { HoldCall = 1 };
            using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                new CoreAISettingsOptions(), null, new EmptyRegistryStore(), factory, null, binding);
            Task<LlmEndpointSnapshot> initial = registry.AddOrUpdateEndpointAsync(Endpoint("https://first.test/v1"));
            await factory.Entered.Task;
            Task retirement = Task.CompletedTask;
            if (action == "deactivate") retirement = registry.SetEndpointActiveAsync("gateway", false);
            else if (action == "remove") await registry.RemoveEndpointAsync("gateway");
            else await registry.AddOrUpdateEndpointAsync(Endpoint("https://replacement.test/v1"));
            factory.CompletePending(owned);
            await AwaitBounded(initial);
            await AwaitBounded(retirement);
            if (owned) Assert.That(factory.ProducedOwnedLeaf.Disposals, Is.EqualTo(1));
            else Assert.That(factory.BorrowedResult.Disposals, Is.Zero);
            if (action == "deactivate") await AwaitBounded(registry.SetEndpointActiveAsync("gateway", true));
            else if (action == "remove") await registry.AddOrUpdateEndpointAsync(Endpoint("https://fresh.test/v1"));
            Assert.That((await registry.ResolveClientForRole("role", "gateway").CompleteAsync(Request())).Ok, Is.True);
            registry.Dispose();
            if (owned) Assert.That(factory.ProducedOwnedLeaf.Disposals, Is.EqualTo(1));
            Assert.That(factory.BorrowedResult.Disposals, Is.Zero);
            Assert.That(binding.Wire.Disposals, Is.Zero);
        }

        [Test]
        public async Task LateOwnedActivation_PreservesAsynchronousHostReleaseCallback()
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            ControlledEndpointFactory factory = new ControlledEndpointFactory(binding) { HoldCall = 1 };
            TaskCompletionSource<bool> releaseEntered = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> releaseFinish = new TaskCompletionSource<bool>();
            int releases = 0;
            factory.PendingRelease = () =>
            {
                releases++; releaseEntered.TrySetResult(true); return releaseFinish.Task;
            };
            using LlmClientRegistry registry = new LlmClientRegistry(new SilentLogger(),
                new CoreAISettingsOptions(), null, new EmptyRegistryStore(), factory, null, binding);
            Task<LlmEndpointSnapshot> activation = registry.AddOrUpdateEndpointAsync(Endpoint("https://first.test/v1"));
            await factory.Entered.Task;
            Task retirement = registry.SetEndpointActiveAsync("gateway", false);
            factory.CompletePending(true);
            await AwaitBounded(activation);
            await AwaitBounded(retirement);
            await AwaitBounded(releaseEntered.Task);
            Assert.That(factory.ProducedOwnedLeaf.Disposals, Is.EqualTo(1));
            Assert.That(releases, Is.EqualTo(1));
            releaseFinish.SetResult(true);
            registry.Dispose();
            Assert.That(factory.ProducedOwnedLeaf.Disposals, Is.EqualTo(1));
        }

        [Test]
        public async Task RegistryDisposal_ThrowingActivationCallbackCannotSkipOtherCancellationsOrOwnedHostReleases()
        {
            using OwnedLeafBinding binding = new OwnedLeafBinding();
            DisposalWarningLogger logger = new DisposalWarningLogger();
            using DisposalCallbackEndpointFactory factory = new DisposalCallbackEndpointFactory(binding);
            using LlmClientRegistry registry = new LlmClientRegistry(logger,
                new CoreAISettingsOptions(), null, new EmptyRegistryStore(), factory, null, binding);
            factory.RegistryGate = typeof(LlmClientRegistry).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(registry);
            LlmEndpointDescriptor ready = Endpoint("https://ready.test/v1");
            ready.EndpointId = "ready";
            await registry.AddOrUpdateEndpointAsync(ready);
            LlmEndpointDescriptor faulting = Endpoint("https://pending-fault.test/v1");
            faulting.EndpointId = "pending-fault";
            Task<LlmEndpointSnapshot> pendingFault = registry.AddOrUpdateEndpointAsync(faulting);
            LlmEndpointDescriptor healthy = Endpoint("https://pending-healthy.test/v1");
            healthy.EndpointId = "pending-healthy";
            Task<LlmEndpointSnapshot> pendingHealthy = registry.AddOrUpdateEndpointAsync(healthy);
            try
            {
                Assert.DoesNotThrow(() => registry.Dispose());
                Assert.IsFalse(factory.CancelledUnderRegistryLock,
                    "Replaceable token callbacks must execute after the registry mutation lock exits.");
                Assert.AreEqual(1, factory.CancellationCount("ready"));
                Assert.AreEqual(1, factory.CancellationCount("pending-fault"));
                Assert.AreEqual(1, factory.CancellationCount("pending-healthy"),
                    "One callback failure must not skip cancellation of the next endpoint.");
                Assert.GreaterOrEqual(logger.WarningCount, 1, "The isolated callback fault remains observable.");
                await AwaitBounded(factory.Released("ready"));
                factory.CompletePending("pending-fault");
                factory.CompletePending("pending-healthy");
                await AwaitBounded(pendingFault);
                await AwaitBounded(pendingHealthy);
                await AwaitBounded(factory.Released("pending-fault"));
                await AwaitBounded(factory.Released("pending-healthy"));
                registry.Dispose();
                foreach (string id in new[] { "ready", "pending-fault", "pending-healthy" })
                    Assert.AreEqual(1, factory.ReleaseCount(id));
                Assert.AreEqual(3, binding.Leaves.Count);
                Assert.IsTrue(binding.Leaves.All(leaf => leaf.Disposals == 1));
                Assert.AreEqual(0, binding.Wire.Disposals);
                Assert.AreEqual(0, binding.Auth.Disposals);
                Assert.AreEqual(0, binding.Headers.Disposals);
            }
            finally
            {
                factory.CompletePending("pending-fault");
                factory.CompletePending("pending-healthy");
            }
        }

        private sealed class DisposalCallbackEndpointFactory : ILlmEndpointClientFactory, IDisposable
        {
            private readonly OwnedLeafBinding _binding;
            private readonly object _gate = new object();
            private readonly Dictionary<string, TaskCompletionSource<LlmEndpointClientActivation>> _pending =
                new Dictionary<string, TaskCompletionSource<LlmEndpointClientActivation>>(StringComparer.Ordinal);
            private readonly Dictionary<string, TaskCompletionSource<bool>> _released =
                new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _releaseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _cancellationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<CancellationTokenRegistration> _registrations = new List<CancellationTokenRegistration>();
            public object RegistryGate;
            public bool CancelledUnderRegistryLock;

            public DisposalCallbackEndpointFactory(OwnedLeafBinding binding) => _binding = binding;

            public Task<LlmEndpointClientActivation> ActivateAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey, CancellationToken cancellationToken)
            {
                string id = descriptor.EndpointId;
                _released[id] = new TaskCompletionSource<bool>();
                _registrations.Add(cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        CancelledUnderRegistryLock |= Monitor.IsEntered(RegistryGate);
                        _cancellationCounts[id] = CancellationCount(id) + 1;
                    }
                    if (id == "pending-fault") throw new InvalidOperationException("The host cancellation callback failed.");
                }));
                if (id == "ready") return Task.FromResult(Create(id));
                TaskCompletionSource<LlmEndpointClientActivation> pending = new TaskCompletionSource<LlmEndpointClientActivation>();
                _pending[id] = pending;
                return pending.Task;
            }

            public void CompletePending(string id)
            {
                if (_pending.TryGetValue(id, out TaskCompletionSource<LlmEndpointClientActivation> pending) && !pending.Task.IsCompleted)
                    pending.TrySetResult(Create(id));
            }

            public int CancellationCount(string id)
            {
                lock (_gate) return _cancellationCounts.TryGetValue(id, out int count) ? count : 0;
            }

            public int ReleaseCount(string id)
            {
                lock (_gate) return _releaseCounts.TryGetValue(id, out int count) ? count : 0;
            }

            public Task Released(string id) => _released[id].Task;

            private LlmEndpointClientActivation Create(string id)
            {
                ServerManagedLlmClient server = new ServerManagedLlmClient(new OpenAiHttpOptions
                    { ApiBaseUrl = "https://owned.test/v1", Model = "model" }, new CoreAISettingsOptions(),
                    new SilentLogger(), null, _binding);
                return new LlmEndpointClientActivation
                {
                    Client = new OwnedGatewayClientChain(server, server),
                    Mode = LlmExecutionMode.ServerManagedApi,
                    ReleaseOwnedHostAsync = () =>
                    {
                        lock (_gate) _releaseCounts[id] = ReleaseCount(id) + 1;
                        _released[id].TrySetResult(true);
                        return Task.CompletedTask;
                    }
                };
            }

            public void Dispose()
            {
                foreach (CancellationTokenRegistration registration in _registrations) registration.Dispose();
            }
        }

        private sealed class DisposalWarningLogger : IGameLogger
        {
            public int WarningCount;
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) => Interlocked.Increment(ref WarningCount);
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
        }

        private static IObjectResolver BuildPipelineContainer(CoreAISettingsAsset settings,
            LlmRoutingManifest manifest, OwnedLeafBinding binding)
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(new SilentLogger());
            builder.RegisterInstance<IAgentMemoryStore>(new NullAgentMemoryStore());
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<IUnityGatewayClientBinding>(new BorrowedBindingAdapter(binding));
            LlmPipelineInstaller.RegisterLlmPipelineForPlatform(builder, settings, manifest,
                RuntimePlatform.WebGLPlayer, new EmptyRegistryStore());
            return builder.Build();
        }

        private static void ConfigureTwoGatewayProfiles(LlmRoutingManifest manifest, OpenAiHttpLlmSettings http)
        {
            SerializedObject serialized = new SerializedObject(manifest);
            SerializedProperty profiles = serialized.FindProperty("profiles");
            profiles.arraySize = 2;
            for (int index = 0; index < 2; index++)
            {
                SerializedProperty profile = profiles.GetArrayElementAtIndex(index);
                profile.FindPropertyRelative("profileId").stringValue = index == 0 ? "first" : "second";
                profile.FindPropertyRelative("executionMode").intValue = (int)LlmExecutionMode.ServerManagedApi;
                profile.FindPropertyRelative("kind").intValue = (int)LlmBackendKind.ServerManagedApi;
                profile.FindPropertyRelative("httpSettings").objectReferenceValue = http;
            }
            SerializedProperty routes = serialized.FindProperty("routes");
            routes.arraySize = 1;
            routes.GetArrayElementAtIndex(0).FindPropertyRelative("rolePattern").stringValue = "*";
            routes.GetArrayElementAtIndex(0).FindPropertyRelative("profileId").stringValue = "first";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            serialized.Dispose();
        }

        private static async Task AwaitBounded(Task operation)
        {
            using CancellationTokenSource deadline = new CancellationTokenSource();
            Task delay = Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
            Task first = await Task.WhenAny(operation, delay);
            deadline.Cancel();
            Assert.That(first, Is.SameAs(operation), "The controlled lifecycle operation did not complete.");
            await operation;
        }

        private static bool ContainsException(Exception failure, Exception expected) =>
            ReferenceEquals(failure, expected) || failure?.InnerException != null && ContainsException(failure.InnerException, expected) ||
            failure is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => ContainsException(inner, expected));

        private static LlmEndpointDescriptor Endpoint(string url) => new LlmEndpointDescriptor
        {
            EndpointId = "gateway", DisplayName = "gateway", Kind = LlmEndpointKind.HttpOpenAi,
            BaseUrl = url, Model = "configured", Active = true
        };


        private sealed class EmptyRegistryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState Load() => new LlmEndpointRegistryState();
            public void Save(LlmEndpointRegistryState state) { }
        }

        private sealed class BorrowedBindingAdapter : IUnityGatewayClientBinding
        {
            private readonly OwnedLeafBinding _binding;
            public BorrowedBindingAdapter(OwnedLeafBinding binding) => _binding = binding;
            public ILlmClient CreateClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings,
                IGameLogger logger, IAgentMemoryStore memoryStore, string routingProfileId = "") =>
                _binding.CreateClient(settings, coreSettings, logger, memoryStore, routingProfileId);
        }

        private sealed class ControlledEndpointFactory : ILlmEndpointClientFactory
        {
            private readonly OwnedLeafBinding _binding;
            private readonly TaskCompletionSource<LlmEndpointClientActivation> _pending =
                new TaskCompletionSource<LlmEndpointClientActivation>();
            private string _url;
            private string _model;
            private int _calls;
            public ControlledEndpointFactory(OwnedLeafBinding binding) => _binding = binding;
            public int HoldCall = 1;
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public readonly BorrowedClient BorrowedResult = new BorrowedClient();
            public CountingOwnedChat ProducedOwnedLeaf;
            public Func<Task> PendingRelease;
            public Task<LlmEndpointClientActivation> ActivateAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey, CancellationToken cancellationToken)
            {
                if (++_calls != HoldCall) return Task.FromResult(Create(descriptor.BaseUrl, descriptor.Model, true));
                _url = descriptor.BaseUrl; _model = descriptor.Model; Entered.TrySetResult(true);
                return _pending.Task;
            }
            public void CompletePending(bool owned)
            {
                LlmEndpointClientActivation result = Create(_url, _model, owned);
                result.ReleaseOwnedHostAsync = PendingRelease;
                if (owned) ProducedOwnedLeaf = _binding.Leaves.Last();
                _pending.TrySetResult(result);
            }
            private LlmEndpointClientActivation Create(string url, string model, bool owned)
            {
                if (!owned) return new LlmEndpointClientActivation { Client = BorrowedResult, Mode = LlmExecutionMode.ServerManagedApi };
                ServerManagedLlmClient server = new ServerManagedLlmClient(new OpenAiHttpOptions
                    { ApiBaseUrl = url, Model = model }, new CoreAISettingsOptions(), new SilentLogger(), null, _binding);
                return new LlmEndpointClientActivation
                { Client = new OwnedGatewayClientChain(server, server), Mode = LlmExecutionMode.ServerManagedApi };
            }
        }

        private sealed class OwnedLeafBinding : IUnityGatewayClientBinding, IDisposable
        {
            private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
            public readonly List<CountingOwnedChat> Leaves = new List<CountingOwnedChat>();
            public readonly BorrowedWire Wire = new BorrowedWire();
            public readonly BorrowedAuth Auth = new BorrowedAuth();
            public readonly BorrowedHeaders Headers = new BorrowedHeaders();
            public ILlmEntitlementPolicy Policy;
            public int FailAtCreate;
            public Exception CreationFailure;
            public Action DisposalCallback;
            private int _creates;
            private int _disposed;
            public ILlmClient CreateClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings,
                IGameLogger logger, IAgentMemoryStore memoryStore, string routingProfileId = "")
            {
                if (++_creates == FailAtCreate) throw CreationFailure;
                GatewayChatClient gateway = GatewayClientFactory.Create(settings, Wire,
                    new GatewayClientOptions { EnableClientPreflight = Policy != null },
                    Auth, Headers, entitlementPolicy: Policy,
                    asyncMarshaler: PassThroughLlmAsyncMarshaler.Instance, hostLifetime: _lifetime.Token);
                CountingOwnedChat leaf = new CountingOwnedChat(gateway);
                leaf.OnDispose = DisposalCallback;
                Leaves.Add(leaf);
                return new MeaiLlmClient(leaf, logger, coreSettings, true, memoryStore, settings, leaf);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _lifetime.Cancel(); } finally { _lifetime.Dispose(); }
            }
        }

        private sealed class CountingOwnedChat : MEAI.IChatClient
        {
            public CountingOwnedChat(GatewayChatClient gateway) => Gateway = gateway;
            public GatewayChatClient Gateway { get; }
            public int Disposals;
            public Action OnDispose;
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                Gateway.GetResponseAsync(messages, options, cancellationToken);
            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default) =>
                Gateway.GetStreamingResponseAsync(messages, options, cancellationToken);
            public object GetService(Type serviceType, object serviceKey = null) =>
                Gateway.GetService(serviceType, serviceKey);
            public void Dispose()
            {
                Disposals++;
                try { OnDispose?.Invoke(); } finally { Gateway.Dispose(); }
            }
        }

        private sealed class BorrowedWire : IOpenAiHttpTransport, IDisposable
        {
            public int Disposals;
            public int Sends;
            public string DebugLabel => "owned-source-fake";
            public bool SupportsSseStreaming => true;
            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); Sends++;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
                });
            }
            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); Sends++;
                string data = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                return Task.FromResult(new OpenAiHttpSseOpenResult { StatusCode = 200 }
                    .WithRawStream(new MemoryStream(Encoding.UTF8.GetBytes(data))));
            }
            public void Dispose() => Disposals++;
        }

        private sealed class BorrowedAuth : ILlmAuthContextProvider, IDisposable
        {
            public int Disposals;
            public string GetAuthorizationHeader() => "Bearer host-owned";
            public string TenantId => "";
            public string UserId => "";
            public string SessionId => "";
            public void Dispose() => Disposals++;
        }

        private sealed class BorrowedHeaders : IRequestHeaderProvider, IDisposable
        {
            public int Disposals;
            public IReadOnlyList<KeyValuePair<string, string>> GetHeaders() =>
                Array.Empty<KeyValuePair<string, string>>();
            public string IdempotencyKey => "";
            public string RequestId => "";
            public void Dispose() => Disposals++;
        }

        private sealed class BorrowedClient : ILlmClient, IDisposable
        {
            public int Disposals;
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LlmCompletionResult { Ok = true, Content = "borrowed" });
            public void Dispose() => Disposals++;
        }

        private sealed class OwnedEndpointFactory : ILlmEndpointClientFactory
        {
            private readonly OwnedLeafBinding _binding;
            public OwnedEndpointFactory(OwnedLeafBinding binding) => _binding = binding;
            public Task<LlmEndpointClientActivation> ActivateAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey, CancellationToken cancellationToken)
            {
                ServerManagedLlmClient server = new ServerManagedLlmClient(new OpenAiHttpOptions
                    { ApiBaseUrl = descriptor.BaseUrl, Model = descriptor.Model },
                    new CoreAISettingsOptions(), new SilentLogger(), null, _binding);
                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = new OwnedGatewayClientChain(server, server), Mode = LlmExecutionMode.ServerManagedApi
                });
            }
        }

        private sealed class ProbeBinding : IUnityGatewayClientBinding
        {
            public int Creates;
            public string Profile;
            public readonly List<string> Endpoints = new List<string>();
            public readonly ProbeClient Client = new ProbeClient();
            public ILlmClient CreateClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings,
                IGameLogger logger, IAgentMemoryStore memoryStore, string routingProfileId = "")
            {
                Creates++; Profile = routingProfileId; Endpoints.Add(settings.ApiBaseUrl); return Client;
            }
        }
        private sealed class ProbeClient : ILlmClient
        {
            public int Calls;
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new LlmCompletionResult
                { Ok = false, Error = "auth", ErrorCode = LlmErrorCode.AuthExpired, HttpStatus = 401 });
            }
        }
        private sealed class LegacyRefresher : IServerManagedAuthRefresher
        {
            public int Calls;
            public Task<bool> RefreshAsync(CancellationToken cancellationToken)
            { Calls++; return Task.FromResult(true); }
        }
        private sealed class DenialPolicy : ILlmEntitlementPolicy
        {
            private readonly string _reason;
            public DenialPolicy(string reason) => _reason = reason;
            public Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
                LlmRouteProfile profile, CancellationToken cancellationToken = default) =>
                Task.FromResult(new LlmEntitlementDecision { IsAllowed = false, ReasonCode = _reason });
        }
        private sealed class PendingPolicy : ILlmEntitlementPolicy
        {
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public CancellationToken Token;
            public async Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
                LlmRouteProfile profile, CancellationToken cancellationToken = default)
            {
                Token = cancellationToken;
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return LlmEntitlementDecision.Allowed;
            }
        }
        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
        }
    }
}
#endif

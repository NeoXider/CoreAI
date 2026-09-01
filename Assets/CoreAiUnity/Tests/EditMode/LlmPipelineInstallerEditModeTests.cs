#if COREAI_LLM
using System;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Tests for <see cref="LlmPipelineInstaller"/> verifying that
    /// <see cref="IAgentMemoryStore"/> is correctly propagated through
    /// all LLM client construction paths (HTTP, ServerManaged, Auto).
    /// </summary>
    [TestFixture]
    public sealed class LlmPipelineInstallerEditModeTests
    {
        private sealed class StubMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = default;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return null;
            }
        }

        /// <summary>
        /// BuildHttpClient must propagate memoryStore to OpenAiChatLlmClient
        /// so that the memory tool's AIFunction can be bound in MeaiLlmClient.
        /// Before the fix, BuildHttpClient was called without memoryStore,
        /// causing memory tool calls to be silently stripped as no-ops.
        /// </summary>
        [Test]
        public void BuildHttpClient_PassesMemoryStore_ToOpenAiChatLlmClient()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Set minimum required fields for HTTP client creation
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            StubMemoryStore memoryStore = new();

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientOwnedApi, memoryStore);

            Assert.IsNotNull(client, "BuildHttpClient should return a client");
            Assert.IsInstanceOf<OpenAiChatLlmClient>(client,
                "ClientOwnedApi mode should return OpenAiChatLlmClient");

            // Verify memory store was propagated by checking the inner MeaiLlmClient
            FieldInfo meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(meaiField, "Should find _client field on OpenAiChatLlmClient");
            object meaiClient = meaiField.GetValue(client);
            Assert.IsNotNull(meaiClient, "MeaiLlmClient should exist");

            FieldInfo storeField = meaiClient.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(storeField, "Should find _memoryStore field on MeaiLlmClient");
            object actualStore = storeField.GetValue(meaiClient);

            Assert.AreSame(memoryStore, actualStore,
                "BuildHttpClient must propagate memoryStore to MeaiLlmClient — " +
                "otherwise memory tool calls are silently stripped in HTTP modes");
        }

        [Test]
        public void BuildHttpClient_WithoutMemoryStore_StillCreatesClient()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientOwnedApi);

            Assert.IsNotNull(client, "Should work without memoryStore (backwards compatible)");

            // memoryStore should be null in the inner client
            FieldInfo meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            object meaiClient = meaiField?.GetValue(client);
            FieldInfo storeField = meaiClient?.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            object actualStore = storeField?.GetValue(meaiClient);

            Assert.IsNull(actualStore, "memoryStore should be null when not provided");
        }

        [Test]
        public void BuildHttpClient_ClientLimited_StillPassesMemoryStore()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            StubMemoryStore memoryStore = new();

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientLimited, memoryStore);

            Assert.IsNotNull(client, "Should return a client for ClientLimited mode");
            // ClientLimited wraps in decorator — unwrap to verify
            Assert.IsInstanceOf<ClientLimitedLlmClientDecorator>(client,
                "ClientLimited should wrap in ClientLimitedLlmClientDecorator");

            FieldInfo innerField = typeof(ClientLimitedLlmClientDecorator)
                .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(innerField, "Should find _inner field");
            OpenAiChatLlmClient innerClient = innerField.GetValue(client) as OpenAiChatLlmClient;
            Assert.IsNotNull(innerClient, "Inner client should be OpenAiChatLlmClient");

            FieldInfo meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            object meaiClient = meaiField?.GetValue(innerClient);
            FieldInfo storeField = meaiClient?.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            object actualStore = storeField?.GetValue(meaiClient);

            Assert.AreSame(memoryStore, actualStore,
                "ClientLimited mode must also propagate memoryStore through the decorator chain");
        }

#if COREAI_HAS_LLMUNITY
        [Test]
        public void WebGlComposition_BuildsWithoutLocalProvider_AndReturnsBrowserLimitation()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                SetField(settings, "executionMode", LlmExecutionMode.LocalModel);
                ContainerBuilder builder = new();
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
                builder.RegisterCore();
                builder.RegisterInstance<IAgentMemoryStore>(new StubMemoryStore());
                LlmPipelineInstaller.RegisterLlmPipelineForPlatform(
                    builder,
                    settings,
                    null,
                    RuntimePlatform.WebGLPlayer);

                container = builder.Build();

                Assert.IsFalse(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNull(provider);

                ILlmClient client = container.Resolve<ILlmClient>();
                LlmCompletionResult result = client.CompleteAsync(new LlmCompletionRequest())
                    .GetAwaiter()
                    .GetResult();
                Assert.IsFalse(result.Ok);
                Assert.AreEqual(LlmErrorCode.RoutingError, result.ErrorCode);
                Assert.AreEqual(LocalModelPlatformSupport.BrowserUnavailableMessage, result.Error);
            }
            finally
            {
                if (container is System.IDisposable disposable)
                {
                    disposable.Dispose();
                }

                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void WebGlComposition_HttpModeBuildsHttpClientWithoutLocalProvider()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                settings.ConfigureClientOwnedApi("https://example.invalid/v1", "test-key", "test-model");
                container = BuildPlatformContainer(settings, RuntimePlatform.WebGLPlayer);

                Assert.IsFalse(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNull(provider);
                Assert.IsInstanceOf<OpenAiChatLlmClient>(
                    container.Resolve<ILlmClientRegistry>().ResolveClientForRole("SmartChat"));
            }
            finally
            {
                Dispose(container);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void WebGlComposition_OfflineModeCompletesWithoutLocalProvider()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                settings.ConfigureOffline(true, "browser-offline", "SmartChat");
                container = BuildPlatformContainer(settings, RuntimePlatform.WebGLPlayer);

                Assert.IsFalse(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNull(provider);
                LlmCompletionResult result = container.Resolve<ILlmClientRegistry>()
                    .ResolveClientForRole("SmartChat")
                    .CompleteAsync(new LlmCompletionRequest { AgentRoleId = "SmartChat" })
                    .GetAwaiter()
                    .GetResult();
                Assert.IsTrue(result.Ok);
                Assert.AreEqual("browser-offline", result.Content);
            }
            finally
            {
                Dispose(container);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void DesktopComposition_RegistersLocalProvider_AndAutostartCapability()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                settings.ConfigureLlmUnity(ggufPath: "desktop.gguf");
                container = BuildPlatformContainer(settings, RuntimePlatform.WindowsPlayer);

#if UNITY_WEBGL
                Assert.IsFalse(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNull(provider);
                Assert.IsFalse(LlmPipelineInstaller.ShouldRegisterLocalModelProvider(RuntimePlatform.WindowsPlayer),
                    "A WebGL-compiled assembly must not restore the stripped local provider through a platform override.");
                Assert.IsFalse(CoreAILifetimeScope.ShouldRegisterLlmUnityAutostart(RuntimePlatform.WindowsPlayer),
                    "A WebGL-compiled assembly must not restore the stripped native autostart through a platform override.");
#else
                Assert.IsTrue(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNotNull(provider);
                Assert.IsTrue(LlmPipelineInstaller.ShouldRegisterLocalModelProvider(RuntimePlatform.WindowsPlayer));
                Assert.IsTrue(CoreAILifetimeScope.ShouldRegisterLlmUnityAutostart(RuntimePlatform.WindowsPlayer));
#endif
                Assert.IsFalse(CoreAILifetimeScope.ShouldRegisterLlmUnityAutostart(RuntimePlatform.WebGLPlayer));
            }
            finally
            {
                Dispose(container);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void LiveScopeWithoutLocalProvider_ApplyOfflineReplacesClientAndCompletesRequest()
        {
            CoreAISettingsAsset previousSettings = CoreAISettingsAsset.Instance;
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            GameObject root = new("CoreAI hot-swap scope without local provider");
            root.SetActive(false);
            IObjectResolver container = null;
            try
            {
                settings.ConfigureLlmUnity(ggufPath: "browser.gguf");
                CoreAISettingsAsset.SetInstance(settings);
                container = BuildPlatformContainer(settings, RuntimePlatform.WebGLPlayer);
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                SetScopeContainer(scope, container);
                Assert.IsFalse(container.TryResolve(out ILlmAgentProvider provider));
                Assert.IsNull(provider);

                ILlmClientRegistry registry = container.Resolve<ILlmClientRegistry>();
                ILlmClient before = registry.ResolveClientForRole("SmartChat");

                bool live = CoreAiBackend.ApplyOffline(true, "hot-swapped-offline", "SmartChat");
                ILlmClient after = registry.ResolveClientForRole("SmartChat");
                LlmCompletionResult result = after.CompleteAsync(
                        new LlmCompletionRequest { AgentRoleId = "SmartChat" })
                    .GetAwaiter()
                    .GetResult();

                Assert.IsTrue(live, "The existing scope must hot-swap even without ILlmAgentProvider.");
                Assert.AreNotSame(before, after);
                Assert.IsInstanceOf<OfflineLlmClient>(after);
                Assert.IsTrue(result.Ok);
                Assert.AreEqual("hot-swapped-offline", result.Content);
            }
            finally
            {
                CoreAISettingsAsset.SetInstance(previousSettings);
                Object.DestroyImmediate(root);
                Dispose(container);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void LiveScopeWithoutLocalProvider_ApplyHttpApiReplacesClient()
        {
            CoreAISettingsAsset previousSettings = CoreAISettingsAsset.Instance;
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            GameObject root = new("CoreAI HTTP hot-swap scope without local provider");
            root.SetActive(false);
            IObjectResolver container = null;
            try
            {
                settings.ConfigureOffline(true, "before-http", "SmartChat");
                CoreAISettingsAsset.SetInstance(settings);
                container = BuildPlatformContainer(settings, RuntimePlatform.WebGLPlayer);
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                SetScopeContainer(scope, container);

                ILlmClientRegistry registry = container.Resolve<ILlmClientRegistry>();
                ILlmClient before = registry.ResolveClientForRole("SmartChat");
                bool live = CoreAiBackend.ApplyHttpApi(
                    "https://example.invalid/v1",
                    "test-key",
                    "test-model");
                ILlmClient after = registry.ResolveClientForRole("SmartChat");

                Assert.IsTrue(live);
                Assert.AreNotSame(before, after);
                Assert.IsInstanceOf<OpenAiChatLlmClient>(after);
            }
            finally
            {
                CoreAISettingsAsset.SetInstance(previousSettings);
                Object.DestroyImmediate(root);
                Dispose(container);
                Object.DestroyImmediate(settings);
            }
        }

        [TestCase(RuntimePlatform.WindowsPlayer, true)]
        [TestCase(RuntimePlatform.LinuxPlayer, true)]
        [TestCase(RuntimePlatform.OSXPlayer, true)]
        [TestCase(RuntimePlatform.Android, true)]
        [TestCase(RuntimePlatform.IPhonePlayer, true)]
        [TestCase(RuntimePlatform.WebGLPlayer, false)]
        [TestCase(RuntimePlatform.WSAPlayerARM, false)]
        public void LocalModelPlatformSupport_MatchesNativeLibraryTargets(RuntimePlatform platform, bool expected)
        {
            Assert.AreEqual(expected, LocalModelPlatformSupport.IsSupported(platform));
        }
#endif

        private static IObjectResolver BuildPlatformContainer(
            CoreAISettingsAsset settings,
            RuntimePlatform platform)
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            builder.RegisterInstance<IAgentMemoryStore>(new StubMemoryStore());
            LlmPipelineInstaller.RegisterLlmPipelineForPlatform(builder, settings, null, platform);
            return builder.Build();
        }

        private static void SetScopeContainer(CoreAILifetimeScope scope, IObjectResolver container)
        {
            PropertyInfo property = typeof(LifetimeScope).GetProperty(
                nameof(LifetimeScope.Container),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            property.SetValue(scope, container);
        }

        private static void Dispose(IObjectResolver container)
        {
            if (container is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private static void SetField(object obj, string fieldName, object value)
        {
            FieldInfo field = obj.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field == null)
            {
                // Unity SerializedField naming
                field = obj.GetType().GetField(fieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            field?.SetValue(obj, value);
        }
    }
}
#endif

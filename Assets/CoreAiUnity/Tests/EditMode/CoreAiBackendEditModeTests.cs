using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAiBackend"/>, the runtime backend switching facade. No live
    /// scope exists in EditMode, so switch methods must mutate settings, return <c>false</c>
    /// (settings-only), and still raise <see cref="CoreAiBackend.OnBackendChanged"/>.
    /// </summary>
    public sealed class CoreAiBackendEditModeTests
    {
        private CoreAISettingsAsset _previousInstance;
        private CoreAISettingsAsset _testSettings;

        [SetUp]
        public void SetUp()
        {
            // Never mutate the real project asset from tests: install a throwaway instance.
            _previousInstance = CoreAISettingsAsset.Instance;
            _testSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset.SetInstance(_testSettings);
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettingsAsset.SetInstance(_previousInstance);
            if (_testSettings != null)
            {
                Object.DestroyImmediate(_testSettings);
            }
        }

        [Test]
        public void ApplyHttpApi_MutatesSettings_FiresEvent_ReturnsFalseWithoutScope()
        {
            List<CoreAiBackendStatus> events = new();

            void Handler(CoreAiBackendStatus s)
            {
                events.Add(s);
            }

            CoreAiBackend.OnBackendChanged += Handler;
            try
            {
                bool live = CoreAiBackend.ApplyHttpApi(
                    "http://127.0.0.1:9999/v1", "test-key", "test-model", 0.4f, 77, 512);

                Assert.IsFalse(live, "No live scope in EditMode: switch must be settings-only.");
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _testSettings.ExecutionMode);
                Assert.AreEqual("http://127.0.0.1:9999/v1", _testSettings.ApiBaseUrl);
                Assert.AreEqual("test-key", _testSettings.ApiKey);
                Assert.AreEqual("test-model", _testSettings.ModelName);
                Assert.AreEqual(77, _testSettings.RequestTimeoutSeconds);
                Assert.AreEqual(512, _testSettings.MaxTokens);

                Assert.AreEqual(1, events.Count, "OnBackendChanged must fire once per switch.");
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, events[0].Mode);
                Assert.AreEqual("test-model", events[0].Model);
                Assert.IsFalse(events[0].IsLive);
            }
            finally
            {
                CoreAiBackend.OnBackendChanged -= Handler;
            }
        }

        [Test]
        public void ApplyOffline_And_ApplyAuto_SwitchExecutionMode()
        {
            CoreAiBackend.ApplyOffline();
            Assert.AreEqual(LlmExecutionMode.Offline, _testSettings.ExecutionMode);

            CoreAiBackend.ApplyAuto();
            Assert.AreEqual(LlmBackendType.Auto, _testSettings.BackendType);
        }

        [Test]
        public void ApplyLlmUnity_SwitchesToLocalModel_AndKeepsGgufWhenNull()
        {
            _testSettings.ConfigureLlmUnity(ggufPath: "MyModel.gguf");

            CoreAiBackend.ApplyLlmUnity();

            Assert.AreEqual(LlmExecutionMode.LocalModel, _testSettings.ExecutionMode);
            Assert.AreEqual("MyModel.gguf", _testSettings.GgufModelPath,
                "Null ggufModelPath must keep the configured path.");

            CoreAiBackend.ApplyLlmUnity("Other.gguf");
            Assert.AreEqual("Other.gguf", _testSettings.GgufModelPath);
        }

        [Test]
        public void HotSetters_MutateSettings_AndFireEvent()
        {
            _testSettings.ConfigureClientOwnedApi("http://a/v1", "k", "m1");

            int fired = 0;

            void Handler(CoreAiBackendStatus s)
            {
                fired++;
            }

            CoreAiBackend.OnBackendChanged += Handler;
            try
            {
                CoreAiBackend.SetModel("m2");
                CoreAiBackend.SetApiBaseUrl("http://b/v1");
                CoreAiBackend.SetApiKey("k2");
            }
            finally
            {
                CoreAiBackend.OnBackendChanged -= Handler;
            }

            Assert.AreEqual("m2", _testSettings.ModelName);
            Assert.AreEqual("http://b/v1", _testSettings.ApiBaseUrl);
            Assert.AreEqual("k2", _testSettings.ApiKey);
            Assert.AreEqual(3, fired);
        }

        [Test]
        public void Status_ReflectsSettings()
        {
            _testSettings.ConfigureClientOwnedApi("http://s/v1", "key", "status-model");

            CoreAiBackendStatus status = CoreAiBackend.Status;

            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, status.Mode);
            Assert.AreEqual("http://s/v1", status.BaseUrl);
            Assert.AreEqual("status-model", status.Model);
            Assert.IsFalse(status.IsLive);
            StringAssert.Contains("status-model", status.ToString());
        }

        [Test]
        public async Task VerifyAsync_WithoutScope_ReportsNotRunning()
        {
            CoreAiBackendHealth health = await CoreAiBackend.VerifyAsync(2);

            Assert.IsFalse(health.Ok);
            StringAssert.Contains("scope", health.Error.ToLowerInvariant());
        }

        [Test]
        public void EventHandlerException_DoesNotBreakSwitch()
        {
            void Throwing(CoreAiBackendStatus s)
            {
                throw new System.InvalidOperationException("boom");
            }

            CoreAiBackend.OnBackendChanged += Throwing;
            try
            {
                Assert.DoesNotThrow(() => CoreAiBackend.ApplyOffline());
                Assert.AreEqual(LlmExecutionMode.Offline, _testSettings.ExecutionMode);
            }
            finally
            {
                CoreAiBackend.OnBackendChanged -= Throwing;
            }
        }
    }
}

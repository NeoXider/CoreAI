using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Newcomer audit A8: <see cref="CoreAiBackend"/> switches used to write into the shared
    /// <see cref="CoreAISettingsAsset"/>, so a key "supplied at runtime" outlived Play Mode, aborted the next build in
    /// the Resources key guard and was saved by the next inspector edit. Runtime switches now land in a
    /// non-serialized session override: the getters return the switched values, the serialized form of the asset
    /// (what the inspector saves and a build packs) stays byte-identical, and the override ends with the session.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiBackendSessionOverrideEditModeTests
    {
        private CoreAISettingsAsset _previousInstance;
        private CoreAISettingsAsset _settings;

        [SetUp]
        public void SetUp()
        {
            _previousInstance = CoreAISettingsAsset.Instance;
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureClientOwnedApi("http://asset/v1", "", "asset-model");
            CoreAISettingsAsset.SetInstance(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettingsAsset.SetInstance(_previousInstance);
            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
            }
        }

        private string Serialized()
        {
            return JsonUtility.ToJson(_settings);
        }

        [Test]
        public void RuntimeKeyChange_LeavesTheSerializedAssetUnchanged()
        {
            string before = Serialized();

            CoreAiBackend.SetApiKey("sk-runtime-only");

            Assert.AreEqual("sk-runtime-only", _settings.ApiKey, "The running pipeline sees the runtime key.");
            Assert.IsTrue(_settings.HasSessionOverrides);
            Assert.AreEqual(before, Serialized(), "Nothing the inspector saves or a build packs may change.");
            StringAssert.DoesNotContain("sk-runtime-only", Serialized());
        }

        [Test]
        public void AuthoringKeyChange_IsWrittenIntoTheAsset()
        {
            string before = Serialized();

            _settings.SetApiKey("sk-authored");

            Assert.AreNotEqual(before, Serialized(), "Negative twin: the asset's own setter edits the asset.");
            StringAssert.Contains("sk-authored", Serialized());
            Assert.IsFalse(_settings.HasSessionOverrides);
        }

        [Test]
        public void EveryBackendSwitch_LeavesTheSerializedAssetUnchanged()
        {
            string before = Serialized();

            CoreAiBackend.ApplyHttpApi("http://runtime/v1", "sk-1", "runtime-model", 0.7f, 33, 1024, true);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.AreEqual("http://runtime/v1", _settings.ApiBaseUrl);
            Assert.AreEqual("runtime-model", _settings.ModelName);
            Assert.AreEqual(33, _settings.RequestTimeoutSeconds);
            Assert.AreEqual(1024, _settings.MaxTokens);

            CoreAiBackend.ApplyHttpApi(LlmExecutionMode.ClientLimited, "http://limited/v1", "sk-2", "m2");
            Assert.AreEqual(LlmExecutionMode.ClientLimited, _settings.ExecutionMode);

            CoreAiBackend.ApplyServerManagedApi("http://gateway/v1", "gateway-model", "token");
            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, _settings.ExecutionMode);

            CoreAiBackend.SetModel("m3");
            CoreAiBackend.SetApiBaseUrl("http://other/v1");
            CoreAiBackend.ApplyOffline(true, "runtime offline line", "*");
            Assert.AreEqual(LlmExecutionMode.Offline, _settings.ExecutionMode);
            Assert.AreEqual("runtime offline line", _settings.OfflineCustomResponse);

            CoreAiBackend.ApplyLlmUnity("Runtime.gguf", null, 12);
            Assert.AreEqual("Runtime.gguf", _settings.GgufModelPath);
            Assert.AreEqual(12, _settings.NumGPULayers);

            CoreAiBackend.ApplyAuto();
            Assert.AreEqual(LlmBackendType.Auto, _settings.BackendType);

            Assert.AreEqual(before, Serialized());
        }

        [Test]
        public void EndOfSession_RestoresTheAssetValues()
        {
            CoreAiBackend.ApplyHttpApi("http://runtime/v1", "sk-runtime", "runtime-model");

            CoreAISettingsAsset.BeginNewSession();

            Assert.IsFalse(_settings.HasSessionOverrides, "Play Mode entry and exit end the override.");
            Assert.AreEqual("", _settings.ApiKey, "The Resources key guard sees the asset's key, not the runtime one.");
            Assert.AreEqual("asset-model", _settings.ModelName);
            Assert.AreEqual("http://asset/v1", _settings.ApiBaseUrl);
        }

        [Test]
        public void ClearSessionOverrides_RestoresTheAssetValues()
        {
            CoreAiBackend.SetModel("runtime-model");

            CoreAiBackend.ClearSessionOverrides();

            Assert.IsFalse(_settings.HasSessionOverrides);
            Assert.AreEqual("asset-model", _settings.ModelName);
        }

        [Test]
        public void ExplicitAssetWrite_WinsOverAnEarlierRuntimeSwitch()
        {
            CoreAiBackend.SetModel("runtime-model");

            _settings.SetModelName("authored-model");

            Assert.AreEqual("authored-model", _settings.ModelName, "The latest explicit write wins.");
            StringAssert.Contains("authored-model", Serialized());
        }

        [Test]
        public void ApplyHttpApi_RejectsANonHttpMode()
        {
            Assert.Throws<System.ArgumentException>(() =>
                CoreAiBackend.ApplyHttpApi(LlmExecutionMode.Offline, "http://x/v1", "", "m"));
        }
    }
}

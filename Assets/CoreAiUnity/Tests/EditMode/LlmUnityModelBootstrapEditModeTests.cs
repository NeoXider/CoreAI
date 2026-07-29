#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the guard around <c>LLMManager.LoadFromDisk()</c>. That call is not a read-only probe: it
    /// replaces the static model registry with the build snapshot from
    /// <c>StreamingAssets/LLMManager.json</c> and resets <c>downloadOnStart</c>/<c>DebugMode</c>. In the
    /// editor the registry is already loaded from PlayerPrefs, so calling it unconditionally wiped every
    /// model registration for the session and the next Model Manager write persisted the empty list.
    /// </summary>
    [TestFixture]
    public sealed class LlmUnityModelBootstrapEditModeTests
    {
        private List<ModelEntry> _originalEntries;
        private bool _originalDownloadOnStart;
        private LLMUnitySetup.DebugModeType _originalDebugMode;

        [SetUp]
        public void SetUp()
        {
            _originalEntries = LLMManager.modelEntries;
            _originalDownloadOnStart = LLMManager.downloadOnStart;
            _originalDebugMode = LLMUnitySetup.DebugMode;
        }

        [TearDown]
        public void TearDown()
        {
            LLMManager.modelEntries = _originalEntries;
            LLMManager.downloadOnStart = _originalDownloadOnStart;
            LLMUnitySetup.DebugMode = _originalDebugMode;
        }

        /// <summary>Builds a registry entry that stands in for a user-registered model.</summary>
        private static ModelEntry CreateEntry(string filename)
        {
            // WHY: ModelEntry has no parameterless constructor, and the non-LoRA path opens the file through
            // GGUFReader, so the entry is constructed as a LoRA and demoted to a plain model afterwards.
            ModelEntry entry = new(filename, lora: true);
            entry.lora = false;
            return entry;
        }

        [Test]
        public void EnsureModelEntriesLoaded_WithRegisteredModels_KeepsTheRegistry()
        {
            LLMManager.modelEntries = new List<ModelEntry> { CreateEntry("coreai-test-model.gguf") };

            LlmUnityModelBootstrap.EnsureModelEntriesLoaded();

            Assert.AreEqual(1, LLMManager.modelEntries.Count,
                "An already-populated Model Manager registry must never be replaced by the build snapshot.");
            Assert.AreEqual("coreai-test-model.gguf", LLMManager.modelEntries[0].filename);
        }

        [Test]
        public void EnsureModelEntriesLoaded_WithEmptyRegistry_KeepsEditorPreferences()
        {
            LLMManager.modelEntries = new List<ModelEntry>();
            LLMManager.downloadOnStart = true;
            LLMUnitySetup.DebugMode = LLMUnitySetup.DebugModeType.Error;

            LlmUnityModelBootstrap.EnsureModelEntriesLoaded();

            Assert.IsNotNull(LLMManager.modelEntries, "The registry must never be left null.");
            Assert.IsTrue(LLMManager.downloadOnStart,
                "Reading the build snapshot must not reset the editor's downloadOnStart preference.");
            Assert.AreEqual(LLMUnitySetup.DebugModeType.Error, LLMUnitySetup.DebugMode,
                "Reading the build snapshot must not reset the editor's LLMUnity debug mode.");
        }

        [Test]
        public void HasLoadedModelEntries_ReflectsRegistryState()
        {
            LLMManager.modelEntries = new List<ModelEntry>();
            Assert.IsFalse(LlmUnityModelBootstrap.HasLoadedModelEntries());

            LLMManager.modelEntries = new List<ModelEntry> { CreateEntry("m.gguf") };
            Assert.IsTrue(LlmUnityModelBootstrap.HasLoadedModelEntries());

            LLMManager.modelEntries = null;
            Assert.IsFalse(LlmUnityModelBootstrap.HasLoadedModelEntries());
        }

        [Test]
        public void RefreshModelEntries_WithRegisteredModels_DoesNotDropThem()
        {
            LLMManager.modelEntries = new List<ModelEntry> { CreateEntry("coreai-rescan-model.gguf") };

            LlmUnityModelBootstrap.RefreshModelEntries();

            Assert.IsNotNull(LLMManager.modelEntries);
            Assert.IsNotEmpty(LLMManager.modelEntries,
                "The Rescan button must never leave the Model Manager registry empty.");
        }
    }
}
#endif

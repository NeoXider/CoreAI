#if COREAI_HAS_HUB
using System.IO;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="CoreAiPlayWorldHubModule"/>'s decisions (the offline line's sentence, the key hint, when the key may
    /// open the Hub) and the Block World scene carrying the module with the Hub prefab, so a Block World-only player
    /// build has the Hub. Each with its twin.
    /// </summary>
    public sealed class PlayWorldHubModuleEditModeTests
    {
        private const string BlockWorldScene = "Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity";
        private const string HubPrefab = "Assets/CoreAIHub/Runtime/CoreAiHub.prefab";

        [Test]
        public void TheOfflineSentence_NamesTheKeyAndTestConnection()
        {
            string hint = CoreAiPlayWorldHubModule.ComposeSetupHint(KeyCode.F2,
                CoreAiRememberedBackendRestore.NothingRemembered, null);

            StringAssert.StartsWith("Press F2 to open the AI settings", hint);
            StringAssert.Contains("Test connection", hint);
            StringAssert.DoesNotContain("Hub", hint);
        }

        [Test]
        public void TheOfflineSentence_SaysTheKeyIsMissing_WhenTheRememberedServerWaitsForIt()
        {
            LlmRememberedBackend pending = new() { BaseUrl = "http://h:8080/v1", Model = "m", NeedsKey = true };

            string missing = CoreAiPlayWorldHubModule.ComposeSetupHint(KeyCode.F2,
                CoreAiRememberedBackendRestore.NeedsKey, pending);
            string withoutServer = CoreAiPlayWorldHubModule.ComposeSetupHint(KeyCode.F2,
                CoreAiRememberedBackendRestore.NeedsKey, null);

            StringAssert.Contains("http://h:8080/v1 needs its API key again", missing);
            StringAssert.Contains("Press F2", missing);
            StringAssert.DoesNotContain("needs its API key", withoutServer);
        }

        [Test]
        public void TheHint_SaysSetUp_WithoutAModel_AndSettings_WithOne()
        {
            Assert.AreEqual("F2  Set up the AI model", CoreAiPlayWorldHubModule.ComposeHintText(KeyCode.F2, false));
            Assert.AreEqual("F2  AI settings", CoreAiPlayWorldHubModule.ComposeHintText(KeyCode.F2, true));
        }

        [Test]
        public void TheKey_OpensTheHub_OnlyWhenNothingElseHasTheKeyboard()
        {
            Assert.IsTrue(CoreAiPlayWorldHubModule.MayOpen(false, false));
            Assert.IsFalse(CoreAiPlayWorldHubModule.MayOpen(true, false), "the chat input is open");
            Assert.IsFalse(CoreAiPlayWorldHubModule.MayOpen(false, true), "a text field elsewhere has the keyboard");
        }

        [Test]
        public void AModelIsSetUp_OnlyWhenTheBackendIsNotOffline()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ConfigureOffline();
                Assert.IsFalse(CoreAiPlayWorldHubModule.HasModel(settings));
                settings.ConfigureHttpApi("http://127.0.0.1:1234/v1", "", "m");
                Assert.IsTrue(CoreAiPlayWorldHubModule.HasModel(settings));
                Assert.IsFalse(CoreAiPlayWorldHubModule.HasModel(null));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void TheBlockWorldScene_CarriesTheHubModule_WithTheHubPrefab()
        {
            string scene = File.ReadAllText(BlockWorldScene);
            string moduleGuid = AssetDatabase.AssetPathToGUID(
                "Assets/CoreAIMods/Runtime/HubIntegration/CoreAiPlayWorldHubModule.cs");
            string prefabGuid = AssetDatabase.AssetPathToGUID(HubPrefab);

            Assert.IsNotEmpty(moduleGuid);
            Assert.IsNotEmpty(prefabGuid);
            StringAssert.Contains("guid: " + moduleGuid, scene,
                "the scene has the Hub module (rebuild it with CoreAI/Demos/Build Block World Demo)");
            StringAssert.Contains("hubPrefab: {fileID: ", scene);
            StringAssert.Contains("guid: " + prefabGuid, scene, "the module points at the Hub prefab");
        }
    }
}
#endif

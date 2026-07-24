#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using CoreAI.Composition;
using NUnit.Framework;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiDemoScenesSmokePlayModeTests
    {
        private Application.LogCallback _capture;
        private bool _previousIgnoreFailingMessages;
        private CoreAISettingsAsset _sharedSettings;
        private string _sharedSettingsSnapshotJson;

        private static readonly string[] ScenePaths =
        {
            "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",
            "Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity",
            "Assets/CoreAI.Demos/LuaMods/LuaModsDemo.unity",
            "Assets/CoreAI.Demos/MiniRpg/MiniRpgModsDemo.unity",
            "Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity",
            "Assets/CoreAI.Demos/Skills/SkillsDemo.unity",
            "Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemo.unity"
        };

        [UnityTest]
        public IEnumerator AllPublishedDemoScenes_LoadWithScopeCameraAndSupportedShaders()
        {
            // WHY: the demo scenes all reference the shared Resources/CoreAISettings asset, so this
            // FastNoLlm smoke would otherwise inherit whatever backend the developer last selected.
            // With LLMUnity + autostart, every Single-mode scene load boots a native llama.cpp
            // service that the next load tears down mid-construction — a real editor crash
            // (LLMService::LLMService on a worker thread), not a test failure. Force Offline for the
            // duration and restore the exact serialized state afterwards.
            _sharedSettings = CoreAISettingsAsset.Instance;
            Assert.IsNotNull(_sharedSettings,
                "Shared Resources/CoreAISettings asset must exist for the demo scene smoke.");
            _sharedSettingsSnapshotJson = EditorJsonUtility.ToJson(_sharedSettings);
            _sharedSettings.ConfigureOffline();

            // WHY: the asset is committed to the repo. Leaving it dirty lets any later Save Project (or an
            // aborted run followed by one) write the test's Offline backend into the developer's file.
            EditorUtility.ClearDirty(_sharedSettings);

            List<string> unexpectedErrors = new();
            string currentScene = "(startup)";
            Application.LogCallback capture = (condition, stackTrace, type) =>
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                {
                    return;
                }

                // Persistent mods are user data shared by Editor PlayMode. A mod authored with Full
                // capabilities can legitimately fail to rehydrate in a lower-capability demo; that
                // must not make this deterministic scene-wiring smoke depend on local saved content.
                if (condition.Contains("Rehydrate of mod"))
                {
                    return;
                }

                unexpectedErrors.Add($"{currentScene}: [{type}] {condition}\n{stackTrace}");
            };
            _previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            _capture = capture;
            Application.logMessageReceived += capture;

            foreach (string scenePath in ScenePaths)
            {
                currentScene = scenePath;
                Scene scene = EditorSceneManager.LoadSceneInPlayMode(
                    scenePath,
                    new LoadSceneParameters(LoadSceneMode.Single));
                Assert.IsTrue(scene.IsValid(), $"Demo scene must load: {scenePath}");

                // Allow Awake/Start plus one player loop for runtime-created demo visuals.
                yield return null;
                yield return null;

                Assert.IsNotNull(Object.FindFirstObjectByType<CoreAILifetimeScope>(),
                    $"Demo scene must contain CoreAILifetimeScope: {scenePath}");
                Assert.IsNotNull(Object.FindFirstObjectByType<Camera>(),
                    $"Demo scene must contain a camera: {scenePath}");

                foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(
                             FindObjectsInactive.Include,
                             FindObjectsSortMode.None))
                {
                    Material material = renderer.sharedMaterial;
                    if (material == null)
                    {
                        continue;
                    }

                    Assert.IsNotNull(material.shader,
                        $"Renderer '{renderer.name}' has a missing shader in {scenePath}.");
                    Assert.IsTrue(material.shader.isSupported,
                        $"Renderer '{renderer.name}' uses unsupported shader '{material.shader.name}' in {scenePath}.");
                }
            }

            CleanupLogCapture();
            Assert.IsEmpty(unexpectedErrors,
                "Published demos emitted unexpected errors:\n" + string.Join("\n\n", unexpectedErrors));
        }

        [TearDown]
        public void TearDown()
        {
            CleanupLogCapture();
            RestoreSharedSettings();
        }

        [UnityTearDown]
        public IEnumerator UnloadLoadedDemoScenes()
        {
            // The smoke leaves the last demo scene loaded otherwise; its live scope + controllers then
            // bleed into every later PlayMode test in the run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();

            // Regression: the DontDestroyOnLoad mod ticker must die with its scope. Before the
            // container dispose hook, every demo scene leaked an immortal CoreAI_LuaModTicker that kept
            // driving persisted user mods into later tests (and eventually OOM-crashed the editor).
            yield return null;
            Assert.IsNull(GameObject.Find("CoreAI_LuaModTicker"),
                "Mod tickers must be destroyed when their owning scope disposes (no cross-test leaks).");
        }

        private void RestoreSharedSettings()
        {
            if (_sharedSettings != null && !string.IsNullOrEmpty(_sharedSettingsSnapshotJson))
            {
                // In-memory restore only: the asset was never saved, so disk state is untouched.
                EditorJsonUtility.FromJsonOverwrite(_sharedSettingsSnapshotJson, _sharedSettings);
                EditorUtility.ClearDirty(_sharedSettings);

                string assetPath = AssetDatabase.GetAssetPath(_sharedSettings);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    // The committed file is the authority; reimport so nothing this run touched survives.
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }

            _sharedSettings = null;
            _sharedSettingsSnapshotJson = null;
        }

        private void CleanupLogCapture()
        {
            if (_capture != null)
            {
                Application.logMessageReceived -= _capture;
                _capture = null;
            }

            LogAssert.ignoreFailingMessages = _previousIgnoreFailingMessages;
        }
    }
}
#endif

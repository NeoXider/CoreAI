#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Chat;
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
        // WHY: the published QA matrix is the frozen G11 WebGL scene list owned by
        // CoreAIG11WebGlBuild.FrozenScenePaths (Assets/CoreAiUnity/Editor/CoreAIBuildMenu.cs). Pinning it
        // here — instead of globbing the demo folder — makes this smoke cover exactly the scenes that
        // ship. FrozenList_MatchesBuildEntryPointAndProjectScenes below fails the moment the two drift.
        private static readonly string[] FrozenDemoScenePaths =
        {
            "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",
            "Assets/CoreAI.Demos/GameplayServices/GameplayServicesDemo.unity",
            "Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity",
            "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity",
            "Assets/CoreAI.Demos/LuaMods/LuaModsDemo.unity",
            "Assets/CoreAI.Demos/MiniRpg/MiniRpgModsDemo.unity",
            "Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity",
            "Assets/CoreAI.Demos/MultiplayerFoundation/MultiplayerFoundationDemo.unity",
            "Assets/CoreAI.Demos/ProceduralMaterials/ProceduralMaterialsShowcase.unity",
            "Assets/CoreAI.Demos/QwenDemo/QwenGenieDemo.unity",
            "Assets/CoreAI.Demos/QwenDemo/QwenSpellcraftDemo.unity",
            "Assets/CoreAI.Demos/Skills/SkillsDemo.unity",
            "Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemo.unity",
            "Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity"
        };

        private Application.LogCallback _capture;
        private bool _previousIgnoreFailingMessages;
        private CoreAISettingsAsset _sharedSettings;
        private string _sharedSettingsSnapshotJson;

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

            Assert.AreEqual(
                15,
                FrozenDemoScenePaths.Length,
                "Published first-party demo inventory changed; update the G11 build matrix and QA evidence.");
            foreach (string scenePath in FrozenDemoScenePaths)
            {
                currentScene = scenePath;
                AssertSerializedAssetReferencesResolve(scenePath);
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
                    Material[] materials = renderer.sharedMaterials;
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        AssertMaterialSupported(
                            materials[materialIndex],
                            $"Renderer '{renderer.name}' material slot {materialIndex}",
                            scenePath);
                    }
                }

                foreach (UnityEngine.UI.Graphic graphic in Object.FindObjectsByType<UnityEngine.UI.Graphic>(
                             FindObjectsInactive.Include,
                             FindObjectsSortMode.None))
                {
                    AssertMaterialSupported(
                        graphic.material,
                        $"UI Graphic '{graphic.name}'",
                        scenePath);
                }
            }

            CleanupLogCapture();
            Assert.IsEmpty(unexpectedErrors,
                "Published demos emitted unexpected errors:\n" + string.Join("\n\n", unexpectedErrors));
        }

        private static void AssertMaterialSupported(
            Material material,
            string owner,
            string scenePath)
        {
            Assert.IsNotNull(material, $"{owner} has a missing material in {scenePath}.");
            if (material == null)
            {
                return;
            }

            Assert.IsNotNull(material.shader, $"{owner} has a missing shader in {scenePath}.");
            if (material.shader == null)
            {
                return;
            }

            Assert.AreNotEqual(
                "Hidden/InternalErrorShader",
                material.shader.name,
                $"{owner} resolves to Unity's error shader in {scenePath}.");
            Assert.IsTrue(
                material.shader.isSupported,
                $"{owner} uses unsupported shader '{material.shader.name}' in {scenePath}.");
        }

        /// <summary>
        /// Keeps the pinned QA matrix, the G11 WebGL build entry point and the scenes actually present in
        /// the project in lockstep. Any of the three drifting is a real defect: a scene the player build
        /// ships but the smoke never loads, or a demo scene nobody added to the build matrix.
        /// </summary>
        [Test]
        public void FrozenList_MatchesBuildEntryPointAndProjectScenes()
        {
            foreach (string scenePath in FrozenDemoScenePaths)
            {
                Assert.IsTrue(
                    File.Exists(Path.GetFullPath(scenePath)),
                    $"Frozen demo scene is missing from the project: {scenePath}");
            }

            CollectionAssert.AreEqual(
                FrozenDemoScenePaths,
                ReadBuildEntryPointFrozenScenePaths(),
                "The pinned QA matrix and CoreAIG11WebGlBuild.FrozenScenePaths disagree. Both must list " +
                "the same scenes in the same order.");

            CollectionAssert.AreEquivalent(
                FrozenDemoScenePaths,
                FindFirstPartyDemoScenePaths(),
                "First-party demo scenes on disk differ from the frozen G11 matrix. Add the new scene to " +
                "CoreAIG11WebGlBuild.FrozenScenePaths and to this test, or delete the stale scene.");
        }

        /// <summary>
        /// Reads the build matrix from <c>CoreAIG11WebGlBuild</c> by reflection: the FastNoLlm test
        /// assembly deliberately does not reference CoreAI.Editor, and an asmdef reference just to read a
        /// string array is not worth the coupling.
        /// </summary>
        private static string[] ReadBuildEntryPointFrozenScenePaths()
        {
            const string typeName = "CoreAI.Editor.CoreAIG11WebGlBuild";
            System.Type buildType = null;
            foreach (System.Reflection.Assembly assembly in
                     System.AppDomain.CurrentDomain.GetAssemblies())
            {
                buildType = assembly.GetType(typeName, false);
                if (buildType != null)
                {
                    break;
                }
            }

            Assert.IsNotNull(buildType, $"{typeName} was not found; the G11 WebGL build entry point moved.");
            System.Reflection.MethodInfo method = buildType.GetMethod(
                "GetFrozenScenePaths",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(method, $"{typeName}.GetFrozenScenePaths() was not found.");
            return (string[])method.Invoke(null, null);
        }

        private static IReadOnlyList<string> FindFirstPartyDemoScenePaths()
        {
            string[] sceneGuids = AssetDatabase.FindAssets(
                "t:Scene",
                new[] { "Assets/CoreAI.Demos" });
            List<string> scenePaths = new(sceneGuids.Length + 1);
            for (int index = 0; index < sceneGuids.Length; index++)
            {
                string path = AssetDatabase.GUIDToAssetPath(sceneGuids[index]);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    scenePaths.Add(path);
                }
            }

            scenePaths.Add("Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity");
            scenePaths.Sort(System.StringComparer.Ordinal);
            return scenePaths;
        }

        private static void AssertSerializedAssetReferencesResolve(string scenePath)
        {
            string yaml = File.ReadAllText(Path.GetFullPath(scenePath));
            MatchCollection matches = Regex.Matches(
                yaml,
                @"guid:\s*([0-9a-fA-F]{32}),\s*type:\s*3");
            HashSet<string> checkedGuids = new(System.StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < matches.Count; index++)
            {
                string guid = matches[index].Groups[1].Value;
                if (!checkedGuids.Add(guid))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                Assert.IsFalse(
                    string.IsNullOrEmpty(assetPath),
                    $"Demo scene has a missing serialized asset GUID {guid}: {scenePath}");
            }
        }

        [UnityTest]
        public IEnumerator ExternalDriver_RejectsSceneMissingFromPlayerBuild()
        {
            Scene activeBefore = SceneManager.GetActiveScene();
            GameObject driverObject = new(CoreAiChatExternalDriver.DriverObjectName + "_Test");
            CoreAiChatExternalDriver driver = driverObject.AddComponent<CoreAiChatExternalDriver>();

            driver.LoadScene("__coreai_missing_scene__");
            yield return null;

            Assert.AreEqual(activeBefore, SceneManager.GetActiveScene());
            Object.Destroy(driverObject);
            yield return null;
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

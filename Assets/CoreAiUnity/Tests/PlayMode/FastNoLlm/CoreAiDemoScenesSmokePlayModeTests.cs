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
using UnityEditor.Callbacks;
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
            "Assets/CoreAI.Demos/OnlineAuthority/OnlineAuthorityDemo.unity",
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
            List<string> skippedModelScenes = new();
            List<string> mirrorAutoIdentityScenes = new();
            string mirrorIdentityScriptGuid = FindMirrorNetworkIdentityScriptGuid();
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

                // Model-backed demos boot a local LLM service from the committed settings asset.
                // Without a model file on disk that boot logs errors that prove nothing about the
                // scene wiring this smoke owns; those scenes are reported as skipped, not failed.
                if (IsMissingModelNoise(condition) && IsModelBackedScene(currentScene))
                {
                    if (!skippedModelScenes.Contains(currentScene))
                    {
                        skippedModelScenes.Add(currentScene);
                    }

                    return;
                }

                unexpectedErrors.Add($"{currentScene}: [{type}] {condition}\n{stackTrace}");
            };
            _previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            _capture = capture;
            Application.logMessageReceived += capture;

            Assert.AreEqual(
                17,
                FrozenDemoScenePaths.Length,
                "Published first-party demo inventory changed; update the G11 build matrix and QA evidence.");
            foreach (string scenePath in FrozenDemoScenePaths)
            {
                currentScene = scenePath;
                string sceneYaml = File.ReadAllText(Path.GetFullPath(scenePath));
                AssertSerializedAssetReferencesResolve(scenePath, sceneYaml);
                MirrorAutoIdentityPatch.Arm(scenePath);
                Scene scene = EditorSceneManager.LoadSceneInPlayMode(
                    scenePath,
                    new LoadSceneParameters(LoadSceneMode.Single));
                Assert.IsTrue(scene.IsValid(), $"Demo scene must load: {scenePath}");

                // Allow Awake/Start plus one player loop for runtime-created demo visuals.
                yield return null;
                yield return null;

                foreach (string patchedObject in MirrorAutoIdentityPatch.TakePatchedObjects(scenePath))
                {
                    // WHY: a scene that bakes NetworkIdentities and still shipped one without a sceneId is
                    // the defect Mirror's error describes; only an identity absent from the scene file is
                    // the optional-package artefact the patch exists for.
                    if (mirrorIdentityScriptGuid != null
                        && sceneYaml.Contains("guid: " + mirrorIdentityScriptGuid))
                    {
                        unexpectedErrors.Add(
                            $"{scenePath}: baked NetworkIdentity on '{patchedObject}' has no sceneId; " +
                            "open and resave the scene with Mirror installed.");
                    }
                    else if (!mirrorAutoIdentityScenes.Contains(scenePath))
                    {
                        mirrorAutoIdentityScenes.Add(scenePath);
                    }
                }

                MirrorAutoIdentityPatch.Disarm();

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

            MirrorAutoIdentityPatch.Disarm();
            CleanupLogCapture();
            Assert.IsEmpty(unexpectedErrors,
                "Published demos emitted unexpected errors:\n" + string.Join("\n\n", unexpectedErrors));
            if (skippedModelScenes.Count > 0)
            {
                Debug.LogWarning(
                    "[CoreAI] Demo smoke skipped model-backed scenes with no local model file: " +
                    string.Join(", ", skippedModelScenes));
            }

            if (mirrorAutoIdentityScenes.Count > 0)
            {
                Debug.LogWarning(
                    "[CoreAI] Demo smoke: the locally installed Mirror auto-created a NetworkIdentity on a " +
                    "scene object; the smoke gave it a sceneId ahead of Mirror's post-processor so the run " +
                    "could continue (committed scenes ship Mirror-free): " +
                    string.Join(", ", mirrorAutoIdentityScenes));
            }
        }

        /// <summary>
        /// Gives a sceneId to the NetworkIdentities that the optional Mirror package auto-creates while a
        /// demo scene loads in play mode, ahead of Mirror's own scene post-processor.
        /// </summary>
        /// <remarks>
        /// WHY: Mirror is a gitignored local install, so committed scenes bake no NetworkIdentity. With it
        /// installed, Neo movement controllers are NetworkBehaviours with [RequireComponent(NetworkIdentity)];
        /// Unity creates the identity (sceneId 0) during the load, and Mirror's NetworkScenePostProcess
        /// (callback order 1) answers with EditorApplication.isPlaying = false. The editor honours that stop
        /// before the test coroutine resumes, so nothing in the test body can see or cancel it: the Test
        /// Framework aborts the whole run and writes no results file. Running at order 0, a non-zero
        /// sceneId puts the object on the path a scene saved under Mirror takes — Mirror disables it and
        /// Neo's order-100 post-processor re-enables objects that opt out of networking. Mirror is reached
        /// by reflection because this assembly must compile without it, and the patch is armed only while
        /// the smoke runs, so builds and manual play mode are untouched.
        /// </remarks>
        private static class MirrorAutoIdentityPatch
        {
            private static readonly List<(string ScenePath, string ObjectName)> Patched = new();
            private static string _armedScenePath;
            private static ulong _nextSceneId;

            // WHY a static constructor and not a [SetUp] subscription: this project disables domain
            // reload on entering play mode (ProjectSettings/EditorSettings.asset,
            // m_EnterPlayModeOptions), so static state OUTLIVES a play session. A forced stop while the
            // smoke is suspended would otherwise skip TearDown and leave the hook armed into whatever the
            // editor does next. Registering once here, and disarming when play mode exits, closes that.
            static MirrorAutoIdentityPatch()
            {
                EditorApplication.playModeStateChanged += state =>
                {
                    if (state == PlayModeStateChange.ExitingPlayMode)
                    {
                        _armedScenePath = null;
                    }
                };
            }

            /// <summary>Arms the hook for ONE scene path, so no other load can be mutated.</summary>
            public static void Arm(string scenePath)
            {
                _armedScenePath = scenePath;
            }

            /// <summary>
            /// Stops the hook acting. Deliberately does NOT clear <see cref="Patched"/>: the records are
            /// the smoke's evidence and are consumed by TakePatchedObjects, so clearing here would erase
            /// them before they are read and silently weaken the test.
            /// </summary>
            public static void Disarm()
            {
                _armedScenePath = null;
            }

            /// <summary>
            /// Names of the objects patched in <paramref name="scenePath"/>; clears the whole record, so
            /// identities that belonged to the scene being unloaded are dropped with it.
            /// </summary>
            public static List<string> TakePatchedObjects(string scenePath)
            {
                List<string> names = new();
                for (int index = 0; index < Patched.Count; index++)
                {
                    if (Patched[index].ScenePath == scenePath)
                    {
                        names.Add(Patched[index].ObjectName);
                    }
                }

                Patched.Clear();
                return names;
            }

            [PostProcessScene(0)]
            public static void OnPostProcessScene()
            {
                // WHY three gates and not one flag: [PostProcessScene] is discovered GLOBALLY, so this
                // runs for a player build too. Mutating scene objects during a build would ship whatever
                // this invents. isPlaying keeps it to a play session, isBuildingPlayer is the explicit
                // build exclusion, and the armed path keeps it to the one scene the smoke is loading.
                if (_armedScenePath == null
                    || !EditorApplication.isPlaying
                    || BuildPipeline.isBuildingPlayer)
                {
                    return;
                }

                System.Type identityType = FindLoadedType("Mirror.NetworkIdentity");
                System.Reflection.FieldInfo sceneIdField = identityType?.GetField(
                    "sceneId",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (sceneIdField == null)
                {
                    return;
                }

                foreach (Object candidate in Resources.FindObjectsOfTypeAll(identityType))
                {
                    Component identity = (Component)candidate;
                    string scenePath = identity.gameObject.scene.path;
                    // WHY the candidate's own path and not the active scene: an additive load elsewhere
                    // must not be mutated just because this hook happens to be armed.
                    if (!string.Equals(scenePath, _armedScenePath, System.StringComparison.Ordinal)
                        || (ulong)sceneIdField.GetValue(identity) != 0)
                    {
                        continue;
                    }

                    sceneIdField.SetValue(identity, ++_nextSceneId);
                    Patched.Add((scenePath, identity.gameObject.name));
                }
            }
        }

        /// <summary>
        /// GUID of Mirror's <c>NetworkIdentity</c> script when the optional package is installed
        /// locally; null when it is not.
        /// </summary>
        private static string FindMirrorNetworkIdentityScriptGuid()
        {
            foreach (string guid in AssetDatabase.FindAssets("NetworkIdentity t:MonoScript"))
            {
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (script != null && script.GetClass()?.FullName == "Mirror.NetworkIdentity")
                {
                    return guid;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a type by full name across the loaded assemblies: the FastNoLlm test assembly
        /// deliberately references neither CoreAI.Editor nor the optional Mirror package.
        /// </summary>
        private static System.Type FindLoadedType(string fullName)
        {
            foreach (System.Reflection.Assembly assembly in
                     System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>
        /// Model-boot noise that proves nothing about scene wiring: emitted when the committed
        /// settings asset points at a local GGUF file that is absent on this machine.
        /// </summary>
        private static bool IsMissingModelNoise(string condition)
        {
            return condition.Contains("No model file provided!")
                || condition.Contains("LLM failed to start");
        }

        /// <summary>
        /// Scenes whose composition boots a local model service. Kept next to the frozen list on
        /// purpose: a new model-backed demo added to the matrix must opt into the skip here too,
        /// otherwise its missing-model noise fails the smoke instead of skipping it.
        /// </summary>
        private static bool IsModelBackedScene(string scenePath)
        {
            return !string.IsNullOrEmpty(scenePath) &&
                (scenePath.Contains("/QwenDemo/QwenGenieDemo.unity") ||
                 scenePath.Contains("/QwenDemo/QwenSpellcraftDemo.unity"));
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
        /// Reads the build matrix from <c>CoreAIG11WebGlBuild</c> by reflection: an asmdef reference to
        /// CoreAI.Editor just to read a string array is not worth the coupling.
        /// </summary>
        private static string[] ReadBuildEntryPointFrozenScenePaths()
        {
            const string typeName = "CoreAI.Editor.CoreAIG11WebGlBuild";
            System.Type buildType = FindLoadedType(typeName);
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

        private static void AssertSerializedAssetReferencesResolve(string scenePath, string yaml)
        {
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
            MirrorAutoIdentityPatch.Disarm();
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

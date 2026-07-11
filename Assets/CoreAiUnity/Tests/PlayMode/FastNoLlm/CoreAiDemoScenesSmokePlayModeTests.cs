#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using CoreAI.Composition;
using NUnit.Framework;
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

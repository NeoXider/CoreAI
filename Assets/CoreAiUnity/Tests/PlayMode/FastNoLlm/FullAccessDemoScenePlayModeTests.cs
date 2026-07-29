#if UNITY_EDITOR
using System.Collections;
using CoreAI.Composition;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class FullAccessDemoScenePlayModeTests
    {
        [UnityTearDown]
        public IEnumerator UnloadLoadedScenes()
        {
            // Single-mode scene loads otherwise persist past this test and leak their scope into the
            // rest of the PlayMode run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private const string ScenePath = "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity";

        [UnityTest]
        public IEnumerator FullAccessDemo_LoadsWithFullLuaAndAnEmptyWorld()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                Assert.Ignore($"Demo scene '{ScenePath}' is not present in this project; skipping.");
                yield break;
            }

            Scene scene = EditorSceneManager.LoadSceneInPlayMode(
                ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            Assert.IsTrue(scene.IsValid(), "FullAccessDemo scene must load in PlayMode.");

            yield return null;

            CoreAILifetimeScope scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            Assert.IsNotNull(scope, "FullAccessDemo must contain a CoreAILifetimeScope.");

            CoreAiLuaWorldModule luaModule = scope.GetComponentInChildren<CoreAiLuaWorldModule>(true);
            Assert.IsNotNull(luaModule,
                "FullAccessDemo must own Lua configuration in a child module.");
            Assert.IsTrue(luaModule.FullAccessEnabled,
                "FullAccessDemo must grant Full Lua so Programmer can inspect and modify scene objects.");
            Assert.IsFalse(luaModule.FullPrivateAccessEnabled,
                "FullAccessDemo should keep private reflection access off by default.");

            Assert.IsNotNull(FindBehaviour("CoreAI.Demos.FullAccessHubDemoController"),
                "FullAccessHubDemoController must be present.");

            // WHY: since 6.3.1 the demo opens on an empty world. Nothing is auto-spawned in the centre;
            // a target exists only when one is assigned in the inspector, and the controller then renames
            // it to "TargetCube" so prompts and mods keep finding it by name. The scene ships unassigned,
            // so an object under that name here means the auto-spawn regressed back in.
            Assert.IsNull(GameObject.Find("TargetCube"),
                "FullAccessDemo must start with an empty world; TargetCube is inspector-assigned only.");

            // Prompt templates are surfaced by the chat panel's own "≡" example menu (enabled by the host),
            // so the demo must contain a chat panel rather than the legacy prompt-buttons controller.
            MonoBehaviour chat = FindBehaviour("CoreAI.Chat.CoreAiChatPanel");
            Assert.IsNotNull(chat, "FullAccessDemo must contain a CoreAiChatPanel that hosts the example prompts.");
        }

        private static MonoBehaviour FindBehaviour(string typeFullName)
        {
            foreach (MonoBehaviour behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour != null && behaviour.GetType().FullName == typeFullName)
                {
                    return behaviour;
                }
            }

            return null;
        }
    }
}
#endif

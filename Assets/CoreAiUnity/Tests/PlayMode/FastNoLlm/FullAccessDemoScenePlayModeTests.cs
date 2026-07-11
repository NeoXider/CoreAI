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
        private const string ScenePath = "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity";

        [UnityTest]
        public IEnumerator FullAccessDemo_LoadsWithFullLuaAndTargetCube()
        {
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(
                ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            Assert.IsTrue(scene.IsValid(), "FullAccessDemo scene must load in PlayMode.");

            yield return null;

            CoreAILifetimeScope scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            Assert.IsNotNull(scope, "FullAccessDemo must contain a CoreAILifetimeScope.");

            SerializedObject scopeSo = new(scope);
            Assert.IsTrue(scopeSo.FindProperty("enableFullLuaAccess").boolValue,
                "FullAccessDemo must grant Full Lua so Programmer can inspect and modify scene objects.");
            Assert.IsFalse(scopeSo.FindProperty("enableFullLuaPrivateAccess").boolValue,
                "FullAccessDemo should keep private reflection access off by default.");

            Assert.IsNotNull(FindBehaviour("CoreAI.Demos.FullAccessDemoController"),
                "FullAccessDemoController must be present.");

            GameObject target = GameObject.Find("TargetCube");
            Assert.IsNotNull(target, "FullAccessDemoController must create or normalize TargetCube.");
            Assert.AreEqual(0.5f, target.transform.position.y, 0.01f);

            MonoBehaviour buttons = FindBehaviour("CoreAI.Demos.ChatPromptButtonsController");
            Assert.IsNotNull(buttons, "FullAccessDemo must expose prompt buttons for manual demo checks.");
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

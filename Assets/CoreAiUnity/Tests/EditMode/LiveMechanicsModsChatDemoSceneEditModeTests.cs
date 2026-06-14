using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class LiveMechanicsModsChatDemoSceneEditModeTests
    {
        private const string WaveScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity";

        [Test]
        public void WaveAutoBattlerModsDemo_HasFullLuaEnabled()
        {
            EditorSceneManager.OpenScene(WaveScenePath, OpenSceneMode.Single);

            MonoBehaviour scope = FindBehaviour("CoreAI.Composition.CoreAILifetimeScope");
            Assert.IsNotNull(scope, "Scene must include CoreAILifetimeScope.");

            SerializedObject scopeSo = new(scope);
            Assert.IsTrue(scopeSo.FindProperty("enableFullLuaAccess").boolValue,
                "Wave auto-battler demo must grant Full Lua for scene-object mod tasks.");
        }

        private static MonoBehaviour FindBehaviour(string fullName)
        {
            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null && behaviour.GetType().FullName == fullName)
                {
                    return behaviour;
                }
            }

            return null;
        }

    }
}

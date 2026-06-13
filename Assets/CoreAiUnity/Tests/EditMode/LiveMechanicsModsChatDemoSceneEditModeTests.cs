using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class LiveMechanicsModsChatDemoSceneEditModeTests
    {
        private const string ScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity";

        [Test]
        public void LiveMechanicsModsChatDemo_HasAutoRepairPersistenceAndUserFacingPrompts()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            MonoBehaviour autoRepair = FindBehaviour("CoreAI.Presentation.CoreAiLuaModAutoRepair");
            MonoBehaviour persistence = FindBehaviour("CoreAI.Demos.LiveMechanicsModsChatPersistenceController");
            MonoBehaviour promptButtons = FindBehaviour("CoreAI.Demos.ChatPromptButtonsController");

            Assert.IsNotNull(autoRepair, "Scene must include the active Lua mod auto-repair bridge.");
            Assert.IsNotNull(persistence, "Scene must include the saved/active mod manager panel.");
            Assert.IsNotNull(promptButtons, "Scene must include user-facing demo prompt buttons.");

            SerializedObject autoRepairSo = new(autoRepair);
            Assert.IsTrue(autoRepairSo.FindProperty("autoRepairEnabled").boolValue);
            Assert.AreEqual(3, autoRepairSo.FindProperty("minConsecutiveErrors").intValue);
            Assert.AreEqual(2, autoRepairSo.FindProperty("maxAttemptsPerMod").intValue);
            Assert.AreEqual(
                "demo.live_mechanics.mods_chat.mod.",
                autoRepairSo.FindProperty("modVersionKeyPrefix").stringValue);

            SerializedObject promptSo = new(promptButtons);
            Assert.AreEqual(560f, promptSo.FindProperty("chatReserveWidth").floatValue, 0.01f);
            SerializedProperty prompts = promptSo.FindProperty("prompts");
            Assert.IsNotNull(prompts);
            Assert.AreEqual(3, prompts.arraySize);

            for (int i = 0; i < prompts.arraySize; i++)
            {
                string prompt = prompts.GetArrayElementAtIndex(i).FindPropertyRelative("Prompt").stringValue;
                Assert.IsNotEmpty(prompt);
                AssertUserFacingPrompt(prompt);
            }
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

        private static void AssertUserFacingPrompt(string prompt)
        {
            string lower = prompt.ToLowerInvariant();
            Assert.IsFalse(lower.Contains("execute_lua"), prompt);
            Assert.IsFalse(lower.Contains("manage_mods"), prompt);
            Assert.IsFalse(lower.Contains("unity_"), prompt);
            Assert.IsFalse(lower.Contains("hooks_"), prompt);
            Assert.IsFalse(lower.Contains("logic_define"), prompt);
            Assert.IsFalse(lower.Contains("one-shot"), prompt);
        }
    }
}

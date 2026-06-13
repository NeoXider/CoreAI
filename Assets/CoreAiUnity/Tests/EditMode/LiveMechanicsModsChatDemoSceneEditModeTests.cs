using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CoreAI.Infrastructure.World;

namespace CoreAI.Tests.EditMode
{
    public sealed class LiveMechanicsModsChatDemoSceneEditModeTests
    {
        private const string ScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity";
        private const string WaveScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity";

        [Test]
        public void LiveMechanicsModsChatDemo_HasAutoRepairPersistenceAndUserFacingPrompts()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            MonoBehaviour scope = FindBehaviour("CoreAI.Composition.CoreAILifetimeScope");
            MonoBehaviour autoRepair = FindBehaviour("CoreAI.Presentation.CoreAiLuaModAutoRepair");
            MonoBehaviour persistence = FindBehaviour("CoreAI.Demos.LiveMechanicsModsChatPersistenceController");
            MonoBehaviour promptButtons = FindBehaviour("CoreAI.Demos.ChatPromptButtonsController");

            Assert.IsNotNull(scope, "Scene must include CoreAILifetimeScope.");
            Assert.IsNotNull(autoRepair, "Scene must include the active Lua mod auto-repair bridge.");
            Assert.IsNotNull(persistence, "Scene must include the saved/active mod manager panel.");
            Assert.IsNotNull(promptButtons, "Scene must include user-facing demo prompt buttons.");

            SerializedObject scopeSo = new(scope);
            Assert.IsTrue(scopeSo.FindProperty("enableFullLuaAccess").boolValue,
                "Mods-chat demo must grant Full Lua so Programmer can inspect and modify scene objects.");

            SerializedObject autoRepairSo = new(autoRepair);
            Assert.IsTrue(autoRepairSo.FindProperty("autoRepairEnabled").boolValue);
            Assert.AreEqual(3, autoRepairSo.FindProperty("minConsecutiveErrors").intValue);
            Assert.AreEqual(2, autoRepairSo.FindProperty("maxAttemptsPerMod").intValue);
            Assert.AreEqual(
                "demo.live_mechanics.mods_chat.mod.",
                autoRepairSo.FindProperty("modVersionKeyPrefix").stringValue);

            SerializedObject promptSo = new(promptButtons);
            Assert.AreEqual(560f, promptSo.FindProperty("chatReserveWidth").floatValue, 0.01f);

            SerializedObject persistenceSo = new(persistence);
            SerializedProperty transientIds = persistenceSo.FindProperty("transientModIds");
            Assert.IsNotNull(transientIds);
            AssertContainsArrayString(transientIds, "auto_repair_smoke");

            SerializedProperty prompts = promptSo.FindProperty("prompts");
            Assert.IsNotNull(prompts);
            Assert.AreEqual(3, prompts.arraySize);

            for (int i = 0; i < prompts.arraySize; i++)
            {
                string prompt = prompts.GetArrayElementAtIndex(i).FindPropertyRelative("Prompt").stringValue;
                Assert.IsNotEmpty(prompt);
                AssertUserFacingPrompt(prompt);
            }

            CoreAiPrefabRegistryAsset registry =
                AssetDatabase.LoadAssetAtPath<CoreAiPrefabRegistryAsset>(
                    "Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset");
            Assert.IsNotNull(registry, "Demo world prefab registry must exist.");
            Assert.IsTrue(registry.TryResolve("enemy.basic", out GameObject enemyPrefab));
            Assert.IsNotNull(enemyPrefab, "Lua coreai_world_spawn('enemy.basic', ...) must create a visible prefab.");
        }

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

        private static void AssertContainsArrayString(SerializedProperty array, string expected)
        {
            for (int i = 0; i < array.arraySize; i++)
            {
                if (array.GetArrayElementAtIndex(i).stringValue == expected)
                {
                    return;
                }
            }

            Assert.Fail($"Expected serialized array '{array.name}' to contain '{expected}'.");
        }
    }
}

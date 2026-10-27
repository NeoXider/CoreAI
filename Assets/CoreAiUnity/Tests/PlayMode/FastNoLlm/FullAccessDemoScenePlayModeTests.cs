#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using CoreAI.Composition;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    public sealed class FullAccessDemoScenePlayModeTests
    {
        [UnityTearDown]
        public IEnumerator UnloadLoadedScenes()
        {
            // WHY: Single-mode scene loads otherwise persist past this test and leak their scope into the
            // rest of the PlayMode run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private const string ScenePath = "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity";

        [UnityTest]
        public IEnumerator CommandInputField_ConstructsAndInitializesWithInstalledTmp()
        {
            Type inputType = FindLoadedType("IngameDebugConsole.CommandInputField");
            Type managerType = FindLoadedType("IngameDebugConsole.DebugLogManager");
            Assert.IsNotNull(inputType, "The FullAccess console input component must be available.");
            Assert.IsNotNull(managerType, "The FullAccess console manager component must be available.");

            GameObject root = new("CommandInputFieldCompatibilityRegression");
            try
            {
                GameObject managerObject = new("InactiveConsoleManager", typeof(RectTransform));
                managerObject.transform.SetParent(root.transform, false);
                managerObject.SetActive(false);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Plugins/IngameDebugConsole/IngameDebugConsole.prefab");
                Assert.IsNotNull(prefab, "The console manager requires its serialized prefab references.");
                GameObject managerClone = Object.Instantiate(prefab, managerObject.transform, false);
                Component manager = managerClone.GetComponentInChildren(managerType, true);
                Assert.IsNotNull(manager);

                FieldInfo showSuggestions = managerType.GetField("showCommandSuggestions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(showSuggestions);
                showSuggestions.SetValue(manager, false);

                GameObject inputObject = new("CommandInput", typeof(RectTransform));
                inputObject.transform.SetParent(root.transform, false);
                inputObject.SetActive(false);
                TMP_InputField input = null;
                Assert.DoesNotThrow(() => input = inputObject.AddComponent(inputType) as TMP_InputField,
                    "The component constructor must tolerate an installed TMP without the legacy composition field.");
                Assert.IsNotNull(input);

                GameObject suggestions = new("Suggestions", typeof(RectTransform));
                suggestions.transform.SetParent(inputObject.transform, false);
                FieldInfo suggestionsContainer = inputType.GetField("commandSuggestionsContainer",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(suggestionsContainer);
                suggestionsContainer.SetValue(input, suggestions.GetComponent<RectTransform>());

                MethodInfo initialize = inputType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { managerType }, null);
                Assert.IsNotNull(initialize);
                Assert.DoesNotThrow(() => initialize.Invoke(input, new object[] { manager }));
                Assert.IsFalse(suggestions.activeSelf,
                    "Console initialization must hide its suggestions container.");
                Assert.IsNotNull(input.onValidateInput,
                    "Console initialization must install its real character-validation callback.");

                // WHY: the manager stays inactive so its unrelated full-console Awake wiring and singleton do not run.
                inputObject.SetActive(true);
                input.text = "help";
                Assert.AreEqual("help", input.text);
                Assert.AreEqual('x', input.onValidateInput(input.text, input.text.Length, 'x'));

                yield return null;

                Assert.IsTrue(input.isActiveAndEnabled);
                Assert.AreEqual("help", input.text,
                    "The initialized input must survive its first update with the installed TMP.");
            }
            finally
            {
                Object.Destroy(root);
            }

            yield return null;
        }

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
                "FullAccessDemo must own world-command configuration in a child module.");

            // WHY: only the mods scope grants the Full tier to execute_lua and manage_mods; the module's
            // legacy Full flags are ignored, so asserting them would not prove the demo works.
            CoreAiModsLifetimeScope modsScope =
                Object.FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
            Assert.IsNotNull(modsScope, "FullAccessDemo must contain a CoreAiModsLifetimeScope.");
            Assert.IsTrue(modsScope.FullLuaAccessEnabled,
                "FullAccessDemo must grant Full Lua so Programmer can inspect and modify scene objects.");
            Assert.IsFalse(modsScope.FullLuaPrivateAccessEnabled,
                "FullAccessDemo should keep private reflection access off by default.");

            Assert.IsNotNull(FindBehaviour("CoreAI.Demos.FullAccessHubDemoController"),
                "FullAccessHubDemoController must be present.");

            // WHY: since 6.3.1 the demo opens on an empty world. Nothing is auto-spawned in the centre;
            // a target exists only when one is assigned in the inspector, and the controller then renames
            // it to "TargetCube" so prompts and mods keep finding it by name. The scene ships unassigned,
            // so an object under that name here means the auto-spawn regressed back in.
            Assert.IsNull(GameObject.Find("TargetCube"),
                "FullAccessDemo must start with an empty world; TargetCube is inspector-assigned only.");

            // WHY: prompt templates are surfaced by the chat panel's own "≡" example menu (enabled by the host),
            // so the demo must contain a chat panel rather than the legacy prompt-buttons controller.
            MonoBehaviour chat = FindBehaviour("CoreAI.Chat.CoreAiChatPanel");
            Assert.IsNotNull(chat, "FullAccessDemo must contain a CoreAiChatPanel that hosts the example prompts.");
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
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

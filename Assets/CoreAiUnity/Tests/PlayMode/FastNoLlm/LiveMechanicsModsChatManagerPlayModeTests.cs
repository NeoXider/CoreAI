#if UNITY_EDITOR && COREAI_LUA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// DEMO-REVIEW-001 without a model: in the real published Mods-chat scene, a mod loaded or unloaded
    /// through the runtime (the path the chat's <c>manage_mods</c> tool takes) must show up as a real
    /// manager row whose buttons work.
    /// <para>
    /// WHY: the manager recomputed its cached lists on runtime source events but did not rebuild the
    /// panel, so a mod the chat loaded had no row to operate until something else repainted. The only
    /// test of that path was the explicit live Demo05 case, which needs a real provider. The scene's
    /// stores are safe to build here because the test run redirects <c>CoreAiStorageRoot</c>.
    /// </para>
    /// </summary>
    public sealed class LiveMechanicsModsChatManagerPlayModeTests
    {
        private const string ScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity";
        private const string ControllerTypeName = "LiveMechanicsModsChatPersistenceController";

        private readonly Dictionary<CoreAISettingsAsset, string> _settingsSnapshots = new();

        [UnityTest]
        public IEnumerator ManagerRows_RepaintAfterRuntimeLoadAndUnloadEvents()
        {
            ForceOffline(CoreAISettingsAsset.Instance);
            foreach (string dependency in AssetDatabase.GetDependencies(ScenePath, true))
            {
                if (dependency.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    ForceOffline(AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(dependency));
                }
            }

            // WHY: with the optional Mirror install present, loading an unopened demo scene whose Neo
            // controllers carry an un-baked NetworkIdentity makes Mirror stop Play Mode and abort the run;
            // the smoke test's patch gives those identities a scene id for this one load only.
            CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Arm(ScenePath);
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            Assert.IsTrue(scene.IsValid(), "The published Mods-chat scene must load: " + ScenePath);

            MonoBehaviour controller = null;
            yield return WaitUntil(() => (controller = FindController()) != null, 5f, "manager controller");
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            CoreAILifetimeScope coreScope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            CoreAiModsLifetimeScope modsScope =
                Object.FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
            Assert.IsNotNull(coreScope?.Container, "The scene must build its production CoreAI scope.");
            Assert.IsNotNull(modsScope?.Container, "The scene must build its production Mods scope.");
            ILuaModRuntime runtime = modsScope.Container.Resolve<ILuaModRuntime>();
            ActorContext actor = coreScope.Container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            SetPanelVisible(controller, true);

            string id = "repaint_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                runtime.LoadMod(actor, id, "local probe = 1", LuaCapabilities.Read);
                yield return WaitUntil(() => FindButton("Deactivate", id) != null, 5f,
                    "a manager row for a mod loaded through the runtime (no button action may trigger the repaint)");

                Click("Deactivate", id);
                Assert.IsFalse(runtime.IsLoaded(actor, id), "The row's Deactivate button must unload the mod.");
                yield return WaitUntil(() => FindButton("Activate", id) != null, 5f, "the inactive saved row");

                Click("Activate", id);
                Assert.IsTrue(runtime.IsLoaded(actor, id), "The inactive row's Activate button must load the mod again.");
            }
            finally
            {
                if (runtime.IsLoaded(actor, id))
                {
                    runtime.UnloadMod(actor, id);
                }
            }
        }

        [UnityTearDown]
        public IEnumerator UnloadSceneAndRestoreSettings()
        {
            CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Disarm();
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
            foreach (KeyValuePair<CoreAISettingsAsset, string> snapshot in _settingsSnapshots)
            {
                if (snapshot.Key == null)
                {
                    continue;
                }

                EditorJsonUtility.FromJsonOverwrite(snapshot.Value, snapshot.Key);
                EditorUtility.ClearDirty(snapshot.Key);
                string assetPath = AssetDatabase.GetAssetPath(snapshot.Key);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }

            _settingsSnapshots.Clear();
        }

        /// <summary>
        /// WHY Offline: the demo scenes share committed settings assets, so the scene would otherwise boot
        /// whatever backend the developer last selected. The snapshot is restored in memory and the asset
        /// reimported, so nothing this test changes reaches the committed file.
        /// </summary>
        private void ForceOffline(CoreAISettingsAsset settings)
        {
            if (settings == null || _settingsSnapshots.ContainsKey(settings))
            {
                return;
            }

            _settingsSnapshots.Add(settings, EditorJsonUtility.ToJson(settings));
            settings.ConfigureOffline();
            EditorUtility.ClearDirty(settings);
        }

        private static MonoBehaviour FindController()
        {
            foreach (MonoBehaviour component in
                     Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (component != null && component.GetType().Name == ControllerTypeName)
                {
                    return component;
                }
            }

            return null;
        }

        private static void SetPanelVisible(MonoBehaviour controller, bool visible)
        {
            PropertyInfo property = controller.GetType().GetProperty("PanelVisible", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, "The manager must expose PanelVisible.");
            property.SetValue(controller, visible);
        }

        private static Button FindButton(string caption, string rowId)
        {
            foreach (Button button in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                if (button == null || ReadText(button.transform) != caption)
                {
                    continue;
                }

                if (button.transform.parent != null && ReadText(button.transform.parent).Contains(rowId))
                {
                    return button;
                }
            }

            return null;
        }

        private static void Click(string caption, string rowId)
        {
            Button button = FindButton(caption, rowId);
            Assert.IsNotNull(button, "Missing manager row button: " + caption + " for " + rowId);
            Assert.IsTrue(button.interactable, "Manager row button is disabled: " + caption);
            button.onClick.Invoke();
        }

        private static string ReadText(Transform root)
        {
            List<string> parts = new();
            foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>())
            {
                parts.Add(text.text);
            }

            return string.Join("\n", parts).Trim();
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail($"Timed out after {timeoutSeconds}s waiting for {what}.");
                }

                yield return null;
            }
        }
    }
}
#endif

using NUnit.Framework;
using CoreAI.Composition;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
#if COREAI_LUA
using System.Reflection;
using CoreAI.Ai;
#endif

namespace CoreAI.Tests.EditMode
{
    public sealed class LiveMechanicsModsChatDemoSceneEditModeTests
    {
        private const string WaveScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity";
        private const string ChatScenePath = "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity";

        [Test]
        public void WaveAutoBattlerModsDemo_HasFullLuaEnabled()
        {
            AssertSceneGrantsFullLua(
                WaveScenePath,
                "Wave auto-battler demo must grant Full Lua for scene-object mod tasks.");
        }

        [Test]
        public void LiveMechanicsModsChatDemo_HasFullLuaEnabled()
        {
            AssertSceneGrantsFullLua(
                ChatScenePath,
                "Mods-chat demo must grant Full Lua so its persistence controller restores Full-tier mods.");
        }

#if COREAI_LUA
        private const string PersistenceControllerTypeName =
            "CoreAI.Demos.LiveMechanicsModsChatPersistenceController, CoreAI.Demos";

        // WHY: the first case is the defect. The hidden legacy module flag is on and the mods scope flag is
        // off; the controller used to read the legacy flag and asked for a tier the host never granted.
        [TestCase(false, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        public void PersistenceController_RequestsFullOnlyWhenModsScopeGrantsIt(
            bool modsScopeFull,
            bool legacyModuleFull,
            bool expectFull)
        {
            System.Type controllerType = System.Type.GetType(PersistenceControllerTypeName);
            Assert.IsNotNull(controllerType, "Demo assembly must provide the mods-chat persistence controller.");
            MethodInfo resolve = controllerType.GetMethod(
                "ResolveModLoadCapabilities",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(resolve, "Controller must pick its load tier in ResolveModLoadCapabilities.");

            SceneSetup[] originalSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                // WHY: an empty scene so the controller's scene-wide scope lookup sees only these objects.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                GameObject coreRoot = new("CoreAI Scope");
                coreRoot.SetActive(false);
                CoreAILifetimeScope coreScope = coreRoot.AddComponent<CoreAILifetimeScope>();
                GameObject moduleObject = new("Lua Module");
                moduleObject.transform.SetParent(coreRoot.transform, false);
                CoreAiLuaWorldModule module = moduleObject.AddComponent<CoreAiLuaWorldModule>();
                module.ConfigureForMigration(null, null, legacyModuleFull, legacyModuleFull);
                coreScope.SetLuaWorldModuleForMigration(module);

                GameObject modsRoot = new("CoreAI Mods Scope");
                modsRoot.SetActive(false);
                CoreAiModsLifetimeScope modsScope = modsRoot.AddComponent<CoreAiModsLifetimeScope>();
                SerializedObject modsSerialized = new(modsScope);
                modsSerialized.FindProperty("enableFullLuaAccess").boolValue = modsScopeFull;
                modsSerialized.ApplyModifiedPropertiesWithoutUndo();

                GameObject controllerObject = new("Mods Chat Persistence");
                controllerObject.SetActive(false);
                Component controller = controllerObject.AddComponent(controllerType);
                SerializedObject controllerSerialized = new(controller);
                controllerSerialized.FindProperty("coreAiScope").objectReferenceValue = coreScope;
                controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

                LuaCapabilities requested = (LuaCapabilities)resolve.Invoke(controller, null);

                Assert.AreEqual(LuaCapabilities.All, requested & LuaCapabilities.All,
                    "Saved mods always get the standard tiers.");
                Assert.AreEqual(expectFull, (requested & LuaCapabilities.Full) != 0,
                    $"Full must follow the mods scope flag ({modsScopeFull}), " +
                    $"not the legacy module flag ({legacyModuleFull}).");
            }
            finally
            {
                RestoreSceneSetupOrCreateEmptyScene(originalSetup);
            }
        }
#endif

        private static void AssertSceneGrantsFullLua(string scenePath, string message)
        {
            SceneSetup[] originalSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                MonoBehaviour scope = FindBehaviour("CoreAI.Composition.CoreAILifetimeScope");
                Assert.IsNotNull(scope, "Scene must include CoreAILifetimeScope.");

                CoreAiLuaWorldModule module = scope.GetComponentInChildren<CoreAiLuaWorldModule>(true);
                Assert.IsNotNull(module, "Scene must own world-command configuration in a child module.");

                // WHY: only the mods scope grants the Full tier to execute_lua, manage_mods and the demo's own
                // mod loads; the module's legacy flag is ignored.
                CoreAiModsLifetimeScope modsScope =
                    Object.FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
                Assert.IsNotNull(modsScope, "Scene must include CoreAiModsLifetimeScope.");
                Assert.IsTrue(modsScope.FullLuaAccessEnabled, message);
            }
            finally
            {
                RestoreSceneSetupOrCreateEmptyScene(originalSetup);
            }
        }

        private static void RestoreSceneSetupOrCreateEmptyScene(SceneSetup[] originalSetup)
        {
            foreach (SceneSetup scene in originalSetup)
            {
                if (scene.isLoaded)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                    return;
                }
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

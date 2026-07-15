using System.Reflection;
using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace CoreAI.Tests.EditMode
{
    /// <summary>EditMode coverage for Lua/world-command child-module ownership and legacy migration.</summary>
    public sealed class CoreAiLuaWorldModuleEditModeTests
    {
        [Test]
        public void Scope_AutoDiscoversInactiveChildModule()
        {
            GameObject root = new("CoreAI Scope");
            GameObject child = new("Lua Module");
            try
            {
                root.SetActive(false);
                child.transform.SetParent(root.transform, false);
                CoreAiLuaWorldModule module = child.AddComponent<CoreAiLuaWorldModule>();
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();

                Assert.AreSame(module, scope.LuaWorldModule);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LegacySerializedNames_AreLoadedAndCopiedToModule()
        {
            GameObject root = new("CoreAI Scope");
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                root.SetActive(false);
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                SetLegacy(scope, "worldPrefabRegistry", registry);
                SetLegacy(scope, "legacyLuaAllowedScenes", new[] { "Arena", "Hub" });
                SetLegacy(scope, "legacyEnableFullLuaAccess", true);
                SetLegacy(scope, "legacyEnableFullLuaPrivateAccess", true);

                GameObject child = new("Lua Module");
                child.transform.SetParent(root.transform, false);
                CoreAiLuaWorldModule module = child.AddComponent<CoreAiLuaWorldModule>();
                scope.CopyLegacyLuaWorldConfigurationTo(module);

                Assert.AreSame(registry, module.WorldPrefabRegistry);
                CollectionAssert.AreEqual(new[] { "Arena", "Hub" }, module.AllowedScenes);
                Assert.IsTrue(module.FullAccessEnabled);
                Assert.IsTrue(module.FullPrivateAccessEnabled);

                AssertFormerName("worldPrefabRegistry", "legacyWorldPrefabRegistry");
                AssertFormerName("legacyLuaAllowedScenes", "luaAllowedScenes");
                AssertFormerName("legacyEnableFullLuaAccess", "enableFullLuaAccess");
                AssertFormerName("legacyEnableFullLuaPrivateAccess", "enableFullLuaPrivateAccess");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void Scope_ModuleTakesPrecedenceOverLegacyFullAccess()
        {
            GameObject root = new("CoreAI Scope");
            try
            {
                root.SetActive(false);
                CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
                SetLegacy(scope, "legacyEnableFullLuaAccess", true);
                GameObject child = new("Lua Module");
                child.transform.SetParent(root.transform, false);
                CoreAiLuaWorldModule module = child.AddComponent<CoreAiLuaWorldModule>();
                scope.SetLuaWorldModuleForMigration(module);

                Assert.IsFalse(scope.FullLuaAccessEnabled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RootScope_NoLongerExposesFlatLuaFieldsInInspector()
        {
            FieldInfo[] fields = typeof(CoreAILifetimeScope).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (FieldInfo field in fields)
            {
                if (!field.Name.StartsWith("legacy", System.StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.IsNotNull(field.GetCustomAttribute<HideInInspector>(), field.Name);
            }

            Assert.IsNotNull(typeof(CoreAILifetimeScope).GetField(
                "luaWorldModule",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        [TestCase("Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemo.unity", false)]
        [TestCase("Assets/CoreAI.Demos/LuaMods/LuaModsDemo.unity", false)]
        [TestCase("Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity", true)]
        [TestCase("Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity", true)]
        [TestCase("Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity", false)]
        [TestCase("Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity", false)]
        [TestCase("Assets/CoreAI.Demos/MiniRpg/MiniRpgModsDemo.unity", true)]
        [TestCase("Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity", true)]
        [TestCase("Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity", true)]
        [TestCase("Assets/CoreAI.Demos/Skills/SkillsDemo.unity", false)]
        public void MigratedDemo_OwnsLuaConfigurationInChildModule(string scenePath, bool fullAccess)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                CoreAILifetimeScope scope = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    scope = root.GetComponentInChildren<CoreAILifetimeScope>(true);
                    if (scope != null)
                    {
                        break;
                    }
                }

                Assert.IsNotNull(scope, scenePath);
                CoreAiLuaWorldModule module = scope.LuaWorldModule;
                Assert.IsNotNull(module, scenePath);
                Assert.AreEqual(scope.transform, module.transform.parent, scenePath);
                Assert.IsNotNull(module.WorldPrefabRegistry, scenePath);
                Assert.AreEqual(fullAccess, module.FullAccessEnabled, scenePath);
                Assert.AreEqual(fullAccess, scope.FullLuaAccessEnabled, scenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void SetLegacy(CoreAILifetimeScope scope, string fieldName, object value)
        {
            FieldInfo field = typeof(CoreAILifetimeScope).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, fieldName);
            field.SetValue(scope, value);
        }

        private static void AssertFormerName(string fieldName, string oldName)
        {
            FieldInfo field = typeof(CoreAILifetimeScope).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, fieldName);
            FormerlySerializedAsAttribute attribute = field.GetCustomAttribute<FormerlySerializedAsAttribute>();
            Assert.IsNotNull(attribute, fieldName);
            Assert.AreEqual(oldName, attribute.oldName);
        }
    }
}

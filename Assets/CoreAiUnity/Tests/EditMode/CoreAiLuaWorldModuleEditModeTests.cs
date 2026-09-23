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
                // WHY: migration must still carry the deprecated values over so old scenes lose no data;
                // reading them is what this test checks, not a use of a grant.
#pragma warning disable CS0618
                Assert.IsTrue(module.FullAccessEnabled);
                Assert.IsTrue(module.FullPrivateAccessEnabled);
#pragma warning restore CS0618

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

                // WHY: pins the value the deprecated accessor still returns to callers that have not migrated.
#pragma warning disable CS0618
                Assert.IsFalse(scope.FullLuaAccessEnabled);
#pragma warning restore CS0618
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

                // WHY: only the mods scope grants the Full tier, so that is the flag a demo must set.
                CoreAiModsLifetimeScope modsScope = FindModsScope(scene);
                if (fullAccess)
                {
                    Assert.IsNotNull(modsScope, scenePath + " must contain a CoreAiModsLifetimeScope to grant Full.");
                    Assert.IsTrue(modsScope.FullLuaAccessEnabled, scenePath);
                }
                else
                {
                    Assert.IsFalse(modsScope != null && modsScope.FullLuaAccessEnabled, scenePath);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [TestCase("enableFullAccess")]
        [TestCase("enableFullPrivateAccess")]
        public void Module_LegacyFullFlags_StaySerializedButHidden(string fieldName)
        {
            FieldInfo field = typeof(CoreAiLuaWorldModule).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, fieldName);
            Assert.IsNotNull(field.GetCustomAttribute<SerializeField>(),
                fieldName + " must stay serialized so existing scenes keep their data.");
            Assert.IsNotNull(field.GetCustomAttribute<HideInInspector>(),
                fieldName + " grants nothing and must not be offered in the inspector.");
        }

        [TestCase(typeof(CoreAiLuaWorldModule), "FullAccessEnabled")]
        [TestCase(typeof(CoreAiLuaWorldModule), "FullPrivateAccessEnabled")]
        [TestCase(typeof(CoreAILifetimeScope), "FullLuaAccessEnabled")]
        public void LegacyFullAccessors_AreObsoleteWarningsPointingAtModsScope(System.Type type, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, propertyName);

            System.ObsoleteAttribute obsolete = property.GetCustomAttribute<System.ObsoleteAttribute>();
            Assert.IsNotNull(obsolete, propertyName + " never grants Full and must be marked obsolete.");
            Assert.IsFalse(obsolete.IsError, propertyName + " must stay a warning so existing callers compile.");
            StringAssert.Contains("CoreAiModsLifetimeScope", obsolete.Message, propertyName);
        }

        [Test]
        public void ModsScope_FullLuaAccessors_ReadTheSerializedGrant()
        {
            GameObject root = new("CoreAI Mods Scope");
            try
            {
                root.SetActive(false);
                CoreAiModsLifetimeScope modsScope = root.AddComponent<CoreAiModsLifetimeScope>();
                Assert.IsFalse(modsScope.FullLuaAccessEnabled);
                Assert.IsFalse(modsScope.FullLuaPrivateAccessEnabled);

                UnityEditor.SerializedObject serialized = new(modsScope);
                serialized.FindProperty("enableFullLuaAccess").boolValue = true;
                serialized.FindProperty("enableFullLuaPrivateAccess").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.IsTrue(modsScope.FullLuaAccessEnabled);
                Assert.IsTrue(modsScope.FullLuaPrivateAccessEnabled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static CoreAiModsLifetimeScope FindModsScope(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CoreAiModsLifetimeScope modsScope = root.GetComponentInChildren<CoreAiModsLifetimeScope>(true);
                if (modsScope != null)
                {
                    return modsScope;
                }
            }

            return null;
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

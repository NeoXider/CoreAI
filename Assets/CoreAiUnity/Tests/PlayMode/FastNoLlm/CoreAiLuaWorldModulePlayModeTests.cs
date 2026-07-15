using System.Collections;
using System.Reflection;
using CoreAI;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>Fast runtime composition coverage for the optional Lua/world-command child module.</summary>
    public sealed class CoreAiLuaWorldModulePlayModeTests
    {
        private CoreAISettingsAsset _previousSettings;
        private CoreAISettingsAsset _settings;
        private GameObject _root;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousSettings = CoreAISettingsAsset.Instance;
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureOffline();
            CoreAISettingsAsset.SetInstance(_settings);

            _root = new GameObject("CoreAI Lua Module Runtime Test");
            _root.SetActive(false);
            CoreAILifetimeScope scope = _root.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(scope, _settings);

            GameObject child = new("Lua and World Commands");
            child.transform.SetParent(_root.transform, false);
            child.AddComponent<CoreAiLuaWorldModule>();

            _root.SetActive(true);
            CoreAi.Invalidate();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            CoreAISettingsAsset.SetInstance(_previousSettings);
            CoreAi.Invalidate();
            if (_settings != null)
            {
                Object.Destroy(_settings);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator ChildModule_ComposesWithRealRootScope()
        {
            CoreAILifetimeScope scope = _root.GetComponent<CoreAILifetimeScope>();
            CoreAiLuaWorldModule module = _root.GetComponentInChildren<CoreAiLuaWorldModule>(true);

            Assert.AreSame(module, scope.LuaWorldModule);
            Assert.IsTrue(CoreAi.IsReady);
            yield break;
        }
    }
}

#if UNITY_EDITOR
using System;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Tests.EditMode
{
    public sealed class QwenDemoScenesEditModeTests
    {
        private const string GenieScenePath = "Assets/CoreAI.Demos/QwenDemo/QwenGenieDemo.unity";
        private const string SpellcraftScenePath = "Assets/CoreAI.Demos/QwenDemo/QwenSpellcraftDemo.unity";

        [Test]
        public void QwenDemos_RequireNativeToolCalls()
        {
            Type meter = Type.GetType("CoreAI.ExampleGame.QwenDemo.LlmMeter, CoreAI.Demos", true);
            object mode = meter.GetProperty("ToolChoiceMode")?.GetValue(null);
            Assert.AreEqual(LlmToolChoiceMode.RequireAny, mode);

            MethodInfo build = meter.GetMethod("BuildTaskRequest");
            AiTaskRequest request = (AiTaskRequest)build.Invoke(null,
                new object[] { "DemoMage", "мега молния", 160, new[] { "cast_spell" } });
            Assert.AreEqual(LlmToolChoiceMode.RequireSpecific, request.ForcedToolMode);
            Assert.AreEqual("cast_spell", request.RequiredToolName);

            AiTaskRequest multiToolRequest = (AiTaskRequest)build.Invoke(null,
                new object[] { "Demo", "choose", 160, new[] { "first", "second" } });
            Assert.AreEqual(LlmToolChoiceMode.RequireAny, multiToolRequest.ForcedToolMode);
            Assert.AreEqual(string.Empty, multiToolRequest.RequiredToolName);
        }

        [Test]
        public void SpellcraftPrompt_MapsRussianLightningToStorm()
        {
            Type spellcraft = Type.GetType(
                "CoreAI.ExampleGame.QwenDemo.SpellcraftDemo, CoreAI.Demos", true);
            FieldInfo promptField = spellcraft.GetField("SystemPrompt",
                BindingFlags.Static | BindingFlags.NonPublic);
            string prompt = (string)promptField.GetRawConstantValue();

            StringAssert.Contains("молния, гром, гроза", prompt);
            StringAssert.Contains("'мега молния' => cast_spell(element='storm', power=3)", prompt);
            StringAssert.Contains("Never answer with element/power as text", prompt);
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("   ", false)]
        [TestCase("native startup failed", true)]
        public void HotReload_EmptyRestoredError_DoesNotDisableDemo(string error, bool expected)
        {
            Type state = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenDemoState, CoreAI.Demos", true);
            MethodInfo predicate = state.GetMethod("HasBlockingError");

            Assert.AreEqual(expected, predicate.Invoke(null, new object[] { error }));
        }

        [Test]
        public void CompactGameView_UsesNonOverlappingHudPanels()
        {
            Type layout = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenDemoLayout, CoreAI.Demos", true);
            object[] args = { 745f, 524f, null, null };
            layout.GetMethod("Calculate")?.Invoke(null, args);
            Rect top = (Rect)args[2];
            Rect log = (Rect)args[3];

            Assert.LessOrEqual(top.xMax, 745f);
            Assert.LessOrEqual(top.yMax, log.yMin);
            Assert.LessOrEqual(log.yMax, 524f);
            Assert.GreaterOrEqual(top.height, 250f);
        }

        [TestCase(320f, 360f)]
        [TestCase(480f, 270f)]
        [TestCase(745f, 524f)]
        [TestCase(1920f, 1080f)]
        public void EverySupportedGameView_UsesBoundedNonOverlappingHudPanels(float width, float height)
        {
            Type layout = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenDemoLayout, CoreAI.Demos", true);
            object[] args = { width, height, null, null };
            layout.GetMethod("Calculate")?.Invoke(null, args);
            Rect top = (Rect)args[2];
            Rect log = (Rect)args[3];

            Assert.Greater(top.width, 0f);
            Assert.Greater(top.height, 0f);
            Assert.Greater(log.height, 0f);
            Assert.LessOrEqual(top.xMax, width + 0.01f);
            Assert.LessOrEqual(top.yMax, log.yMin + 0.01f);
            Assert.LessOrEqual(log.yMax, height + 0.01f);
        }

        [Test]
        public void NarrowPanel_StacksActionButtons()
        {
            Type layout = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenDemoLayout, CoreAI.Demos", true);
            MethodInfo method = layout.GetMethod("StackActionButtons");

            Assert.IsTrue((bool)method.Invoke(null, new object[] { 320f }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { 560f }));
        }

        [Test]
        public void SpellcraftTarget_RemainsInTheWorldLaneBeyondTheCompactHud()
        {
            Type spellcraft = Type.GetType(
                "CoreAI.ExampleGame.QwenDemo.SpellcraftDemo, CoreAI.Demos", true);
            FieldInfo targetX = spellcraft.GetField("TargetWorldX",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.IsNotNull(targetX);
            Assert.GreaterOrEqual((float)targetX.GetRawConstantValue(), 5f);
        }

        [Test]
        public void ToolsOnlyContract_RejectsMissingFailedUnexpectedAndMultipleCalls()
        {
            Type contract = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenToolContract, CoreAI.Demos", true);
            MethodInfo validate = contract.GetMethod("ValidateExactlyOne");
            string[] allowed = { "cast_spell" };

            Assert.IsNotNull(validate.Invoke(null, new object[] { Array.Empty<LlmToolCallTrace>(), allowed }));
            Assert.IsNotNull(validate.Invoke(null, new object[]
            {
                new[] { new LlmToolCallTrace("cast_spell", false, 1, "native", "bad args") }, allowed
            }));
            Assert.IsNotNull(validate.Invoke(null, new object[]
            {
                new[] { new LlmToolCallTrace("other", true, 1, "native") }, allowed
            }));
            Assert.IsNotNull(validate.Invoke(null, new object[]
            {
                new[]
                {
                    new LlmToolCallTrace("cast_spell", true, 1, "native"),
                    new LlmToolCallTrace("cast_spell", true, 1, "native")
                },
                allowed
            }));
            Assert.IsNull(validate.Invoke(null, new object[]
            {
                new[] { new LlmToolCallTrace("cast_spell", true, 1, "native") }, allowed
            }));
        }

        [Test]
        public void DeterminismVerdict_RejectsConsistentToolFailure()
        {
            Type verdict = Type.GetType("CoreAI.ExampleGame.QwenDemo.QwenDeterminismVerdict, CoreAI.Demos", true);
            MethodInfo passed = verdict.GetMethod("Passed");

            Assert.IsFalse((bool)passed.Invoke(null, new object[] { 5, 0, 5, 0 }));
            Assert.IsFalse((bool)passed.Invoke(null, new object[] { 5, 4, 1, 1 }));
            Assert.IsFalse((bool)passed.Invoke(null, new object[] { 5, 5, 0, 2 }));
            Assert.IsTrue((bool)passed.Invoke(null, new object[] { 5, 5, 0, 1 }));
        }

        [TestCase(GenieScenePath, "CoreAI.ExampleGame.QwenDemo.GenieDemo")]
        [TestCase(SpellcraftScenePath, "CoreAI.ExampleGame.QwenDemo.SpellcraftDemo")]
        public void Scene_HasStandaloneLocalModelComposition(string scenePath, string controllerType)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                CoreAILifetimeScope scope = FindInRoots<CoreAILifetimeScope>(roots);
                Assert.IsNotNull(scope, "Standalone Qwen demo must contain CoreAILifetimeScope.");
                Assert.IsNotNull(FindInRoots<Camera>(roots), "Standalone Qwen demo must contain a camera.");
                Assert.IsNotNull(FindBehaviour(roots, controllerType), "Standalone Qwen demo controller is missing.");
                MonoBehaviour controller = FindBehaviour(roots, controllerType);
                Assert.IsNotNull(controller.GetType().GetField("_toolTurnGuard",
                    BindingFlags.Instance | BindingFlags.NonPublic));
                Assert.IsNotNull(controller.GetType().GetField("_ready",
                    BindingFlags.Instance | BindingFlags.NonPublic));
                Assert.IsNull(FindInRoots<LLM>(roots), "CoreAI must create and configure the LLMUnity host at runtime.");
                Assert.IsNull(FindInRoots<LLMAgent>(roots), "CoreAI must create and configure the LLMAgent at runtime.");

                CoreAISettingsAsset settings = scope.Settings;
                Assert.IsNotNull(settings, "Standalone Qwen demo must assign dedicated settings.");
                Assert.AreEqual(LlmExecutionMode.LocalModel, settings.ExecutionMode);
                StringAssert.Contains("Qwen3.5-0.8B", settings.GgufModelPath);
                Assert.IsTrue(settings.LlmUnityAutoCreateRuntimeHost);
                Assert.IsTrue(settings.LlmUnityAutostartLocalServer);

                foreach (GameObject root in roots)
                {
                    Assert.Zero(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(root),
                        $"Scene object '{root.name}' has a missing script.");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static T FindInRoots<T>(GameObject[] roots) where T : Component
        {
            foreach (GameObject root in roots)
            {
                T component = root.GetComponentInChildren<T>(true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static MonoBehaviour FindBehaviour(GameObject[] roots, string fullTypeName)
        {
            foreach (GameObject root in roots)
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && string.Equals(behaviour.GetType().FullName, fullTypeName,
                            StringComparison.Ordinal))
                    {
                        return behaviour;
                    }
                }
            }

            return null;
        }
    }
}
#endif

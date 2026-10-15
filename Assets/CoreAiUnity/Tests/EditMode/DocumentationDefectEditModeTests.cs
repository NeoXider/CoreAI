using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CoreAI.Composition;
using CoreAI.Editor;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class DocumentationDefectEditModeTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SceneValidation_AcceptsAssignedOrResourcesSettings(bool assigned)
        {
            GameObject host = new("Validation test");
            host.SetActive(false);
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset previous = CoreAISettingsAsset.Instance;
            try
            {
                CoreAILifetimeScope scope = host.AddComponent<CoreAILifetimeScope>();
                if (assigned)
                {
                    SerializedObject serialized = new(scope);
                    serialized.FindProperty("coreAiSettings").objectReferenceValue = settings;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                {
                    CoreAISettingsAsset.SetInstance(settings);
                }

                Assert.IsTrue(CoreAIBuildMenu.HasLlmSettings(scope),
                    "The validator must accept the settings source used by the runtime scope without requiring a scene LLMAgent.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                CoreAISettingsAsset.SetInstance(previous);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void SceneValidation_RejectsMissingScope()
        {
            Assert.IsFalse(CoreAIBuildMenu.HasLlmSettings(null));
        }

        [Test]
        public void NuGetBootstrapper_VersionMatchesPackagesConfig()
        {
            FieldInfo version = typeof(CoreAINuGetBootstrapper).GetField("MeaiPackageVersion",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(version);
            string manifest = File.ReadAllText(Path.Combine(Application.dataPath, "packages.config"));
            Match pin = Regex.Match(manifest,
                "id=\"Microsoft\\.Extensions\\.AI\"\\s+version=\"([^\"]+)\"");
            Assert.IsTrue(pin.Success);
            Assert.AreEqual(pin.Groups[1].Value, version.GetRawConstantValue(),
                "The installer must not select assemblies that Unity cannot load.");
        }

        [Test]
        public void SettingsReset_RestoresFreshAssetDefaults()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset defaults = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.name = "Host settings";
                settings.ConfigureHttpApi("https://example.com/v1", "test-key", "cloud-model");
                settings.ApplyOptions(new CoreAISettingsOptions { MaxTokens = 4096, EnableStreaming = false });

                CoreAISettingsAssetEditor.ResetToDefaults(settings);

                Assert.AreEqual(defaults.BackendType, settings.BackendType);
                Assert.AreEqual(defaults.ExecutionMode, settings.ExecutionMode);
                Assert.AreEqual(defaults.ApiBaseUrl, settings.ApiBaseUrl);
                Assert.AreEqual(defaults.ApiKey, settings.ApiKey);
                Assert.AreEqual(defaults.ModelName, settings.ModelName);
                Assert.AreEqual(defaults.MaxTokens, settings.MaxTokens);
                Assert.AreEqual(defaults.EnableStreaming, settings.EnableStreaming);
                Assert.AreEqual("Host settings", settings.name);
            }
            finally
            {
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(defaults);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SettingsApplyOptions_CopiesWorldPrimitivePermission(bool allowed)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                SerializedObject serialized = new(settings);
                serialized.FindProperty("allowWorldPrimitives").boolValue = !allowed;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                settings.ApplyOptions(new CoreAISettingsOptions { AllowWorldPrimitives = allowed });

                Assert.AreEqual(allowed, settings.AllowWorldPrimitives);
                Assert.AreEqual(allowed, settings.ToOptions().AllowWorldPrimitives);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Renders the published procedural-materials showcase into PNG files for visual review: every view
    /// of the first material, then one shot per material in the default view.
    /// <para>
    /// WHY explicit and opt-in: the output is evidence for a person (or a reviewer reading images) to
    /// judge, not a pass/fail property, and it writes files. Set <see cref="OutputVariable"/> to an
    /// absolute directory and select this test by name. The automated part it does assert is that each
    /// view and material renders a non-blank frame.
    /// </para>
    /// </summary>
    [Explicit("Writes review captures; set COREAI_VISUAL_CAPTURE_DIR and select it by name.")]
    [Category("Visual")]
    public sealed class ProceduralMaterialsCapturePlayModeTests
    {
        /// <summary>Absolute directory the captures are written to.</summary>
        public const string OutputVariable = "COREAI_VISUAL_CAPTURE_DIR";

        private const string ScenePath = "Assets/CoreAI.Demos/ProceduralMaterials/ProceduralMaterialsShowcase.unity";
        private const string DriverTypeName = "RbxProceduralMaterialsShowcase";
        private const int Width = 1280;
        private const int Height = 720;
        private const int ViewCount = 5;
        private const int MaxMaterials = 24;

        private readonly Dictionary<CoreAISettingsAsset, string> _settingsSnapshots = new();

        [UnityTest]
        public IEnumerator Showcase_RendersEveryViewAndMaterialToReviewCaptures()
        {
            string output = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output))
            {
                Assert.Ignore(OutputVariable + " must name an absolute directory for the captures.");
            }

            Directory.CreateDirectory(output);
            ForceOffline(CoreAISettingsAsset.Instance);
            // WHY: with the optional Mirror install present, loading an unopened demo scene whose Neo
            // controllers carry an un-baked NetworkIdentity makes Mirror stop Play Mode and abort the run;
            // the smoke test's patch gives those identities a scene id for this one load only.
            CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Arm(ScenePath);
            try
            {
                EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
                MonoBehaviour driver = null;
                for (int frame = 0; frame < 300 && (driver = FindDriver()) == null; frame++)
                {
                    yield return null;
                }

                Assert.IsNotNull(driver, "The showcase driver did not appear.");
                for (int frame = 0; frame < 30; frame++)
                {
                    yield return null;
                }

                Invoke(driver, "SetSweepEnabled", false);
                int firstMaterial = Read<int>(driver, "SelectedMaterialIndex");
                for (int view = 0; view < ViewCount; view++)
                {
                    Invoke(driver, "SelectView", view);
                    yield return Settle();
                    Capture(Path.Combine(output, $"view-{view}-material-{firstMaterial}.png"));
                }

                Invoke(driver, "SelectView", 0);
                HashSet<int> seen = new();
                for (int step = 0; step < MaxMaterials; step++)
                {
                    int material = Read<int>(driver, "SelectedMaterialIndex");
                    if (!seen.Add(material))
                    {
                        break;
                    }

                    yield return Settle();
                    Capture(Path.Combine(output, $"material-{material:D2}.png"));
                    Invoke(driver, "AdvanceMaterial", 1);
                }

                TestContext.WriteLine($"[Visual] {seen.Count} materials and {ViewCount} views written to {output}");
            }
            finally
            {
                CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Disarm();
                RestoreSettings();
            }
        }

        [UnityTearDown]
        public IEnumerator UnloadScene()
        {
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private static IEnumerator Settle()
        {
            for (int frame = 0; frame < 10; frame++)
            {
                yield return null;
            }
        }

        /// <summary>
        /// WHY Camera.Render into an explicit target: a screen capture needs a presented frame, and
        /// end-of-frame never resumes under -batchmode.
        /// </summary>
        private static void Capture(string path)
        {
            Camera camera = Camera.main;
            Assert.IsNotNull(camera, "The showcase needs a main camera to capture.");
            RenderTexture target = new(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            Texture2D image = new(Width, Height, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                Assert.IsFalse(IsBlank(image), "The captured frame is a single flat colour: " + path);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static bool IsBlank(Texture2D image)
        {
            Color32[] pixels = image.GetPixels32();
            Color32 first = pixels[0];
            for (int index = 0; index < pixels.Length; index += 97)
            {
                Color32 pixel = pixels[index];
                if (Math.Abs(pixel.r - first.r) > 8 || Math.Abs(pixel.g - first.g) > 8 || Math.Abs(pixel.b - first.b) > 8)
                {
                    return false;
                }
            }

            return true;
        }

        private static MonoBehaviour FindDriver()
        {
            foreach (MonoBehaviour component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component != null && component.GetType().Name == DriverTypeName)
                {
                    return component;
                }
            }

            return null;
        }

        private static void Invoke(object target, string method, object argument)
        {
            MethodInfo member = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(member, "Missing showcase driver method: " + method);
            member.Invoke(target, new[] { argument });
        }

        private static T Read<T>(object target, string property)
        {
            PropertyInfo member = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(member, "Missing showcase driver state: " + property);
            return (T)member.GetValue(target);
        }

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

        private void RestoreSettings()
        {
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
    }
}
#endif

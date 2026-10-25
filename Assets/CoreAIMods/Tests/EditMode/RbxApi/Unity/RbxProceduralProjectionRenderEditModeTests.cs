using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Renders the production procedural shader against geometrically equivalent meshes.</summary>
    [TestFixture]
    [Category("MaterialRender")]
    public sealed class RbxProceduralProjectionRenderEditModeTests
    {
        private const int Resolution = 256;
        private readonly List<Object> _ownedObjects = new();
        private Scene _previewScene;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Procedural projection rendering requires a graphics device; -nographics is not a pass.");
            }

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("Procedural projection rendering requires a floating-point render target.");
            }

            if (GraphicsSettings.currentRenderPipeline == null ||
                GraphicsSettings.currentRenderPipeline.GetType().Name != "UniversalRenderPipelineAsset")
            {
                Assert.Ignore("Procedural projection rendering requires an active Universal Render Pipeline asset.");
            }

            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            _previewScene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            if (_previewScene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_previewScene);
            }

            for (int index = _ownedObjects.Count - 1; index >= 0; index--)
            {
                if (_ownedObjects[index] != null)
                {
                    Object.DestroyImmediate(_ownedObjects[index]);
                }
            }

            _ownedObjects.Clear();
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [TestCase(false, false, false, false)]
        [TestCase(false, true, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(false, false, false, true)]
        [TestCase(false, true, false, true)]
        [TestCase(true, false, false, true)]
        [TestCase(true, true, false, true)]
        [TestCase(false, false, true, false)]
        [TestCase(true, true, true, true)]
        public void PhysicalProjection_TransformScaleMatchesBakedGeometry(
            bool curved, bool rotated, bool uniformScale, bool missingTextureFallback)
        {
            Vector3 scale = uniformScale ? Vector3.one * 1.4f : new Vector3(1f, 1f, 2f);
            Quaternion rotation = rotated ? Quaternion.Euler(23f, 41f, -17f) : Quaternion.identity;
            Mesh sourceMesh = Own(CreateSurface(curved, Vector3.one));
            Mesh bakedMesh = Own(CreateSurface(curved, scale));
            if (!curved && !uniformScale)
            {
                Assert.Greater(bakedMesh.normals[0].y, bakedMesh.normals[0].z + 0.1f,
                    "The independently scaled wedge must face predominantly along its physical Y axis.");
            }

            Material material = Own(new Material(ResolveBrick(missingTextureFallback)));
            material.SetFloat("_BumpStrength", 0f);
            Assert.IsTrue(material.shader.isSupported, "The production surface shader must compile on this device.");

            GameObject surface = CreateSceneObject("ProceduralProjectionSurface");
            MeshFilter filter = surface.AddComponent<MeshFilter>();
            MeshRenderer renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            surface.transform.rotation = rotation;

            GameObject cameraObject = CreateSceneObject("ProceduralProjectionCamera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.scene = _previewScene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.03f, 0.07f, 0f);
            camera.orthographic = true;
            camera.orthographicSize = 1.7f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 12f;
            camera.aspect = 1f;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            Vector3 center = rotation * bakedMesh.bounds.center;
            Vector3 viewDirection = rotation * new Vector3(0.2f, 1f, 1.3f).normalized;
            cameraObject.transform.position = center + viewDirection * 4f;
            cameraObject.transform.rotation = Quaternion.LookRotation(-viewDirection, rotation * Vector3.up);

            GameObject lightObject = CreateSceneObject("ProceduralProjectionLight");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.4f;
            light.shadows = LightShadows.None;
            lightObject.transform.rotation = cameraObject.transform.rotation;

            string caseName = (curved ? "curved" : "wedge") + (rotated ? "-rotated" : "-identity") +
                (uniformScale ? "-uniform" : "-nonuniform") +
                (missingTextureFallback ? "-missing-texture" : "-direct");

            filter.sharedMesh = sourceMesh;
            surface.transform.localScale = scale;
            Color[] actual = Capture(camera, caseName + "-scaled.png");
            filter.sharedMesh = bakedMesh;
            surface.transform.localScale = Vector3.one;
            Color[] reference = Capture(camera, caseName + "-baked.png");

            AssertEquivalentVisiblePixels(actual, reference);
        }

        private Material ResolveBrick(bool missingTextureFallback)
        {
            RbxMaterialId brick = new("Brick", 848);
            IRbxMaterialProvider<Material> provider;
            if (missingTextureFallback)
            {
                RbxMaterialTextureCatalog catalog = Own(ScriptableObject.CreateInstance<RbxMaterialTextureCatalog>());
                catalog.ReplaceEntries(new[]
                {
                    new RbxMaterialTextureCatalog.Entry { MaterialName = "Brick", MaterialValue = 848 }
                });
                Shader texturedShader = Shader.Find("CoreAI/Rbx/Textured Surface");
                Assert.IsNotNull(texturedShader);
                provider = new RbxTextureMaterialProvider(catalog, null, texturedShader);
                LogAssert.Expect(LogType.Error, new Regex(
                    "Catalog entry for Enum\\.Material\\.Brick.*missing required.*using procedural",
                    RegexOptions.IgnoreCase));
            }
            else
            {
                provider = new RbxProceduralMaterialProvider();
            }

            Assert.IsTrue(provider.TryGetMaterial(in brick, out Material shared));
            Assert.AreEqual("CoreAI/Rbx/Procedural Surface", shared.shader.name);
            return shared;
        }

        private static Mesh CreateSurface(bool curved, Vector3 bakeScale)
        {
            int segments = curved ? 32 : 1;
            Vector3[] vertices = new Vector3[(segments + 1) * 2];
            Vector3[] normals = new Vector3[vertices.Length];
            int[] triangles = new int[segments * 6];
            for (int row = 0; row <= segments; row++)
            {
                float fraction = (float)row / segments;
                float angle = Mathf.Lerp(25f, 65f, fraction) * Mathf.Deg2Rad;
                Vector3 center = curved
                    ? new Vector3(0f, Mathf.Sin(angle) * 0.9f, Mathf.Cos(angle) * 0.9f)
                    : new Vector3(0f, 0.6f - fraction * 1.2f, -0.6f + fraction * 1.2f);
                Vector3 rowTangent = curved
                    ? new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle))
                    : new Vector3(0f, -1f, 1f);
                Vector3 scaledAcross = Vector3.Scale(Vector3.right, bakeScale);
                Vector3 scaledAlong = Vector3.Scale(rowTangent, bakeScale);
                Vector3 normal = curved
                    ? Vector3.Cross(scaledAcross, scaledAlong).normalized
                    : Vector3.Cross(scaledAlong, scaledAcross).normalized;
                for (int column = 0; column < 2; column++)
                {
                    int index = row * 2 + column;
                    vertices[index] = Vector3.Scale(center + Vector3.right * (column == 0 ? -0.7f : 0.7f), bakeScale);
                    normals[index] = normal;
                }

                if (row < segments)
                {
                    int offset = row * 6;
                    int first = row * 2;
                    triangles[offset] = first;
                    triangles[offset + 1] = first + (curved ? 1 : 2);
                    triangles[offset + 2] = first + (curved ? 2 : 1);
                    triangles[offset + 3] = first + 1;
                    triangles[offset + 4] = first + (curved ? 3 : 2);
                    triangles[offset + 5] = first + (curved ? 2 : 3);
                }
            }

            Mesh mesh = new() { name = "ProceduralProjectionGeometricOracle" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Color[] Capture(Camera camera, string fileName)
        {
            RenderTexture target = new(Resolution, Resolution, 24, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear) { antiAliasing = 1 };
            Texture2D image = new(Resolution, Resolution, TextureFormat.RGBAFloat, false, true);
            Texture2D preview = new(Resolution, Resolution, TextureFormat.RGBA32, false, true);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                RenderPipeline.StandardRequest request = new() { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    Assert.Ignore("The active pipeline cannot execute the required SRP render request.");
                }

                camera.targetTexture = target;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                image.Apply();
                Color[] pixels = image.GetPixels();
                preview.SetPixels(pixels);
                preview.Apply();
                string workDirectory = TestContext.CurrentContext.WorkDirectory;
                if (string.IsNullOrEmpty(workDirectory))
                {
                    workDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults"));
                }
                string outputDirectory = Path.Combine(workDirectory, "CoreAI-ProceduralProjection");
                Directory.CreateDirectory(outputDirectory);
                string outputPath = Path.Combine(outputDirectory, fileName);
                File.WriteAllBytes(outputPath, preview.EncodeToPNG());
                TestContext.WriteLine("Projection capture: " + outputPath + "; graphics=" +
                    SystemInfo.graphicsDeviceType + "; pipeline=" + GraphicsSettings.currentRenderPipeline.name);
                return pixels;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(preview);
                Object.DestroyImmediate(image);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static void AssertEquivalentVisiblePixels(Color[] actual, Color[] reference)
        {
            int comparedPixels = 0;
            double absoluteError = 0d;
            float minimumLuminance = float.PositiveInfinity;
            float maximumLuminance = float.NegativeInfinity;
            Color background = reference[0];
            for (int index = 0; index < actual.Length; index++)
            {
                Color scaled = actual[index];
                Color baked = reference[index];
                Assert.IsTrue(IsFinite(scaled) && IsFinite(baked), "Shader output must contain finite channels.");
                if (Mathf.Abs(baked.r - background.r) + Mathf.Abs(baked.g - background.g) +
                    Mathf.Abs(baked.b - background.b) < 0.005f)
                {
                    continue;
                }

                comparedPixels++;
                absoluteError += Math.Abs(scaled.r - baked.r) + Math.Abs(scaled.g - baked.g) +
                    Math.Abs(scaled.b - baked.b);
                minimumLuminance = Mathf.Min(minimumLuminance, baked.grayscale);
                maximumLuminance = Mathf.Max(maximumLuminance, baked.grayscale);
            }

            Assert.Greater(comparedPixels, 1500, "Both renders must contain the surface, not just matching clear pixels.");
            Assert.Greater(maximumLuminance - minimumLuminance, 0.015f,
                "The production brick pattern must be visible; a flat error or unlit frame is not a pass.");
            double meanChannelError = absoluteError / (comparedPixels * 3d);
            TestContext.WriteLine("Compared surface pixels=" + comparedPixels + "; mean channel error=" + meanChannelError);
            Assert.Less(meanChannelError, 0.01d,
                "Transform scaling and independently baked geometry must select the same physical projection.");
        }

        private static bool IsFinite(Color color)
        {
            return !float.IsNaN(color.r) && !float.IsInfinity(color.r) &&
                !float.IsNaN(color.g) && !float.IsInfinity(color.g) &&
                !float.IsNaN(color.b) && !float.IsInfinity(color.b) &&
                !float.IsNaN(color.a) && !float.IsInfinity(color.a);
        }

        private GameObject CreateSceneObject(string name)
        {
            GameObject created = new(name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(created, _previewScene);
            return Own(created);
        }

        private T Own<T>(T value) where T : Object
        {
            _ownedObjects.Add(value);
            return value;
        }
    }
}

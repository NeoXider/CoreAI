#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CoreAI.Demos.ProceduralMaterials
{
    /// <summary>Creates the labelled procedural-material grid without hand-authored scene YAML.</summary>
    public static class RbxProceduralMaterialsShowcaseBuilder
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/ProceduralMaterials/ProceduralMaterialsShowcase.unity";
        private const int Columns = 6;
        private const float ColumnSpacing = 4.2f;
        private const float RowSpacing = 4.2f;

        private static readonly SwatchSpec[] Swatches =
        {
            new SwatchSpec("Plastic", 256, PrimitiveType.Cube),
            new SwatchSpec("SmoothPlastic", 272, PrimitiveType.Sphere),
            new SwatchSpec("Neon", 288, PrimitiveType.Sphere),
            new SwatchSpec("ForceField", 1584, PrimitiveType.Sphere),
            new SwatchSpec("Glass", 1568, PrimitiveType.Sphere),
            new SwatchSpec("Wood", 512, PrimitiveType.Cylinder),
            new SwatchSpec("WoodPlanks", 528, PrimitiveType.Cube),
            new SwatchSpec("Metal", 1088, PrimitiveType.Sphere),
            new SwatchSpec("DiamondPlate", 1056, PrimitiveType.Cube),
            new SwatchSpec("CorrodedMetal", 1040, PrimitiveType.Sphere),
            new SwatchSpec("Marble", 784, PrimitiveType.Sphere),
            new SwatchSpec("Slate", 800, PrimitiveType.Cube),
            new SwatchSpec("Concrete", 816, PrimitiveType.Cube),
            new SwatchSpec("Brick", 848, PrimitiveType.Cube),
            new SwatchSpec("Cobblestone", 880, PrimitiveType.Sphere),
            new SwatchSpec("Grass", 1280, PrimitiveType.Cube),
            new SwatchSpec("Sand", 1296, PrimitiveType.Sphere),
            new SwatchSpec("Ground", 1360, PrimitiveType.Cube),
            new SwatchSpec("Rock", 896, PrimitiveType.Sphere),
            new SwatchSpec("Ice", 1536, PrimitiveType.Sphere),
            new SwatchSpec("Snow", 1328, PrimitiveType.Sphere),
            new SwatchSpec("Fabric", 1312, PrimitiveType.Capsule),
            new SwatchSpec("FALLBACK", -1, PrimitiveType.Cube)
        };

        [MenuItem("CoreAI/Demos/Build Procedural Materials Showcase")]
        public static void BuildScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ConfigureEnvironment();

            GameObject root = new GameObject("Procedural Materials Showcase");
            RbxProceduralMaterialsShowcase showcase =
                root.AddComponent<RbxProceduralMaterialsShowcase>();
            Camera camera = CreateCamera(root.transform);
            CreateLights(root.transform);
            CreateFloor(root.transform);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            CreateHeader(root.transform, camera, font);
            List<RbxProceduralMaterialsShowcase.Entry> entries =
                new List<RbxProceduralMaterialsShowcase.Entry>(Swatches.Length);

            int rowCount = Mathf.CeilToInt(Swatches.Length / (float)Columns);
            for (int index = 0; index < Swatches.Length; index++)
            {
                SwatchSpec spec = Swatches[index];
                int column = index % Columns;
                int row = index / Columns;
                float x = (column - (Columns - 1) * 0.5f) * ColumnSpacing;
                float z = ((rowCount - 1) * 0.5f - row) * RowSpacing;
                Vector3 tilePosition = new Vector3(x, 0f, z);
                Renderer renderer = CreateTile(root.transform, spec, tilePosition, camera, font);
                entries.Add(new RbxProceduralMaterialsShowcase.Entry(
                    renderer, spec.MaterialName, spec.MaterialValue));
            }

            showcase.Configure(entries.ToArray());
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[CoreAI.Demos] Built procedural material showcase at " + ScenePath);
        }

        private static void ConfigureEnvironment()
        {
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.29f, 0.31f, 0.35f);
            RenderSettings.ambientEquatorColor = new Color(0.14f, 0.15f, 0.17f);
            RenderSettings.ambientGroundColor = new Color(0.035f, 0.038f, 0.045f);
            RenderSettings.ambientIntensity = 0.85f;
            RenderSettings.reflectionIntensity = 1f;
        }

        private static Camera CreateCamera(Transform parent)
        {
            GameObject cameraObject = new GameObject("Showcase Camera");
            cameraObject.transform.SetParent(parent, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.tag = "MainCamera";
            camera.transform.position = new Vector3(0f, 20f, -26f);
            camera.transform.LookAt(new Vector3(0f, 0.8f, 0f));
            camera.fieldOfView = 43f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.018f, 0.022f, 0.035f);
            return camera;
        }

        private static void CreateLights(Transform parent)
        {
            GameObject keyObject = new GameObject("Key Light");
            keyObject.transform.SetParent(parent, false);
            keyObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            Light key = keyObject.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.96f, 0.91f);
            key.intensity = 1.2f;
            key.shadows = LightShadows.Soft;

            CreatePointLight(parent, "Cool Rim", new Vector3(-10f, 8f, 2f),
                new Color(0.72f, 0.82f, 1f), 5.5f, 18f);
            CreatePointLight(parent, "Warm Fill", new Vector3(10f, 6f, -5f),
                new Color(1f, 0.78f, 0.64f), 4f, 16f);
        }

        private static void CreatePointLight(Transform parent, string name, Vector3 position,
            Color color, float intensity, float range)
        {
            GameObject lightObject = new GameObject(name);
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.position = position;
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        private static void CreateFloor(Transform parent)
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Backdrop Floor";
            floor.transform.SetParent(parent, false);
            floor.transform.position = new Vector3(0f, -0.28f, 0f);
            floor.transform.localScale = new Vector3(28f, 0.35f, 20f);
        }

        private static Renderer CreateTile(Transform parent, SwatchSpec spec, Vector3 position,
            Camera camera, Font font)
        {
            GameObject tile = new GameObject(spec.Label);
            tile.transform.SetParent(parent, false);
            tile.transform.position = position;

            GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedestal.name = "Pedestal";
            pedestal.transform.SetParent(tile.transform, false);
            pedestal.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            pedestal.transform.localScale = new Vector3(3.35f, 0.16f, 3.35f);

            GameObject visual = GameObject.CreatePrimitive(spec.Shape);
            visual.name = "Material Sample";
            visual.transform.SetParent(tile.transform, false);
            visual.transform.localPosition = new Vector3(0f, 1.18f, 0f);
            visual.transform.localRotation = Quaternion.Euler(0f, 24f, 0f);
            visual.transform.localScale = SampleScale(spec.Shape);
            Collider collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            Vector3 labelPosition = position + new Vector3(0f, 2.95f, 0f);
            CreateText(tile.transform, spec.Label, labelPosition, camera, font, 0.052f,
                new Color(0.93f, 0.95f, 1.0f));
            return visual.GetComponent<Renderer>();
        }

        private static Vector3 SampleScale(PrimitiveType shape)
        {
            if (shape == PrimitiveType.Cube)
            {
                return new Vector3(2.75f, 1.75f, 2.75f);
            }

            if (shape == PrimitiveType.Cylinder)
            {
                return new Vector3(1.2f, 0.82f, 1.2f);
            }

            if (shape == PrimitiveType.Capsule)
            {
                return new Vector3(1.18f, 1.18f, 1.18f);
            }

            return new Vector3(2.45f, 2.45f, 2.45f);
        }

        private static void CreateHeader(Transform parent, Camera camera, Font font)
        {
            CreateText(parent, "PROCEDURAL RBX MATERIALS", new Vector3(0f, 4.8f, 9.6f),
                camera, font, 0.09f, new Color(0.86f, 0.92f, 1.0f));
            CreateText(parent, "22 runtime materials + visible unmapped fallback",
                new Vector3(0f, 4.1f, 9.6f), camera, font, 0.045f,
                new Color(0.5f, 0.62f, 0.78f));
        }

        private static void CreateText(Transform parent, string content, Vector3 position,
            Camera camera, Font font, float characterSize, Color color)
        {
            GameObject labelObject = new GameObject(content + " Label");
            labelObject.transform.SetParent(parent, true);
            labelObject.transform.position = position;
            labelObject.transform.rotation = Quaternion.LookRotation(position - camera.transform.position);
            TextMesh text = labelObject.AddComponent<TextMesh>();
            text.text = content;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 64;
            text.characterSize = characterSize;
            text.color = color;
            text.richText = false;
            if (font != null)
            {
                text.font = font;
                MeshRenderer renderer = labelObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = font.material;
            }
        }

        private readonly struct SwatchSpec
        {
            public readonly string Label;
            public readonly string MaterialName;
            public readonly int MaterialValue;
            public readonly PrimitiveType Shape;

            public SwatchSpec(string materialName, int materialValue, PrimitiveType shape)
            {
                Label = materialName;
                MaterialName = materialName;
                MaterialValue = materialValue;
                Shape = shape;
            }
        }
    }
}
#endif

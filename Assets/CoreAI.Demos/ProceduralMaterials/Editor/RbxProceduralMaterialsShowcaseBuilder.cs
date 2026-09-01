#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using CoreAI.Composition;
using Neo.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CoreAI.Demos.ProceduralMaterials
{
    /// <summary>Builds the labelled URP material-judging rig without hand-authored scene YAML.</summary>
    public static class RbxProceduralMaterialsShowcaseBuilder
    {
        private const string DemoRoot = "Assets/CoreAI.Demos/ProceduralMaterials";
        private const string GeneratedRoot = DemoRoot + "/Generated";
        private const string ScenePath = DemoRoot + "/ProceduralMaterialsShowcase.unity";
        private const string BevelledCubePath = GeneratedRoot + "/BevelledCube.asset";
        private const string SkyboxPath = GeneratedRoot + "/NeutralProceduralSky.mat";
        private const string StudioDarkPath = GeneratedRoot + "/StudioDark.mat";
        private const string StudioMidPath = GeneratedRoot + "/StudioMid.mat";
        private const string StudioLightPath = GeneratedRoot + "/StudioLight.mat";
        private const string StudioAccentAPath = GeneratedRoot + "/StudioAccentA.mat";
        private const string StudioAccentBPath = GeneratedRoot + "/StudioAccentB.mat";
        private const string SoftboxPath = GeneratedRoot + "/Softbox.mat";
        private const string FixedVolumePath = GeneratedRoot + "/FixedExposureTonemapping.asset";
        private const string NeonVolumePath = GeneratedRoot + "/NeonBloomOnly.asset";
        private const int CatalogColumns = 6;
        private const float CatalogColumnSpacing = 4.1f;
        private const float CatalogRowSpacing = 4.1f;

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

            EnsureGeneratedFolder();
            RigAssets assets = CreateOrUpdateRigAssets();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ConfigureEnvironment(assets.Skybox);

            GameObject root = new GameObject("Rbx Material Judging Rig");
            GameObject services = new GameObject("CoreAI Production Services");
            services.transform.SetParent(root.transform, false);
            services.AddComponent<CoreAILifetimeScope>();
            RbxProceduralMaterialsShowcase showcase =
                root.AddComponent<RbxProceduralMaterialsShowcase>();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            ViewSetup viewSetup = CreateViews(root.transform, font);

            CreatePostProcessing(root.transform, viewSetup.NeonCamera, assets);
            CreateStudioShell(root.transform, assets);
            CreateLightingAndReflections(root.transform, assets, viewSetup.SweepTarget.position);

            List<RbxProceduralMaterialsShowcase.Entry> entries =
                new List<RbxProceduralMaterialsShowcase.Entry>(Swatches.Length + 3);
            Renderer[] diagnosticRenderers = CreateDiagnosticStation(root.transform, assets,
                viewSetup.FaceOnCamera, font, out TextMesh materialSelectionLabel);
            CreateNeutralCatalog(root.transform, assets, viewSetup.MidFarCamera, font, entries);
            CreateTransparencyStage(root.transform, assets, viewSetup.TransparencyCamera, font,
                entries);
            CreateNeonStage(root.transform, assets, viewSetup.NeonCamera, font, entries);

            RbxProceduralMaterialsShowcase.MaterialSelection[] selections =
                CreateMaterialSelections();
            showcase.ConfigureRig(entries.ToArray(), diagnosticRenderers, selections,
                materialSelectionLabel, viewSetup.Views, viewSetup.SweepNear,
                viewSetup.SweepFar, viewSetup.SweepTarget, FindMaterialIndex("Metal"));

            DynamicGI.UpdateEnvironment();
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[CoreAI.Demos] Built material judging rig at " + ScenePath);
        }

        private static void EnsureGeneratedFolder()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedRoot))
            {
                AssetDatabase.CreateFolder(DemoRoot, "Generated");
            }
        }

        private static RigAssets CreateOrUpdateRigAssets()
        {
            Material skybox = CreateOrUpdateSkybox();
            Material studioDark = CreateOrUpdateLitMaterial(StudioDarkPath,
                new Color(0.045f, 0.05f, 0.06f), 0f, 0.2f);
            Material studioMid = CreateOrUpdateLitMaterial(StudioMidPath,
                new Color(0.18f, 0.19f, 0.21f), 0f, 0.3f);
            Material studioLight = CreateOrUpdateLitMaterial(StudioLightPath,
                new Color(0.52f, 0.54f, 0.57f), 0f, 0.38f);
            Material studioAccentA = CreateOrUpdateLitMaterial(StudioAccentAPath,
                new Color(0.16f, 0.34f, 0.48f), 0f, 0.32f);
            Material studioAccentB = CreateOrUpdateLitMaterial(StudioAccentBPath,
                new Color(0.48f, 0.23f, 0.12f), 0f, 0.32f);
            Material softbox = CreateOrUpdateUnlitMaterial(SoftboxPath,
                new Color(0.82f, 0.84f, 0.86f));
            Mesh bevelledCube = CreateOrUpdateBevelledCube();
            VolumeProfile fixedVolume = CreateOrUpdateFixedVolumeProfile();
            VolumeProfile neonVolume = CreateOrUpdateNeonVolumeProfile();
            return new RigAssets(skybox, studioDark, studioMid, studioLight, studioAccentA,
                studioAccentB, softbox, bevelledCube, fixedVolume, neonVolume);
        }

        private static Material CreateOrUpdateSkybox()
        {
            Material material = GetOrCreateMaterial(SkyboxPath, "Skybox/Procedural");
            material.SetFloat("_SunDisk", 2f);
            material.SetFloat("_SunSize", 0.055f);
            material.SetFloat("_SunSizeConvergence", 5f);
            material.SetFloat("_AtmosphereThickness", 0.62f);
            material.SetColor("_SkyTint", new Color(0.52f, 0.56f, 0.62f));
            material.SetColor("_GroundColor", new Color(0.16f, 0.17f, 0.19f));
            material.SetFloat("_Exposure", 1.08f);
            material.DisableKeyword("_SUNDISK_NONE");
            material.DisableKeyword("_SUNDISK_SIMPLE");
            material.EnableKeyword("_SUNDISK_HIGH_QUALITY");
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material CreateOrUpdateLitMaterial(string path, Color color,
            float metallic, float smoothness)
        {
            Material material = GetOrCreateMaterial(path, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Surface", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material CreateOrUpdateUnlitMaterial(string path, Color color)
        {
            Material material = GetOrCreateMaterial(path, "Universal Render Pipeline/Unlit");
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            material.SetFloat("_Surface", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material GetOrCreateMaterial(string path, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException("Required shader is unavailable: " + shaderName);
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader)
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(path)
                };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            return material;
        }

        private static VolumeProfile CreateOrUpdateFixedVolumeProfile()
        {
            VolumeProfile profile = GetOrCreateVolumeProfile(FixedVolumePath);
            ColorAdjustments colorAdjustments = GetOrAddVolumeComponent<ColorAdjustments>(profile);
            colorAdjustments.postExposure.Override(0f);
            colorAdjustments.contrast.Override(0f);
            colorAdjustments.colorFilter.Override(Color.white);
            Tonemapping tonemapping = GetOrAddVolumeComponent<Tonemapping>(profile);
            tonemapping.mode.Override(TonemappingMode.ACES);
            EditorUtility.SetDirty(colorAdjustments);
            EditorUtility.SetDirty(tonemapping);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static VolumeProfile CreateOrUpdateNeonVolumeProfile()
        {
            VolumeProfile profile = GetOrCreateVolumeProfile(NeonVolumePath);
            Bloom bloom = GetOrAddVolumeComponent<Bloom>(profile);
            bloom.threshold.Override(1.5f);
            bloom.intensity.Override(0.24f);
            bloom.scatter.Override(0.68f);
            bloom.clamp.Override(8f);
            bloom.highQualityFiltering.Override(true);
            EditorUtility.SetDirty(bloom);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static VolumeProfile GetOrCreateVolumeProfile(string path)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = System.IO.Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(profile, path);
            }

            return profile;
        }

        private static T GetOrAddVolumeComponent<T>(VolumeProfile profile)
            where T : VolumeComponent
        {
            if (profile.TryGet(out T component))
            {
                return component;
            }

            return profile.Add<T>(true);
        }

        private static Mesh CreateOrUpdateBevelledCube()
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BevelledCubePath);
            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "BevelledCube"
                };
                AssetDatabase.CreateAsset(mesh, BevelledCubePath);
            }

            const int subdivisions = 8;
            const float halfSize = 0.5f;
            const float bevel = 0.12f;
            List<Vector3> vertices = new List<Vector3>(6 * 81);
            List<Vector3> normals = new List<Vector3>(6 * 81);
            List<Vector2> uvs = new List<Vector2>(6 * 81);
            List<int> triangles = new List<int>(6 * 8 * 8 * 6);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.right, Vector3.up,
                Vector3.forward, subdivisions, halfSize, bevel);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.left, Vector3.up,
                Vector3.back, subdivisions, halfSize, bevel);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.up, Vector3.forward,
                Vector3.right, subdivisions, halfSize, bevel);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.down, Vector3.forward,
                Vector3.left, subdivisions, halfSize, bevel);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.forward, Vector3.right,
                Vector3.up, subdivisions, halfSize, bevel);
            AddRoundedFace(vertices, normals, uvs, triangles, Vector3.back, Vector3.left,
                Vector3.up, subdivisions, halfSize, bevel);

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private static void AddRoundedFace(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uvs, List<int> triangles, Vector3 faceNormal, Vector3 axisU,
            Vector3 axisV, int subdivisions, float halfSize, float bevel)
        {
            int vertexStart = vertices.Count;
            float innerHalf = halfSize - bevel;
            for (int y = 0; y <= subdivisions; y++)
            {
                float v = y / (float)subdivisions;
                for (int x = 0; x <= subdivisions; x++)
                {
                    float u = x / (float)subdivisions;
                    Vector3 cubePoint = faceNormal * halfSize +
                                        axisU * ((u * 2f - 1f) * halfSize) +
                                        axisV * ((v * 2f - 1f) * halfSize);
                    Vector3 innerPoint = new Vector3(
                        Mathf.Clamp(cubePoint.x, -innerHalf, innerHalf),
                        Mathf.Clamp(cubePoint.y, -innerHalf, innerHalf),
                        Mathf.Clamp(cubePoint.z, -innerHalf, innerHalf));
                    Vector3 outward = cubePoint - innerPoint;
                    Vector3 normal = outward.sqrMagnitude > 0f
                        ? outward.normalized
                        : faceNormal;
                    vertices.Add(innerPoint + normal * bevel);
                    normals.Add(normal);
                    uvs.Add(new Vector2(u, v));
                }
            }

            int stride = subdivisions + 1;
            for (int y = 0; y < subdivisions; y++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int lowerLeft = vertexStart + y * stride + x;
                    int lowerRight = lowerLeft + 1;
                    int upperLeft = lowerLeft + stride;
                    int upperRight = upperLeft + 1;
                    triangles.Add(lowerLeft);
                    triangles.Add(lowerRight);
                    triangles.Add(upperLeft);
                    triangles.Add(lowerRight);
                    triangles.Add(upperRight);
                    triangles.Add(upperLeft);
                }
            }
        }

        private static void ConfigureEnvironment(Material skybox)
        {
            RenderSettings.fog = false;
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.defaultReflectionResolution = 256;
            RenderSettings.reflectionIntensity = 1f;
            RenderSettings.reflectionBounces = 2;
            RenderSettings.subtractiveShadowColor = new Color(0.2f, 0.22f, 0.25f);
        }

        private static ViewSetup CreateViews(Transform parent, Font font)
        {
            GameObject viewsRoot = new GameObject("Judging Views 1-5");
            viewsRoot.transform.SetParent(parent, false);
            Transform sweepTarget = CreateMarker(viewsRoot.transform, "Sweep Look Target",
                new Vector3(0f, 2f, 10f));
            Transform sweepNear = CreateMarker(viewsRoot.transform, "Mid Marker",
                new Vector3(0f, 15f, -29f));
            Transform sweepFar = CreateMarker(viewsRoot.transform, "Far Marker",
                new Vector3(0f, 22f, -45f));

            Camera midFar = CreateCamera(viewsRoot.transform, "View 1 - Mid Far Sweep",
                sweepNear.position, sweepTarget.position, 44f);
            Camera faceOn = CreateCamera(viewsRoot.transform, "View 2 - Face On Close",
                new Vector3(0f, 4.2f, -16f), new Vector3(0f, 1.5f, 2f), 46f);
            Camera grazing = CreateCamera(viewsRoot.transform, "View 3 - Grazing Close",
                new Vector3(-17f, 2.6f, -4.5f), new Vector3(0f, 1.2f, 1.5f), 48f);
            Camera transparency = CreateCamera(viewsRoot.transform,
                "View 4 - Glass Ice Backdrop", new Vector3(20f, 3.8f, -10f),
                new Vector3(20f, 1.5f, 1.4f), 42f);
            Camera neon = CreateCamera(viewsRoot.transform, "View 5 - Neon HDR Bloom",
                new Vector3(-20f, 3.4f, -9f), new Vector3(-20f, 1.6f, 0.6f), 42f);

            RbxProceduralMaterialsShowcase.CameraView[] views =
            {
                CreateCameraView("MID/FAR SHIMMER SWEEP", midFar, font),
                CreateCameraView("FACE-ON CLOSE-UP", faceOn, font),
                CreateCameraView("GRAZING CLOSE-UP", grazing, font),
                CreateCameraView("GLASS / ICE BACKDROP", transparency, font),
                CreateCameraView("NEON-ONLY HDR BLOOM", neon, font)
            };
            for (int index = 0; index < views.Length; index++)
            {
                views[index].Camera.gameObject.SetActive(index == 0);
            }

            return new ViewSetup(views, midFar, faceOn, transparency, neon, sweepNear,
                sweepFar, sweepTarget);
        }

        private static Transform CreateMarker(Transform parent, string name, Vector3 position)
        {
            GameObject marker = new GameObject(name);
            marker.transform.SetParent(parent, false);
            marker.transform.position = position;
            return marker.transform;
        }

        private static Camera CreateCamera(Transform parent, string name, Vector3 position,
            Vector3 target, float fieldOfView)
        {
            GameObject cameraObject = new GameObject(name);
            cameraObject.transform.SetParent(parent, false);
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = position;
            cameraObject.transform.LookAt(target);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 120f;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.allowDynamicResolution = false;
            UniversalAdditionalCameraData cameraData =
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = true;
            cameraData.requiresDepthOption = CameraOverrideOption.On;
            cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            return camera;
        }

        private static RbxProceduralMaterialsShowcase.CameraView CreateCameraView(string label,
            Camera camera, Font font)
        {
            GameObject labelObject = new GameObject("View Status - " + label);
            labelObject.transform.SetParent(camera.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, 2.15f, 7f);
            labelObject.transform.localRotation = Quaternion.identity;
            TextMesh text = ConfigureTextMesh(labelObject, font, 0.034f,
                new Color(0.88f, 0.92f, 0.96f));
            text.anchor = TextAnchor.UpperCenter;
            return new RbxProceduralMaterialsShowcase.CameraView(label, camera, text);
        }

        private static void CreatePostProcessing(Transform parent, Camera neonCamera,
            RigAssets assets)
        {
            GameObject globalObject = new GameObject("Fixed Exposure 0 + ACES Tonemapping");
            globalObject.transform.SetParent(parent, false);
            Volume globalVolume = globalObject.AddComponent<Volume>();
            globalVolume.isGlobal = true;
            globalVolume.priority = 0f;
            globalVolume.sharedProfile = assets.FixedVolume;

            GameObject neonObject = new GameObject("Neon Camera Local Bloom Only");
            neonObject.transform.SetParent(parent, false);
            neonObject.transform.position = neonCamera.transform.position;
            BoxCollider trigger = neonObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(6f, 6f, 6f);
            Volume neonVolume = neonObject.AddComponent<Volume>();
            neonVolume.isGlobal = false;
            neonVolume.priority = 10f;
            neonVolume.blendDistance = 0f;
            neonVolume.weight = 1f;
            neonVolume.sharedProfile = assets.NeonVolume;
        }

        private static void CreateStudioShell(Transform parent, RigAssets assets)
        {
            GameObject shell = new GameObject("Neutral Studio + AO Receivers");
            shell.transform.SetParent(parent, false);
            CreateBox(shell.transform, "Main Contact Shadow Receiver",
                new Vector3(0f, -0.3f, 8f), new Vector3(58f, 0.5f, 62f),
                assets.StudioMid);
            CreateBox(shell.transform, "Far Neutral Back Wall",
                new Vector3(0f, 8f, 31f), new Vector3(58f, 16f, 0.5f),
                assets.StudioDark);
            CreateBox(shell.transform, "Left Contrast Wall",
                new Vector3(-28f, 7f, 8f), new Vector3(0.5f, 14f, 45f),
                assets.StudioDark);
            CreateBox(shell.transform, "Right Contrast Wall",
                new Vector3(28f, 7f, 8f), new Vector3(0.5f, 14f, 45f),
                assets.StudioLight);
        }

        private static void CreateLightingAndReflections(Transform parent, RigAssets assets,
            Vector3 target)
        {
            GameObject lightingRoot = new GameObject("Neutral Reflection Environment + Grazing Key");
            lightingRoot.transform.SetParent(parent, false);

            Vector3 softboxPosition = new Vector3(-13f, 8.5f, -7f);
            GameObject softbox = CreateBox(lightingRoot.transform,
                "Large Grazing Key Reflection Card", softboxPosition,
                new Vector3(11f, 5f, 0.08f), assets.Softbox);
            softbox.transform.LookAt(target);

            GameObject keyObject = new GameObject("Large Grazing Key Light");
            keyObject.transform.SetParent(lightingRoot.transform, false);
            keyObject.transform.position = softboxPosition;
            keyObject.transform.LookAt(target);
            Light key = keyObject.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = Color.white;
            key.useColorTemperature = true;
            key.colorTemperature = 5600f;
            key.intensity = 1.65f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.9f;
            key.shadowAngle = 4f;
            RenderSettings.sun = key;

            GameObject fillObject = new GameObject("Neutral Low Fill");
            fillObject.transform.SetParent(lightingRoot.transform, false);
            fillObject.transform.position = new Vector3(15f, 8f, -2f);
            Light fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.color = new Color(0.84f, 0.9f, 1f);
            fill.intensity = 2.2f;
            fill.range = 24f;
            fill.shadows = LightShadows.None;

            CreateBox(lightingRoot.transform, "Dark Reflection Flag",
                new Vector3(15f, 7f, 7f), new Vector3(9f, 8f, 0.1f),
                assets.StudioDark).transform.LookAt(target);
            CreateBox(lightingRoot.transform, "Neutral Reflection Strip",
                new Vector3(0f, 11f, 20f), new Vector3(18f, 3f, 0.1f),
                assets.StudioLight).transform.LookAt(target);

            GameObject probeObject = new GameObject("Realtime Neutral Reflection Probe");
            probeObject.transform.SetParent(lightingRoot.transform, false);
            probeObject.transform.position = new Vector3(0f, 7f, 8f);
            ReflectionProbe probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            probe.hdr = true;
            probe.boxProjection = true;
            probe.renderDynamicObjects = true;
            probe.resolution = 256;
            probe.intensity = 1f;
            probe.size = new Vector3(56f, 20f, 60f);
        }

        private static Renderer[] CreateDiagnosticStation(Transform parent, RigAssets assets,
            Camera faceOnCamera, Font font, out TextMesh materialSelectionLabel)
        {
            GameObject station = new GameObject("A - Selectable Shape Scale Rotation Lab");
            station.transform.SetParent(parent, false);
            CreateBox(station.transform, "Diagnostic AO Receiver", new Vector3(0f, -0.02f, 2f),
                new Vector3(21f, 0.25f, 10f), assets.StudioLight);

            List<Renderer> renderers = new List<Renderer>(9);
            float[] shapeX = { -7f, -3.5f, 0f, 3.5f, 7f };
            string[] shapeLabels = { "CUBE", "BEVELLED CUBE", "SPHERE", "CYLINDER", "PLANE" };
            Renderer cube = CreatePrimitiveSample(station.transform, "Cube Sample",
                PrimitiveType.Cube, new Vector3(shapeX[0], 1.15f, 0f), Quaternion.identity,
                new Vector3(2.1f, 2.1f, 2.1f), null);
            Renderer bevelled = CreateMeshSample(station.transform, "Bevelled Cube Sample",
                assets.BevelledCube, new Vector3(shapeX[1], 1.15f, 0f),
                Quaternion.Euler(0f, 18f, 0f), new Vector3(2.1f, 2.1f, 2.1f), null);
            Renderer sphere = CreatePrimitiveSample(station.transform, "Sphere Sample",
                PrimitiveType.Sphere, new Vector3(shapeX[2], 1.15f, 0f), Quaternion.identity,
                new Vector3(2.2f, 2.2f, 2.2f), null);
            Renderer cylinder = CreatePrimitiveSample(station.transform, "Cylinder Sample",
                PrimitiveType.Cylinder, new Vector3(shapeX[3], 1.15f, 0f),
                Quaternion.Euler(0f, 18f, 0f), new Vector3(1.05f, 1.05f, 1.05f), null);
            Renderer plane = CreatePrimitiveSample(station.transform, "Plane Sample",
                PrimitiveType.Plane, new Vector3(shapeX[4], 1.35f, 0f),
                Quaternion.Euler(-90f, 0f, 0f), new Vector3(0.23f, 1f, 0.23f), null);
            renderers.Add(cube);
            renderers.Add(bevelled);
            renderers.Add(sphere);
            renderers.Add(cylinder);
            renderers.Add(plane);

            for (int index = 0; index < shapeLabels.Length; index++)
            {
                CreateText(station.transform, shapeLabels[index],
                    new Vector3(shapeX[index], 2.85f, -0.2f), faceOnCamera, font, 0.035f,
                    new Color(0.84f, 0.88f, 0.92f));
            }

            float[] scaleX = { -5.3f, -1.6f, 2.6f };
            float[] sizes = { 0.7f, 1.45f, 2.5f };
            string[] scaleLabels = { "SMALL 0.7M", "MEDIUM 1.45M", "LARGE 2.5M" };
            for (int index = 0; index < sizes.Length; index++)
            {
                float size = sizes[index];
                Renderer scaleSample = CreateMeshSample(station.transform,
                    scaleLabels[index] + " Scale Sample", assets.BevelledCube,
                    new Vector3(scaleX[index], size * 0.5f + 0.14f, 4f),
                    Quaternion.Euler(0f, 22f, 0f), new Vector3(size, size, size), null);
                renderers.Add(scaleSample);
                CreateText(station.transform, scaleLabels[index],
                    new Vector3(scaleX[index], 3.1f, 3.8f), faceOnCamera, font, 0.034f,
                    new Color(0.78f, 0.83f, 0.88f));
            }

            Renderer rotating = CreateMeshSample(station.transform, "Rotating Projection Sample",
                assets.BevelledCube, new Vector3(7f, 1.15f, 4f),
                Quaternion.Euler(18f, 0f, 12f), new Vector3(2f, 2f, 2f), null);
            ConstantRotator rotator = rotating.gameObject.AddComponent<ConstantRotator>();
            rotator.axisSource = ConstantRotator.AxisSource.Custom;
            rotator.customAxis = Vector3.up;
            rotator.spaceLocal = false;
            rotator.SetDegreesPerSecond(18f);
            renderers.Add(rotating);
            CreateText(station.transform, "ROTATING: CHECK SWIMMING",
                new Vector3(7f, 3.1f, 3.8f), faceOnCamera, font, 0.034f,
                new Color(0.92f, 0.78f, 0.5f));

            CreateText(station.transform, "A  PROJECTION / SCALE / ROTATION LAB",
                new Vector3(0f, 5.25f, 4.6f), faceOnCamera, font, 0.062f,
                new Color(0.9f, 0.93f, 0.96f));
            materialSelectionLabel = CreateText(station.transform,
                "SELECTED MATERIAL: METAL\nPART.COLOR: NEUTRAL WHITE",
                new Vector3(0f, 4.5f, 4.6f), faceOnCamera, font, 0.044f,
                new Color(0.58f, 0.78f, 0.94f));
            CreateText(station.transform,
                "Q/E OR ARROWS: MATERIAL  |  1-5: VIEW  |  SPACE: SWEEP",
                new Vector3(0f, 3.85f, 4.6f), faceOnCamera, font, 0.034f,
                new Color(0.62f, 0.66f, 0.7f));
            return renderers.ToArray();
        }

        private static void CreateNeutralCatalog(Transform parent, RigAssets assets, Camera camera,
            Font font, List<RbxProceduralMaterialsShowcase.Entry> entries)
        {
            GameObject catalog = new GameObject("B - Neutral Part Color Material Catalog");
            catalog.transform.SetParent(parent, false);
            int rowCount = Mathf.CeilToInt(Swatches.Length / (float)CatalogColumns);
            for (int index = 0; index < Swatches.Length; index++)
            {
                SwatchSpec spec = Swatches[index];
                int column = index % CatalogColumns;
                int row = index / CatalogColumns;
                float x = (column - (CatalogColumns - 1) * 0.5f) * CatalogColumnSpacing;
                float z = 13f + row * CatalogRowSpacing;
                CreateBox(catalog.transform, spec.Label + " AO Pedestal",
                    new Vector3(x, 0.08f, z), new Vector3(3.3f, 0.16f, 3.3f),
                    assets.StudioDark);
                Renderer renderer = CreatePrimitiveSample(catalog.transform,
                    spec.Label + " Material Sample", spec.Shape,
                    new Vector3(x, 1.18f, z), Quaternion.Euler(0f, 24f, 0f),
                    CatalogSampleScale(spec.Shape), null);
                entries.Add(new RbxProceduralMaterialsShowcase.Entry(renderer,
                    spec.MaterialName, spec.MaterialValue));
                CreateText(catalog.transform, spec.Label, new Vector3(x, 2.95f, z - 0.2f),
                    camera, font, 0.048f, new Color(0.9f, 0.92f, 0.95f));
            }

            CreateText(catalog.transform, "B  ALL MATERIALS / SAME NEUTRAL PART.COLOR",
                new Vector3(0f, 4.4f, 10.2f), camera, font, 0.068f,
                new Color(0.88f, 0.92f, 0.96f));
            CreateText(catalog.transform,
                "FIXED EXPOSURE 0 + ACES  |  PROCEDURAL SKY (NO HDRI ASSET AVAILABLE)",
                new Vector3(0f, 3.7f, 10.2f), camera, font, 0.038f,
                new Color(0.56f, 0.68f, 0.78f));
        }

        private static void CreateTransparencyStage(Transform parent, RigAssets assets,
            Camera camera, Font font, List<RbxProceduralMaterialsShowcase.Entry> entries)
        {
            GameObject stage = new GameObject("C - Glass Ice Backdrop Lab");
            stage.transform.SetParent(parent, false);
            CreateBox(stage.transform, "Transparency Contact Receiver",
                new Vector3(20f, 0f, 1f), new Vector3(10f, 0.25f, 9f), assets.StudioLight);
            for (int index = 0; index < 7; index++)
            {
                float x = 17f + index;
                float height = index % 2 == 0 ? 3.8f : 2.4f;
                Material material = index % 2 == 0 ? assets.StudioAccentA : assets.StudioAccentB;
                CreateBox(stage.transform, "Backdrop Detail " + (index + 1),
                    new Vector3(x, height * 0.5f, 3.6f),
                    new Vector3(0.7f, height, 0.55f), material);
            }

            CreateBox(stage.transform, "Backdrop Horizontal Reference",
                new Vector3(20f, 2.15f, 3.25f), new Vector3(8.5f, 0.22f, 0.22f),
                assets.StudioLight);
            Renderer glass = CreatePrimitiveSample(stage.transform, "Glass With Detail Behind",
                PrimitiveType.Cube, new Vector3(18f, 1.45f, 0.7f),
                Quaternion.Euler(0f, 18f, 0f), new Vector3(2.4f, 2.8f, 0.55f), null);
            Renderer ice = CreatePrimitiveSample(stage.transform, "Ice With Detail Behind",
                PrimitiveType.Sphere, new Vector3(22f, 1.45f, 0.7f), Quaternion.identity,
                new Vector3(2.6f, 2.6f, 2.6f), null);
            entries.Add(new RbxProceduralMaterialsShowcase.Entry(glass, "Glass", 1568));
            entries.Add(new RbxProceduralMaterialsShowcase.Entry(ice, "Ice", 1536));
            CreateText(stage.transform, "C  GLASS / ICE: DETAIL MUST REMAIN VISIBLE",
                new Vector3(20f, 5f, 3.8f), camera, font, 0.052f,
                new Color(0.86f, 0.92f, 0.98f));
            CreateText(stage.transform, "GLASS", new Vector3(18f, 3.35f, 0.5f), camera,
                font, 0.044f, Color.white);
            CreateText(stage.transform, "ICE", new Vector3(22f, 3.35f, 0.5f), camera,
                font, 0.044f, Color.white);
        }

        private static void CreateNeonStage(Transform parent, RigAssets assets, Camera camera,
            Font font, List<RbxProceduralMaterialsShowcase.Entry> entries)
        {
            GameObject stage = new GameObject("D - Neon Only HDR Bloom Lab");
            stage.transform.SetParent(parent, false);
            CreateBox(stage.transform, "Neon Contact Receiver", new Vector3(-20f, 0f, 1f),
                new Vector3(9f, 0.25f, 8f), assets.StudioDark);
            CreateBox(stage.transform, "Neon Dark Backdrop", new Vector3(-20f, 3f, 4f),
                new Vector3(9f, 6f, 0.35f), assets.StudioDark);
            Renderer neon = CreatePrimitiveSample(stage.transform, "Neon Bloom Sample",
                PrimitiveType.Cylinder, new Vector3(-20f, 1.6f, 0.8f),
                Quaternion.Euler(0f, 20f, 0f), new Vector3(1.25f, 1.5f, 1.25f), null);
            entries.Add(new RbxProceduralMaterialsShowcase.Entry(neon, "Neon", 288));
            CreateText(stage.transform, "D  NEON-ONLY LOCAL HDR BLOOM",
                new Vector3(-20f, 5f, 3.8f), camera, font, 0.055f,
                new Color(0.72f, 0.92f, 1f));
            CreateText(stage.transform, "BLOOM IS DISABLED IN VIEWS 1-4",
                new Vector3(-20f, 4.35f, 3.8f), camera, font, 0.036f,
                new Color(0.54f, 0.66f, 0.74f));
        }

        private static GameObject CreateBox(Transform parent, string name, Vector3 position,
            Vector3 scale, Material material)
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gameObject.name = name;
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.position = position;
            gameObject.transform.localScale = scale;
            Renderer renderer = gameObject.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            RemoveCollider(gameObject);
            return gameObject;
        }

        private static Renderer CreatePrimitiveSample(Transform parent, string name,
            PrimitiveType shape, Vector3 position, Quaternion rotation, Vector3 scale,
            Material material)
        {
            GameObject gameObject = GameObject.CreatePrimitive(shape);
            gameObject.name = name;
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.position = position;
            gameObject.transform.rotation = rotation;
            gameObject.transform.localScale = scale;
            RemoveCollider(gameObject);
            Renderer renderer = gameObject.GetComponent<Renderer>();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }

            return renderer;
        }

        private static Renderer CreateMeshSample(Transform parent, string name, Mesh mesh,
            Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.position = position;
            gameObject.transform.rotation = rotation;
            gameObject.transform.localScale = scale;
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        private static void RemoveCollider(GameObject gameObject)
        {
            Collider collider = gameObject.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        private static Vector3 CatalogSampleScale(PrimitiveType shape)
        {
            if (shape == PrimitiveType.Cube)
            {
                return new Vector3(2.65f, 1.7f, 2.65f);
            }

            if (shape == PrimitiveType.Cylinder)
            {
                return new Vector3(1.15f, 0.8f, 1.15f);
            }

            if (shape == PrimitiveType.Capsule)
            {
                return new Vector3(1.12f, 1.12f, 1.12f);
            }

            return new Vector3(2.35f, 2.35f, 2.35f);
        }

        private static TextMesh CreateText(Transform parent, string content, Vector3 position,
            Camera camera, Font font, float characterSize, Color color)
        {
            GameObject labelObject = new GameObject(content.Replace('\n', ' ') + " Label");
            labelObject.transform.SetParent(parent, true);
            labelObject.transform.position = position;
            labelObject.transform.rotation = Quaternion.LookRotation(
                position - camera.transform.position);
            TextMesh text = ConfigureTextMesh(labelObject, font, characterSize, color);
            text.text = content;
            return text;
        }

        private static TextMesh ConfigureTextMesh(GameObject labelObject, Font font,
            float characterSize, Color color)
        {
            TextMesh text = labelObject.AddComponent<TextMesh>();
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
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return text;
        }

        private static RbxProceduralMaterialsShowcase.MaterialSelection[]
            CreateMaterialSelections()
        {
            RbxProceduralMaterialsShowcase.MaterialSelection[] selections =
                new RbxProceduralMaterialsShowcase.MaterialSelection[Swatches.Length];
            for (int index = 0; index < Swatches.Length; index++)
            {
                SwatchSpec spec = Swatches[index];
                selections[index] = new RbxProceduralMaterialsShowcase.MaterialSelection(
                    spec.MaterialName, spec.MaterialValue);
            }

            return selections;
        }

        private static int FindMaterialIndex(string materialName)
        {
            for (int index = 0; index < Swatches.Length; index++)
            {
                if (string.Equals(Swatches[index].MaterialName, materialName,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return 0;
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

        private readonly struct RigAssets
        {
            public readonly Material Skybox;
            public readonly Material StudioDark;
            public readonly Material StudioMid;
            public readonly Material StudioLight;
            public readonly Material StudioAccentA;
            public readonly Material StudioAccentB;
            public readonly Material Softbox;
            public readonly Mesh BevelledCube;
            public readonly VolumeProfile FixedVolume;
            public readonly VolumeProfile NeonVolume;

            public RigAssets(Material skybox, Material studioDark, Material studioMid,
                Material studioLight, Material studioAccentA, Material studioAccentB,
                Material softbox, Mesh bevelledCube, VolumeProfile fixedVolume,
                VolumeProfile neonVolume)
            {
                Skybox = skybox;
                StudioDark = studioDark;
                StudioMid = studioMid;
                StudioLight = studioLight;
                StudioAccentA = studioAccentA;
                StudioAccentB = studioAccentB;
                Softbox = softbox;
                BevelledCube = bevelledCube;
                FixedVolume = fixedVolume;
                NeonVolume = neonVolume;
            }
        }

        private readonly struct ViewSetup
        {
            public readonly RbxProceduralMaterialsShowcase.CameraView[] Views;
            public readonly Camera MidFarCamera;
            public readonly Camera FaceOnCamera;
            public readonly Camera TransparencyCamera;
            public readonly Camera NeonCamera;
            public readonly Transform SweepNear;
            public readonly Transform SweepFar;
            public readonly Transform SweepTarget;

            public ViewSetup(RbxProceduralMaterialsShowcase.CameraView[] views,
                Camera midFarCamera, Camera faceOnCamera, Camera transparencyCamera,
                Camera neonCamera, Transform sweepNear, Transform sweepFar,
                Transform sweepTarget)
            {
                Views = views;
                MidFarCamera = midFarCamera;
                FaceOnCamera = faceOnCamera;
                TransparencyCamera = transparencyCamera;
                NeonCamera = neonCamera;
                SweepNear = sweepNear;
                SweepFar = sweepFar;
                SweepTarget = sweepTarget;
            }
        }
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CoreAI.Demos.BlockWorld
{
    /// <summary>
    /// Builds the Block World demo scene from the one-click play world preset: the play world bootstrap with the
    /// controls, look and overlay chat slots, no baseplate (the voxel Terrain is the ground), this scene's own mods
    /// store with the <c>Samples/Block World</c> folder switched on, and AI from Lua allowed for this scene only.
    /// The spawn area is generated once by the world mod itself and baked as a preview, so the scene shows the world
    /// while it is open in the Editor; at Play the mods generate it again from the same seed.
    /// </summary>
    /// <remarks>
    /// WHY generated rather than hand-edited: the scene is then a function of this file and of the world mod, so it
    /// is rebuilt in one step after a seed or a component changes. Batch mode:
    /// <c>-executeMethod CoreAI.Demos.BlockWorld.BlockWorldDemoSceneBuilder.BuildScene</c>.
    /// </remarks>
    public static class BlockWorldDemoSceneBuilder
    {
        /// <summary>The scene this builder writes.</summary>
        public const string ScenePath = "Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity";

        /// <summary>The scene's own mods store namespace, so its saved world never opens in another demo.</summary>
        public const string StoreId = "block-world-demo";

        /// <summary>Where the baked preview meshes and materials are written.</summary>
        public const string PreviewFolder = "Assets/CoreAI.Demos/BlockWorld/Preview";

        private const string WorldModPath = "Assets/CoreAIMods/Runtime/Resources/CoreAIMods/BlockWorld/blockworld_world.lua";
        private const string PreviewMeshesPath = PreviewFolder + "/BlockWorldPreviewMeshes.asset";
        private const string PreviewMaterialsFolder = PreviewFolder + "/Materials";
        private const string EditorOnlyTag = "EditorOnly";
        private const string PlayWorldObjectName = "CoreAI Play World";
        private const int PreviewRadius = 1;
        private const float FirstViewEyeHeightStuds = 1.5f;
        private const float FirstViewPitchDegrees = 12f;
        private const int MaxBakeFrames = 4000;
        private const double FrameSeconds = 1d / 60d;

        /// <summary>Creates or replaces the demo scene, after asking to save the open one.</summary>
        [MenuItem("CoreAI/Demos/Build Block World Demo", priority = 32)]
        public static void BuildSceneMenuItem()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                BuildScene();
            }
        }

        /// <summary>Creates or replaces the demo scene and its baked preview; throws when the bake fails.</summary>
        public static void BuildScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PreviewBake bake = BakeSpawnArea();
            CreateSun();
            Camera camera = CreateCamera(bake.Spawn, bake.SpawnLook);
            CreatePlayWorld(camera);
            CreatePreview(bake);

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? ".");
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new InvalidOperationException("[BlockWorldDemo] Failed to save " + ScenePath + ".");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[BlockWorldDemo] Built " + ScenePath + ": " + bake.Chunks.Count + " preview chunks, "
                      + bake.Columns + " columns, spawn " + bake.Spawn + ".");
        }

        private static void CreateSun()
        {
            GameObject sunObject = new("Sun");
            sunObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
        }

        /// <remarks>
        /// WHY at the eyes: the default camera takes its first look direction from the scene camera, so this is the
        /// first view a player gets, and the view the scene shows in the Editor without Play.
        /// </remarks>
        private static Camera CreateCamera(RbxVector3 spawn, RbxVector3 look)
        {
            GameObject cameraObject = new("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 600f;
            cameraObject.AddComponent<AudioListener>();
            RbxVector3 eye = spawn + new RbxVector3(0f, FirstViewEyeHeightStuds, 0f);
            RbxVector3 ahead = eye + look * 10f + new RbxVector3(0f, -10f * Mathf.Tan(FirstViewPitchDegrees * Mathf.Deg2Rad), 0f);
            cameraObject.transform.position = RbxSpace.ToUnity(eye);
            cameraObject.transform.LookAt(RbxSpace.ToUnity(ahead));
            return camera;
        }

        private static void CreatePlayWorld(Camera camera)
        {
            GameObject root = new(PlayWorldObjectName);
            CoreAiPlayWorldBootstrap bootstrap = root.AddComponent<CoreAiPlayWorldBootstrap>();
            CoreAiPlayerControlsModule controls = root.AddComponent<CoreAiPlayerControlsModule>();
            CoreAiPlayWorldLookModule look = root.AddComponent<CoreAiPlayWorldLookModule>();
            CoreAiOverlayChatModule chat = root.AddComponent<CoreAiOverlayChatModule>();
            BlockWorldDemoModule demo = root.AddComponent<BlockWorldDemoModule>();
            MonoBehaviour hub = AddHub(root);

            SerializedObject serialized = new(bootstrap);
            Assign(serialized, "worldCamera", camera);
            Assign(serialized, "controlsModule", controls);
            Assign(serialized, "lookModule", look);
            Assign(serialized, "worldContentModule", null);
            Assign(serialized, "overlayChatModule", chat);
            SerializedProperty storeId = serialized.FindProperty("modStoreId");
            storeId.stringValue = StoreId;
            SerializedProperty additional = serialized.FindProperty("additionalModules");
            additional.arraySize = hub != null ? 2 : 1;
            additional.GetArrayElementAtIndex(0).objectReferenceValue = demo;
            if (hub != null)
            {
                additional.GetArrayElementAtIndex(1).objectReferenceValue = hub;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bootstrap);
        }

        /// <summary>The Hub on F2 (AI settings, Test connection, Mods); null without the Hub package.</summary>
        private static MonoBehaviour AddHub(GameObject root)
        {
#if COREAI_HAS_HUB
            const string hubPrefabPath = "Assets/CoreAIHub/Runtime/CoreAiHub.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(hubPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("[BlockWorldDemo] The Hub prefab is missing: " + hubPrefabPath);
            }

            CoreAI.Ai.Hub.CoreAiPlayWorldHubModule hub = root.AddComponent<CoreAI.Ai.Hub.CoreAiPlayWorldHubModule>();
            hub.SetHubPrefab(prefab);
            EditorUtility.SetDirty(hub);
            return hub;
#else
            return null;
#endif
        }

        private static void Assign(SerializedObject serialized, string field, Object value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property == null)
            {
                throw new InvalidOperationException("[BlockWorldDemo] The play world bootstrap has no '" + field
                                                    + "' field any more; update this builder.");
            }

            property.objectReferenceValue = value;
        }

        // ---- the baked preview ------------------------------------------------------------------------------

        private sealed class PreviewChunk
        {
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
            public Mesh Mesh;
            public Material[] Materials;
            public bool CastsShadows;
        }

        private sealed class PreviewBake
        {
            public readonly List<PreviewChunk> Chunks = new();
            public RbxVector3 Spawn;
            public RbxVector3 SpawnLook = new(0f, 0f, -1f);
            public int Columns;
        }

        private sealed class BakeLogger : IGameLogger
        {
            private readonly List<string> _problems;

            public BakeLogger(List<string> problems)
            {
                _problems = problems;
            }

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                _problems.Add("warning: " + message);
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                _problems.Add("error: " + message);
            }
        }

        /// <summary>
        /// Runs the world mod headless until the columns around the spawn exist, then meshes them with the same
        /// Terrain renderer the world host uses and keeps copies of the meshes and their materials.
        /// </summary>
        private static PreviewBake BakeSpawnArea()
        {
            TextAsset worldMod = AssetDatabase.LoadAssetAtPath<TextAsset>(WorldModPath);
            if (worldMod == null)
            {
                throw new InvalidOperationException("[BlockWorldDemo] Missing " + WorldModPath + ".");
            }

            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            List<string> problems = new();
            List<string> hostNotes = new();
            PreviewBake bake = new();
            LuaCsRbxApiBindings roblox = new(log: line => hostNotes.Add(line));
            GameObject holder = null;
            RbxTerrainRenderer renderer = null;
            try
            {
                LuaLogService modLog = new();
                modLog.EntryAppended += entry =>
                {
                    if (entry.Level != LuaLogLevel.Print)
                    {
                        problems.Add(entry.ModId + " " + entry.Level + ": " + entry.Message);
                    }
                };
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new BakeLogger(hostNotes),
                    LogService = modLog,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = roblox
                });
                RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
                workspace.SetAttribute("BlockWorldViewRadius", (double)PreviewRadius);
                ActorContext baker = new LocalActorIdentityProvider("blockworld-preview-bake").GetActorContext(null);
                stack.Runtime.LoadMod(baker, "blockworld_world", worldMod.text, LuaCapabilities.All, false);

                int wanted = ColumnsWithin(PreviewRadius);
                for (int frame = 0; frame < MaxBakeFrames && Columns(workspace) < wanted && problems.Count == 0;
                     frame++)
                {
                    roblox.Scheduler.Advance(FrameSeconds);
                }

                bake.Columns = Columns(workspace);
                if (problems.Count > 0 || bake.Columns < wanted)
                {
                    throw new InvalidOperationException("[BlockWorldDemo] The spawn area did not generate: "
                                                        + bake.Columns + " of " + wanted + " columns; mod log: "
                                                        + string.Join(" | ", problems) + "; host: "
                                                        + string.Join(" | ", hostNotes));
                }

                bake.Spawn = (RbxVector3)workspace.GetAttribute("BlockWorldSpawn");
                if (workspace.GetAttribute("BlockWorldSpawnLook") is RbxVector3 look)
                {
                    bake.SpawnLook = look;
                }
                RbxTerrain terrain = RbxTerrainBootstrap.Find(workspace);
                holder = new GameObject("Block World Bake");
                renderer = new RbxTerrainRenderer(terrain, holder.transform);
                while (renderer.PendingChunkCount > 0)
                {
                    renderer.ProcessDirty(int.MaxValue, double.MaxValue);
                }

                KeepChunks(renderer.Root, bake);
                return bake;
            }
            finally
            {
                renderer?.Dispose();
                if (holder != null)
                {
                    Object.DestroyImmediate(holder);
                }

                roblox.Dispose();
            }
        }

        private static int ColumnsWithin(int radius)
        {
            int count = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    count += dx * dx + dz * dz <= radius * radius + 1 ? 1 : 0;
                }
            }

            return count;
        }

        private static int Columns(RbxInstance workspace)
        {
            object value = workspace.GetAttribute("BlockWorldColumns");
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static void KeepChunks(GameObject root, PreviewBake bake)
        {
            if (AssetDatabase.IsValidFolder(PreviewFolder))
            {
                AssetDatabase.DeleteAsset(PreviewFolder);
            }

            AssetDatabase.CreateFolder("Assets/CoreAI.Demos/BlockWorld", "Preview");
            AssetDatabase.CreateFolder(PreviewFolder, "Materials");
            Dictionary<Material, Material> savedMaterials = new();
            Mesh container = null;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer source = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || source == null || !source.enabled || filter.sharedMesh.vertexCount == 0)
                {
                    continue;
                }

                Mesh mesh = Object.Instantiate(filter.sharedMesh);
                mesh.name = filter.sharedMesh.name;
                if (container == null)
                {
                    AssetDatabase.CreateAsset(mesh, PreviewMeshesPath);
                    container = mesh;
                }
                else
                {
                    AssetDatabase.AddObjectToAsset(mesh, container);
                }

                Material[] materials = source.sharedMaterials;
                Material[] saved = new Material[materials.Length];
                for (int index = 0; index < materials.Length; index++)
                {
                    saved[index] = SaveMaterial(materials[index], savedMaterials);
                }

                bake.Chunks.Add(new PreviewChunk
                {
                    Name = filter.gameObject.name,
                    Position = filter.transform.position,
                    Rotation = filter.transform.rotation,
                    Mesh = mesh,
                    Materials = saved,
                    CastsShadows = source.shadowCastingMode != ShadowCastingMode.Off
                });
            }
        }

        private static Material SaveMaterial(Material source, Dictionary<Material, Material> saved)
        {
            if (source == null)
            {
                return null;
            }

            if (saved.TryGetValue(source, out Material existing))
            {
                return existing;
            }

            Material copy = new(source) { name = source.name };
            string fileName = string.Join("_", source.name.Split(Path.GetInvalidFileNameChars()));
            string path = AssetDatabase.GenerateUniqueAssetPath(PreviewMaterialsFolder + "/" + fileName + ".mat");
            AssetDatabase.CreateAsset(copy, path);
            foreach (string property in copy.GetTexturePropertyNames())
            {
                Texture texture = copy.GetTexture(property);
                if (texture != null && !EditorUtility.IsPersistent(texture))
                {
                    Debug.LogWarning("[BlockWorldDemo] Preview material " + source.name + " uses a texture made at "
                                     + "runtime (" + property + "); the preview draws it without that texture.");
                }
            }

            saved.Add(source, copy);
            return copy;
        }

        private static void CreatePreview(PreviewBake bake)
        {
            GameObject preview = new("Block World Preview (Editor only)") { tag = EditorOnlyTag };
            preview.AddComponent<BlockWorldEditorPreview>();
            foreach (PreviewChunk chunk in bake.Chunks)
            {
                GameObject chunkObject = new(chunk.Name);
                chunkObject.transform.SetParent(preview.transform, false);
                chunkObject.transform.SetPositionAndRotation(chunk.Position, chunk.Rotation);
                chunkObject.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
                MeshRenderer renderer = chunkObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = chunk.Materials;
                renderer.shadowCastingMode = chunk.CastsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }
    }
}
#endif

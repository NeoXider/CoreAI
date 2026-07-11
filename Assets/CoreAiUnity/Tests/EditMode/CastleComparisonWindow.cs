#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Editor window that assembles a "model castle comparison" scene from the castle prefabs the
    /// game-creation benchmark exports to <c>Assets/Benchmark/&lt;model&gt;/&lt;scenario&gt;.prefab</c>.
    ///
    /// Each top-level folder under <c>Assets/Benchmark</c> is one model; a folder may hold several
    /// castle prefabs (repeated runs produce numbered variants). This window groups them by model,
    /// lets the user include/exclude each model and pick which castle to use (defaulting to the newest
    /// by file write time), then lays the chosen castles out in a non-overlapping grid with a 3D text
    /// label above each one naming the model and its parsed <c>BuiltBy_..._&lt;score&gt;of100</c> score.
    /// </summary>
    public sealed class CastleComparisonWindow : EditorWindow
    {
        // Where the benchmark exports castles (see GameCreationBenchmarkHarness.SaveCastlePrefab).
        private const string BenchmarkRoot = "Assets/Benchmark";

        // Non-castle subfolder written beside each prefab; never a model on its own.
        private const string MaterialsFolder = "Materials";

        // Root object that owns everything this window spawns, so a rebuild can wipe it cleanly.
        private const string ComparisonRootName = "CastleComparison";

        // Parses the self-identifying child the harness adds: "BuiltBy_<model>__<score>of100".
        private static readonly Regex BuiltByPattern =
            new(@"^BuiltBy_(?<model>.+)__(?<score>\d+)of100$", RegexOptions.Compiled);

        private readonly List<ModelEntry> _models = new();
        private Vector2 _scroll;

        // Layout tuning, surfaced in the window.
        private float _gap = 10f;
        private int _maxPerRow = 6;
        private float _labelScale = 1f;
        private bool _createNewScene = true;

        [MenuItem("CoreAI/Benchmarks/Castle Comparison Scene...", priority = 160)]
        public static void Open()
        {
            CastleComparisonWindow window = GetWindow<CastleComparisonWindow>("Castle Comparison");
            window.minSize = new Vector2(420f, 360f);
            window.Rescan();
            window.Show();
        }

        private void OnEnable()
        {
            if (_models.Count == 0)
            {
                Rescan();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Castle Comparison Scene Builder", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Builds a scene from the benchmark's exported castle prefabs (Assets/Benchmark/<model>/). " +
                "One castle per model, each labelled with the model name and its score.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rescan", GUILayout.Width(80f)))
                {
                    Rescan();
                }

                if (GUILayout.Button("Select All Models"))
                {
                    SetAll(true);
                }

                if (GUILayout.Button("Clear"))
                {
                    SetAll(false);
                }
            }

            EditorGUILayout.Space();
            DrawLayoutOptions();
            EditorGUILayout.Space();

            if (_models.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No castle prefabs found under {BenchmarkRoot}/<model>/. Run the game-creation " +
                    "benchmark (free-build scenario) first — it exports the castles this tool consumes.",
                    MessageType.Warning);
                return;
            }

            DrawModelList();

            EditorGUILayout.Space();
            int selectedCount = _models.Count(m => m.Include && m.Castles.Count > 0);
            using (new EditorGUI.DisabledScope(selectedCount == 0))
            {
                if (GUILayout.Button($"Build Scene ({selectedCount} model(s))", GUILayout.Height(30f)))
                {
                    BuildScene(_models.Where(m => m.Include && m.Castles.Count > 0));
                }
            }

            if (GUILayout.Button("Build scene from ALL models (latest each)", GUILayout.Height(24f)))
            {
                foreach (ModelEntry m in _models)
                {
                    if (m.Castles.Count > 0)
                    {
                        m.Include = true;
                        m.SelectedIndex = 0; // Castles are pre-sorted newest-first.
                    }
                }

                BuildScene(_models.Where(m => m.Include && m.Castles.Count > 0));
            }
        }

        private void DrawLayoutOptions()
        {
            EditorGUILayout.LabelField("Layout", EditorStyles.miniBoldLabel);
            _gap = EditorGUILayout.Slider(
                new GUIContent("Gap (units)", "Extra empty space between adjacent castles."),
                _gap, 0f, 60f);
            _maxPerRow = EditorGUILayout.IntSlider(
                new GUIContent("Max per row", "Castles per row before wrapping into a grid."),
                _maxPerRow, 1, 20);
            _labelScale = EditorGUILayout.Slider(
                new GUIContent("Label size", "Multiplier for the 3D model-name labels."),
                _labelScale, 0.25f, 5f);
            _createNewScene = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Build in a fresh empty scene",
                    "On: opens a new empty scene (prompts to save the current one). " +
                    "Off: builds into the active scene, replacing any previous CastleComparison root."),
                _createNewScene);
        }

        private void DrawModelList()
        {
            EditorGUILayout.LabelField($"Models ({_models.Count})", EditorStyles.miniBoldLabel);
            using EditorGUILayout.ScrollViewScope scope = new(_scroll);
            _scroll = scope.scrollPosition;

            foreach (ModelEntry model in _models)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        model.Include = EditorGUILayout.ToggleLeft(
                            model.DisplayName, model.Include, EditorStyles.boldLabel);
                    }

                    if (model.Castles.Count == 0)
                    {
                        EditorGUILayout.LabelField("(no castle prefabs)", EditorStyles.miniLabel);
                        continue;
                    }

                    using (new EditorGUI.DisabledScope(!model.Include))
                    {
                        model.SelectedIndex = EditorGUILayout.Popup(
                            "Castle", model.SelectedIndex, model.CastleLabels);

                        CastleFile chosen = model.Castles[model.SelectedIndex];
                        EditorGUILayout.LabelField(
                            "Modified", chosen.WriteTimeUtc.ToLocalTime()
                                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                            EditorStyles.miniLabel);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ scanning

        private void Rescan()
        {
            _models.Clear();

            if (!AssetDatabase.IsValidFolder(BenchmarkRoot))
            {
                Repaint();
                return;
            }

            // Top-level subfolders of Assets/Benchmark are the models.
            foreach (string modelFolder in AssetDatabase.GetSubFolders(BenchmarkRoot).OrderBy(f => f))
            {
                string modelName = Path.GetFileName(modelFolder);
                if (string.Equals(modelName, MaterialsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ModelEntry entry = new()
                {
                    ModelName = modelName,
                    FolderPath = modelFolder,
                };

                // Only .prefab files that live directly in the model folder (skip the Materials subfolder
                // and any nested assets). FindAssets recurses, so filter by the immediate parent folder.
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { modelFolder }))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string parent = assetPath[..assetPath.LastIndexOf('/')];
                    if (!string.Equals(parent, modelFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DateTime writeTime;
                    try
                    {
                        writeTime = File.GetLastWriteTimeUtc(Path.GetFullPath(assetPath));
                    }
                    catch
                    {
                        writeTime = DateTime.MinValue;
                    }

                    entry.Castles.Add(new CastleFile
                    {
                        AssetPath = assetPath,
                        FileName = Path.GetFileName(assetPath),
                        WriteTimeUtc = writeTime,
                    });
                }

                // Newest first, so index 0 is the default "latest" selection.
                entry.Castles.Sort((a, b) => b.WriteTimeUtc.CompareTo(a.WriteTimeUtc));
                entry.RebuildLabels();

                if (entry.Castles.Count > 0)
                {
                    _models.Add(entry);
                }
            }

            Repaint();
        }

        private void SetAll(bool include)
        {
            foreach (ModelEntry model in _models)
            {
                model.Include = include && model.Castles.Count > 0;
            }

            Repaint();
        }

        // ------------------------------------------------------------------ building

        private void BuildScene(IEnumerable<ModelEntry> selection)
        {
            List<ModelEntry> chosen = selection.ToList();
            if (chosen.Count == 0)
            {
                return;
            }

            if (_createNewScene)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }

                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                CreateSceneEssentials(scene);
            }

            // A single dedicated root so a rebuild wipes only what we own.
            GameObject existingRoot = GameObject.Find(ComparisonRootName);
            if (existingRoot != null)
            {
                DestroyImmediate(existingRoot);
            }

            GameObject root = new(ComparisonRootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Castle Comparison");

            // 1) Instantiate every chosen castle first, then measure so the grid cell can fit the widest.
            List<Placement> placements = new();
            foreach (ModelEntry model in chosen)
            {
                CastleFile file = model.Castles[Mathf.Clamp(model.SelectedIndex, 0, model.Castles.Count - 1)];
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(file.AssetPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"[CastleComparison] could not load prefab: {file.AssetPath}");
                    continue;
                }

                GameObject instance = PrefabUtility.InstantiatePrefab(prefab, root.transform) as GameObject;
                if (instance == null)
                {
                    continue;
                }

                instance.name = model.ModelName;
                instance.transform.localPosition = Vector3.zero;

                placements.Add(new Placement
                {
                    Model = model,
                    Instance = instance,
                    Bounds = ComputeBounds(instance),
                    Score = ParseScore(instance),
                });
            }

            if (placements.Count == 0)
            {
                Debug.LogWarning("[CastleComparison] nothing to place.");
                return;
            }

            // 2) Uniform grid sized to the widest / deepest castle so nothing overlaps.
            float cellWidth = placements.Max(p => p.Bounds.size.x) + _gap;
            float cellDepth = placements.Max(p => p.Bounds.size.z) + _gap;
            float maxHeight = placements.Max(p => p.Bounds.size.y);
            int columns = Mathf.Clamp(Mathf.Min(_maxPerRow, placements.Count), 1, placements.Count);

            Bounds overall = new(Vector3.zero, Vector3.zero);
            bool overallInit = false;

            for (int i = 0; i < placements.Count; i++)
            {
                Placement p = placements[i];
                int col = i % columns;
                int row = i / columns;

                // Cell centre (rows extend toward +Z, away from the front camera at -Z).
                float targetX = col * cellWidth;
                float targetZ = row * cellDepth;

                // Shift so the castle's footprint centre sits on the cell centre and it rests on y = 0.
                Vector3 delta = new(
                    targetX - p.Bounds.center.x,
                    -p.Bounds.min.y,
                    targetZ - p.Bounds.center.z);
                p.Instance.transform.position += delta;

                Bounds placed = p.Bounds;
                placed.center += delta;
                CreateLabel(root.transform, p, placed, maxHeight);

                if (!overallInit)
                {
                    overall = placed;
                    overallInit = true;
                }
                else
                {
                    overall.Encapsulate(placed);
                }
            }

            // 3) Frame the whole comparison for the user.
            FrameCamera(overall);
            Selection.activeGameObject = root;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Frame(overall, false);
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[CastleComparison] built scene with {placements.Count} castle(s).");
        }

        private static void CreateSceneEssentials(Scene scene)
        {
            // A directional light so URP/Lit castle materials aren't rendered black.
            GameObject lightGo = new("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            SceneManager.MoveGameObjectToScene(lightGo, scene);

            // A camera so the scene is immediately viewable in play/game view.
            GameObject cameraGo = new("Main Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.AddComponent<Camera>();
            SceneManager.MoveGameObjectToScene(cameraGo, scene);
        }

        private void FrameCamera(Bounds bounds)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            // Pull back along -Z and up so the whole row/grid is in frame, then look at its centre.
            float radius = Mathf.Max(bounds.extents.magnitude, 1f);
            Vector3 pos = bounds.center + new Vector3(0f, radius * 0.6f, -radius * 2.4f);
            cam.transform.position = pos;
            cam.transform.LookAt(bounds.center);
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, radius * 6f);
        }

        private void CreateLabel(Transform parent, Placement placement, Bounds placedBounds, float maxHeight)
        {
            GameObject labelGo = new($"Label_{placement.Model.ModelName}");
            labelGo.transform.SetParent(parent, false);

            TextMesh text = labelGo.AddComponent<TextMesh>();
            text.text = placement.Score >= 0
                ? $"{placement.Model.DisplayName}\n{placement.Score}/100"
                : placement.Model.DisplayName;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 90;
            text.characterSize = 1f;
            text.color = Color.white;
            text.richText = false;

            MeshRenderer renderer = labelGo.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            // Big, prominent label relative to castle size, floating clearly above its top.
            float scale = Mathf.Max(0.15f, placedBounds.size.x * 0.035f) * _labelScale;
            labelGo.transform.localScale = Vector3.one * scale;
            labelGo.transform.position = new Vector3(
                placedBounds.center.x,
                placedBounds.max.y + Mathf.Max(4f, maxHeight * 0.35f),
                placedBounds.center.z);

            // TextMesh reads from its +Z side; the framing camera sits on -Z, so face it (180° about Y).
            labelGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        }

        private static Bounds ComputeBounds(GameObject instance)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            bool init = false;
            Bounds bounds = new(instance.transform.position, Vector3.zero);

            foreach (Renderer r in renderers)
            {
                if (r == null)
                {
                    continue;
                }

                if (!init)
                {
                    bounds = r.bounds;
                    init = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!init)
            {
                // No renderers (unlikely for a castle) — give it a unit footprint so layout still works.
                bounds = new Bounds(instance.transform.position, Vector3.one);
            }

            return bounds;
        }

        private static int ParseScore(GameObject instance)
        {
            foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
            {
                Match match = BuiltByPattern.Match(child.name);
                if (match.Success &&
                    int.TryParse(match.Groups["score"].Value, out int score))
                {
                    return score;
                }
            }

            return -1;
        }

        // ------------------------------------------------------------------ data

        private sealed class ModelEntry
        {
            public string ModelName = string.Empty;
            public string FolderPath = string.Empty;
            public bool Include;
            public int SelectedIndex;
            public readonly List<CastleFile> Castles = new();
            public string[] CastleLabels = Array.Empty<string>();

            // Folder names use underscores as separators; present them as spaces for readability.
            public string DisplayName => ModelName.Replace('_', ' ');

            public void RebuildLabels()
            {
                CastleLabels = Castles
                    .Select(c => $"{c.FileName}  ({c.WriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm})")
                    .ToArray();
            }
        }

        private sealed class CastleFile
        {
            public string AssetPath = string.Empty;
            public string FileName = string.Empty;
            public DateTime WriteTimeUtc;
        }

        private sealed class Placement
        {
            public ModelEntry Model = null!;
            public GameObject Instance = null!;
            public Bounds Bounds;
            public int Score;
        }
    }
}
#endif

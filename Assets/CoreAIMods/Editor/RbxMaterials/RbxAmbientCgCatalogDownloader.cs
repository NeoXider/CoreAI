using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>Frozen, API-confirmed ambientCG catalog mapping.</summary>
    public readonly struct RbxAmbientCgMapping
    {
        public RbxAmbientCgMapping(string materialName, string assetId)
        {
            MaterialName = materialName;
            AssetId = assetId;
        }

        public string MaterialName { get; }
        public string AssetId { get; }
    }

    /// <summary>UI Toolkit ambientCG downloader and local override-catalog merger.</summary>
    public sealed class RbxAmbientCgCatalogDownloader : EditorWindow
    {
        private const string LocalRoot = "Assets/CoreAIRbxTexturesLocal/ambientCG";
        private const string LicensePath = LocalRoot + "/LICENSE.md";

        private static readonly ReadOnlyCollection<RbxAmbientCgMapping> MappingList =
            Array.AsReadOnly(new[]
            {
                new RbxAmbientCgMapping("Brick", "Bricks104"),
                new RbxAmbientCgMapping("Wood", "Wood049"),
                new RbxAmbientCgMapping("WoodPlanks", "WoodFloor051"),
                new RbxAmbientCgMapping("Cobblestone", "PavingStones150"),
                new RbxAmbientCgMapping("Metal", "Metal049A"),
                new RbxAmbientCgMapping("Grass", "Grass005"),
                new RbxAmbientCgMapping("Pavement", "PavingStones128"),
                new RbxAmbientCgMapping("Pebble", "Gravel023"),
                new RbxAmbientCgMapping("CeramicTiles", "Tiles133A"),
                new RbxAmbientCgMapping("LeafyGrass", "Moss002"),
                new RbxAmbientCgMapping("Mud", "Ground106"),
                new RbxAmbientCgMapping("Ground", "Ground103"),
                new RbxAmbientCgMapping("ClayRoofTiles", "RoofingTiles006"),
                new RbxAmbientCgMapping("RoofShingles", "RoofingTiles003"),
                new RbxAmbientCgMapping("Fabric", "Fabric036"),
                new RbxAmbientCgMapping("Carpet", "Carpet016"),
                new RbxAmbientCgMapping("Leather", "Leather037"),
                new RbxAmbientCgMapping("Slate", "Rock022"),
                new RbxAmbientCgMapping("Sandstone", "Bricks084"),
                new RbxAmbientCgMapping("Rock", "Rock028"),
                new RbxAmbientCgMapping("Limestone", "Travertine009"),
                new RbxAmbientCgMapping("Granite", "Granite001A"),
                new RbxAmbientCgMapping("Basalt", "Rock035"),
                new RbxAmbientCgMapping("Concrete", "Concrete048"),
                new RbxAmbientCgMapping("Asphalt", "Asphalt033"),
                new RbxAmbientCgMapping("Snow", "Snow010A"),
                new RbxAmbientCgMapping("Sand", "Ground093C"),
                new RbxAmbientCgMapping("Marble", "Marble012"),
                new RbxAmbientCgMapping("Cardboard", "Cardboard004"),
                new RbxAmbientCgMapping("Plaster", "Plaster001"),
                new RbxAmbientCgMapping("Rubber", "Rubber004"),
                new RbxAmbientCgMapping("CorrodedMetal", "Rust004"),
                new RbxAmbientCgMapping("DiamondPlate", "DiamondPlate008C"),
                new RbxAmbientCgMapping("CrackedLava", "Lava004"),
                new RbxAmbientCgMapping("Ice", "Ice002"),
                new RbxAmbientCgMapping("Foil", "Foil003")
            });

        private readonly HashSet<string> _selectedAssetIds = new(StringComparer.Ordinal);
        private readonly Queue<RbxAmbientCgMapping> _pending = new();
        private readonly List<RbxTextureCatalogEntryData> _completedEntries = new();
        private readonly List<RbxAmbientCgMapping> _completedMappings = new();
        private UnityWebRequest _request;
        private RbxAmbientCgMapping _activeMapping;
        private string _resolution = "2K";
        private string _downloadResolution = "2K";
        private string _temporaryRoot;
        private string _activeZipPath;
        private int _totalDownloads;
        private Label _status;
        private Button _downloadButton;
        private Button _cancelButton;

        /// <summary>API-confirmed default mapping table.</summary>
        public static IReadOnlyList<RbxAmbientCgMapping> Mappings => MappingList;

        /// <summary>Builds the ambientCG JPG zip URL.</summary>
        public static string BuildDownloadUrl(string assetId, string resolution)
        {
            return "https://ambientcg.com/get?file=" + Uri.EscapeDataString(
                assetId + "_" + resolution + "-JPG.zip");
        }

        [MenuItem("CoreAI/Materials/Download CC0 texture sets (ambientCG)...")]
        private static void OpenDownloader()
        {
            RbxAmbientCgCatalogDownloader window = GetWindow<RbxAmbientCgCatalogDownloader>();
            window.titleContent = new GUIContent("Rbx ambientCG Download");
            window.minSize = new Vector2(620f, 520f);
            window.Show();
        }

        /// <summary>Builds the downloader UI without IMGUI.</summary>
        public void CreateGUI()
        {
            if (_selectedAssetIds.Count == 0)
            {
                for (int index = 0; index < MappingList.Count; index++)
                {
                    _selectedAssetIds.Add(MappingList[index].AssetId);
                }
            }

            VisualElement root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = 10f;
            root.style.paddingRight = 10f;
            root.style.paddingTop = 8f;
            root.style.paddingBottom = 8f;
            Label heading = new("ambientCG CC0 material sets");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(heading);
            DropdownField resolution = new("Resolution", new List<string> { "1K", "2K", "4K" },
                _resolution);
            resolution.RegisterValueChangedCallback(evt => _resolution = evt.newValue);
            root.Add(resolution);

            ScrollView mappings = new(ScrollViewMode.Vertical);
            mappings.style.flexGrow = 1f;
            root.Add(mappings);
            for (int index = 0; index < MappingList.Count; index++)
            {
                RbxAmbientCgMapping mapping = MappingList[index];
                Toggle toggle = new(mapping.MaterialName + " — " + mapping.AssetId)
                {
                    value = _selectedAssetIds.Contains(mapping.AssetId)
                };
                toggle.RegisterValueChangedCallback(evt => SetSelected(mapping.AssetId, evt.newValue));
                mappings.Add(toggle);
            }

            VisualElement buttons = new();
            buttons.style.flexDirection = FlexDirection.Row;
            _downloadButton = new Button(StartSelectedDownloads) { text = "Download selected" };
            _cancelButton = new Button(CancelDownloads) { text = "Cancel" };
            _cancelButton.SetEnabled(_request != null || _pending.Count > 0);
            buttons.Add(_downloadButton);
            buttons.Add(_cancelButton);
            root.Add(buttons);
            _status = new Label("Ready");
            root.Add(_status);
        }

        private void OnDisable()
        {
            CancelDownloads();
        }

        private void SetSelected(string assetId, bool selected)
        {
            if (selected)
            {
                _selectedAssetIds.Add(assetId);
            }
            else
            {
                _selectedAssetIds.Remove(assetId);
            }
        }

        private void StartSelectedDownloads()
        {
            if (_request != null || _pending.Count > 0)
            {
                return;
            }

            _completedEntries.Clear();
            _completedMappings.Clear();
            for (int index = 0; index < MappingList.Count; index++)
            {
                if (_selectedAssetIds.Contains(MappingList[index].AssetId))
                {
                    _pending.Enqueue(MappingList[index]);
                }
            }

            if (_pending.Count == 0)
            {
                EditorUtility.DisplayDialog("CoreAI Materials", "Select at least one mapping.", "OK");
                return;
            }

            _totalDownloads = _pending.Count;
            _downloadResolution = _resolution;
            _temporaryRoot = Path.Combine(Path.GetTempPath(),
                "CoreAiAmbientCg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryRoot);
            _downloadButton.SetEnabled(false);
            _cancelButton.SetEnabled(true);
            EditorApplication.update += PumpDownload;
            BeginNextDownload();
        }

        private void BeginNextDownload()
        {
            if (_pending.Count == 0)
            {
                FinishDownloads();
                return;
            }

            _activeMapping = _pending.Dequeue();
            _activeZipPath = Path.Combine(_temporaryRoot,
                _activeMapping.AssetId + "_" + _downloadResolution + "-JPG.zip");
            string url = BuildDownloadUrl(_activeMapping.AssetId, _downloadResolution);
            _request = UnityWebRequest.Get(url);
            _request.downloadHandler = new DownloadHandlerFile(_activeZipPath);
            _request.SendWebRequest();
            UpdateStatus();
        }

        private void PumpDownload()
        {
            if (_request == null || !_request.isDone)
            {
                return;
            }

            UnityWebRequest completedRequest = _request;
            _request = null;
            try
            {
                if (completedRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[CoreAI.RbxMaterials] ambientCG download failed for " +
                                   _activeMapping.AssetId + ": " + completedRequest.error);
                }
                else
                {
                    ImportDownloadedArchive(_activeMapping, _activeZipPath);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                completedRequest.Dispose();
                if (File.Exists(_activeZipPath))
                {
                    File.Delete(_activeZipPath);
                }
            }

            BeginNextDownload();
        }

        private void ImportDownloadedArchive(RbxAmbientCgMapping mapping, string zipPath)
        {
            string destinationAssetFolder = LocalRoot + "/" + mapping.AssetId;
            string destinationAbsoluteFolder = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                destinationAssetFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(destinationAbsoluteFolder);
            ExtractAllowedMaps(zipPath, destinationAbsoluteFolder);
            AssetDatabase.Refresh();
            RbxMegascansSurfaceScan surface =
                RbxMegascansCatalogImporter.ScanSurfaceFolder(destinationAbsoluteFolder);
            if (surface == null || string.IsNullOrEmpty(surface.AlbedoPath)
                || string.IsNullOrEmpty(surface.NormalPath)
                || string.IsNullOrEmpty(surface.RoughnessPath))
            {
                throw new InvalidDataException(mapping.AssetId +
                    " archive did not contain Color, NormalGL, and Roughness maps.");
            }

            surface.SelectedMaterialName = mapping.MaterialName;
            string albedo = ImportMap(surface.AlbedoPath, true, false);
            string normal = ImportMap(surface.NormalPath, false, true);
            string roughness = ImportMap(surface.RoughnessPath, false, false);
            string metalness = ImportOptionalMap(surface.MetalnessPath);
            string occlusion = ImportOptionalMap(surface.AmbientOcclusionPath);
            _completedEntries.Add(new RbxTextureCatalogEntryData
            {
                MaterialName = mapping.MaterialName,
                MaterialValue = RbxMaterialCatalogEditorUtility.MaterialValue(mapping.MaterialName),
                AlbedoAssetPath = albedo,
                NormalAssetPath = normal,
                IsOpenGlNormal = true,
                RoughnessAssetPath = roughness,
                IsSmoothnessMap = false,
                MetalnessAssetPath = metalness,
                AmbientOcclusionAssetPath = occlusion
            });
            _completedMappings.Add(mapping);
        }

        private static void ExtractAllowedMaps(string zipPath, string destinationFolder)
        {
            string destinationRoot = Path.GetFullPath(destinationFolder) +
                                     Path.DirectorySeparatorChar;
            using FileStream zipStream = File.OpenRead(zipPath);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, false);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string fileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(fileName) || !IsAllowedMap(fileName))
                {
                    continue;
                }

                string destination = Path.GetFullPath(Path.Combine(destinationFolder, fileName));
                if (!destination.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Unsafe ambientCG archive path: " + entry.FullName);
                }

                using Stream source = entry.Open();
                using FileStream target = File.Create(destination);
                source.CopyTo(target);
            }
        }

        private static bool IsAllowedMap(string fileName)
        {
            string token = fileName.ToLowerInvariant();
            return token.Contains("_color.", StringComparison.Ordinal)
                   || token.Contains("_normalgl.", StringComparison.Ordinal)
                   || token.Contains("_roughness.", StringComparison.Ordinal)
                   || token.Contains("_ambientocclusion.", StringComparison.Ordinal)
                   || token.Contains("_metalness.", StringComparison.Ordinal);
        }

        private static string ImportOptionalMap(string absolutePath)
        {
            return string.IsNullOrEmpty(absolutePath)
                ? null
                : ImportMap(absolutePath, false, false);
        }

        private static string ImportMap(string absolutePath, bool isAlbedo, bool isNormal)
        {
            string assetPath = RbxMaterialCatalogEditorUtility.ToAssetPath(absolutePath);
            RbxMaterialCatalogEditorUtility.ApplyTextureImportSettings(assetPath, isAlbedo,
                isNormal);
            return assetPath;
        }

        private void FinishDownloads()
        {
            EditorApplication.update -= PumpDownload;
            EditorUtility.ClearProgressBar();
            try
            {
                if (_completedEntries.Count > 0)
                {
                    RbxMaterialCatalogEditorUtility.MergeOverrideCatalog(_completedEntries);
                    WriteLicense();
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (!string.IsNullOrEmpty(_temporaryRoot) && Directory.Exists(_temporaryRoot))
                {
                    Directory.Delete(_temporaryRoot, true);
                }

                _temporaryRoot = null;
                _downloadButton?.SetEnabled(true);
                _cancelButton?.SetEnabled(false);
                if (_status != null)
                {
                    _status.text = "Completed " + _completedEntries.Count + " of " +
                                   _totalDownloads + " downloads";
                }

                AssetDatabase.Refresh();
            }
        }

        private void WriteLicense()
        {
            StringBuilder text = new();
            text.AppendLine("# ambientCG texture provenance");
            text.AppendLine();
            text.AppendLine("These files are provided under CC0 1.0: " +
                            "https://creativecommons.org/publicdomain/zero/1.0/");
            text.AppendLine();
            text.AppendLine("| Enum.Material | ambientCG asset | Resolution | Downloaded (UTC) | URL |");
            text.AppendLine("|---|---|---:|---|---|");
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string absoluteLicense = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                LicensePath.Replace('/', Path.DirectorySeparatorChar));
            SortedDictionary<string, string> rows = ReadExistingProvenanceRows(absoluteLicense);
            for (int index = 0; index < _completedMappings.Count; index++)
            {
                RbxAmbientCgMapping mapping = _completedMappings[index];
                rows[mapping.MaterialName] = "| " + mapping.MaterialName + " | " + mapping.AssetId +
                                             " | " + _downloadResolution + " | " + date + " | " +
                                             BuildDownloadUrl(mapping.AssetId, _downloadResolution) +
                                             " |";
            }

            foreach (KeyValuePair<string, string> row in rows)
            {
                text.AppendLine(row.Value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absoluteLicense));
            File.WriteAllText(absoluteLicense, text.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Keeps provenance rows of sets downloaded in earlier runs, so a partial re-download never
        /// erases the record of files that are still in the project.
        /// </summary>
        internal static SortedDictionary<string, string> ReadExistingProvenanceRows(string licensePath)
        {
            SortedDictionary<string, string> rows = new(StringComparer.Ordinal);
            if (!File.Exists(licensePath))
            {
                return rows;
            }

            foreach (string line in File.ReadAllLines(licensePath))
            {
                if (!line.StartsWith("| ", StringComparison.Ordinal) ||
                    line.StartsWith("| Enum.Material", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] cells = line.Split('|');
                if (cells.Length < 3)
                {
                    continue;
                }

                string materialName = cells[1].Trim();
                if (materialName.Length > 0)
                {
                    rows[materialName] = line;
                }
            }

            return rows;
        }

        private void CancelDownloads()
        {
            EditorApplication.update -= PumpDownload;
            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }

            _pending.Clear();
            if (!string.IsNullOrEmpty(_temporaryRoot) && Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, true);
            }

            _temporaryRoot = null;
            EditorUtility.ClearProgressBar();
            _downloadButton?.SetEnabled(true);
            _cancelButton?.SetEnabled(false);
            if (_status != null)
            {
                _status.text = "Cancelled";
            }
        }

        private void UpdateStatus()
        {
            int completed = _totalDownloads - _pending.Count - 1;
            float progress = _totalDownloads == 0 ? 0f : (float)completed / _totalDownloads;
            EditorUtility.DisplayProgressBar("CoreAI ambientCG",
                "Downloading " + _activeMapping.AssetId + " (" + _downloadResolution + ")",
                progress);
            if (_status != null)
            {
                _status.text = "Downloading " + _activeMapping.AssetId + " — " +
                               (completed + 1) + "/" + _totalDownloads;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>Pure folder-scan result for one Bridge or Fab surface.</summary>
    public sealed class RbxMegascansSurfaceScan
    {
        public string FolderPath { get; internal set; }
        public string FolderName { get; internal set; }
        public string SuggestedMaterialName { get; internal set; }
        public string SelectedMaterialName { get; set; }
        public string AlbedoPath { get; internal set; }
        public string NormalPath { get; internal set; }
        public bool IsOpenGlNormal { get; internal set; }
        public string RoughnessPath { get; internal set; }
        public bool IsSmoothnessMap { get; internal set; }
        public string MetalnessPath { get; internal set; }
        public string AmbientOcclusionPath { get; internal set; }
        public string DisplacementPath { get; internal set; }
    }

    /// <summary>UI Toolkit importer for project-local Bridge and Fab texture folders.</summary>
    public sealed class RbxMegascansCatalogImporter : EditorWindow
    {
        private static readonly string[] TextureExtensions =
        {
            ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr", ".tga"
        };

        private static readonly Regex DirectXJson = new(
            "\\\"normal\\\"\\s*:\\s*\\\"DirectX\\\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex OpenGlJson = new(
            "\\\"normal\\\"\\s*:\\s*\\\"OpenGL\\\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<RbxMegascansSurfaceScan> _surfaces = new();
        private string _sourceFolder = string.Empty;

        /// <summary>Scans each immediate subfolder without importing or decoding textures.</summary>
        public static IReadOnlyList<RbxMegascansSurfaceScan> ScanFolder(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("A Bridge or Fab folder is required.", nameof(rootFolder));
            }

            string fullRoot = Path.GetFullPath(rootFolder);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException(fullRoot);
            }

            string[] directories = Directory.GetDirectories(fullRoot, "*",
                SearchOption.TopDirectoryOnly);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            List<RbxMegascansSurfaceScan> results = new(directories.Length);
            for (int index = 0; index < directories.Length; index++)
            {
                RbxMegascansSurfaceScan surface = ScanSurfaceFolder(directories[index]);
                if (surface != null)
                {
                    results.Add(surface);
                }
            }

            return results;
        }

        /// <summary>Scans one surface folder into semantic PBR map slots.</summary>
        public static RbxMegascansSurfaceScan ScanSurfaceFolder(string surfaceFolder)
        {
            string[] files = Directory.GetFiles(surfaceFolder, "*", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            RbxMegascansSurfaceScan result = new()
            {
                FolderPath = Path.GetFullPath(surfaceFolder),
                FolderName = Path.GetFileName(surfaceFolder)
            };
            bool foundTexture = false;
            bool explicitOpenGl = false;
            bool explicitDirectX = false;
            for (int index = 0; index < files.Length; index++)
            {
                string file = files[index];
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                {
                    string json = File.ReadAllText(file);
                    explicitDirectX |= DirectXJson.IsMatch(json);
                    explicitOpenGl |= OpenGlJson.IsMatch(json);
                    continue;
                }

                if (!IsTextureExtension(extension))
                {
                    continue;
                }

                foundTexture = true;
                string token = Normalize(Path.GetFileNameWithoutExtension(file));
                if (ContainsAny(token, "albedo", "basecolor", "diffuse", "color"))
                {
                    result.AlbedoPath ??= file;
                }
                else if (token.Contains("normal", StringComparison.Ordinal))
                {
                    result.NormalPath ??= file;
                    explicitOpenGl |= token.Contains("normalgl", StringComparison.Ordinal)
                                      || token.Contains("opengl", StringComparison.Ordinal);
                    explicitDirectX |= token.Contains("normaldx", StringComparison.Ordinal)
                                        || token.EndsWith("dx", StringComparison.Ordinal)
                                        || token.Contains("directx", StringComparison.Ordinal);
                }
                else if (ContainsAny(token, "roughness", "rough"))
                {
                    result.RoughnessPath ??= file;
                    result.IsSmoothnessMap = false;
                }
                else if (ContainsAny(token, "smoothness", "gloss"))
                {
                    result.RoughnessPath ??= file;
                    result.IsSmoothnessMap = true;
                }
                else if (ContainsAny(token, "ambientocclusion", "occlusion", "ao"))
                {
                    result.AmbientOcclusionPath ??= file;
                }
                else if (ContainsAny(token, "metalness", "metallic"))
                {
                    result.MetalnessPath ??= file;
                }
                else if (ContainsAny(token, "displacement", "height"))
                {
                    result.DisplacementPath ??= file;
                }
            }

            if (!foundTexture)
            {
                return null;
            }

            result.IsOpenGlNormal = explicitOpenGl && !explicitDirectX;
            result.SuggestedMaterialName = SuggestMaterial(result.FolderName);
            result.SelectedMaterialName = result.SuggestedMaterialName;
            return result;
        }

        [MenuItem("CoreAI/Materials/Import Bridge-Megascans folder...")]
        private static void OpenImporter()
        {
            string folder = EditorUtility.OpenFolderPanel(
                "Choose Bridge or Fab surface folder", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            string assetPath;
            try
            {
                assetPath = RbxMaterialCatalogEditorUtility.ToAssetPath(folder);
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("CoreAI Materials", exception.Message, "OK");
                return;
            }

            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("CoreAI Materials",
                    "Choose a folder under this project's Assets directory. No texture files are copied.",
                    "OK");
                return;
            }

            RbxMegascansCatalogImporter window = GetWindow<RbxMegascansCatalogImporter>();
            window.titleContent = new GUIContent("Rbx Megascans Import");
            window.minSize = new Vector2(720f, 420f);
            window.LoadFolder(folder);
            window.Show();
        }

        /// <summary>Builds the importer UI without IMGUI.</summary>
        public void CreateGUI()
        {
            RebuildUi();
        }

        private void LoadFolder(string folder)
        {
            _sourceFolder = Path.GetFullPath(folder);
            _surfaces.Clear();
            IReadOnlyList<RbxMegascansSurfaceScan> scanned = ScanFolder(_sourceFolder);
            for (int index = 0; index < scanned.Count; index++)
            {
                _surfaces.Add(scanned[index]);
            }

            RebuildUi();
        }

        private void RebuildUi()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = 10f;
            root.style.paddingRight = 10f;
            root.style.paddingTop = 8f;
            root.style.paddingBottom = 8f;
            Label heading = new("Bridge / Fab surfaces");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(heading);
            root.Add(new Label(string.IsNullOrEmpty(_sourceFolder)
                ? "Choose the menu command again to select a folder."
                : _sourceFolder));

            ScrollView scroll = new(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            root.Add(scroll);
            List<string> choices = new() { "Skip" };
            for (int index = 0; index < RbxMaterialCatalogEditorUtility.MaterialNames.Count; index++)
            {
                choices.Add(RbxMaterialCatalogEditorUtility.MaterialNames[index]);
            }

            for (int index = 0; index < _surfaces.Count; index++)
            {
                RbxMegascansSurfaceScan surface = _surfaces[index];
                VisualElement card = new();
                card.style.marginTop = 6f;
                card.style.paddingLeft = 6f;
                card.style.paddingRight = 6f;
                card.style.paddingTop = 4f;
                card.style.paddingBottom = 4f;
                card.style.borderBottomWidth = 1f;
                card.Add(new Label(surface.FolderName));
                card.Add(new Label(MapSummary(surface)));
                int selectedIndex = choices.IndexOf(surface.SelectedMaterialName);
                DropdownField dropdown = new("Enum.Material", choices,
                    selectedIndex >= 0 ? selectedIndex : 0);
                dropdown.RegisterValueChangedCallback(evt => surface.SelectedMaterialName = evt.newValue);
                card.Add(dropdown);
                scroll.Add(card);
            }

            Button import = new(ImportSelected) { text = "Import selected mappings" };
            import.SetEnabled(_surfaces.Count > 0);
            root.Add(import);
        }

        private void ImportSelected()
        {
            try
            {
                AssetDatabase.Refresh();
                List<RbxTextureCatalogEntryData> entries = new();
                for (int index = 0; index < _surfaces.Count; index++)
                {
                    RbxMegascansSurfaceScan surface = _surfaces[index];
                    if (string.IsNullOrEmpty(surface.SelectedMaterialName)
                        || string.Equals(surface.SelectedMaterialName, "Skip",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(surface.AlbedoPath)
                        || string.IsNullOrEmpty(surface.NormalPath)
                        || string.IsNullOrEmpty(surface.RoughnessPath))
                    {
                        throw new InvalidOperationException(surface.FolderName +
                            " needs albedo, normal, and roughness or smoothness maps.");
                    }

                    entries.Add(ImportSurface(surface));
                }

                RbxMaterialCatalogEditorUtility.MergeOverrideCatalog(entries);
                EditorUtility.DisplayDialog("CoreAI Materials",
                    "Imported " + entries.Count + " mappings into " +
                    RbxMaterialCatalogEditorUtility.OverrideCatalogAssetPath, "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("CoreAI Materials", exception.Message, "OK");
            }
        }

        private static RbxTextureCatalogEntryData ImportSurface(
            RbxMegascansSurfaceScan surface)
        {
            string albedo = ImportMap(surface.AlbedoPath, true, false);
            string normal = ImportMap(surface.NormalPath, false, true);
            string roughness = ImportMap(surface.RoughnessPath, false, false);
            string metalness = ImportOptionalMap(surface.MetalnessPath);
            string occlusion = ImportOptionalMap(surface.AmbientOcclusionPath);
            return new RbxTextureCatalogEntryData
            {
                MaterialName = surface.SelectedMaterialName,
                MaterialValue = RbxMaterialCatalogEditorUtility.MaterialValue(
                    surface.SelectedMaterialName),
                AlbedoAssetPath = albedo,
                NormalAssetPath = normal,
                IsOpenGlNormal = surface.IsOpenGlNormal,
                RoughnessAssetPath = roughness,
                IsSmoothnessMap = surface.IsSmoothnessMap,
                MetalnessAssetPath = metalness,
                AmbientOcclusionAssetPath = occlusion
            };
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

        private static string MapSummary(RbxMegascansSurfaceScan surface)
        {
            StringBuilder summary = new();
            summary.Append("Albedo: ").Append(FileName(surface.AlbedoPath));
            summary.Append(" | Normal: ").Append(FileName(surface.NormalPath));
            summary.Append(surface.IsOpenGlNormal ? " (OpenGL)" : " (DirectX)");
            summary.Append(" | Roughness: ").Append(FileName(surface.RoughnessPath));
            summary.Append(" | AO: ").Append(FileName(surface.AmbientOcclusionPath));
            summary.Append(" | Metal: ").Append(FileName(surface.MetalnessPath));
            return summary.ToString();
        }

        private static string FileName(string path)
        {
            return string.IsNullOrEmpty(path) ? "-" : Path.GetFileName(path);
        }

        private static string SuggestMaterial(string folderName)
        {
            string normalized = Normalize(folderName);
            string best = "Skip";
            int bestLength = 0;
            for (int index = 0; index < RbxMaterialCatalogEditorUtility.MaterialNames.Count; index++)
            {
                string candidate = RbxMaterialCatalogEditorUtility.MaterialNames[index];
                string candidateToken = Normalize(candidate);
                if (normalized.Contains(candidateToken, StringComparison.Ordinal)
                    && candidateToken.Length > bestLength)
                {
                    best = candidate;
                    bestLength = candidateToken.Length;
                }
            }

            if (!string.Equals(best, "Skip", StringComparison.Ordinal))
            {
                return best;
            }

            if (normalized.Contains("tile", StringComparison.Ordinal))
            {
                return "CeramicTiles";
            }

            if (normalized.Contains("plank", StringComparison.Ordinal)
                || normalized.Contains("woodfloor", StringComparison.Ordinal))
            {
                return "WoodPlanks";
            }

            return best;
        }

        private static bool IsTextureExtension(string extension)
        {
            for (int index = 0; index < TextureExtensions.Length; index++)
            {
                if (string.Equals(extension, TextureExtensions[index],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int index = 0; index < tokens.Length; index++)
            {
                if (value.Contains(tokens[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string value)
        {
            StringBuilder result = new();
            for (int index = 0; index < value.Length; index++)
            {
                char character = char.ToLowerInvariant(value[index]);
                if (char.IsLetterOrDigit(character))
                {
                    result.Append(character);
                }
            }

            return result.ToString();
        }
    }
}

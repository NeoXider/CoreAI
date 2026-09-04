using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>
    /// Rebuilds the project-local override catalog from texture sets that are already on disk.
    /// <para>
    /// WHY: the downloader window fetches and imports in one interactive pass, so a catalog could
    /// only be rebuilt by re-downloading a gigabyte of archives through the UI. Sets live under
    /// <c>Assets/CoreAIRbxTexturesLocal/</c> and are gitignored, so a fresh clone re-fetches them
    /// once and then rebuilds from disk — including headlessly, via -executeMethod.
    /// </para>
    /// </summary>
    internal static class RbxLocalTextureCatalogImport
    {
        private const string TextureRoot = "Assets/CoreAIRbxTexturesLocal";

        /// <summary>All CC0 sets, read from the single source of truth.</summary>
        internal static IReadOnlyList<RbxCc0TextureSet> Sets => RbxCc0TextureSets.Sets;

        /// <summary>
        /// Stamps the shared texture import policy onto the packaged 1K sets.
        /// <para>
        /// WHY: the packaged folder ships inside the package, so its .meta files are committed and were
        /// only ever correct because someone set them by hand once. A newly added map arrives with
        /// Unity's defaults instead — an sRGB roughness map and a normal imported as a colour texture,
        /// which the acceptance tests catch but nothing repairs. This applies the same policy the
        /// override importer uses, so adding a packaged map is a one-command operation.
        /// </para>
        /// </summary>
        [MenuItem("CoreAI/Materials/Apply import policy to packaged textures")]
        public static void ApplyPackagedImportPolicy()
        {
            const string packagedRoot =
                "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxTextures";
            AssetDatabase.Refresh();
            string[] files = Directory.GetFiles(packagedRoot, "*.jpg", SearchOption.TopDirectoryOnly);
            int stamped = 0;
            foreach (string file in files)
            {
                string assetPath = file.Replace(Path.DirectorySeparatorChar, '/');
                bool isAlbedo = assetPath.EndsWith("_Color.jpg", StringComparison.Ordinal);
                bool isNormal = assetPath.Contains("_Normal", StringComparison.Ordinal);
                RbxMaterialCatalogEditorUtility.ApplyTextureImportSettings(assetPath, isAlbedo, isNormal);
                stamped++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[CoreAI] Import policy applied to " + stamped + " packaged textures.");
        }

        [MenuItem("CoreAI/Materials/Rebuild override catalog from local sets")]
        public static void Rebuild()
        {
            // WHY: on a fresh clone the sets are on disk but gitignored, so they carry no .meta and
            // AssetImporter.GetAtPath returns null — the very scenario this entry point exists for.
            AssetDatabase.Refresh();

            int imported = 0;
            List<string> skipped = new();
            List<RbxTextureCatalogEntryData> entries = new();
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            foreach (RbxCc0TextureSet set in Sets)
            {
                string assetFolder = TextureRoot + "/" + set.Folder;
                string absolute = Path.Combine(projectRoot,
                    assetFolder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(absolute))
                {
                    skipped.Add($"{set.MaterialName}: no folder {assetFolder}");
                    continue;
                }

                RbxMegascansSurfaceScan surface = RbxMegascansCatalogImporter.ScanSurfaceFolder(absolute);
                if (surface == null || string.IsNullOrEmpty(surface.AlbedoPath)
                    || string.IsNullOrEmpty(surface.NormalPath)
                    || string.IsNullOrEmpty(surface.RoughnessPath))
                {
                    skipped.Add($"{set.MaterialName}: {set.Folder} lacks Color/Normal/Roughness");
                    continue;
                }

                // WHY: 24 of these sets ship both _NormalDX and _NormalGL, and ScanSurfaceFolder keeps
                // whichever sorts first — DX. The download path always records GL, so prefer GL here or a
                // rebuild silently flips the green channel's meaning on two thirds of the catalog.
                string normalPath = surface.NormalPath;
                bool openGlNormal = surface.IsOpenGlNormal;
                string openGlCandidate = FindOpenGlNormal(absolute);
                if (!string.IsNullOrEmpty(openGlCandidate))
                {
                    normalPath = openGlCandidate;
                    openGlNormal = true;
                }

                RbxTextureCatalogEntryData entry = new()
                {
                    MaterialName = set.MaterialName,
                    MaterialValue = RbxMaterialCatalogEditorUtility.MaterialValue(set.MaterialName),
                    AlbedoAssetPath = Import(surface.AlbedoPath, true, false),
                    NormalAssetPath = Import(normalPath, false, true),
                    IsOpenGlNormal = openGlNormal,
                    RoughnessAssetPath = Import(surface.RoughnessPath, false, false),
                    IsSmoothnessMap = false,
                    MetalnessAssetPath = ImportOptional(surface.MetalnessPath),
                    AmbientOcclusionAssetPath = ImportOptional(surface.AmbientOcclusionPath)
                };
                RbxMaterialSurfaceProfiles.Apply(entry);
                entries.Add(entry);
                imported++;
            }

            string report = $"imported {imported}/{Sets.Count} sets" +
                            (skipped.Count == 0 ? "" : "; skipped: " + string.Join(" | ", skipped));
            if (entries.Count == 0)
            {
                // WHY: -executeMethod exits 0 on a silent no-op, so a total failure looked identical to
                // a rebuild. Throwing is the only signal a headless caller can act on.
                throw new InvalidOperationException(
                    "[RbxLocalTextureCatalogImport] no set could be imported; " + report);
            }

            RbxMaterialCatalogEditorUtility.MergeOverrideCatalog(entries);
            AssetDatabase.SaveAssets();
            Debug.Log("[RbxLocalTextureCatalogImport] " + report);
        }

        /// <summary>The folder's OpenGL normal map, or null when it only ships a DirectX one.</summary>
        private static string FindOpenGlNormal(string absoluteFolder)
        {
            foreach (string path in Directory.EnumerateFiles(absoluteFolder))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name != null && name.EndsWith("NormalGL", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }

        private static string Import(string absolutePath, bool isAlbedo, bool isNormal)
        {
            string assetPath = RbxMaterialCatalogEditorUtility.ToAssetPath(absolutePath);
            RbxMaterialCatalogEditorUtility.ApplyTextureImportSettings(assetPath, isAlbedo, isNormal);
            return assetPath;
        }

        private static string ImportOptional(string absolutePath)
        {
            return string.IsNullOrEmpty(absolutePath) ? null : Import(absolutePath, false, false);
        }
    }
}

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

        /// <summary>One <see cref="UnityEngine.Material"/> slot and the folder that surfaces it.</summary>
        private readonly struct LocalSet
        {
            public LocalSet(string materialName, string folder)
            {
                MaterialName = materialName;
                Folder = folder;
            }

            public string MaterialName { get; }
            public string Folder { get; }
        }

        // Every entry was confirmed against the source catalog's own API before being written here:
        // ambientCG ids through api/v3/assets, Poly Haven slugs through api.polyhaven.com/info.
        private static readonly LocalSet[] Sets =
        {
            new("Cobblestone", "ambientCG/PavingStones151"),
            new("Brick", "ambientCG/Bricks104"),
            new("Slate", "polyhaven/patterned_slate_tiles"),
            new("Limestone", "ambientCG/Tiles139"),
            new("Sandstone", "ambientCG/Tiles144"),
            new("Granite", "ambientCG/Granite002A"),
            new("Basalt", "polyhaven/volcanic_rock_tiles"),
            new("Rock", "ambientCG/Rock064"),
            new("Concrete", "ambientCG/Concrete034"),
            new("Marble", "ambientCG/Marble016"),
            new("Plaster", "ambientCG/Plaster001"),
            new("Pavement", "ambientCG/PavingStones150"),
            new("Pebble", "ambientCG/Gravel041"),
            new("CeramicTiles", "ambientCG/Tiles141"),
            new("ClayRoofTiles", "ambientCG/RoofingTiles012A"),
            new("RoofShingles", "ambientCG/RoofingTiles013A"),
            new("Wood", "ambientCG/Wood095"),
            new("WoodPlanks", "ambientCG/WoodFloor064"),
            new("Metal", "ambientCG/Metal063"),
            new("CorrodedMetal", "ambientCG/Metal021"),
            new("DiamondPlate", "ambientCG/DiamondPlate009"),
            new("Foil", "ambientCG/Foil002"),
            new("Grass", "ambientCG/Grass005"),
            new("LeafyGrass", "ambientCG/Ground106"),
            new("Ground", "ambientCG/Ground110"),
            new("Mud", "ambientCG/Ground109"),
            new("Sand", "ambientCG/Ground054"),
            new("Snow", "ambientCG/Snow015"),
            new("Ice", "ambientCG/Ice003"),
            new("CrackedLava", "ambientCG/Lava004"),
            new("Asphalt", "ambientCG/Asphalt033"),
            new("Fabric", "ambientCG/Fabric081C"),
            new("Carpet", "ambientCG/Carpet016"),
            new("Leather", "ambientCG/Leather037"),
            new("Cardboard", "ambientCG/Cardboard002"),
            new("Rubber", "ambientCG/Rubber004")
        };

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

            foreach (LocalSet set in Sets)
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

            string report = $"imported {imported}/{Sets.Length} sets" +
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

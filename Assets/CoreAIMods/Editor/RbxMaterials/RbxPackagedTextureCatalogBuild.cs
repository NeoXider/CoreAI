using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>
    /// Rebuilds the catalog that ships inside the package from the 1K sets in its Resources folder.
    /// <para>
    /// WHY: the shipped catalog used to describe six materials while the project-local override
    /// described all thirty-six, so everything a consumer did not import themselves fell back to the
    /// procedural shader. The packaged folder now carries every set, and this command is what keeps
    /// the asset beside it in step — a set is added by dropping its maps in and running this.
    /// </para>
    /// </summary>
    internal static class RbxPackagedTextureCatalogBuild
    {
        private const string PackagedRoot =
            "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxTextures";

        private const string CatalogPath = PackagedRoot + "/RbxMaterialTextureCatalog.asset";

        // The packaged folder is flat and every map is 1K JPG, so a set is addressed by stem alone.
        private const string Resolution = "_1K-JPG_";

        [MenuItem("CoreAI/Materials/Rebuild packaged catalog from packaged textures")]
        public static void Rebuild()
        {
            AssetDatabase.Refresh();

            List<RbxTextureCatalogEntryData> entries = new();
            List<string> skipped = new();

            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                string stem = set.Folder.Substring(set.Folder.IndexOf('/') + 1);
                string albedo = MapPath(stem, "Color");
                string normal = MapPath(stem, "NormalGL");
                string roughness = MapPath(stem, "Roughness");
                if (albedo == null || normal == null || roughness == null)
                {
                    skipped.Add(set.MaterialName + ": " + stem + " lacks Color/NormalGL/Roughness");
                    continue;
                }

                RbxTextureCatalogEntryData entry = new()
                {
                    MaterialName = set.MaterialName,
                    MaterialValue = RbxMaterialCatalogEditorUtility.MaterialValue(set.MaterialName),
                    AlbedoAssetPath = Stamp(albedo, true, false),
                    NormalAssetPath = Stamp(normal, false, true),
                    // WHY: only GL normals are fetched into this folder, and the shader flips the
                    // green channel for DX ones — recording it wrong inverts every lit surface.
                    IsOpenGlNormal = true,
                    RoughnessAssetPath = Stamp(roughness, false, false),
                    IsSmoothnessMap = false,
                    MetalnessAssetPath = OptionalMap(stem, "Metalness"),
                    AmbientOcclusionAssetPath = null
                };
                RbxMaterialSurfaceProfiles.Apply(entry);
                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "No packaged sets resolved — refusing to write an empty catalog. " +
                    string.Join("; ", skipped));
            }

            RbxMaterialCatalogEditorUtility.MergeCatalogAt(CatalogPath, entries);
            AssetDatabase.SaveAssets();

            Debug.Log("[CoreAI] Packaged catalog rebuilt: " + entries.Count + " materials"
                      + (skipped.Count == 0 ? "." : ", skipped " + skipped.Count + ": "
                                                     + string.Join("; ", skipped)));
        }

        private static string MapPath(string stem, string map)
        {
            string path = PackagedRoot + "/" + stem + Resolution + map + ".jpg";
            return File.Exists(path) ? path : null;
        }

        private static string OptionalMap(string stem, string map)
        {
            return MapPath(stem, map);
        }

        private static string Stamp(string assetPath, bool isAlbedo, bool isNormal)
        {
            RbxMaterialCatalogEditorUtility.ApplyTextureImportSettings(assetPath, isAlbedo, isNormal);
            return assetPath;
        }
    }
}

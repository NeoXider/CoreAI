using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>
    /// Re-applies <see cref="RbxMaterialSurfaceProfiles"/> to catalogs that were imported before the
    /// table existed, or after the table is retuned.
    /// <para>
    /// WHY: tiling and relief live in the catalog asset, so changing the table alone does not touch a
    /// catalog that is already on disk. Without this, every project that imported textures earlier keeps
    /// the old flat 8-studs-for-everything look.
    /// </para>
    /// </summary>
    internal static class RbxMaterialSurfaceProfileRetuner
    {
        private const string PackagedCatalogAssetPath =
            "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxTextures/" +
            "RbxMaterialTextureCatalog.asset";

        [MenuItem("CoreAI/Materials/Retune surface profiles (tiling + relief)")]
        private static void Retune()
        {
            int total = 0;
            List<string> touched = new();
            foreach (string path in new[]
                     {
                         PackagedCatalogAssetPath,
                         RbxMaterialCatalogEditorUtility.OverrideCatalogAssetPath
                     })
            {
                int changed = RetuneCatalog(path);
                if (changed < 0)
                {
                    continue;
                }

                total += changed;
                touched.Add($"{System.IO.Path.GetFileNameWithoutExtension(path)}: {changed} entries");
            }

            if (touched.Count == 0)
            {
                EditorUtility.DisplayDialog("CoreAI materials",
                    "No texture catalog was found to retune.", "OK");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("CoreAI materials",
                $"Retuned {total} catalog entries.\n\n{string.Join("\n", touched)}", "OK");
        }

        /// <summary>Rewrites tiling and relief on one catalog; returns -1 when the asset is missing.</summary>
        private static int RetuneCatalog(string assetPath)
        {
            Object catalog = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (catalog == null)
            {
                return -1;
            }

            SerializedObject serialized = new(catalog);
            SerializedProperty entries = serialized.FindProperty("_entries");
            if (entries == null || !entries.isArray)
            {
                return -1;
            }

            int changed = 0;
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                string name = entry.FindPropertyRelative("_materialName")?.stringValue;
                if (!RbxMaterialSurfaceProfiles.Has(name))
                {
                    continue;
                }

                RbxMaterialSurfaceProfiles.Profile profile = RbxMaterialSurfaceProfiles.For(name);
                entry.FindPropertyRelative("_tileWidthStuds").floatValue = profile.TileWidthStuds;
                entry.FindPropertyRelative("_normalStrength").floatValue = profile.NormalStrength;
                entry.FindPropertyRelative("_roughnessScale").floatValue = profile.RoughnessScale;
                entry.FindPropertyRelative("_partColorInfluence").floatValue =
                    profile.PartColorInfluence;
                changed++;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            return changed;
        }
    }
}

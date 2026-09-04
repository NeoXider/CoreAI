using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>Editor-only texture catalog entry data independent of the runtime asmdef.</summary>
    internal sealed class RbxTextureCatalogEntryData
    {
        public string MaterialName { get; set; }
        public int MaterialValue { get; set; }
        public string AlbedoAssetPath { get; set; }
        public string NormalAssetPath { get; set; }
        public bool IsOpenGlNormal { get; set; }
        public string RoughnessAssetPath { get; set; }
        public bool IsSmoothnessMap { get; set; }
        public string MetalnessAssetPath { get; set; }
        public string AmbientOcclusionAssetPath { get; set; }
        public float TileWidthStuds { get; set; } = 8f;
        public Color IntrinsicColor { get; set; } = Color.white;
        public float PartColorInfluence { get; set; } = 0.75f;
        public float RoughnessScale { get; set; } = 1f;
        public float NormalStrength { get; set; } = 1f;
    }

    /// <summary>Shared import settings and serialized catalog writer.</summary>
    internal static class RbxMaterialCatalogEditorUtility
    {
        internal const string OverrideCatalogAssetPath =
            "Assets/CoreAIRbxTexturesLocal/Resources/CoreAIRbxTextureCatalogOverride.asset";

        private const string CatalogTypeName =
            "CoreAI.Mods.Rbx.Rendering.RbxMaterialTextureCatalog";

        private static readonly string[] MaterialNameArray =
        {
            "Plastic", "SmoothPlastic", "Neon", "Wood", "WoodPlanks", "Marble", "Basalt",
            "Slate", "CrackedLava", "Concrete", "Limestone", "Granite", "Pavement", "Brick",
            "Pebble", "Cobblestone", "Rock", "Sandstone", "CorrodedMetal", "DiamondPlate",
            "Foil", "Metal", "Grass", "LeafyGrass", "Sand", "Fabric", "Snow", "Mud",
            "Ground", "Asphalt", "Salt", "Ice", "Glacier", "Glass", "ForceField", "Air",
            "Water", "Cardboard", "Carpet", "CeramicTiles", "ClayRoofTiles", "RoofShingles",
            "Leather", "Plaster", "Rubber"
        };

        private static readonly Dictionary<string, int> MaterialValues = new(StringComparer.Ordinal)
        {
            ["Plastic"] = 256, ["SmoothPlastic"] = 272, ["Neon"] = 288, ["Wood"] = 512,
            ["WoodPlanks"] = 528, ["Marble"] = 784, ["Basalt"] = 788, ["Slate"] = 800,
            ["CrackedLava"] = 804, ["Concrete"] = 816, ["Limestone"] = 820,
            ["Granite"] = 832, ["Pavement"] = 836, ["Brick"] = 848, ["Pebble"] = 864,
            ["Cobblestone"] = 880, ["Rock"] = 896, ["Sandstone"] = 912,
            ["CorrodedMetal"] = 1040, ["DiamondPlate"] = 1056, ["Foil"] = 1072,
            ["Metal"] = 1088, ["Grass"] = 1280, ["LeafyGrass"] = 1284, ["Sand"] = 1296,
            ["Fabric"] = 1312, ["Snow"] = 1328, ["Mud"] = 1344, ["Ground"] = 1360,
            ["Asphalt"] = 1376, ["Salt"] = 1392, ["Ice"] = 1536, ["Glacier"] = 1552,
            ["Glass"] = 1568, ["ForceField"] = 1584, ["Air"] = 1792, ["Water"] = 2048,
            ["Cardboard"] = 2304, ["Carpet"] = 2305, ["CeramicTiles"] = 2306,
            ["ClayRoofTiles"] = 2307, ["RoofShingles"] = 2308, ["Leather"] = 2309,
            ["Plaster"] = 2310, ["Rubber"] = 2311
        };

        /// <summary>Canonical material names available to mapping controls.</summary>
        internal static IReadOnlyList<string> MaterialNames => MaterialNameArray;

        /// <summary>Returns the canonical enum value for a material name.</summary>
        internal static int MaterialValue(string materialName)
        {
            if (!MaterialValues.TryGetValue(materialName, out int value))
            {
                throw new ArgumentException("Unknown Enum.Material name: " + materialName,
                    nameof(materialName));
            }

            return value;
        }

        /// <summary>Converts an absolute in-project path to an AssetDatabase path.</summary>
        internal static string ToAssetPath(string absolutePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.GetFullPath(absolutePath);
            string fullRoot = Path.GetFullPath(projectRoot) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Texture folders must be inside this project's Assets directory.");
            }

            return fullPath.Substring(fullRoot.Length).Replace('\\', '/');
        }

        /// <summary>Applies the shared desktop and WebGL texture import policy.</summary>
        internal static void ApplyTextureImportSettings(string assetPath, bool isAlbedo,
            bool isNormal)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("TextureImporter not found for " + assetPath);
            }

            importer.sRGBTexture = isAlbedo;
            importer.textureType = isNormal
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.flipGreenChannel = false;
            importer.maxTextureSize = 4096;
            importer.crunchedCompression = false;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 8;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.isReadable = false;

            TextureImporterPlatformSettings webGl = importer.GetPlatformTextureSettings("WebGL");
            webGl.name = "WebGL";
            webGl.overridden = true;
            webGl.maxTextureSize = 1024;
            webGl.crunchedCompression = false;
            importer.SetPlatformTextureSettings(webGl);
            importer.SaveAndReimport();
        }

        /// <summary>Merges entries into the project-local override catalog.</summary>
        internal static ScriptableObject MergeOverrideCatalog(
            IEnumerable<RbxTextureCatalogEntryData> incomingEntries)
        {
            return MergeCatalogAt(OverrideCatalogAssetPath, incomingEntries);
        }

        /// <summary>
        /// Merges entries into the catalog asset at <paramref name="catalogAssetPath" />, creating it
        /// when it does not exist yet.
        /// </summary>
        /// <remarks>
        /// WHY: the same merge serves two catalogs — the gitignored project override and the one that
        /// ships inside the package. They differ only in where the asset lives, so the path is a
        /// parameter rather than a second copy of this code.
        /// </remarks>
        internal static ScriptableObject MergeCatalogAt(string catalogAssetPath,
            IEnumerable<RbxTextureCatalogEntryData> incomingEntries)
        {
            Type catalogType = FindCatalogType();
            Directory.CreateDirectory(Path.GetDirectoryName(catalogAssetPath));
            AssetDatabase.Refresh();
            ScriptableObject catalog = AssetDatabase.LoadAssetAtPath(
                catalogAssetPath, catalogType) as ScriptableObject;
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance(catalogType);
                AssetDatabase.CreateAsset(catalog, catalogAssetPath);
            }

            SerializedObject serializedCatalog = new(catalog);
            SerializedProperty entries = serializedCatalog.FindProperty("_entries");
            if (entries == null || !entries.isArray)
            {
                throw new InvalidOperationException(
                    "RbxMaterialTextureCatalog serialized entry list was not found.");
            }

            foreach (RbxTextureCatalogEntryData incoming in incomingEntries)
            {
                int index = FindEntry(entries, incoming.MaterialValue);
                if (index < 0)
                {
                    index = entries.arraySize;
                    entries.InsertArrayElementAtIndex(index);
                }

                WriteEntry(entries.GetArrayElementAtIndex(index), incoming);
            }

            serializedCatalog.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
            return catalog;
        }

        private static Type FindCatalogType()
        {
            Type catalogType = Type.GetType(CatalogTypeName + ", CoreAI.RbxApi.Unity", false);
            if (catalogType == null)
            {
                throw new InvalidOperationException(
                    "CoreAI.RbxApi.Unity is loaded without RbxMaterialTextureCatalog.");
            }

            return catalogType;
        }

        private static int FindEntry(SerializedProperty entries, int materialValue)
        {
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty value = entries.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("_materialValue");
                if (value != null && value.intValue == materialValue)
                {
                    return index;
                }
            }

            return -1;
        }

        private static void WriteEntry(SerializedProperty entry,
            RbxTextureCatalogEntryData data)
        {
            SetString(entry, "_materialName", data.MaterialName);
            SetInt(entry, "_materialValue", data.MaterialValue);
            SetObject(entry, "_albedo", data.AlbedoAssetPath);
            SetObject(entry, "_normal", data.NormalAssetPath);
            SetBool(entry, "_isOpenGlNormal", data.IsOpenGlNormal);
            SetObject(entry, "_roughnessOrSmoothness", data.RoughnessAssetPath);
            SetBool(entry, "_isSmoothnessMap", data.IsSmoothnessMap);
            SetObject(entry, "_metalness", data.MetalnessAssetPath);
            SetObject(entry, "_ambientOcclusion", data.AmbientOcclusionAssetPath);
            SetFloat(entry, "_tileWidthStuds", data.TileWidthStuds);
            SetColor(entry, "_intrinsicColor", data.IntrinsicColor);
            SetFloat(entry, "_partColorInfluence", data.PartColorInfluence);
            SetFloat(entry, "_roughnessScale", data.RoughnessScale);
            SetFloat(entry, "_normalStrength", data.NormalStrength);
        }

        private static void SetString(SerializedProperty parent, string name, string value)
        {
            parent.FindPropertyRelative(name).stringValue = value ?? string.Empty;
        }

        private static void SetInt(SerializedProperty parent, string name, int value)
        {
            parent.FindPropertyRelative(name).intValue = value;
        }

        private static void SetBool(SerializedProperty parent, string name, bool value)
        {
            parent.FindPropertyRelative(name).boolValue = value;
        }

        private static void SetFloat(SerializedProperty parent, string name, float value)
        {
            parent.FindPropertyRelative(name).floatValue = value;
        }

        private static void SetColor(SerializedProperty parent, string name, Color value)
        {
            parent.FindPropertyRelative(name).colorValue = value;
        }

        private static void SetObject(SerializedProperty parent, string name, string assetPath)
        {
            parent.FindPropertyRelative(name).objectReferenceValue = string.IsNullOrEmpty(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }
    }
}

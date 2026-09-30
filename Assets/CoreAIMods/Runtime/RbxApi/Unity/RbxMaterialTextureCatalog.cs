using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>Runtime-authored texture catalog for Rbx material surfaces.</summary>
    [CreateAssetMenu(fileName = "RbxMaterialTextureCatalog",
        menuName = "CoreAI/Rbx Material Texture Catalog")]
    public sealed class RbxMaterialTextureCatalog : ScriptableObject
    {
        [SerializeField] private List<Entry> _entries = new();

        /// <summary>Serialized material entries in authoring order.</summary>
        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>Replaces all entries while preserving the supplied order.</summary>
        public void ReplaceEntries(IEnumerable<Entry> entries)
        {
            _entries.Clear();
            if (entries == null)
            {
                return;
            }

            foreach (Entry entry in entries)
            {
                if (entry != null)
                {
                    _entries.Add(entry);
                }
            }
        }

        /// <summary>Merges packaged and project-local entries by enum value.</summary>
        internal static Dictionary<int, Entry> MergeEntries(IEnumerable<Entry> packagedEntries,
            IEnumerable<Entry> overrideEntries)
        {
            Dictionary<int, Entry> merged = new();
            AddEntries(merged, packagedEntries);
            AddEntries(merged, overrideEntries);
            return merged;
        }

        /// <summary>Creates compatibility entries for the six packaged CC0 sets.</summary>
        internal static Entry[] CreatePackagedCompatibilityEntries(
            Func<string, Texture2D> textureLoader)
        {
            if (textureLoader == null)
            {
                throw new ArgumentNullException(nameof(textureLoader));
            }

            // WHY: these mirror the packaged catalog asset for the six original sets so a missing
            // asset degrades to the same look, including the occlusion map baked from each normal
            // map and the reflectance lift of the photographed metal.
            return new[]
            {
                CreatePackagedEntry(textureLoader, "Wood", 512, "Wood095", 10f, 1f,
                    0.75f, false, 0.25f, 0f),
                CreatePackagedEntry(textureLoader, "WoodPlanks", 528, "Wood095", 8f, 1.1f,
                    0.78f, false, 0.25f, 0f),
                CreatePackagedEntry(textureLoader, "Brick", 848, "Bricks104", 10f, 1.1f,
                    0.82f, false, 0.75f, 0f),
                CreatePackagedEntry(textureLoader, "Cobblestone", 880, "PavingStones151", 14f,
                    1.1f, 0.72f, false, 0.6f, 0f),
                CreatePackagedEntry(textureLoader, "Metal", 1088, "Metal063", 3.5f, 0.85f,
                    0.68f, true, 0f, 0.4f),
                // WHY: Grass005 was a featureless mat — albedo variation 8.5 with a normal too weak
                // to carve blades, so it read as flat green felt. Grass004 shows real blades; the
                // tile drops to 4.5 studs because at 7 the blades fell below one screen pixel.
                CreatePackagedEntry(textureLoader, "Grass", 1280, "Grass004", 4.5f, 1.2f,
                    0.7f, false, 0.45f, 0f)
            };
        }

        private static void AddEntries(Dictionary<int, Entry> destination,
            IEnumerable<Entry> entries)
        {
            if (entries == null)
            {
                return;
            }

            foreach (Entry entry in entries)
            {
                if (entry != null)
                {
                    destination[entry.MaterialValue] = entry;
                }
            }
        }

        private static Entry CreatePackagedEntry(Func<string, Texture2D> textureLoader,
            string materialName, int materialValue, string textureStem, float tileWidthStuds,
            float normalStrength, float partColorInfluence, bool hasMetalness, float cavityStrength,
            float metalAlbedoLift)
        {
            string prefix = "CoreAIRbxTextures/" + textureStem + "_1K-JPG_";
            Entry entry = new()
            {
                MaterialName = materialName,
                MaterialValue = materialValue,
                Albedo = textureLoader(prefix + "Color"),
                Normal = textureLoader(prefix + "NormalGL"),
                IsOpenGlNormal = true,
                RoughnessOrSmoothness = textureLoader(prefix + "Roughness"),
                IsSmoothnessMap = false,
                Metalness = hasMetalness ? textureLoader(prefix + "Metalness") : null,
                AmbientOcclusion = cavityStrength > 0f ? textureLoader(prefix + "Cavity") : null,
                TileWidthStuds = tileWidthStuds,
                IntrinsicColor = Color.white,
                PartColorInfluence = partColorInfluence,
                RoughnessScale = 1f,
                NormalStrength = normalStrength,
                CavityStrength = cavityStrength,
                MetalAlbedoLift = metalAlbedoLift
            };
            return entry;
        }

        /// <summary>One textured Enum.Material surface definition.</summary>
        [Serializable]
        public sealed class Entry
        {
            [SerializeField] private string _materialName = string.Empty;
            [SerializeField] private int _materialValue;
            [SerializeField] private Texture2D _albedo;
            [SerializeField] private Texture2D _normal;
            [SerializeField] private bool _isOpenGlNormal = true;
            [SerializeField] private Texture2D _roughnessOrSmoothness;
            [SerializeField] private bool _isSmoothnessMap;
            [SerializeField] private Texture2D _metalness;
            [SerializeField] private Texture2D _ambientOcclusion;
            [SerializeField] private float _tileWidthStuds = 8f;
            [SerializeField] private Color _intrinsicColor = Color.white;
            [SerializeField, Range(0f, 1f)] private float _partColorInfluence = 0.75f;
            [SerializeField, Min(0f)] private float _roughnessScale = 1f;
            [SerializeField, Min(0f)] private float _normalStrength = 1f;
            [SerializeField, Range(0f, 1f)] private float _cavityStrength;
            [SerializeField, Range(0f, 1f)] private float _metalAlbedoLift;

            /// <summary>Canonical Enum.Material name.</summary>
            public string MaterialName
            {
                get => _materialName;
                set => _materialName = value ?? string.Empty;
            }

            /// <summary>Canonical Enum.Material numeric value.</summary>
            public int MaterialValue
            {
                get => _materialValue;
                set => _materialValue = value;
            }

            /// <summary>Albedo texture sampled as sRGB.</summary>
            public Texture2D Albedo
            {
                get => _albedo;
                set => _albedo = value;
            }

            /// <summary>Tangent-space normal texture.</summary>
            public Texture2D Normal
            {
                get => _normal;
                set => _normal = value;
            }

            /// <summary>Whether the normal uses OpenGL green-up convention.</summary>
            public bool IsOpenGlNormal
            {
                get => _isOpenGlNormal;
                set => _isOpenGlNormal = value;
            }

            /// <summary>Linear roughness or smoothness texture.</summary>
            public Texture2D RoughnessOrSmoothness
            {
                get => _roughnessOrSmoothness;
                set => _roughnessOrSmoothness = value;
            }

            /// <summary>Whether the scalar data map stores smoothness instead of roughness.</summary>
            public bool IsSmoothnessMap
            {
                get => _isSmoothnessMap;
                set => _isSmoothnessMap = value;
            }

            /// <summary>Optional linear metalness texture.</summary>
            public Texture2D Metalness
            {
                get => _metalness;
                set => _metalness = value;
            }

            /// <summary>Optional linear ambient-occlusion texture.</summary>
            public Texture2D AmbientOcclusion
            {
                get => _ambientOcclusion;
                set => _ambientOcclusion = value;
            }

            /// <summary>World-space tile width in studs.</summary>
            public float TileWidthStuds
            {
                get => _tileWidthStuds;
                set => _tileWidthStuds = value;
            }

            /// <summary>Intrinsic material tint multiplied with albedo.</summary>
            public Color IntrinsicColor
            {
                get => _intrinsicColor;
                set => _intrinsicColor = value;
            }

            /// <summary>Strength of Part.Color modulation.</summary>
            public float PartColorInfluence
            {
                get => _partColorInfluence;
                set => _partColorInfluence = value;
            }

            /// <summary>Multiplier applied to sampled roughness.</summary>
            public float RoughnessScale
            {
                get => _roughnessScale;
                set => _roughnessScale = value;
            }

            /// <summary>Normal-map strength.</summary>
            public float NormalStrength
            {
                get => _normalStrength;
                set => _normalStrength = value;
            }

            /// <summary>
            /// How strongly the ambient-occlusion map also darkens the albedo, 0..1. Packaged sets
            /// set it for the occlusion map baked from their normal map, which keeps grooves visible
            /// once the mip chain has averaged the normal map flat; 0 (imported sets) leaves the
            /// occlusion map on ambient light only.
            /// </summary>
            public float CavityStrength
            {
                get => _cavityStrength;
                set => _cavityStrength = Mathf.Clamp01(value);
            }

            /// <summary>
            /// Share of the way metallic texels move toward the brightest shade of their own hue,
            /// 0..1. Only packaged photographed metals, whose colour maps are far darker than real
            /// metal reflectance, set it; 0 leaves a calibrated metalness workflow untouched.
            /// </summary>
            public float MetalAlbedoLift
            {
                get => _metalAlbedoLift;
                set => _metalAlbedoLift = Mathf.Clamp01(value);
            }

            /// <summary>Whether any texture reference is assigned.</summary>
            internal bool HasAnyTexture => _albedo != null || _normal != null ||
                                           _roughnessOrSmoothness != null || _metalness != null ||
                                           _ambientOcclusion != null;

            /// <summary>Whether every required PBR texture reference is assigned.</summary>
            internal bool HasRequiredTextures => _albedo != null && _normal != null &&
                                                  _roughnessOrSmoothness != null;
        }
    }
}

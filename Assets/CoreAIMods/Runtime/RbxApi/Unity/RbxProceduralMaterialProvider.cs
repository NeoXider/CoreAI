using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CoreAI.Mods.Rbx.Datatypes;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>Runtime URP catalog for Rbx part materials. One process-wide cache owns every
    /// native material; lookups only return shared handles and never clone a material per part.</summary>
    public sealed class RbxProceduralMaterialProvider : IRbxMaterialProvider<Material>
    {
        private const string ResourceRoot = "CoreAIRbxMaterials/";
        private const string SurfaceResource = ResourceRoot + "RbxProceduralSurface";
        private const string NeonResource = ResourceRoot + "RbxProceduralNeon";
        private const string TransparentResource = ResourceRoot + "RbxProceduralTransparent";
        private const string FallbackResource = ResourceRoot + "RbxProceduralFallback";

        private const string SurfaceShaderName = "CoreAI/Rbx/Procedural Surface";
        private const string NeonShaderName = "CoreAI/Rbx/Procedural Neon";
        private const string TransparentShaderName = "CoreAI/Rbx/Procedural Transparent";
        private const string FallbackShaderName = "CoreAI/Rbx/Material Fallback";

        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int MaterialColorPropertyId = Shader.PropertyToID("_MaterialColor");
        private static readonly int PartColorInfluencePropertyId = Shader.PropertyToID("_PartColorInfluence");
        private static readonly int MaterialModePropertyId = Shader.PropertyToID("_MaterialMode");
        private static readonly int PatternScalePropertyId = Shader.PropertyToID("_PatternScale");
        private static readonly int BumpStrengthPropertyId = Shader.PropertyToID("_BumpStrength");
        private static readonly int SrcBlendPropertyId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendPropertyId = Shader.PropertyToID("_DstBlend");

        private static readonly CatalogDefinition[] Definitions =
        {
            new("Plastic", 256, ShaderKind.Surface, 0, 2.2f, 0.08f,
                new Color(0.16f, 0.43f, 0.9f), 0.72f),
            new("SmoothPlastic", 272, ShaderKind.Surface, 1, 1.3f, 0.015f,
                new Color(0.9f, 0.16f, 0.2f), 0.76f),
            new("Neon", 288, ShaderKind.Neon, 0, 1f, 0f,
                new Color(0.06f, 0.82f, 1f), 0.62f),
            new("Wood", 512, ShaderKind.Surface, 2, 0.72f, 0.22f,
                new Color(0.5f, 0.22f, 0.055f), 0.32f),
            new("WoodPlanks", 528, ShaderKind.Surface, 3, 0.9f, 0.34f,
                new Color(0.59f, 0.27f, 0.065f), 0.32f),
            new("Marble", 784, ShaderKind.Surface, 7, 0.48f, 0.11f,
                new Color(0.9f, 0.87f, 0.77f), 0.18f),
            new("Slate", 800, ShaderKind.Surface, 8, 0.7f, 0.35f,
                new Color(0.22f, 0.29f, 0.36f), 0.22f),
            new("Concrete", 816, ShaderKind.Surface, 9, 1.25f, 0.38f,
                new Color(0.51f, 0.49f, 0.45f), 0.22f),
            new("Brick", 848, ShaderKind.Surface, 10, 0.8f, 0.42f,
                new Color(0.63f, 0.17f, 0.065f), 0.2f),
            new("Cobblestone", 880, ShaderKind.Surface, 11, 1.15f, 0.48f,
                new Color(0.39f, 0.42f, 0.44f), 0.2f),
            new("Rock", 896, ShaderKind.Surface, 15, 0.7f, 0.58f,
                new Color(0.33f, 0.34f, 0.35f), 0.18f),
            new("CorrodedMetal", 1040, ShaderKind.Surface, 6, 0.9f, 0.45f,
                new Color(0.43f, 0.38f, 0.29f), 0.18f),
            new("DiamondPlate", 1056, ShaderKind.Surface, 5, 0.95f, 0.52f,
                new Color(0.52f, 0.57f, 0.62f), 0.24f),
            new("Metal", 1088, ShaderKind.Surface, 4, 1.0f, 0.06f,
                new Color(0.58f, 0.63f, 0.68f), 0.26f),
            new("Grass", 1280, ShaderKind.Surface, 12, 0.9f, 0.38f,
                new Color(0.13f, 0.46f, 0.075f), 0.2f),
            new("Sand", 1296, ShaderKind.Surface, 13, 1.1f, 0.3f,
                new Color(0.74f, 0.59f, 0.3f), 0.16f),
            new("Fabric", 1312, ShaderKind.Surface, 17, 1.5f, 28f / 100f,
                new Color(0.59f, 0.085f, 0.27f), 0.5f),
            new("Snow", 1328, ShaderKind.Surface, 16, 1.1f, 0.24f,
                new Color(0.9f, 0.95f, 1f), 0.12f),
            new("Ground", 1360, ShaderKind.Surface, 14, 0.78f, 0.44f,
                new Color(28f / 100f, 0.13f, 0.045f), 0.18f),
            new("Ice", 1536, ShaderKind.Transparent, 2, 1.05f, 0.3f,
                new Color(0.4f, 0.74f, 1f, 0.86f), 0.2f),
            new("Glass", 1568, ShaderKind.Transparent, 1, 0.85f, 0.06f,
                new Color(0.47f, 0.78f, 0.9f, 0.72f), 0.34f),
            new("ForceField", 1584, ShaderKind.Transparent, 0, 1.35f, 0f,
                new Color(0.45f, 0.1f, 1f, 0.84f), 0.42f)
        };

        private static readonly Dictionary<int, CatalogDefinition> DefinitionsByValue =
            BuildDefinitionLookup();

        private static readonly ReadOnlyCollection<RbxMaterialId> SupportedMaterialIds =
            Array.AsReadOnly(BuildSupportedMaterialIds());

        private static Dictionary<int, Material> _sharedMaterials;
        private static Material _fallbackMaterial;
        private static int _sharedMaterialAllocationCount;

        /// <summary>Canonical material ids implemented by this catalog.</summary>
        public static IReadOnlyList<RbxMaterialId> SupportedMaterials => SupportedMaterialIds;

        /// <summary>Opaque magenta/black diagnostic material returned for an invalid or unmapped id.</summary>
        public Material FallbackMaterial
        {
            get
            {
                EnsureSharedCache();
                return _fallbackMaterial;
            }
        }

        /// <summary>Resolves one canonical catalog id to its process-wide shared material.</summary>
        public bool TryGetMaterial(in RbxMaterialId material, out Material visualMaterial)
        {
            EnsureSharedCache();
            if (DefinitionsByValue.TryGetValue(material.Value, out CatalogDefinition definition)
                && string.Equals(definition.Name, material.Name, StringComparison.Ordinal)
                && _sharedMaterials.TryGetValue(material.Value, out visualMaterial))
            {
                return true;
            }

            visualMaterial = _fallbackMaterial;
            return false;
        }

        internal static int SharedMaterialAllocationCount => _sharedMaterialAllocationCount;

        internal static void ResetSharedCacheForTests()
        {
            if (_sharedMaterials != null)
            {
                foreach (Material material in _sharedMaterials.Values)
                {
                    DestroyMaterial(material);
                }
            }

            DestroyMaterial(_fallbackMaterial);
            _sharedMaterials = null;
            _fallbackMaterial = null;
            _sharedMaterialAllocationCount = 0;
        }

        private static void EnsureSharedCache()
        {
            if (_sharedMaterials != null)
            {
                return;
            }

            _fallbackMaterial = CreateFallbackMaterial();
            Dictionary<int, Material> materials = new(Definitions.Length);
            foreach (CatalogDefinition definition in Definitions)
            {
                Shader shader = LoadShader(definition.Kind);
                if (shader == null)
                {
                    Debug.LogError(
                        "[CoreAI.RbxApi] Procedural material shader is missing for Enum.Material." +
                        definition.Name + "; the visible diagnostic fallback will be used.");
                    continue;
                }

                Material material = CreateSharedMaterial(shader, definition);
                materials.Add(definition.Value, material);
            }

            _sharedMaterials = materials;
        }

        private static Material CreateFallbackMaterial()
        {
            Shader shader = Resources.Load<Shader>(FallbackResource);
            shader = shader != null ? shader : Shader.Find(FallbackShaderName);
            shader = shader != null ? shader : Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "CoreAI Rbx material fallback shader could not be loaded. A silent material fallback is not allowed.");
            }

            Material material = new(shader)
            {
                name = "CoreAiRbxMaterial_FALLBACK_UNMAPPED",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            _sharedMaterialAllocationCount++;
            return material;
        }

        private static Material CreateSharedMaterial(Shader shader, CatalogDefinition definition)
        {
            Material material = new(shader)
            {
                name = "CoreAiRbxMaterial_" + definition.Name,
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            material.SetColor(BaseColorPropertyId, definition.MaterialColor);
            material.SetColor(ColorPropertyId, Color.white);
            material.SetColor(MaterialColorPropertyId, definition.MaterialColor);
            material.SetFloat(PartColorInfluencePropertyId, definition.PartColorInfluence);
            material.SetFloat(MaterialModePropertyId, definition.Mode);
            material.SetFloat(PatternScalePropertyId, definition.PatternScale);
            material.SetFloat(BumpStrengthPropertyId, definition.BumpStrength);

            if (definition.Kind == ShaderKind.Transparent)
            {
                BlendMode destinationBlend = definition.Mode == 0
                    ? BlendMode.One
                    : BlendMode.OneMinusSrcAlpha;
                material.SetFloat(SrcBlendPropertyId, (float)BlendMode.SrcAlpha);
                material.SetFloat(DstBlendPropertyId, (float)destinationBlend);
                material.renderQueue = (int)RenderQueue.Transparent;
            }

            _sharedMaterialAllocationCount++;
            return material;
        }

        private static Shader LoadShader(ShaderKind kind)
        {
            string resourcePath;
            string shaderName;
            switch (kind)
            {
                case ShaderKind.Surface:
                    resourcePath = SurfaceResource;
                    shaderName = SurfaceShaderName;
                    break;
                case ShaderKind.Neon:
                    resourcePath = NeonResource;
                    shaderName = NeonShaderName;
                    break;
                default:
                    resourcePath = TransparentResource;
                    shaderName = TransparentShaderName;
                    break;
            }

            Shader shader = Resources.Load<Shader>(resourcePath);
            return shader != null ? shader : Shader.Find(shaderName);
        }

        private static Dictionary<int, CatalogDefinition> BuildDefinitionLookup()
        {
            Dictionary<int, CatalogDefinition> lookup = new(Definitions.Length);
            foreach (CatalogDefinition definition in Definitions)
            {
                lookup.Add(definition.Value, definition);
            }

            return lookup;
        }

        private static RbxMaterialId[] BuildSupportedMaterialIds()
        {
            RbxMaterialId[] ids = new RbxMaterialId[Definitions.Length];
            for (int index = 0; index < Definitions.Length; index++)
            {
                CatalogDefinition definition = Definitions[index];
                ids[index] = new RbxMaterialId(definition.Name, definition.Value);
            }

            return ids;
        }

        private static void DestroyMaterial(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(material);
            }
            else
            {
                Object.DestroyImmediate(material);
            }
        }

        private enum ShaderKind
        {
            Surface,
            Neon,
            Transparent
        }

        private readonly struct CatalogDefinition
        {
            public readonly string Name;
            public readonly int Value;
            public readonly ShaderKind Kind;
            public readonly int Mode;
            public readonly float PatternScale;
            public readonly float BumpStrength;
            public readonly Color MaterialColor;
            public readonly float PartColorInfluence;

            public CatalogDefinition(string name, int value, ShaderKind kind, int mode,
                float patternScale, float bumpStrength, Color materialColor, float partColorInfluence)
            {
                Name = name;
                Value = value;
                Kind = kind;
                Mode = mode;
                PatternScale = patternScale;
                BumpStrength = bumpStrength;
                MaterialColor = materialColor;
                PartColorInfluence = partColorInfluence;
            }
        }
    }
}

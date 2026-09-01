using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>Runtime hybrid material catalog. Selected Rbx materials use shared CC0 PBR
    /// textures; every other canonical material delegates to the procedural catalog.</summary>
    public sealed class RbxTextureMaterialProvider : IRbxMaterialProvider<Material>
    {
        private const string ShaderResource = "CoreAIRbxMaterials/RbxTexturedSurface";
        private const string ShaderName = "CoreAI/Rbx/Textured Surface";
        private const string TextureResourceRoot = "CoreAIRbxTextures/";
        private const string MetallicMapKeyword = "_RBX_METALLIC_MAP";

        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int MaterialColorPropertyId = Shader.PropertyToID("_MaterialColor");
        private static readonly int PartColorInfluencePropertyId =
            Shader.PropertyToID("_PartColorInfluence");
        private static readonly int NeutralDefaultPartColorPropertyId =
            Shader.PropertyToID("_NeutralDefaultPartColor");
        private static readonly int TextureScalePropertyId = Shader.PropertyToID("_TextureScale");
        private static readonly int TextureAspectPropertyId = Shader.PropertyToID("_TextureAspect");
        private static readonly int BumpScalePropertyId = Shader.PropertyToID("_BumpScale");
        private static readonly int BaseMapPropertyId = Shader.PropertyToID("_BaseMap");
        private static readonly int BumpMapPropertyId = Shader.PropertyToID("_BumpMap");
        private static readonly int RoughnessMapPropertyId = Shader.PropertyToID("_RoughnessMap");
        private static readonly int MetallicMapPropertyId = Shader.PropertyToID("_MetallicMap");

        private static readonly TextureDefinition[] Definitions =
        {
            new("Wood", 512, "Wood095", 10f, 0.65f, 0.75f, false),
            new("WoodPlanks", 528, "Wood095", 8f, 0.65f, 0.78f, false),
            new("Brick", 848, "Bricks104", 10f, 0.6f, 0.82f, false),
            new("Cobblestone", 880, "PavingStones151", 14f, 0.7f, 0.72f, false),
            new("Metal", 1088, "Metal063", 3.5f, 0.45f, 0.68f, true),
            new("Grass", 1280, "Grass005", 7f, 0.7f, 0.78f, false)
        };

        private static readonly Dictionary<int, TextureDefinition> DefinitionsByValue =
            BuildDefinitionLookup();

        private static Dictionary<int, Material> _sharedMaterials;
        private static bool _textureCatalogPresent;
        private static int _sharedMaterialAllocationCount;

        private readonly RbxProceduralMaterialProvider _proceduralProvider;
        private readonly Func<string, Texture2D> _textureLoader;

        /// <summary>Creates the runtime hybrid provider backed by package Resources.</summary>
        public RbxTextureMaterialProvider()
            : this(new RbxProceduralMaterialProvider(), Resources.Load<Texture2D>)
        {
        }

        internal RbxTextureMaterialProvider(Func<string, Texture2D> textureLoader)
            : this(new RbxProceduralMaterialProvider(), textureLoader)
        {
        }

        private RbxTextureMaterialProvider(RbxProceduralMaterialProvider proceduralProvider,
            Func<string, Texture2D> textureLoader)
        {
            _proceduralProvider = proceduralProvider ??
                throw new ArgumentNullException(nameof(proceduralProvider));
            _textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        }

        /// <summary>The same conspicuous diagnostic material used by the procedural catalog.</summary>
        public Material FallbackMaterial => _proceduralProvider.FallbackMaterial;

        /// <summary>Resolves a process-wide shared textured or procedural material handle.</summary>
        public bool TryGetMaterial(in RbxMaterialId material, out Material visualMaterial)
        {
            if (!DefinitionsByValue.TryGetValue(material.Value, out TextureDefinition definition))
            {
                return _proceduralProvider.TryGetMaterial(in material, out visualMaterial);
            }

            EnsureSharedCache();
            if (!_textureCatalogPresent)
            {
                return _proceduralProvider.TryGetMaterial(in material, out visualMaterial);
            }

            if (string.Equals(definition.Name, material.Name, StringComparison.Ordinal)
                && _sharedMaterials.TryGetValue(material.Value, out visualMaterial))
            {
                return true;
            }

            visualMaterial = FallbackMaterial;
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

            _sharedMaterials = null;
            _textureCatalogPresent = false;
            _sharedMaterialAllocationCount = 0;
        }

        private void EnsureSharedCache()
        {
            if (_sharedMaterials != null)
            {
                return;
            }

            Shader shader = Resources.Load<Shader>(ShaderResource);
            shader = shader != null ? shader : Shader.Find(ShaderName);
            Dictionary<int, LoadedTextureSet> loadedSets = LoadTextureSets();
            Dictionary<int, Material> materials = new(Definitions.Length);
            bool anyTextureLoaded = false;

            foreach (LoadedTextureSet loadedSet in loadedSets.Values)
            {
                anyTextureLoaded |= loadedSet.AnyTextureLoaded;
            }

            _textureCatalogPresent = anyTextureLoaded;
            if (!anyTextureLoaded)
            {
                Debug.LogWarning(
                    "[CoreAI.RbxApi] No texture-backed material resources were found; the complete " +
                    "procedural catalog remains active.");
                _sharedMaterials = materials;
                return;
            }

            if (shader == null)
            {
                Debug.LogError(
                    "[CoreAI.RbxApi] Texture-backed material resources are present but shader '" +
                    ShaderName + "' is missing; affected materials will use the visible diagnostic " +
                    "fallback.");
                _sharedMaterials = materials;
                return;
            }

            foreach (TextureDefinition definition in Definitions)
            {
                LoadedTextureSet loadedSet = loadedSets[definition.Value];
                if (!loadedSet.IsComplete)
                {
                    Debug.LogError(
                        "[CoreAI.RbxApi] Incomplete PBR texture set for Enum.Material." +
                        definition.Name + " (" + definition.TextureStem +
                        "); the visible diagnostic fallback will be used.");
                    continue;
                }

                Material material = CreateSharedMaterial(shader, in definition, in loadedSet);
                materials.Add(definition.Value, material);
            }

            _sharedMaterials = materials;
        }

        private Dictionary<int, LoadedTextureSet> LoadTextureSets()
        {
            Dictionary<int, LoadedTextureSet> loadedSets = new(Definitions.Length);
            foreach (TextureDefinition definition in Definitions)
            {
                string prefix = TextureResourceRoot + definition.TextureStem + "_1K-JPG_";
                Texture2D color = _textureLoader(prefix + "Color");
                Texture2D normal = _textureLoader(prefix + "NormalGL");
                Texture2D roughness = _textureLoader(prefix + "Roughness");
                Texture2D metallic = definition.HasMetallicMap
                    ? _textureLoader(prefix + "Metalness")
                    : null;
                loadedSets.Add(definition.Value,
                    new LoadedTextureSet(color, normal, roughness, metallic,
                        definition.HasMetallicMap));
            }

            return loadedSets;
        }

        private static Material CreateSharedMaterial(Shader shader,
            in TextureDefinition definition, in LoadedTextureSet loadedSet)
        {
            Material material = new(shader)
            {
                name = "CoreAiRbxTextureMaterial_" + definition.Name,
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            material.SetColor(BaseColorPropertyId, Color.white);
            material.SetColor(ColorPropertyId, Color.white);
            material.SetColor(MaterialColorPropertyId, Color.white);
            material.SetFloat(PartColorInfluencePropertyId, definition.PartColorInfluence);
            material.SetFloat(NeutralDefaultPartColorPropertyId, 1f);
            float textureAspect = (float)loadedSet.Color.width / loadedSet.Color.height;
            float textureScale = textureAspect /
                                 RbxSpace.LengthToUnity(definition.TileWidthStuds);
            material.SetFloat(TextureScalePropertyId, textureScale);
            material.SetFloat(TextureAspectPropertyId, textureAspect);
            material.SetFloat(BumpScalePropertyId, definition.BumpScale);
            material.SetTexture(BaseMapPropertyId, loadedSet.Color);
            material.SetTexture(BumpMapPropertyId, loadedSet.Normal);
            material.SetTexture(RoughnessMapPropertyId, loadedSet.Roughness);
            if (definition.HasMetallicMap)
            {
                material.SetTexture(MetallicMapPropertyId, loadedSet.Metallic);
                material.EnableKeyword(MetallicMapKeyword);
            }

            _sharedMaterialAllocationCount++;
            return material;
        }

        private static Dictionary<int, TextureDefinition> BuildDefinitionLookup()
        {
            Dictionary<int, TextureDefinition> lookup = new(Definitions.Length);
            foreach (TextureDefinition definition in Definitions)
            {
                lookup.Add(definition.Value, definition);
            }

            return lookup;
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

        private readonly struct TextureDefinition
        {
            public readonly string Name;
            public readonly int Value;
            public readonly string TextureStem;
            public readonly float TileWidthStuds;
            public readonly float BumpScale;
            public readonly float PartColorInfluence;
            public readonly bool HasMetallicMap;

            public TextureDefinition(string name, int value, string textureStem, float tileWidthStuds,
                float bumpScale, float partColorInfluence, bool hasMetallicMap)
            {
                Name = name;
                Value = value;
                TextureStem = textureStem;
                TileWidthStuds = tileWidthStuds;
                BumpScale = bumpScale;
                PartColorInfluence = partColorInfluence;
                HasMetallicMap = hasMetallicMap;
            }
        }

        private readonly struct LoadedTextureSet
        {
            public readonly Texture2D Color;
            public readonly Texture2D Normal;
            public readonly Texture2D Roughness;
            public readonly Texture2D Metallic;
            public readonly bool RequiresMetallic;

            public LoadedTextureSet(Texture2D color, Texture2D normal, Texture2D roughness,
                Texture2D metallic, bool requiresMetallic)
            {
                Color = color;
                Normal = normal;
                Roughness = roughness;
                Metallic = metallic;
                RequiresMetallic = requiresMetallic;
            }

            public bool AnyTextureLoaded => Color != null || Normal != null || Roughness != null ||
                                            Metallic != null;

            public bool IsComplete => Color != null && Normal != null && Roughness != null &&
                                      (!RequiresMetallic || Metallic != null);
        }
    }
}

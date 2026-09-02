using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>Runtime catalog provider that overlays textured Rbx materials on the complete
    /// procedural catalog.</summary>
    public sealed class RbxTextureMaterialProvider : IRbxMaterialProvider<Material>
    {
        internal const string DefaultCatalogResource =
            "CoreAIRbxTextures/RbxMaterialTextureCatalog";
        internal const string OverrideCatalogResource = "CoreAIRbxTextureCatalogOverride";
        private const string ShaderResource = "CoreAIRbxMaterials/RbxTexturedSurface";
        private const string ShaderName = "CoreAI/Rbx/Textured Surface";
        private const string MetallicMapKeyword = "_RBX_METALLIC_MAP";
        private const string OcclusionMapKeyword = "_RBX_OCCLUSION_MAP";
        private const string DirectXNormalKeyword = "_RBX_NORMAL_DIRECTX";

        private static readonly ReadOnlyCollection<RbxMaterialId> PackagedTexturedMaterialIds =
            Array.AsReadOnly(new[]
            {
                new RbxMaterialId("Wood", 512),
                new RbxMaterialId("WoodPlanks", 528),
                new RbxMaterialId("Brick", 848),
                new RbxMaterialId("Cobblestone", 880),
                new RbxMaterialId("Metal", 1088),
                new RbxMaterialId("Grass", 1280)
            });

        private static Dictionary<int, Material> _sharedMaterials;
        private static Dictionary<int, RbxMaterialTextureCatalog.Entry> _effectiveEntries;
        private static int _sharedMaterialAllocationCount;
        private static float _cachedMetersPerStud;

        private readonly RbxProceduralMaterialProvider _proceduralProvider;
        private readonly Func<string, RbxMaterialTextureCatalog> _catalogLoader;
        private readonly Func<string, Texture2D> _textureLoader;
        private readonly RbxMaterialTextureCatalog _providedDefaultCatalog;
        private readonly RbxMaterialTextureCatalog _providedOverrideCatalog;
        private readonly Shader _providedShader;
        private readonly bool _catalogInputsProvided;
        private readonly bool _forceCompatibilityEntries;

        /// <summary>Creates the runtime hybrid provider backed by package and project Resources.</summary>
        public RbxTextureMaterialProvider()
            : this(new RbxProceduralMaterialProvider(), Resources.Load<RbxMaterialTextureCatalog>,
                Resources.Load<Texture2D>, null, null, null, false, false)
        {
        }

        internal RbxTextureMaterialProvider(Func<string, Texture2D> textureLoader)
            : this(new RbxProceduralMaterialProvider(), Resources.Load<RbxMaterialTextureCatalog>,
                textureLoader, null, null, null, false, true)
        {
        }

        internal RbxTextureMaterialProvider(RbxMaterialTextureCatalog defaultCatalog,
            RbxMaterialTextureCatalog overrideCatalog, Shader shader)
            : this(new RbxProceduralMaterialProvider(), null, Resources.Load<Texture2D>,
                defaultCatalog, overrideCatalog, shader, true, false)
        {
        }

        private RbxTextureMaterialProvider(RbxProceduralMaterialProvider proceduralProvider,
            Func<string, RbxMaterialTextureCatalog> catalogLoader,
            Func<string, Texture2D> textureLoader,
            RbxMaterialTextureCatalog providedDefaultCatalog,
            RbxMaterialTextureCatalog providedOverrideCatalog, Shader providedShader,
            bool catalogInputsProvided, bool forceCompatibilityEntries)
        {
            _proceduralProvider = proceduralProvider ??
                throw new ArgumentNullException(nameof(proceduralProvider));
            _catalogLoader = catalogLoader;
            _textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
            _providedDefaultCatalog = providedDefaultCatalog;
            _providedOverrideCatalog = providedOverrideCatalog;
            _providedShader = providedShader;
            _catalogInputsProvided = catalogInputsProvided;
            _forceCompatibilityEntries = forceCompatibilityEntries;
        }

        /// <summary>The same conspicuous diagnostic material used by the procedural catalog.</summary>
        public Material FallbackMaterial => _proceduralProvider.FallbackMaterial;

        /// <summary>Canonical ids of the six texture sets packaged with CoreAI.</summary>
        internal static IReadOnlyList<RbxMaterialId> TexturedMaterials =>
            PackagedTexturedMaterialIds;

        /// <summary>Cycles per metre for a tile width authored in studs, so one full texture
        /// spans <paramref name="tileWidthStuds"/> studs horizontally at the given session scale.</summary>
        internal static float ComputeTextureScale(float textureAspect, float tileWidthStuds,
            float metersPerStud)
        {
            return textureAspect / (tileWidthStuds * metersPerStud);
        }

        /// <summary>Resolves a process-wide shared textured or procedural material handle.</summary>
        public bool TryGetMaterial(in RbxMaterialId material, out Material visualMaterial)
        {
            EnsureSharedCache();
            if (!_effectiveEntries.TryGetValue(material.Value,
                    out RbxMaterialTextureCatalog.Entry entry))
            {
                return _proceduralProvider.TryGetMaterial(in material, out visualMaterial);
            }

            if (!string.Equals(entry.MaterialName, material.Name, StringComparison.Ordinal))
            {
                return _proceduralProvider.TryGetMaterial(in material, out visualMaterial);
            }

            SyncTextureScaleToSessionScale();
            if (_sharedMaterials.TryGetValue(material.Value, out visualMaterial))
            {
                return true;
            }

            return _proceduralProvider.TryGetMaterial(in material, out visualMaterial);
        }

        internal static int SharedMaterialAllocationCount => _sharedMaterialAllocationCount;

        /// <summary>
        /// Test seam: when true the project-local override catalog is ignored so fixtures that pin
        /// the packaged CC0 sets stay deterministic on a machine with a local 2K catalog installed.
        /// Production never sets it.
        /// </summary>
        internal static bool IgnoreProjectOverrideForTests { get; set; }

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
            _effectiveEntries = null;
            _sharedMaterialAllocationCount = 0;
            _cachedMetersPerStud = 0f;
        }

        /// <summary>Resynchronizes stud-authored tile widths after a session-scale change.</summary>
        private static void SyncTextureScaleToSessionScale()
        {
            float metersPerStud = RbxSpace.MetersPerStud;
            if (ScaleMath.Approximately(metersPerStud, _cachedMetersPerStud))
            {
                return;
            }

            foreach (KeyValuePair<int, Material> pair in _sharedMaterials)
            {
                RbxMaterialTextureCatalog.Entry entry = _effectiveEntries[pair.Key];
                float textureAspect = pair.Value.GetFloat(PropertyIds.TextureAspect);
                pair.Value.SetFloat(PropertyIds.TextureScale,
                    ComputeTextureScale(textureAspect, entry.TileWidthStuds, metersPerStud));
            }

            _cachedMetersPerStud = metersPerStud;
        }

        private void EnsureSharedCache()
        {
            if (_sharedMaterials != null)
            {
                return;
            }

            bool compatibilityEntries;
            Dictionary<int, RbxMaterialTextureCatalog.Entry> entries =
                LoadMergedEntries(out compatibilityEntries);
            Dictionary<int, Material> materials = new(entries.Count);
            _effectiveEntries = entries;
            _sharedMaterials = materials;

            if (compatibilityEntries && !AnyTextureAssigned(entries.Values))
            {
                Debug.LogWarning(
                    "[CoreAI.RbxApi] No texture-backed material resources were found; the complete " +
                    "procedural catalog remains active.");
                entries.Clear();
                return;
            }

            if (entries.Count == 0)
            {
                return;
            }

            Shader shader = _providedShader;
            if (shader == null)
            {
                shader = Resources.Load<Shader>(ShaderResource);
                shader = shader != null ? shader : Shader.Find(ShaderName);
            }

            if (shader == null)
            {
                Debug.LogError(
                    "[CoreAI.RbxApi] Textured material catalog entries are present but shader '" +
                    ShaderName + "' is missing; affected materials will use procedural surfaces.");
                return;
            }

            float metersPerStud = RbxSpace.MetersPerStud;
            foreach (KeyValuePair<int, RbxMaterialTextureCatalog.Entry> pair in entries)
            {
                RbxMaterialTextureCatalog.Entry entry = pair.Value;
                RbxMaterialId id = new(entry.MaterialName, entry.MaterialValue);
                if (!RbxProceduralMaterialProvider.TryGetPartColorContract(in id, out _, out _))
                {
                    Debug.LogError(
                        "[CoreAI.RbxApi] Catalog entry '" + entry.MaterialName + "' (" +
                        entry.MaterialValue + ") is not a canonical Enum.Material item; using " +
                        "procedural material for that value.");
                    continue;
                }

                if (!entry.HasRequiredTextures)
                {
                    Debug.LogError(
                        "[CoreAI.RbxApi] Catalog entry for Enum.Material." + entry.MaterialName +
                        " is missing required albedo, normal, or roughness/smoothness texture; " +
                        "using procedural material.");
                    continue;
                }

                if (entry.TileWidthStuds <= 0f)
                {
                    Debug.LogError(
                        "[CoreAI.RbxApi] Catalog entry for Enum.Material." + entry.MaterialName +
                        " has a non-positive tile width; using procedural material.");
                    continue;
                }

                Material material = CreateSharedMaterial(shader, entry, metersPerStud);
                materials.Add(pair.Key, material);
            }

            _cachedMetersPerStud = metersPerStud;
        }

        private Dictionary<int, RbxMaterialTextureCatalog.Entry> LoadMergedEntries(
            out bool compatibilityEntries)
        {
            RbxMaterialTextureCatalog defaultCatalog = _providedDefaultCatalog;
            RbxMaterialTextureCatalog overrideCatalog = _providedOverrideCatalog;
            compatibilityEntries = false;

            if (!_catalogInputsProvided && !_forceCompatibilityEntries)
            {
                defaultCatalog = _catalogLoader(DefaultCatalogResource);
                overrideCatalog = IgnoreProjectOverrideForTests
                    ? null
                    : _catalogLoader(OverrideCatalogResource);
            }

            IEnumerable<RbxMaterialTextureCatalog.Entry> packagedEntries;
            if (defaultCatalog != null)
            {
                packagedEntries = defaultCatalog.Entries;
            }
            else if (!_catalogInputsProvided || _forceCompatibilityEntries)
            {
                packagedEntries = RbxMaterialTextureCatalog.CreatePackagedCompatibilityEntries(
                    _textureLoader);
                compatibilityEntries = true;
            }
            else
            {
                packagedEntries = Array.Empty<RbxMaterialTextureCatalog.Entry>();
            }

            IEnumerable<RbxMaterialTextureCatalog.Entry> overrideEntries = overrideCatalog != null
                ? overrideCatalog.Entries
                : Array.Empty<RbxMaterialTextureCatalog.Entry>();
            return RbxMaterialTextureCatalog.MergeEntries(packagedEntries, overrideEntries);
        }

        private static bool AnyTextureAssigned(
            IEnumerable<RbxMaterialTextureCatalog.Entry> entries)
        {
            foreach (RbxMaterialTextureCatalog.Entry entry in entries)
            {
                if (entry.HasAnyTexture)
                {
                    return true;
                }
            }

            return false;
        }

        private static Material CreateSharedMaterial(Shader shader,
            RbxMaterialTextureCatalog.Entry entry, float metersPerStud)
        {
            Material material = new(shader)
            {
                name = "CoreAiRbxTextureMaterial_" + entry.MaterialName,
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            Color intrinsicColor = entry.IntrinsicColor;
            material.SetColor(PropertyIds.BaseColor, intrinsicColor);
            material.SetColor(PropertyIds.Color, Color.white);
            material.SetColor(PropertyIds.MaterialColor, intrinsicColor);
            material.SetFloat(PropertyIds.PartColorInfluence,
                Mathf.Clamp01(entry.PartColorInfluence));
            material.SetFloat(PropertyIds.NeutralDefaultPartColor, 1f);
            float textureAspect = (float)entry.Albedo.width / entry.Albedo.height;
            material.SetFloat(PropertyIds.TextureScale,
                ComputeTextureScale(textureAspect, entry.TileWidthStuds, metersPerStud));
            material.SetFloat(PropertyIds.TextureAspect, textureAspect);
            material.SetFloat(PropertyIds.BumpScale, Mathf.Max(0f, entry.NormalStrength));
            material.SetFloat(PropertyIds.RoughnessScale, Mathf.Max(0f, entry.RoughnessScale));
            material.SetFloat(PropertyIds.InvertRoughness, entry.IsSmoothnessMap ? 1f : 0f);
            material.SetTexture(PropertyIds.BaseMap, entry.Albedo);
            material.SetTexture(PropertyIds.BumpMap, entry.Normal);
            material.SetTexture(PropertyIds.RoughnessMap, entry.RoughnessOrSmoothness);
            if (!entry.IsOpenGlNormal)
            {
                material.EnableKeyword(DirectXNormalKeyword);
            }

            if (entry.Metalness != null)
            {
                material.SetTexture(PropertyIds.MetallicMap, entry.Metalness);
                material.EnableKeyword(MetallicMapKeyword);
            }

            if (entry.AmbientOcclusion != null)
            {
                material.SetTexture(PropertyIds.OcclusionMap, entry.AmbientOcclusion);
                material.EnableKeyword(OcclusionMapKeyword);
            }

            _sharedMaterialAllocationCount++;
            return material;
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

        /// <summary>Lazily initialized native shader property identifiers.</summary>
        private static class PropertyIds
        {
            public static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
            public static readonly int Color = Shader.PropertyToID("_Color");
            public static readonly int MaterialColor = Shader.PropertyToID("_MaterialColor");
            public static readonly int PartColorInfluence = Shader.PropertyToID("_PartColorInfluence");
            public static readonly int NeutralDefaultPartColor =
                Shader.PropertyToID("_NeutralDefaultPartColor");
            public static readonly int TextureScale = Shader.PropertyToID("_TextureScale");
            public static readonly int TextureAspect = Shader.PropertyToID("_TextureAspect");
            public static readonly int BumpScale = Shader.PropertyToID("_BumpScale");
            public static readonly int RoughnessScale = Shader.PropertyToID("_RoughnessScale");
            public static readonly int InvertRoughness = Shader.PropertyToID("_InvertRoughness");
            public static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
            public static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
            public static readonly int RoughnessMap = Shader.PropertyToID("_RoughnessMap");
            public static readonly int MetallicMap = Shader.PropertyToID("_MetallicMap");
            public static readonly int OcclusionMap = Shader.PropertyToID("_OcclusionMap");
        }
    }
}

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
    public sealed class RbxTextureMaterialProvider : IRbxMaterialProvider<Material>,
        IRbxMaterialVariantConsumer
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
                new RbxMaterialId("Grass", 1280),
                new RbxMaterialId("Slate", 800),
                new RbxMaterialId("Limestone", 820),
                new RbxMaterialId("Sandstone", 912),
                new RbxMaterialId("Granite", 832),
                new RbxMaterialId("Basalt", 788),
                new RbxMaterialId("Rock", 896),
                new RbxMaterialId("Concrete", 816),
                new RbxMaterialId("Marble", 784),
                new RbxMaterialId("Plaster", 2310),
                new RbxMaterialId("Pavement", 836),
                new RbxMaterialId("Pebble", 864),
                new RbxMaterialId("CeramicTiles", 2306),
                new RbxMaterialId("ClayRoofTiles", 2307),
                new RbxMaterialId("RoofShingles", 2308),
                new RbxMaterialId("CorrodedMetal", 1040),
                new RbxMaterialId("DiamondPlate", 1056),
                new RbxMaterialId("Foil", 1072),
                new RbxMaterialId("LeafyGrass", 1284),
                new RbxMaterialId("Ground", 1360),
                new RbxMaterialId("Mud", 1344),
                new RbxMaterialId("Sand", 1296),
                new RbxMaterialId("Snow", 1328),
                new RbxMaterialId("Ice", 1536),
                new RbxMaterialId("CrackedLava", 804),
                new RbxMaterialId("Asphalt", 1376),
                new RbxMaterialId("Fabric", 1312),
                new RbxMaterialId("Carpet", 2305),
                new RbxMaterialId("Leather", 2309),
                new RbxMaterialId("Cardboard", 2304),
                new RbxMaterialId("Rubber", 2311)
            });

        private static Dictionary<int, Material> _sharedMaterials;
        private static Dictionary<int, RbxMaterialTextureCatalog.Entry> _effectiveEntries;
        private static Dictionary<RbxMaterialId, VariantMaterialRecord> _variantMaterials;
        private static Shader _sharedShader;
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

        /// <summary>Variant lookup port the binder points at the world's MaterialService;
        /// null renders every part plain.</summary>
        public IRbxMaterialVariantSource VariantSource { get; set; }

        /// <summary>Canonical ids of the 36 texture sets packaged with CoreAI.</summary>
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
            if (material.Variant != null)
            {
                return TryGetVariantMaterial(in material, out visualMaterial);
            }

            return TryGetPlainMaterial(in material, out visualMaterial);
        }

        private bool TryGetPlainMaterial(in RbxMaterialId material, out Material visualMaterial)
        {
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

        private bool TryGetVariantMaterial(in RbxMaterialId material, out Material visualMaterial)
        {
            IRbxMaterialVariantSource source = VariantSource;
            if (source == null ||
                !source.TryGetVariant(material.Variant, out RbxMaterialVariantData data))
            {
                RbxMaterialId plain = new(material.Name, material.Value);
                return TryGetPlainMaterial(in plain, out visualMaterial);
            }

            if (_variantMaterials.TryGetValue(material, out VariantMaterialRecord record))
            {
                if (!VariantDataEquals(record.Snapshot, in data))
                {
                    // WHY: the entry the record was built from supplies every slot the variant does
                    // not override, so a variant that repoints its BaseMaterial must re-resolve it.
                    // Reusing the cached entry left the unoverridden maps on the OLD base material,
                    // which is exactly what happens when one world reuses a variant name from
                    // another with a different base.
                    if (TryResolveVariantBaseEntry(in material, in data,
                            out RbxMaterialTextureCatalog.Entry refreshedEntry))
                    {
                        record.BaseEntry = refreshedEntry;
                    }

                    ApplyVariantOverrides(record.Material, record.BaseEntry, in data,
                        material.Variant);
                    record.Snapshot = data;
                }

                SyncTextureScaleToSessionScale();
                visualMaterial = record.Material;
                return true;
            }

            if (!TryResolveVariantBaseEntry(in material, in data,
                    out RbxMaterialTextureCatalog.Entry baseEntry) || _sharedShader == null)
            {
                RbxMaterialId plain = new(material.Name, material.Value);
                return TryGetPlainMaterial(in plain, out visualMaterial);
            }

            Material variantMaterial =
                CreateSharedMaterial(_sharedShader, baseEntry, RbxSpace.MetersPerStud);
            variantMaterial.name =
                "CoreAiRbxTextureMaterial_" + material.Name + "_" + material.Variant;
            ApplyVariantOverrides(variantMaterial, baseEntry, in data, material.Variant);
            _variantMaterials[material] =
                new VariantMaterialRecord(variantMaterial, baseEntry, data);
            SyncTextureScaleToSessionScale();
            visualMaterial = variantMaterial;
            return true;
        }

        private bool TryResolveVariantBaseEntry(in RbxMaterialId material,
            in RbxMaterialVariantData data, out RbxMaterialTextureCatalog.Entry baseEntry)
        {
            baseEntry = null;
            if (_effectiveEntries == null || _sharedMaterials == null)
            {
                return false;
            }

            if (TryGetValidatedEntry(data.BaseMaterial, out baseEntry))
            {
                return true;
            }

            return TryGetValidatedEntry(in material, out baseEntry);
        }

        private bool TryGetValidatedEntry(in RbxMaterialId id,
            out RbxMaterialTextureCatalog.Entry entry)
        {
            entry = null;
            if (!_effectiveEntries.TryGetValue(id.Value, out entry))
            {
                return false;
            }

            if (!string.Equals(entry.MaterialName, id.Name, StringComparison.Ordinal))
            {
                entry = null;
                return false;
            }

            if (!_sharedMaterials.ContainsKey(id.Value))
            {
                entry = null;
                return false;
            }

            return true;
        }

        private void ApplyVariantOverrides(Material material,
            RbxMaterialTextureCatalog.Entry baseEntry, in RbxMaterialVariantData data,
            string variantName)
        {
            Texture2D albedo = ResolveVariantMap(variantName, "ColorMap", data.ColorMap) ??
                baseEntry.Albedo;
            Texture2D normal = ResolveVariantMap(variantName, "NormalMap", data.NormalMap) ??
                baseEntry.Normal;
            Texture2D roughness =
                ResolveVariantMap(variantName, "RoughnessMap", data.RoughnessMap) ??
                baseEntry.RoughnessOrSmoothness;
            Texture2D metalness = baseEntry.Metalness;
            if (!string.IsNullOrEmpty(data.MetalnessMap))
            {
                metalness = ResolveVariantMap(variantName, "MetalnessMap", data.MetalnessMap) ??
                    baseEntry.Metalness;
            }

            material.SetTexture(PropertyIds.BaseMap, albedo);
            material.SetTexture(PropertyIds.BumpMap, normal);
            material.SetTexture(PropertyIds.RoughnessMap, roughness);
            float textureAspect = material.GetFloat(PropertyIds.TextureAspect);
            if (albedo != null && albedo.height > 0)
            {
                textureAspect = (float)albedo.width / albedo.height;
                material.SetFloat(PropertyIds.TextureAspect, textureAspect);
            }

            float tileWidthStuds =
                data.StudsPerTile > 0f ? data.StudsPerTile : baseEntry.TileWidthStuds;
            material.SetFloat(PropertyIds.TextureScale,
                ComputeTextureScale(textureAspect, tileWidthStuds, RbxSpace.MetersPerStud));
            if (metalness != null)
            {
                material.SetTexture(PropertyIds.MetallicMap, metalness);
                material.EnableKeyword(MetallicMapKeyword);
            }
            else
            {
                material.SetTexture(PropertyIds.MetallicMap, null);
                material.DisableKeyword(MetallicMapKeyword);
            }
        }

        private Texture2D ResolveVariantMap(string variantName, string slotName,
            string mapReference)
        {
            if (string.IsNullOrEmpty(mapReference))
            {
                return null;
            }

            Texture2D texture = _textureLoader(mapReference);
            if (texture == null)
            {
                Debug.LogError("[CoreAI.RbxApi] MaterialVariant '" + variantName + "' map '" +
                    mapReference + "' (" + slotName + ") failed to load; keeping the base " +
                    "texture for that slot.");
            }

            return texture;
        }

        private static bool VariantDataEquals(in RbxMaterialVariantData left,
            in RbxMaterialVariantData right)
        {
            return left.BaseMaterial == right.BaseMaterial &&
                string.Equals(left.ColorMap, right.ColorMap, StringComparison.Ordinal) &&
                string.Equals(left.NormalMap, right.NormalMap, StringComparison.Ordinal) &&
                string.Equals(left.RoughnessMap, right.RoughnessMap, StringComparison.Ordinal) &&
                string.Equals(left.MetalnessMap, right.MetalnessMap, StringComparison.Ordinal) &&
                left.StudsPerTile == right.StudsPerTile;
        }

        /// <summary>One cached variant shared material with the base entry and data snapshot
        /// it was built from.</summary>
        private sealed class VariantMaterialRecord
        {
            public VariantMaterialRecord(Material material,
                RbxMaterialTextureCatalog.Entry baseEntry, RbxMaterialVariantData snapshot)
            {
                Material = material;
                BaseEntry = baseEntry;
                Snapshot = snapshot;
            }

            public Material Material { get; }

            public RbxMaterialTextureCatalog.Entry BaseEntry { get; set; }

            public RbxMaterialVariantData Snapshot { get; set; }

            public float EffectiveTileWidthStuds => Snapshot.StudsPerTile > 0f
                ? Snapshot.StudsPerTile
                : BaseEntry.TileWidthStuds;
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

            if (_variantMaterials != null)
            {
                foreach (VariantMaterialRecord record in _variantMaterials.Values)
                {
                    DestroyMaterial(record.Material);
                }
            }

            _sharedMaterials = null;
            _effectiveEntries = null;
            _variantMaterials = null;
            _sharedShader = null;
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

            if (_variantMaterials != null)
            {
                foreach (VariantMaterialRecord record in _variantMaterials.Values)
                {
                    float textureAspect = record.Material.GetFloat(PropertyIds.TextureAspect);
                    record.Material.SetFloat(PropertyIds.TextureScale,
                        ComputeTextureScale(textureAspect, record.EffectiveTileWidthStuds,
                            metersPerStud));
                }
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
            _variantMaterials = new Dictionary<RbxMaterialId, VariantMaterialRecord>();

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

            _sharedShader = shader;
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

using System;
using System.Collections.Generic;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>
    /// Per-<c>Enum.Material</c> surface tuning applied when a texture set is imported into the catalog:
    /// how many studs one texture tile covers, how strongly its normal map reads, and how its roughness
    /// is scaled.
    /// <para>
    /// WHY: importers used to leave every entry at the generic default (8 studs, normal 1.0), so a
    /// cobblestone yard, a brick wall and a grass field all repeated at the same rate and every surface
    /// got the same relief. Tiling is what makes a photographed surface read at Part scale, so it belongs
    /// to the material, not to the importer's default. Values for Wood, WoodPlanks, Brick, Cobblestone,
    /// Metal and Grass are the ones hand-tuned for the packaged CC0 catalog and are kept as the anchors
    /// the rest of the table is scaled against.
    /// </para>
    /// <para>
    /// WHY normal strengths stay at or under 1.2: the earlier 1.3-1.5 values were tuned while the
    /// shader's tangent frame was shrunk to a few percent, so they had no visible effect. With a unit
    /// frame, close and far shots showed Cobblestone at 1.5 as a glittering highlight. Relief at a
    /// distance comes from the baked cavity map (<see cref="PackagedRelief"/>), not from a stronger
    /// normal map.
    /// </para>
    /// </summary>
    internal static class RbxMaterialSurfaceProfiles
    {
        /// <summary>Tiling and relief for one <c>Enum.Material</c>.</summary>
        internal readonly struct Profile
        {
            public Profile(float tileWidthStuds, float normalStrength, float roughnessScale,
                float partColorInfluence)
            {
                TileWidthStuds = tileWidthStuds;
                NormalStrength = normalStrength;
                RoughnessScale = roughnessScale;
                PartColorInfluence = partColorInfluence;
            }

            /// <summary>Studs covered by one texture tile; bigger means fewer, larger repeats.</summary>
            public float TileWidthStuds { get; }

            /// <summary>Multiplier on the normal map: above 1 deepens relief, below 1 flattens it.</summary>
            public float NormalStrength { get; }

            /// <summary>Multiplier on the roughness map; below 1 makes the surface glossier.</summary>
            public float RoughnessScale { get; }

            /// <summary>
            /// How strongly <c>Part.Color</c> tints this material, 0..1. Low values keep a busy or dark
            /// texture readable under a saturated colour; high values let a prop take the colour fully.
            /// </summary>
            public float PartColorInfluence { get; }
        }

        private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.Ordinal)
        {
            // tile studs, normal strength, roughness scale, Part.Color influence.
            // Plastics and light emitters: no photographed relief, and the colour is the whole point.
            ["Plastic"] = new Profile(8f, 0.4f, 0.85f, 0.95f),
            ["SmoothPlastic"] = new Profile(8f, 0.25f, 0.6f, 0.95f),
            ["Neon"] = new Profile(8f, 0f, 1f, 1f),
            ["ForceField"] = new Profile(8f, 0f, 1f, 1f),
            ["Air"] = new Profile(8f, 0f, 1f, 1f),

            // Wood.
            ["Wood"] = new Profile(10f, 1f, 0.75f, 0.65f),
            ["WoodPlanks"] = new Profile(9f, 1.1f, 0.78f, 0.65f),

            // Masonry and cut stone: walls and towers, medium repeats.
            ["Brick"] = new Profile(10f, 1.1f, 0.82f, 0.6f),
            ["Slate"] = new Profile(10f, 1.1f, 0.8f, 0.65f),
            ["Limestone"] = new Profile(12f, 1.05f, 0.82f, 0.7f),
            ["Granite"] = new Profile(12f, 1f, 0.78f, 0.65f),
            ["Sandstone"] = new Profile(10f, 1.15f, 0.85f, 0.7f),
            ["Marble"] = new Profile(12f, 0.55f, 0.55f, 0.7f),
            ["Basalt"] = new Profile(13f, 1.15f, 0.8f, 0.55f),
            ["Concrete"] = new Profile(14f, 1f, 0.85f, 0.75f),
            ["Plaster"] = new Profile(8f, 1f, 0.9f, 0.85f),
            ["Salt"] = new Profile(12f, 1f, 0.85f, 0.8f),

            // Loose ground and paving: coarse features, the widest repeats.
            ["Cobblestone"] = new Profile(14f, 1.1f, 0.72f, 0.7f),
            ["Pavement"] = new Profile(12f, 1.1f, 0.8f, 0.7f),
            ["Pebble"] = new Profile(9f, 1.2f, 0.8f, 0.7f),
            ["Rock"] = new Profile(18f, 1.2f, 0.85f, 0.65f),
            ["Sand"] = new Profile(7f, 1.05f, 0.9f, 0.75f),
            ["Mud"] = new Profile(15f, 1.15f, 0.85f, 0.7f),
            ["Ground"] = new Profile(16f, 1.15f, 0.88f, 0.7f),
            ["Asphalt"] = new Profile(8f, 1.1f, 0.85f, 0.6f),
            ["Grass"] = new Profile(4.5f, 1.2f, 0.78f, 0.7f),
            ["LeafyGrass"] = new Profile(5f, 1.2f, 0.8f, 0.7f),
            ["Snow"] = new Profile(9f, 1f, 0.8f, 0.8f),
            ["CrackedLava"] = new Profile(13f, 1.2f, 0.85f, 0.6f),

            // Transparent and frozen surfaces are glossy, so their roughness is pulled well down.
            ["Ice"] = new Profile(14f, 0.85f, 0.45f, 0.8f),
            ["Glacier"] = new Profile(16f, 0.9f, 0.5f, 0.8f),
            ["Glass"] = new Profile(8f, 0.3f, 0.35f, 0.9f),
            ["Water"] = new Profile(12f, 0.6f, 0.35f, 0.9f),

            // Metals: manufactured panels, so a small tile, and the colour must not wash out the metal.
            ["Metal"] = new Profile(3.5f, 0.85f, 0.68f, 0.45f),
            ["CorrodedMetal"] = new Profile(6f, 1.1f, 0.85f, 0.5f),
            ["DiamondPlate"] = new Profile(6f, 1.2f, 0.72f, 0.5f),
            ["Foil"] = new Profile(3f, 1f, 0.55f, 0.5f),

            // Roofs and interior tiling.
            ["ClayRoofTiles"] = new Profile(7f, 1.2f, 0.8f, 0.7f),
            ["RoofShingles"] = new Profile(7f, 1.2f, 0.85f, 0.7f),
            ["CeramicTiles"] = new Profile(6f, 0.95f, 0.7f, 0.8f),

            // Soft goods and props: the smallest weave, and they take a colour readily.
            ["Fabric"] = new Profile(2.5f, 1.1f, 0.9f, 0.9f),
            ["Carpet"] = new Profile(3f, 1.15f, 0.95f, 0.9f),
            ["Leather"] = new Profile(2.5f, 1.05f, 0.8f, 0.85f),
            ["Cardboard"] = new Profile(3f, 1f, 0.9f, 0.8f),
            ["Rubber"] = new Profile(3f, 1f, 0.9f, 0.85f)
        };

        /// <summary>Relief that belongs to the packaged CC0 set of a material, not to any imported set.</summary>
        internal readonly struct PackagedRelief
        {
            public PackagedRelief(float cavityStrength, float metalAlbedoLift)
            {
                CavityStrength = cavityStrength;
                MetalAlbedoLift = metalAlbedoLift;
            }

            /// <summary>
            /// How strongly the occlusion map baked from the set's normal map darkens the albedo;
            /// 0 means no occlusion map is baked or wired up for the set.
            /// </summary>
            public float CavityStrength { get; }

            /// <summary>Reflectance lift for a photographed metal colour map; 0 for dielectrics.</summary>
            public float MetalAlbedoLift { get; }
        }

        // WHY: these only apply to the CC0 sets this package ships or fetches (RbxCc0TextureSets). A
        // Bridge or Fab import never passes through ApplyPackagedRelief, so a calibrated set with its
        // own AO and metal colour keeps the neutral 0 / 0. Cavity strengths were chosen from close,
        // mid and far shots: grooved masonry and paving read as recessed from about 0.5, while fine
        // grain (wood, fabric) only needs a trace before it turns into dirt. Concrete, Plaster,
        // Asphalt, Snow and Cardboard bake to broad blotches rather than grooves (Concrete read as
        // brown stains even at 0.15), so they get no cavity map.
        private static readonly Dictionary<string, PackagedRelief> PackagedReliefs =
            new(StringComparer.Ordinal)
            {
                ["Brick"] = new PackagedRelief(0.75f, 0f),
                ["Cobblestone"] = new PackagedRelief(0.6f, 0f),
                ["Pavement"] = new PackagedRelief(0.55f, 0f),
                ["Pebble"] = new PackagedRelief(0.5f, 0f),
                ["Slate"] = new PackagedRelief(0.6f, 0f),
                ["Limestone"] = new PackagedRelief(0.5f, 0f),
                ["CeramicTiles"] = new PackagedRelief(0.5f, 0f),
                ["Sandstone"] = new PackagedRelief(0.5f, 0f),
                ["Basalt"] = new PackagedRelief(0.55f, 0f),
                ["Rock"] = new PackagedRelief(0.5f, 0f),
                ["Concrete"] = new PackagedRelief(0f, 0f),
                ["Plaster"] = new PackagedRelief(0f, 0f),
                ["Asphalt"] = new PackagedRelief(0f, 0f),
                ["Ground"] = new PackagedRelief(0.45f, 0f),
                ["Mud"] = new PackagedRelief(0.45f, 0f),
                ["Sand"] = new PackagedRelief(0.35f, 0f),
                ["Grass"] = new PackagedRelief(0.45f, 0f),
                ["LeafyGrass"] = new PackagedRelief(0.45f, 0f),
                ["Snow"] = new PackagedRelief(0f, 0f),
                ["CrackedLava"] = new PackagedRelief(0.5f, 0f),
                ["ClayRoofTiles"] = new PackagedRelief(0.55f, 0f),
                ["RoofShingles"] = new PackagedRelief(0.55f, 0f),
                ["Wood"] = new PackagedRelief(0.25f, 0f),
                ["WoodPlanks"] = new PackagedRelief(0.45f, 0f),
                ["Fabric"] = new PackagedRelief(0.35f, 0f),
                ["Carpet"] = new PackagedRelief(0.35f, 0f),
                ["Leather"] = new PackagedRelief(0.3f, 0f),
                ["Cardboard"] = new PackagedRelief(0f, 0f),
                ["Rubber"] = new PackagedRelief(0.3f, 0f),
                ["CorrodedMetal"] = new PackagedRelief(0.35f, 0.4f),
                ["DiamondPlate"] = new PackagedRelief(0.45f, 0.4f),
                ["Metal"] = new PackagedRelief(0f, 0.4f)
            };

        /// <summary>The packaged-set relief for a material; zero when it has none.</summary>
        internal static PackagedRelief ReliefFor(string materialName)
        {
            return !string.IsNullOrEmpty(materialName)
                   && PackagedReliefs.TryGetValue(materialName, out PackagedRelief relief)
                ? relief
                : new PackagedRelief(0f, 0f);
        }

        /// <summary>
        /// Stamps the packaged-set relief onto an entry built from a CC0 set. Cavity needs an
        /// occlusion map and the lift needs a metalness map; without them the entry stays neutral.
        /// </summary>
        internal static void ApplyPackagedRelief(RbxTextureCatalogEntryData entry)
        {
            if (entry == null)
            {
                return;
            }

            PackagedRelief relief = ReliefFor(entry.MaterialName);
            entry.CavityStrength = string.IsNullOrEmpty(entry.AmbientOcclusionAssetPath)
                ? 0f
                : relief.CavityStrength;
            entry.MetalAlbedoLift = string.IsNullOrEmpty(entry.MetalnessAssetPath)
                ? 0f
                : relief.MetalAlbedoLift;
        }

        /// <summary>The tuning for a material, or the generic default when the name is unknown.</summary>
        internal static Profile For(string materialName)
        {
            return !string.IsNullOrEmpty(materialName)
                   && Profiles.TryGetValue(materialName, out Profile profile)
                ? profile
                : new Profile(8f, 1f, 1f, 0.75f);
        }

        /// <summary>Whether the table has an explicit entry for this material.</summary>
        internal static bool Has(string materialName)
        {
            return !string.IsNullOrEmpty(materialName) && Profiles.ContainsKey(materialName);
        }

        /// <summary>Stamps the material's tiling and relief onto a catalog entry about to be written.</summary>
        internal static void Apply(RbxTextureCatalogEntryData entry)
        {
            if (entry == null)
            {
                return;
            }

            Profile profile = For(entry.MaterialName);
            entry.TileWidthStuds = profile.TileWidthStuds;
            entry.NormalStrength = profile.NormalStrength;
            entry.RoughnessScale = profile.RoughnessScale;
            entry.PartColorInfluence = profile.PartColorInfluence;
        }
    }
}

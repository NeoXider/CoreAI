using System.Collections.Generic;
using CoreAI.Editor.RbxMaterials;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi
{
    /// <summary>
    /// Guards the per-material tiling and relief table: every entry names a real
    /// <c>Enum.Material</c>, the values stay inside a sane range, and the six surfaces hand-tuned for
    /// the packaged CC0 catalog keep the numbers that were verified by eye.
    /// </summary>
    public sealed class RbxMaterialSurfaceProfilesEditModeTests
    {
        /// <summary>Tile width, roughness scale and Part.Color influence tuned by eye for the CC0 set.</summary>
        private static readonly Dictionary<string, (float Tile, float Roughness, float PartColor)>
            PackagedAnchors = new()
            {
                ["Wood"] = (10f, 0.75f, 0.65f),
                ["WoodPlanks"] = (9f, 0.78f, 0.65f),
                ["Brick"] = (10f, 0.82f, 0.6f),
                ["Cobblestone"] = (14f, 0.72f, 0.7f),
                ["Metal"] = (3.5f, 0.68f, 0.45f),
                ["Grass"] = (4.5f, 0.78f, 0.7f)
            };

        [Test]
        public void UnknownMaterial_FallsBackToTheGenericProfile()
        {
            RbxMaterialSurfaceProfiles.Profile profile =
                RbxMaterialSurfaceProfiles.For("NotAMaterial");

            Assert.IsFalse(RbxMaterialSurfaceProfiles.Has("NotAMaterial"));
            Assert.AreEqual(8f, profile.TileWidthStuds, 0.001f);
            Assert.AreEqual(1f, profile.NormalStrength, 0.001f);
            Assert.AreEqual(1f, profile.RoughnessScale, 0.001f);
            Assert.AreEqual(0.75f, profile.PartColorInfluence, 0.001f);
        }

        [Test]
        public void EveryProfiledMaterial_IsARealRobloxMaterial()
        {
            foreach (string name in RbxMaterialCatalogEditorUtility.MaterialNames)
            {
                if (!RbxMaterialSurfaceProfiles.Has(name))
                {
                    continue;
                }

                Assert.AreNotEqual(0, RbxMaterialCatalogEditorUtility.MaterialValue(name),
                    $"'{name}' has a surface profile but no Enum.Material value");
            }
        }

        [Test]
        public void EveryTexturedMaterial_HasItsOwnProfile()
        {
            // WHY: a material that ships textures but no profile silently falls back to the generic
            // 8-studs-for-everything tiling, which is exactly the flat look this table replaced.
            string[] textured =
            {
                "Brick", "Wood", "WoodPlanks", "Cobblestone", "Metal", "Grass", "Pavement", "Pebble",
                "CeramicTiles", "LeafyGrass", "Mud", "Ground", "ClayRoofTiles", "RoofShingles",
                "Fabric", "Carpet", "Leather", "Slate", "Sandstone", "Limestone", "Granite", "Basalt",
                "Concrete", "Asphalt", "Snow", "Sand", "Marble", "Cardboard", "Plaster", "Rubber",
                "CorrodedMetal", "DiamondPlate", "CrackedLava", "Ice", "Foil", "Rock"
            };

            foreach (string name in textured)
            {
                Assert.IsTrue(RbxMaterialSurfaceProfiles.Has(name), $"'{name}' has no surface profile");
            }
        }

        [Test]
        public void PackagedCatalogAnchors_KeepTheirHandTunedValues()
        {
            foreach (KeyValuePair<string, (float Tile, float Roughness, float PartColor)> anchor
                     in PackagedAnchors)
            {
                RbxMaterialSurfaceProfiles.Profile profile =
                    RbxMaterialSurfaceProfiles.For(anchor.Key);
                Assert.AreEqual(anchor.Value.Tile, profile.TileWidthStuds, 0.001f,
                    $"'{anchor.Key}' drifted from the tiling tuned for the packaged CC0 catalog");
                Assert.AreEqual(anchor.Value.Roughness, profile.RoughnessScale, 0.001f,
                    $"'{anchor.Key}' drifted from the roughness tuned for the packaged CC0 catalog");
                Assert.AreEqual(anchor.Value.PartColor, profile.PartColorInfluence, 0.001f,
                    $"'{anchor.Key}' drifted from the Part.Color influence tuned for the CC0 catalog");
            }
        }

        [Test]
        public void EveryProfile_StaysInsideAUsableRange()
        {
            foreach (string name in RbxMaterialCatalogEditorUtility.MaterialNames)
            {
                if (!RbxMaterialSurfaceProfiles.Has(name))
                {
                    continue;
                }

                RbxMaterialSurfaceProfiles.Profile profile = RbxMaterialSurfaceProfiles.For(name);
                Assert.Greater(profile.TileWidthStuds, 0f, $"{name} tile width");
                Assert.LessOrEqual(profile.TileWidthStuds, 64f, $"{name} tile width");
                Assert.GreaterOrEqual(profile.NormalStrength, 0f, $"{name} normal strength");
                Assert.LessOrEqual(profile.NormalStrength, 2f, $"{name} normal strength");
                Assert.Greater(profile.RoughnessScale, 0f, $"{name} roughness scale");
                Assert.LessOrEqual(profile.RoughnessScale, 2f, $"{name} roughness scale");
                Assert.GreaterOrEqual(profile.PartColorInfluence, 0f, $"{name} part colour influence");
                Assert.LessOrEqual(profile.PartColorInfluence, 1f, $"{name} part colour influence");
            }
        }

        [Test]
        public void Apply_StampsTheProfileOntoACatalogEntry()
        {
            RbxTextureCatalogEntryData entry = new() { MaterialName = "Cobblestone" };

            RbxMaterialSurfaceProfiles.Apply(entry);

            Assert.AreEqual(14f, entry.TileWidthStuds, 0.001f);
            Assert.AreEqual(1.5f, entry.NormalStrength, 0.001f);
            Assert.AreEqual(0.72f, entry.RoughnessScale, 0.001f);
            Assert.AreEqual(0.7f, entry.PartColorInfluence, 0.001f);
        }
    }
}

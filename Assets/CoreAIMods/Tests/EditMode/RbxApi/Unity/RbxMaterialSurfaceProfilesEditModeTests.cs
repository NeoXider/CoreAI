using System.Collections.Generic;
using CoreAI.Editor.RbxMaterials;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEngine;

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
                ["Grass"] = (4.5f, 3.2f, 0.7f)
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
                // WHY: up to 4, because a CC0 roughness map can average a quarter of the surface's real
                // roughness (Grass004 averages 0.26 and needs 3.2 to read as a matte lawn).
                Assert.LessOrEqual(profile.RoughnessScale, 4f, $"{name} roughness scale");
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
            Assert.AreEqual(1.1f, entry.NormalStrength, 0.001f);
            Assert.AreEqual(0.72f, entry.RoughnessScale, 0.001f);
            Assert.AreEqual(0.7f, entry.PartColorInfluence, 0.001f);
        }

        [Test]
        public void ApplyPackagedRelief_LiftsOnlyTheDarkLeafyGrassMap_WithAGreyThatKeepsItsHue()
        {
            RbxTextureCatalogEntryData leaves = new() { MaterialName = "LeafyGrass" };
            RbxTextureCatalogEntryData grass = new() { MaterialName = "Grass" };

            RbxMaterialSurfaceProfiles.ApplyPackagedRelief(leaves);
            RbxMaterialSurfaceProfiles.ApplyPackagedRelief(grass);

            Assert.AreEqual(1.2f, leaves.IntrinsicColor.r, 0.001f);
            Assert.AreEqual(leaves.IntrinsicColor.r, leaves.IntrinsicColor.g, 0.001f);
            Assert.AreEqual(leaves.IntrinsicColor.r, leaves.IntrinsicColor.b, 0.001f);
            Assert.AreEqual(Color.white, grass.IntrinsicColor, "a map without a gain keeps the neutral colour");
        }

        [TestCase("Grass")]
        [TestCase("LeafyGrass")]
        [TestCase("Ground")]
        [TestCase("Sand")]
        public void PackagedCatalog_AgreesWithTheTable_SoARetuneChangesNothing(string name)
        {
            RbxMaterialTextureCatalog catalog = Resources.Load<RbxMaterialTextureCatalog>(
                RbxTextureMaterialProvider.DefaultCatalogResource);
            Assert.IsNotNull(catalog, "the packaged catalog loads from Resources");
            RbxMaterialTextureCatalog.Entry entry = null;
            foreach (RbxMaterialTextureCatalog.Entry candidate in catalog.Entries)
            {
                if (candidate.MaterialName == name)
                {
                    entry = candidate;
                }
            }

            Assert.IsNotNull(entry, name + " is in the packaged catalog");
            RbxMaterialSurfaceProfiles.Profile profile = RbxMaterialSurfaceProfiles.For(name);
            float gain = RbxMaterialSurfaceProfiles.ReliefFor(name).AlbedoGain;
            Assert.AreEqual(profile.RoughnessScale, entry.RoughnessScale, 0.001f, name + " roughness scale");
            Assert.AreEqual(gain, entry.IntrinsicColor.r, 0.001f, name + " intrinsic colour");
        }

        [Test]
        public void EveryNormalStrength_StaysAtOrUnderOnePointTwo()
        {
            // WHY: 1.3-1.5 were tuned while the tangent frame was dead; with a unit frame they read
            // as glittering noise up close. Distance relief comes from the cavity map instead.
            foreach (string name in RbxMaterialCatalogEditorUtility.MaterialNames)
            {
                if (RbxMaterialSurfaceProfiles.Has(name))
                {
                    Assert.LessOrEqual(RbxMaterialSurfaceProfiles.For(name).NormalStrength, 1.2f + 1e-4f,
                        name + " normal strength");
                }
            }
        }

        [Test]
        public void PackagedRelief_NeedsItsMapsAndOnlyLiftsTheThreePhotographedMetals()
        {
            RbxTextureCatalogEntryData withMaps = new()
            {
                MaterialName = "Brick",
                AmbientOcclusionAssetPath = "Assets/x_Cavity.jpg"
            };
            RbxTextureCatalogEntryData withoutMaps = new() { MaterialName = "Brick" };

            RbxMaterialSurfaceProfiles.ApplyPackagedRelief(withMaps);
            RbxMaterialSurfaceProfiles.ApplyPackagedRelief(withoutMaps);

            Assert.Greater(withMaps.CavityStrength, 0.5f);
            Assert.AreEqual(0f, withoutMaps.CavityStrength, "no occlusion map, no cavity");
            foreach (string name in RbxMaterialCatalogEditorUtility.MaterialNames)
            {
                RbxMaterialSurfaceProfiles.PackagedRelief relief = RbxMaterialSurfaceProfiles.ReliefFor(name);
                Assert.That(relief.CavityStrength, Is.InRange(0f, 1f), name);
                bool photographedMetal = name == "Metal" || name == "DiamondPlate" ||
                                         name == "CorrodedMetal";
                Assert.AreEqual(photographedMetal, relief.MetalAlbedoLift > 0f, name + " metal lift");
            }
        }
    }
}

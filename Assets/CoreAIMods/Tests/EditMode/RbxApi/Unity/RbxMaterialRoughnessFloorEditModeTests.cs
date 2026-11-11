using System.IO;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>
    /// Unity EditMode: the roughness floor of the polished materials. Granite, Marble, Foil and Ice in the packaged
    /// catalog leave the floor to the material and get one, so their surfaces read satin rather than mirror; the
    /// shader remaps the scaled map onto floor..1; every other material keeps no floor, and an entry that sets its
    /// own floor (a calibrated import, 0) keeps it (twins).
    /// </summary>
    [TestFixture]
    public sealed class RbxMaterialRoughnessFloorEditModeTests
    {
        // WHY these numbers: the packaged maps' measured averages and the catalog's scales (gfx STATUS, 2026-11-11).
        private static readonly (string Name, float MapAverage, float Scale)[] Polished =
        {
            ("Granite", 0.023f, 0.78f), ("Marble", 0.115f, 0.55f), ("Foil", 0.051f, 0.55f), ("Ice", 0.140f, 0.45f)
        };

        [SetUp]
        public void UsePackagedCatalogOnly()
        {
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = true;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
        }

        [TearDown]
        public void Restore()
        {
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = false;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void ThePolishedMaterials_GetAFloor_ThatMakesThemSatin()
        {
            using RbxTextureMaterialProvider provider = new();
            foreach ((string name, float average, float scale) in Polished)
            {
                float floor = RbxMaterialRoughnessFloors.For(name);
                Assert.Greater(floor, 0.1f, name + " has a floor");
                float effective = Mathf.Lerp(floor, 1f, Mathf.Clamp01(average * scale));
                Assert.Greater(effective, 0.15f, name + " is no longer mirror-like (was " + average * scale + ")");
                Assert.Less(effective, 0.5f, name + " still reads polished");
                Assert.IsTrue(provider.TryGetMaterial(new RbxMaterialId(name, Value(name)), out Material material));
                if (material.HasProperty("_RoughnessFloor"))
                {
                    Assert.AreEqual(floor, material.GetFloat("_RoughnessFloor"), 1e-5f, name + " material");
                }
            }
        }

        [Test]
        public void EveryOtherMaterial_KeepsNoFloor_NegativeTwin()
        {
            using RbxTextureMaterialProvider provider = new();
            foreach (string name in new[] { "Brick", "Cobblestone", "Grass", "Metal" })
            {
                Assert.AreEqual(0f, RbxMaterialRoughnessFloors.For(name), name);
                Assert.IsTrue(provider.TryGetMaterial(new RbxMaterialId(name, Value(name)), out Material material));
                if (material.HasProperty("_RoughnessFloor"))
                {
                    Assert.AreEqual(0f, material.GetFloat("_RoughnessFloor"), name);
                }
            }
        }

        [Test]
        public void AnEntrysOwnFloor_Wins_AndANegativeOneLeavesItToTheMaterial()
        {
            RbxMaterialTextureCatalog.Entry imported = new() { MaterialName = "Granite", RoughnessFloor = 0f };
            Assert.AreEqual(0f, imported.ResolveRoughnessFloor(), "a calibrated import keeps its own map");
            RbxMaterialTextureCatalog.Entry left = new() { MaterialName = "Granite", RoughnessFloor = -5f };
            Assert.AreEqual(RbxMaterialRoughnessFloors.For("Granite"), left.ResolveRoughnessFloor());
            RbxMaterialTextureCatalog.Entry high = new() { MaterialName = "Granite", RoughnessFloor = 3f };
            Assert.AreEqual(1f, high.ResolveRoughnessFloor(), "clamped to 1");
        }

        [Test]
        public void TheShader_RemapsTheScaledMapOntoTheFloor()
        {
            string shader = File.ReadAllText(Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxTexturedSurface.shader"));
            StringAssert.Contains("lerp((half)_RoughnessFloor, 1.0h, axisRoughness)", shader);
            StringAssert.Contains("_RoughnessFloor(\"Roughness Floor\", Range(0,1)) = 0", shader, "0 by default");
        }

        private static int Value(string name)
        {
            return name switch
            {
                "Granite" => 832,
                "Marble" => 784,
                "Foil" => 1072,
                "Ice" => 1536,
                "Brick" => 848,
                "Cobblestone" => 880,
                "Grass" => 1280,
                "Metal" => 1088,
                _ => 0
            };
        }
    }
}

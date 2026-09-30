using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>
    /// Every <c>Enum.Material</c> that has a surface in Roblox renders with real relief: either a
    /// packaged texture set whose normal map is imported as a normal map with a non-zero strength, or
    /// a procedural surface with a non-zero bump strength.
    /// </summary>
    /// <remarks>
    /// WHY: "many materials have no normals" was the owner's complaint. A normal map imported as a
    /// plain texture decodes as a tilted constant, and a zero strength silently flattens a set, and
    /// neither shows up anywhere except on screen. Neon, ForceField and Air are exempt because they
    /// have no surface relief in Roblox either.
    /// </remarks>
    [TestFixture]
    public sealed class RbxMaterialNormalCoverageEditModeTests
    {
        private static readonly HashSet<string> ReliefFreeMaterials = new()
        {
            "Neon", "ForceField", "Air"
        };

        private static readonly int BumpStrengthId = Shader.PropertyToID("_BumpStrength");

        [SetUp]
        public void SetUp()
        {
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = true;
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [TearDown]
        public void TearDown()
        {
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = false;
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void EveryMaterial_HasATexturedNormalMapOrAProceduralBump()
        {
            RbxMaterialTextureCatalog catalog = Resources.Load<RbxMaterialTextureCatalog>(
                RbxTextureMaterialProvider.DefaultCatalogResource);
            Assert.IsNotNull(catalog, "the packaged texture catalog must load");
            Dictionary<string, RbxMaterialTextureCatalog.Entry> textured = new();
            foreach (RbxMaterialTextureCatalog.Entry entry in catalog.Entries)
            {
                textured[entry.MaterialName] = entry;
            }

            Assert.IsTrue(RbxEnumRegistry.CreateWithBuiltins().TryGet("Material", out RbxEnum materials));
            RbxProceduralMaterialProvider procedural = new();
            StringBuilder failures = new();
            foreach (RbxEnumItem item in materials.GetEnumItems())
            {
                if (ReliefFreeMaterials.Contains(item.Name))
                {
                    continue;
                }

                if (textured.TryGetValue(item.Name, out RbxMaterialTextureCatalog.Entry entry))
                {
                    CheckTexturedRelief(item.Name, entry, failures);
                    continue;
                }

                RbxMaterialId id = new(item.Name, item.Value);
                if (!procedural.TryGetMaterial(in id, out Material material) ||
                    !material.HasProperty(BumpStrengthId) || material.GetFloat(BumpStrengthId) <= 0f)
                {
                    failures.AppendLine(item.Name + ": no texture set and no procedural bump");
                }
            }

            Assert.IsEmpty(failures.ToString(), failures.ToString());
        }

        private static void CheckTexturedRelief(string name, RbxMaterialTextureCatalog.Entry entry,
            StringBuilder failures)
        {
            if (entry.Normal == null)
            {
                failures.AppendLine(name + ": textured entry has no normal map");
                return;
            }

            if (entry.NormalStrength <= 0f)
            {
                failures.AppendLine(name + ": normal strength " + entry.NormalStrength);
            }

            string path = AssetDatabase.GetAssetPath(entry.Normal);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer ||
                importer.textureType != TextureImporterType.NormalMap)
            {
                failures.AppendLine(name + ": '" + path + "' is not imported as a normal map");
            }
        }
    }
}

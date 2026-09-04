using System.Collections.Generic;
using CoreAI.Editor.RbxMaterials;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi
{
    /// <summary>
    /// Guards the single Enum.Material to CC0 set table: the downloader and the local-catalog
    /// importer must read the same materials with the same asset ids.
    /// </summary>
    public sealed class RbxCc0TextureSetsEditModeTests
    {
        [Test]
        public void SharedTable_HasExactly36Materials()
        {
            HashSet<string> names = new();
            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                Assert.IsTrue(names.Add(set.MaterialName), $"duplicate material '{set.MaterialName}'");
            }

            Assert.AreEqual(36, RbxCc0TextureSets.Sets.Count);
        }

        [Test]
        public void LocalImporterSets_AreTheSharedTable()
        {
            // WHY: the importer used to carry its own copy of this table, which is how the two
            // lists drifted apart unnoticed.
            Assert.AreSame(RbxCc0TextureSets.Sets, RbxLocalTextureCatalogImport.Sets);
        }

        [Test]
        public void DownloaderMappings_MatchSharedAmbientCgSets()
        {
            // WHY: the downloader used to carry its own copy with stale ids for replaced sets; any
            // edit must go through the shared table, so reconstruct the expectation from it here.
            Dictionary<string, string> expected = new();
            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                if (RbxCc0TextureSets.IsAmbientCg(set))
                {
                    expected[set.MaterialName] = RbxCc0TextureSets.AssetId(set);
                }
            }

            IReadOnlyList<RbxAmbientCgMapping> mappings = RbxAmbientCgCatalogDownloader.Mappings;
            Assert.AreEqual(expected.Count, mappings.Count);
            foreach (RbxAmbientCgMapping mapping in mappings)
            {
                Assert.IsTrue(expected.TryGetValue(mapping.MaterialName, out string assetId),
                    $"'{mapping.MaterialName}' is not an ambientCG set in the shared table");
                Assert.AreEqual(assetId, mapping.AssetId, $"'{mapping.MaterialName}' asset id");
            }
        }

        [Test]
        public void DownloaderMappings_SkipPolyHavenSets()
        {
            Dictionary<string, bool> byMaterial = new();
            foreach (RbxAmbientCgMapping mapping in RbxAmbientCgCatalogDownloader.Mappings)
            {
                byMaterial[mapping.MaterialName] = true;
            }

            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                if (!RbxCc0TextureSets.IsAmbientCg(set))
                {
                    Assert.IsFalse(byMaterial.ContainsKey(set.MaterialName),
                        $"'{set.MaterialName}' is a Poly Haven set and must not be fetched from ambientCG");
                }
            }
        }

        [Test]
        public void EverySharedMaterial_HasSurfaceProfileAndEnumValue()
        {
            // WHY: a material that ships textures but no profile silently falls back to the generic
            // tiling, and a name without an Enum.Material value can never be addressed in-game.
            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                Assert.IsTrue(RbxMaterialSurfaceProfiles.Has(set.MaterialName),
                    $"'{set.MaterialName}' has no surface profile");
                Assert.AreNotEqual(0, RbxMaterialCatalogEditorUtility.MaterialValue(set.MaterialName),
                    $"'{set.MaterialName}' has no Enum.Material value");
            }
        }
    }
}

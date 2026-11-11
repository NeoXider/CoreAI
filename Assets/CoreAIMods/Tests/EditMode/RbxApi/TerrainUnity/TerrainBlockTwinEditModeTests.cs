using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainUnity
{
    /// <summary>
    /// Unity EditMode: the block look of Terrain cells. With painted blocks each solid material is drawn by a twin of
    /// the textured surface that carries its painted tile, one tile per 4-stud cell face and the sky exposure from
    /// the vertex colours, while a Part's catalog material keeps its photographed set and its own tiling; the grass
    /// block keeps its earth sides; Water keeps its own surface; the photo look draws the catalog as before (twin);
    /// a host's own material provider is drawn as it is unless it asks for blocks; each tile's textures are built
    /// once, crisp and mip-mapped; the cells' water is calmer and turquoise while a Part's water keeps the catalog's
    /// (twin: the photo look keeps it too); and the corners where blocks meet are shaded through the vertex colours.
    /// </summary>
    [TestFixture]
    public sealed class TerrainBlockTwinEditModeTests
    {
        private readonly List<Object> _owned = new();
        private RbxTerrain _terrain;
        private RbxTerrainRenderer _renderer;
        private RbxTextureMaterialProvider _catalog;
        private GameObject _host;

        [SetUp]
        public void CreateWorld()
        {
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("Block twin host");
            _owned.Add(_host);
            _catalog = new RbxTextureMaterialProvider();
        }

        [TearDown]
        public void DestroyWorld()
        {
            _renderer?.Dispose();
            _renderer = null;
            _catalog?.Dispose();
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.DestroyImmediate(owned);
                }
            }

            _owned.Clear();
        }

        [Test]
        public void Blocks_DrawEachSolidMaterialWithItsPaintedTile_OnTheCellGrid_WhileAPartKeepsTheCatalog()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog,
                new BlockyTerrainChunkMesher(TerrainFaceMaterialRules.None), blockLook: TerrainBlockLook.Blocks);
            Fill(0, 0, 0, 1, 1, 1, Slot("Cobblestone"));
            Fill(1, 0, 0, 1, 1, 1, Slot("Grass"));
            Fill(2, 0, 0, 1, 1, 1, TerrainMaterials.Water);
            Drain();

            Material cobble = ChunkMaterial("Cobblestone");
            Material partCobble = PartMaterial("Cobblestone");
            Assert.IsTrue(RbxTerrainBlockTextures.TryGet(TerrainBlockStyle.Cobblestone, out Texture2D tile,
                out Texture2D normal, out Texture2D roughness));
            Assert.AreSame(tile, cobble.GetTexture("_BaseMap"), "the cell shows the painted cobblestone");
            Assert.AreSame(normal, cobble.GetTexture("_BumpMap"));
            Assert.AreSame(roughness, cobble.GetTexture("_RoughnessMap"));
            Assert.AreEqual(1f, cobble.GetFloat(RbxTerrainRenderer.CellTilesProperty), "one tile per cell face");
            Assert.AreEqual(1f / RbxSpace.LengthToUnity(4f), cobble.GetFloat("_TextureScale"), 1e-4f,
                "one tile spans one 4-stud cell");
            Assert.AreEqual(1f, cobble.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty),
                "the cell still reads its sky exposure, so caves stay dark");
            Assert.IsFalse(cobble.IsKeywordEnabled("_RBX_OCCLUSION_MAP"), "the photo set's cavity map is not mixed in");
            Assert.AreNotSame(partCobble, cobble);
            Assert.AreNotSame(tile, partCobble.GetTexture("_BaseMap"), "a Cobblestone Part keeps its photographed set");
            Assert.AreEqual(0f, partCobble.GetFloat(RbxTerrainRenderer.CellTilesProperty), "and its own tiling");

            Material water = WaterMaterial();
            Assert.AreEqual("CoreAI/Rbx/Water", water.shader.name, "Water keeps its own surface");
            Assert.AreEqual(1f, water.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty));
        }

        [Test]
        public void TheGrassBlock_KeepsItsEarthSides_WithPaintedTiles()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, blockLook: TerrainBlockLook.Blocks);
            Fill(0, 0, 0, 3, 1, 3, Slot("Grass"));
            Drain();

            List<string> names = new();
            foreach (Material material in Chunk().GetComponent<MeshRenderer>().sharedMaterials)
            {
                names.Add(material.name);
            }

            CollectionAssert.AreEquivalent(new[] { "Terrain block Grass", "Terrain block Ground" }, names,
                "grass on top and in the band, earth on the sides and underneath");
            RbxTerrainBlockTextures.TryGet(TerrainBlockStyle.Earth, out Texture2D earth, out Texture2D _, out Texture2D _);
            Assert.AreSame(earth, ChunkMaterial("Ground").GetTexture("_BaseMap"));
        }

        [Test]
        public void ThePhotoLook_DrawsTheCatalogAsBefore_NegativeTwin()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog,
                new BlockyTerrainChunkMesher(TerrainFaceMaterialRules.None), blockLook: TerrainBlockLook.PhotoSets);
            Fill(0, 0, 0, 1, 1, 1, Slot("Cobblestone"));
            Drain();

            Material cobble = ChunkMaterial("Cobblestone");
            Material partCobble = PartMaterial("Cobblestone");
            Assert.AreSame(partCobble.GetTexture("_BaseMap"), cobble.GetTexture("_BaseMap"));
            Assert.AreEqual(0f, cobble.GetFloat(RbxTerrainRenderer.CellTilesProperty));
            Assert.AreEqual(partCobble.GetFloat("_TextureScale"), cobble.GetFloat("_TextureScale"));
        }

        [Test]
        public void AHostsOwnMaterials_AreDrawnAsTheyAre_UnlessItAsksForBlocks()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog);
            Assert.AreSame(TerrainBlockLook.PhotoSets, _renderer.BlockLook, "a host's provider keeps its materials");
            _renderer.Dispose();
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform);
            Assert.AreSame(TerrainBlockLook.Default, _renderer.BlockLook, "the default catalog paints blocks");
            _renderer.Dispose();
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog, blockLook: TerrainBlockLook.Blocks);
            Assert.AreSame(TerrainBlockLook.Blocks, _renderer.BlockLook, "a host may ask for blocks with its provider");
        }

        [Test]
        public void TheTileTextures_AreBuiltOncePerStyle_CrispAndMipMapped()
        {
            Assert.IsTrue(RbxTerrainBlockTextures.TryGet(TerrainBlockStyle.Planks, out Texture2D first,
                out Texture2D firstNormal, out Texture2D firstRoughness));
            int builds = RbxTerrainBlockTextures.BuildCount;
            Assert.IsTrue(RbxTerrainBlockTextures.TryGet(TerrainBlockStyle.Planks, out Texture2D second,
                out Texture2D _, out Texture2D _));
            Assert.AreSame(first, second, "shared, not rebuilt");
            Assert.AreEqual(builds, RbxTerrainBlockTextures.BuildCount);
            int size = TerrainBlockPainter.TileSize * RbxTerrainBlockTextures.Upscale;
            Assert.AreEqual(size, first.width);
            Assert.AreEqual(size, first.height);
            Assert.Greater(first.mipmapCount, 4, "mip-mapped, so distant blocks stay calm");
            Assert.AreEqual(FilterMode.Trilinear, first.filterMode);
            Assert.AreEqual(TextureWrapMode.Repeat, first.wrapMode);
            Assert.IsFalse(first.isReadable, "the CPU copy is released");
            Assert.AreEqual(TextureFormat.R8, firstRoughness.format);
            Assert.IsTrue(firstNormal.graphicsFormat.ToString().Contains("UNorm"), "the normal map is linear");
            Assert.IsFalse(RbxTerrainBlockTextures.TryGet(TerrainBlockStyle.Photo, out Texture2D none,
                out Texture2D _, out Texture2D _), "the photo style has no tile");
            Assert.IsNull(none);
        }

        [Test]
        public void Blocks_TuneTheCellsWater_ButNotAPartsWater()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog,
                new BlockyTerrainChunkMesher(TerrainFaceMaterialRules.None), blockLook: TerrainBlockLook.Blocks);
            Fill(0, 0, 0, 2, 1, 2, TerrainMaterials.Water);
            Drain();

            Material water = WaterMaterial();
            Material partWater = PartMaterial("Water");
            TerrainWaterLook calm = TerrainBlockLook.Blocks.Water;
            Assert.AreEqual(calm.WaveScale, water.GetFloat("_PatternScale"), 1e-5f, "a longer, calmer swell");
            Assert.AreEqual(calm.WaveStrength, water.GetFloat("_BumpStrength"), 1e-5f);
            Assert.AreEqual(calm.Smoothness, water.GetFloat("_Smoothness"), 1e-5f);
            Assert.AreEqual(calm.Absorption, water.GetFloat("_Absorption"), 1e-5f, "shallow water shows its colour");
            Assert.Greater(water.GetColor("_MaterialColor").g, water.GetColor("_MaterialColor").r + 0.3f, "turquoise");
            Assert.AreNotEqual(calm.WaveScale, partWater.GetFloat("_PatternScale"), "a Part of Water keeps the catalog's");
        }

        [Test]
        public void ThePhotoLook_KeepsTheCatalogsWater_NegativeTwin()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _catalog,
                new BlockyTerrainChunkMesher(TerrainFaceMaterialRules.None), blockLook: TerrainBlockLook.PhotoSets);
            Fill(0, 0, 0, 2, 1, 2, TerrainMaterials.Water);
            Drain();

            Assert.AreEqual(PartMaterial("Water").GetFloat("_PatternScale"), WaterMaterial().GetFloat("_PatternScale"));
        }

        [Test]
        public void Blocks_ShadeTheCornersWhereBlocksMeet_ThroughTheVertexColours()
        {
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, blockLook: TerrainBlockLook.Blocks);
            Fill(0, 0, 0, 8, 1, 8, Slot("Rock"));
            Fill(3, 1, 3, 1, 1, 1, Slot("Rock"));
            Drain();

            Assert.AreEqual(TerrainBlockLook.Blocks.CornerShade, ChunkMaterial("Rock").GetFloat(
                RbxTerrainRenderer.CornerShadeProperty), "the twin draws the shade");
            Mesh mesh = Chunk().GetComponent<MeshFilter>().sharedMesh;
            Color32[] colors = mesh.colors32;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int shadedFloorCorners = 0;
            int openFloorCorners = 0;
            float floorTop = RbxSpace.LengthToUnity(4f);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                if (normals[vertex].y < 0.5f || Mathf.Abs(vertices[vertex].y - floorTop) > 1e-3f)
                {
                    continue;
                }

                if (colors[vertex].r < 255)
                {
                    shadedFloorCorners++;
                }
                else
                {
                    openFloorCorners++;
                }

                Assert.AreEqual(colors[vertex].r, colors[vertex].g, "the shade is grey");
            }

            Assert.AreEqual(12, shadedFloorCorners, "the four floor cells beside the block shade the two corners "
                                                    + "that touch it, the four diagonal ones the one corner");
            Assert.Greater(openFloorCorners, 200, "the rest of the floor is open");
            Assert.AreEqual(0f, TerrainBlockLook.PhotoSets.CornerShade, "the photo look draws no shade");
        }

        [Test]
        public void Preload_BuildsEveryPaintedStyleOnce_AndThePhotoLookBuildsNothing()
        {
            RbxTerrainBlockTextures.Preload(TerrainBlockLook.Blocks);
            Assert.AreEqual(0, RbxTerrainBlockTextures.Preload(TerrainBlockLook.Blocks), "a second preload builds nothing");
            Assert.AreEqual(0, RbxTerrainBlockTextures.Preload(TerrainBlockLook.PhotoSets), "the photo look paints nothing");
            Assert.AreEqual(0, RbxTerrainBlockTextures.Preload(null));
            foreach (TerrainBlockStyle style in System.Enum.GetValues(typeof(TerrainBlockStyle)))
            {
                if (style != TerrainBlockStyle.Photo)
                {
                    Assert.IsTrue(RbxTerrainBlockTextures.TryGet(style, out Texture2D _, out Texture2D _, out Texture2D _),
                        style + " is ready");
                }
            }
        }

        private static byte Slot(string name)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(name, out byte slot), name);
            return slot;
        }

        private void Fill(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte material)
        {
            _terrain.Store.FillBox(new TerrainCellBox(x, y, z, sizeX, sizeY, sizeZ), material, TerrainOccupancy.Full);
        }

        private void Drain()
        {
            while (_renderer.PendingChunkCount > 0)
            {
                _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            }
        }

        private GameObject Chunk()
        {
            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
            return chunk;
        }

        private Material ChunkMaterial(string materialName)
        {
            foreach (Material material in Chunk().GetComponent<MeshRenderer>().sharedMaterials)
            {
                if (material != null && material.name.EndsWith(materialName))
                {
                    return material;
                }
            }

            Assert.Fail("no chunk material for " + materialName);
            return null;
        }

        private Material WaterMaterial()
        {
            return Chunk().transform.GetChild(0).GetComponent<MeshRenderer>().sharedMaterial;
        }

        private Material PartMaterial(string materialName)
        {
            RbxMaterialId id = TerrainMaterials.ToMaterialId(Slot(materialName));
            Assert.IsTrue(_catalog.TryGetMaterial(in id, out Material material), materialName);
            return material;
        }
    }
}

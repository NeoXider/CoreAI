using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Builds a game tree whose Workspace has a Terrain, registering the Terrain class itself when the
    /// catalog does not ship it yet (the class row and the bootstrap line are composition hooks).
    /// </summary>
    internal static class P4TerrainTestWorld
    {
        public static ClassCatalog CatalogWithTerrain()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
                RbxTerrain.RegisterKnownUnimplementedMembers(catalog);
            }

            return catalog;
        }

        public static RbxTerrain CreateTerrain(out InstanceRegistry registry, TerrainLimits limits = null)
        {
            registry = new InstanceRegistry(CatalogWithTerrain());
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance workspace = game.FindFirstChildOfClass("Workspace");
            return RbxTerrainBootstrap.EnsureTerrain(registry, workspace, limits);
        }

        /// <summary>A small world for budget tests: 64 cells across, 16 chunks, 1,000 cells.</summary>
        public static TerrainLimits Tiny(long maxSolidCells = 1000, int maxChunks = 16)
        {
            return new TerrainLimits(-32, -32, -32, 32, 32, 32, maxSolidCells, maxChunks);
        }

        public static byte Slot(string materialName)
        {
            TerrainMaterials.TryGetSlot(materialName, out byte slot);
            return slot;
        }
    }
}

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// Gives a Workspace its one <c>Terrain</c> child, as Roblox does for every place. Called by the
    /// game-tree bootstrap and after a snapshot restore, so a world saved before Terrain existed still
    /// answers <c>workspace.Terrain</c>.
    /// </summary>
    public static class RbxTerrainBootstrap
    {
        /// <summary>
        /// The Workspace's Terrain, created under it when missing. Null when the registry's catalog
        /// has no Terrain row (a host that left the block out).
        /// </summary>
        public static RbxTerrain EnsureTerrain(InstanceRegistry registry, RbxInstance workspace,
            TerrainLimits limits = null)
        {
            if (registry == null || workspace == null)
            {
                return null;
            }

            if (workspace.FindFirstChildOfClass(RbxTerrain.ClassNameValue) is RbxTerrain existing)
            {
                if (limits != null)
                {
                    existing.Store.Limits = limits;
                }

                return existing;
            }

            if (!registry.Catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                return null;
            }

            // WHY HostProtected: every actor may write cells (the WriteProperty decision), and nobody,
            // the host included, may destroy or reparent the world's one Terrain. WHY runtime
            // infrastructure: this bootstrap rebuilds the node in every world, so it stays out of the
            // package tree and the instance quota; its cells travel in their own package entry.
            RbxTerrain terrain = (RbxTerrain)registry.Create(RbxTerrain.ClassNameValue,
                accessScope: InstanceAccessScope.HostProtected, isRuntimeInfrastructure: true);
            if (limits != null)
            {
                terrain.Store.Limits = limits;
            }

            terrain.Parent = workspace;
            return terrain;
        }

        /// <summary>The Workspace's Terrain, or null when it has none.</summary>
        public static RbxTerrain Find(RbxInstance workspace)
        {
            return workspace?.FindFirstChildOfClass(RbxTerrain.ClassNameValue) as RbxTerrain;
        }
    }
}

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Builds the canonical MVP1 game tree: DataModel root (the scene root, D5 — its whole
    /// subtree mirrors into the backing hierarchy) with Workspace registered as the physical
    /// world root, plus the services so Roblox-shaped paths resolve (roadmap §5.1.3):
    /// Lighting, ReplicatedStorage, ServerStorage, ServerScriptService, StarterPlayer.
    /// </summary>
    public static class DataModelBootstrap
    {
        public static RbxDataModel CreateGame(InstanceRegistry registry)
        {
            var game = (RbxDataModel)registry.Create("DataModel");
            // WHY: the scene root is set before children are parented so the DataModel binds to
            // the host GameObject first and every service/part nests under it as it enters.
            registry.SetSceneRoot(game);

            RbxInstance workspace = registry.Create("Workspace");
            workspace.Parent = game;
            registry.SetWorldRoot(workspace);

            CreateService(registry, game, "Lighting");
            CreateService(registry, game, "ReplicatedStorage");
            CreateService(registry, game, "ServerStorage");
            CreateService(registry, game, "ServerScriptService");
            CreateService(registry, game, "StarterPlayer");
            return game;
        }

        /// <summary>Re-attaches the scene and world roots after a snapshot restore of a full
        /// game tree.</summary>
        public static void AttachWorldRoot(InstanceRegistry registry, RbxDataModel game)
        {
            registry.SetSceneRoot(game);
            RbxInstance workspace = game.FindFirstChildOfClass("Workspace");
            if (workspace != null)
            {
                registry.SetWorldRoot(workspace);
            }
        }

        private static void CreateService(InstanceRegistry registry, RbxDataModel game,
            string className)
        {
            RbxInstance service = registry.Create(className);
            service.Parent = game;
        }
    }
}

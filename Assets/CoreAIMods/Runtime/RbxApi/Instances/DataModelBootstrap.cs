namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Builds the canonical MVP1 game tree: DataModel root with Workspace (registered as the
    /// registry's world root, D5) and the container services so Roblox-shaped paths resolve
    /// (roadmap §5.1.3): ReplicatedStorage, ServerStorage, ServerScriptService, StarterPlayer.
    /// </summary>
    public static class DataModelBootstrap
    {
        public static RbxDataModel CreateGame(InstanceRegistry registry)
        {
            var game = (RbxDataModel)registry.Create("DataModel");

            RbxInstance workspace = registry.Create("Workspace");
            workspace.Parent = game;
            registry.SetWorldRoot(workspace);

            CreateService(registry, game, "ReplicatedStorage");
            CreateService(registry, game, "ServerStorage");
            CreateService(registry, game, "ServerScriptService");
            CreateService(registry, game, "StarterPlayer");
            return game;
        }

        /// <summary>Re-attaches the world root after a snapshot restore of a full game tree.</summary>
        public static void AttachWorldRoot(InstanceRegistry registry, RbxDataModel game)
        {
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

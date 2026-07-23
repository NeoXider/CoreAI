namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Builds the canonical MVP1 game tree: DataModel root (the scene root, D5 — its whole
    /// subtree mirrors into the backing hierarchy) with Workspace registered as the physical
    /// world root, plus the services so Roblox-shaped paths resolve (roadmap §5.1.3):
    /// Lighting, ReplicatedStorage, ServerStorage, ServerScriptService, StarterPlayer,
    /// UserInputService.
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
            EnsureCurrentCamera(registry, workspace);

            CreateService(registry, game, "Lighting");
            CreateService(registry, game, "ReplicatedStorage");
            CreateService(registry, game, "ServerStorage");
            CreateService(registry, game, "ServerScriptService");
            CreateService(registry, game, "StarterPlayer");
            CreateService(registry, game, "UserInputService");
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
                // WHY: snapshots taken before the camera slice have no Camera child; restoring
                // them must still yield a resolvable workspace.CurrentCamera.
                EnsureCurrentCamera(registry, workspace);
            }

            // WHY: snapshots taken before the input slice have no UserInputService; restoring
            // them must still yield a resolvable game:GetService("UserInputService").
            if (game.FindFirstChildOfClass("UserInputService") == null)
            {
                CreateService(registry, game, "UserInputService");
            }
        }

        /// <summary>The canonical Camera child workspace.CurrentCamera resolves to.</summary>
        private static void EnsureCurrentCamera(InstanceRegistry registry, RbxInstance workspace)
        {
            if (workspace.FindFirstChildOfClass("Camera") != null)
            {
                return;
            }

            RbxInstance camera = registry.Create("Camera");
            camera.Parent = workspace;
        }

        private static void CreateService(InstanceRegistry registry, RbxDataModel game,
            string className)
        {
            RbxInstance service = registry.Create(className);
            service.Parent = game;
        }
    }
}

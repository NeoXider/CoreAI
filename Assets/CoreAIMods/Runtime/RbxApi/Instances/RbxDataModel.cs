using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The game root (Roblox DataModel) with ServiceProvider semantics: GetService resolves
    /// registered service implementations and deferred stubs; unknown names raise UNKNOWN_SERVICE
    /// with the exact Roblox text "X is not a valid Service name" (roadmap §5.2.4).
    /// </summary>
    public sealed class RbxDataModel : RbxInstance
    {
        internal RbxDataModel(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Game";
            Services = ServiceCatalog.CreateMvp2();
        }

        public ServiceCatalog Services { get; }

        /// <summary>Roblox parity: the DataModel's full name is "game", never part of child paths.</summary>
        public override string GetFullName()
        {
            return "game";
        }

        /// <summary>
        /// Returns the registered service instance with the given ClassName. Tree-backed service
        /// implementations register themselves before the catalog resolves the request.
        /// </summary>
        public RbxInstance GetService(string serviceName)
        {
            ThrowIfDestroyed("GetService");
            RegisterTreeService(serviceName);
            return Services.GetService(serviceName);
        }

        /// <summary>Roblox FindService: null when the (valid) service is not present;
        /// unknown names still raise UNKNOWN_SERVICE.</summary>
        public RbxInstance FindService(string serviceName)
        {
            ThrowIfDestroyed("FindService");
            RegisterTreeService(serviceName);
            return Services.FindService(serviceName);
        }

        private void RegisterTreeService(string serviceName)
        {
            foreach (RbxInstance child in GetChildren())
            {
                if (string.Equals(child.ClassName, serviceName, StringComparison.Ordinal)
                    && child.Descriptor.IsService)
                {
                    Services.Register(serviceName, child);
                    return;
                }
            }
        }

        // TODO: MVP5 — game:BindToClose(fn) with M6.1 semantics (parallel callbacks, bounded flush window).
        public void BindToClose(object callback)
        {
            throw RbxError.NotImplemented("game:BindToClose", "MVP5",
                "rely on mod hot-reload teardown for cleanup until BindToClose lands");
        }
    }
}

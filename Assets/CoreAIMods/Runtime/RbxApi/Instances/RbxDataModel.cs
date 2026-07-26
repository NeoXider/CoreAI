using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The game root (Roblox DataModel) with ServiceProvider semantics: GetService resolves
    /// service children by ClassName; an unknown name raises UNKNOWN_SERVICE with the exact
    /// Roblox text "X is not a valid Service name" (roadmap §5.2.4); a known-but-later service
    /// raises a loud NOT_IMPLEMENTED stub naming its roadmap phase.
    /// </summary>
    public sealed class RbxDataModel : RbxInstance
    {
        /// <summary>Roadmap phases for services that exist in the plan but not in this slice.
        /// WHY: naming the exact phase in the stub is part of the AI self-repair contract.</summary>
        private static readonly Dictionary<string, string> PlannedServices =
            new(StringComparer.Ordinal)
            {
                // WHY: RunService left this table when the per-frame game loop was pulled forward;
                // it now resolves as a real service child created by DataModelBootstrap.
                { "HttpService", "MVP2" },
                { "Players", "MVP8" },
                { "TweenService", "MVP8" },
                { "CollectionService", "MVP8" },
                { "Debris", "MVP8" },
                { "DataStoreService", "MVP9" },
                // WHY: UserInputService left this table when the input slice was pulled into MVP1;
                // it now resolves as a real service child created by DataModelBootstrap.
                { "ContextActionService", "MVP10" },
                { "SoundService", "MVP15" },
                { "AIService", "a future MVP (reserved)" },
                { "PathfindingService", "no planned MVP (not planned)" },
                { "MarketplaceService", "no planned MVP (not planned)" }
            };

        internal RbxDataModel(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Game";
        }

        /// <summary>Roblox parity: the DataModel's full name is "game", never part of child paths.</summary>
        public override string GetFullName()
        {
            return "game";
        }

        /// <summary>
        /// Returns the service instance with the given ClassName. MVP1 resolves the container
        /// services that exist as tree nodes; the MVP2 ServiceCatalog (with StubService objects)
        /// replaces the lookup without changing this surface.
        /// </summary>
        public RbxInstance GetService(string serviceName)
        {
            ThrowIfDestroyed("GetService");
            RbxInstance service = FindRegisteredService(serviceName);
            if (service != null)
            {
                return service;
            }

            if (PlannedServices.TryGetValue(serviceName, out string phase))
            {
                // TODO: MVP2 — ServiceCatalog (RegisterStub returns a StubService failing on first member access)
                throw RbxError.NotImplemented("game:GetService(\"" + serviceName + "\")", phase,
                    "the container children (game.ReplicatedStorage etc.) already work; use implemented services until then");
            }

            throw RbxError.UnknownService(serviceName);
        }

        /// <summary>Roblox FindService: null when the (valid) service is not present;
        /// unknown names still raise UNKNOWN_SERVICE.</summary>
        public RbxInstance FindService(string serviceName)
        {
            ThrowIfDestroyed("FindService");
            RbxInstance service = FindRegisteredService(serviceName);
            if (service != null)
            {
                return service;
            }

            if (PlannedServices.ContainsKey(serviceName))
            {
                return null;
            }

            bool knownServiceClass = Registry != null
                                     && Registry.Catalog.TryGet(serviceName, out ClassDescriptor descriptor)
                                     && descriptor.IsService;
            if (knownServiceClass)
            {
                return null;
            }

            throw RbxError.UnknownService(serviceName);
        }

        private RbxInstance FindRegisteredService(string serviceName)
        {
            foreach (RbxInstance child in GetChildren())
            {
                if (string.Equals(child.ClassName, serviceName, StringComparison.Ordinal)
                    && child.Descriptor.IsService)
                {
                    return child;
                }
            }

            return null;
        }

        // TODO: MVP5 — game:BindToClose(fn) with M6.1 semantics (parallel callbacks, bounded flush window).
        public void BindToClose(object callback)
        {
            throw RbxError.NotImplemented("game:BindToClose", "MVP5",
                "rely on mod hot-reload teardown for cleanup until BindToClose lands");
        }
    }
}

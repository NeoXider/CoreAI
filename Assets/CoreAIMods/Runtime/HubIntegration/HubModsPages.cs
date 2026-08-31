using System;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Hub;
using CoreAI.Hub.UI;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// One-call registration of the CoreAI Mods tab into a <see cref="HubPageRegistry"/>: a single
    /// grouped page with [Mods, Logs] sub-tabs (via <see cref="HubSubTabPage"/>). The page is
    /// registered as a lazy factory (its content is built only when the tab is first activated) at
    /// order 300 by default, so it slots after the built-in Chat (0) / Settings (100) / Statistics
    /// (200) tabs. Overloads accept the Lua-CSharp <see cref="LuaCsModRuntime"/> plus the shared
    /// <see cref="ILuaModSourceStore"/>, or a pre-built <see cref="IHubModService"/> for full control.
    /// </summary>
    public static class HubModsPages
    {
        /// <summary>Registry id of the Mods page.</summary>
        public const string ModsPageId = HubModsPage.DefaultPageId;

        /// <summary>Page id of the Mod Logs page, shown as the Logs sub-tab under the Mods tab.</summary>
        public const string LogsPageId = HubModLogsPage.DefaultPageId;

        /// <summary>Default Hub tab order for the Mods page (after Chat/Settings/Statistics).</summary>
        public const int DefaultOrder = 300;

        /// <summary>Default order of the Mod Logs child page inside the Mods tab.</summary>
        public const int DefaultLogsOrder = 350;

        /// <summary>Registers the Mods page backed by the Lua-CSharp <see cref="LuaCsModRuntime"/>.</summary>
        /// <param name="registry">Target registry. Required.</param>
        /// <param name="runtime">Live mod runtime (also driven by the manage_mods LLM tool). Required.</param>
        /// <param name="actorContext">Trusted host actor performing Hub mod operations.</param>
        /// <param name="sourceStore">Package store persisting mod source + manifest (may be null).</param>
        /// <param name="grant">Capability ceiling applied to every mod loaded from the UI.</param>
        /// <param name="allowFull">When true, <see cref="LuaCapabilities.Full"/> may be granted from the header.</param>
        /// <param name="order">Hub tab order (default 300).</param>
        public static void Register(
            HubPageRegistry registry,
            LuaCsModRuntime runtime,
            ActorContext actorContext,
            ILuaModSourceStore sourceStore = null,
            LuaCapabilities grant = LuaCapabilities.All,
            bool allowFull = false,
            int order = DefaultOrder)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            Register(
                registry,
                new LuaCsModRuntimeHubService(runtime, actorContext, sourceStore, grant, allowFull),
                order);
        }

        /// <summary>Registers the Mods page backed by a pre-built <see cref="IHubModService"/>.</summary>
        public static void Register(HubPageRegistry registry, IHubModService service, int order = DefaultOrder)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            // WHY: one top-level Mods tab with [Mods, Logs] sub-tabs instead of two top tabs, keeping the
            // tab bar compact; HubSubTabPage proxies activation lifecycle to whichever sub-tab is visible.
            registry.Register(
                ModsPageId,
                () => new HubSubTabPage(
                    ModsPageId,
                    "Mods",
                    order,
                    new HubModsPage(service, order),
                    new HubModLogsPage(service, DefaultLogsOrder)),
                order);
        }
    }
}

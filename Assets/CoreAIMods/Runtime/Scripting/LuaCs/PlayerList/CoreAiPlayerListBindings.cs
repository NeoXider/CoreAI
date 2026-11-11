using System;
using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The one-line hook of Roblox's player list (the <c>leaderstats</c> board) into an Rbx world: it builds the
    /// engine-free <see cref="RbxPlayerList"/> over the world's Players, draws it with <see cref="RbxPlayerListView"/>
    /// through the runtime UI host, switches it with <c>StarterGui:SetCoreGuiEnabled(Enum.CoreGuiType.PlayerList, ...)</c>
    /// and ties both to the world's lifetime. The mods composition calls it for every world it builds.
    /// </summary>
    public static class CoreAiPlayerListBindings
    {
        /// <summary><c>Enum.CoreGuiType.PlayerList</c>.</summary>
        public const int PlayerListCoreGuiType = 0;

        private static int _attachCount;

        /// <summary>
        /// Gives <paramref name="rbxApi"/> its player list; returns the view, or null without a world, a Players
        /// service or a runtime UI host (headless, dedicated server).
        /// </summary>
        /// <param name="rbxApi">The world's Rbx bindings.</param>
        /// <param name="backend">The runtime UI host (<c>RuntimeUiLuaSurface.Backend</c>); null draws nothing.</param>
        /// <param name="options">How the list is drawn; null uses the defaults.</param>
        public static RbxPlayerListView AttachTo(LuaCsRbxApiBindings rbxApi, IRbxUiBackend backend,
            RbxPlayerListOptions options = null)
        {
            if (rbxApi == null || backend == null || rbxApi.Players == null)
            {
                return null;
            }

            RbxPlayerListOptions resolved = options ?? new RbxPlayerListOptions();
            int attach = Interlocked.Increment(ref _attachCount);
            // WHY a prefix after the first world: a world load stages the next world while the current one still
            // runs, and both draw on the same host, whose screen ids must stay unique.
            string prefix = attach == 1 ? string.Empty : "w" + attach + ":";
            RbxPlayerList model = new(rbxApi.Registry, rbxApi.Players, resolved.MaxColumns);
            RbxStarterGui starterGui = null;
            Func<bool> enabled = () =>
            {
                if ((starterGui == null || starterGui.IsDestroyed) && rbxApi.Game != null && !rbxApi.Game.IsDestroyed)
                {
                    starterGui = rbxApi.Game.FindFirstChildOfClass("StarterGui") as RbxStarterGui;
                }

                return starterGui == null || starterGui.IsDestroyed
                                          || starterGui.GetCoreGuiEnabled(PlayerListCoreGuiType);
            };
            RbxPlayerListView view = new(model, backend, rbxApi.Scheduler, enabled, rbxApi.ResolveScreenPlayer,
                resolved, prefix);
            rbxApi.RegisterRuntimeLifetime(view);
            rbxApi.RegisterRuntimeLifetime(model);
            return view;
        }
    }
}

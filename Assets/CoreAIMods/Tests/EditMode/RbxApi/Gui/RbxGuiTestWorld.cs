using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// An engine-free world for the Roblox GUI classes: a registry with the standard game tree, one local
    /// player, a fake runtime UI host behind the port, the per-mod UI state and the presenter, driven frame
    /// by frame through a real <see cref="ModScheduler"/>.
    /// </summary>
    internal sealed class RbxGuiTestWorld : IDisposable
    {
        public const string Mod = "hud";

        public RbxGuiTestWorld(RbxUiBudgets budgets = null, FakeRbxUiBackend backend = null)
        {
            Registry = new InstanceRegistry();
            Game = DataModelBootstrap.CreateGame(Registry);
            Players = (RbxPlayers)Game.GetService("Players");
            Players.CharacterAutoLoads = false;
            Player = Players.EnsureActor(Registry, "local-player");
            PlayerGui = (RbxPlayerGui)Player.FindFirstChildOfClass("PlayerGui");
            Scheduler = new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
            Backend = backend ?? new FakeRbxUiBackend();
            World = new RbxUiWorld(Backend, budgets);
            World.AttachScheduler(Scheduler);
            Presenter = new RbxGuiPresenter(Registry, World, () => LocalPlayer, () => Mouse, null, Log.Add);
            World.AttachGuiPresenter(Presenter);
            LocalPlayer = Player;
        }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public RbxPlayers Players { get; }

        public RbxPlayer Player { get; }

        public RbxPlayer LocalPlayer { get; set; }

        public RbxPlayerGui PlayerGui { get; }

        public ModScheduler Scheduler { get; }

        public FakeRbxUiBackend Backend { get; }

        public RbxUiWorld World { get; }

        public RbxGuiPresenter Presenter { get; }

        public RbxVector2 Mouse { get; set; } = new(10f, 20f);

        public List<string> Log { get; } = new();

        /// <summary>Creates a GUI instance owned by <paramref name="owner"/>, parented when a parent is given.</summary>
        public T Create<T>(string className, RbxInstance parent = null, string owner = Mod) where T : RbxInstance
        {
            T instance = (T)Registry.Create(className, owner);
            if (parent != null)
            {
                instance.Parent = parent;
            }

            return instance;
        }

        /// <summary>A ScreenGui shown in the local player's PlayerGui.</summary>
        public RbxScreenGui Screen(string owner = Mod)
        {
            return Create<RbxScreenGui>("ScreenGui", PlayerGui, owner);
        }

        /// <summary>One scheduler frame: deferred signals run, budgets reset, the presenter flushes.</summary>
        public void Frame()
        {
            Scheduler.Advance(1d / 60d);
        }

        public FakeRbxUiBackend.FakeScreen HostScreen(RbxScreenGui screenGui)
        {
            return Presenter.GetScreenPort(screenGui) as FakeRbxUiBackend.FakeScreen;
        }

        public FakeRbxUiBackend.FakeElement Element(RbxGuiObject gui)
        {
            return Presenter.GetElementPort(gui) as FakeRbxUiBackend.FakeElement;
        }

        public List<object[]> Listen(RbxScriptSignal signal)
        {
            List<object[]> fired = new();
            signal.BindScheduler(Scheduler);
            signal.Connect((Action<object[]>)(arguments => fired.Add(arguments)));
            return fired;
        }

        public void Dispose()
        {
            World.Dispose();
        }

        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }
    }
}

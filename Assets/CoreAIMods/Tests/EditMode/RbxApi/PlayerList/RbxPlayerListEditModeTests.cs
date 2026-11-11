using System;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Tests.EditMode.Performance;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.PlayerList
{
    /// <summary>
    /// Roblox's player list from <c>leaderstats</c> (S2.5), engine-free: the model (a Folder named exactly leaderstats
    /// directly under a Player, IntValue/NumberValue/StringValue children as columns in creation order, live values,
    /// a renamed folder found, other names and classes ignored, more players are more rows) and the view over the
    /// runtime UI port (one host screen at the top right below mod screens, the PlayerList core GUI switch, texts
    /// set only when they change, nothing allocated on an unchanged frame).
    /// </summary>
    [TestFixture]
    public sealed class RbxPlayerListEditModeTests
    {
        [Test]
        public void S2_5_ALeaderstatsFolderUnderAPlayer_GivesOneRowWithItsValueColumnsInCreationOrder()
        {
            using World world = new();
            RbxInstance stats = world.Leaderstats(world.Player);
            world.Value<RbxIntValue>("IntValue", "Coins", stats).Value = 12;
            world.Value<RbxNumberValue>("NumberValue", "Time", stats).Value = 2.5;
            world.Value<RbxStringValue>("StringValue", "Rank", stats).Value = "Gold";
            world.Value<RbxBoolValue>("BoolValue", "Ignored", stats);

            Assert.IsTrue(world.List.Refresh());

            CollectionAssert.AreEqual(new[] { "Coins", "Time", "Rank" }, world.List.Columns);
            Assert.AreEqual(1, world.List.RowCount);
            Assert.AreEqual(world.Player.DisplayName, world.List.RowName(0));
            Assert.AreEqual("12", world.List.Cell(0, 0));
            Assert.AreEqual("2.5", world.List.Cell(0, 1));
            Assert.AreEqual("Gold", world.List.Cell(0, 2));
        }

        [Test]
        public void S2_5_AFolderWithAnotherName_OrNotDirectlyUnderThePlayer_GivesNoColumns()
        {
            using World world = new();
            RbxInstance wrongName = world.Create("Folder", "Leaderstats", world.Player);
            world.Value<RbxIntValue>("IntValue", "Coins", wrongName);
            RbxInstance nested = world.Create("Folder", "inner", world.Player);
            RbxInstance deep = world.Create("Folder", "leaderstats", nested);
            world.Value<RbxIntValue>("IntValue", "Gems", deep);

            world.List.Refresh();

            Assert.IsFalse(world.List.HasStats);
            Assert.AreEqual(0, world.List.Columns.Count);
        }

        [Test]
        public void S2_5_AValueChange_UpdatesItsCell_AndAnUnchangedFrameReportsNoChange()
        {
            using World world = new();
            RbxIntValue coins = world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player));
            world.List.Refresh();
            int version = world.List.Version;

            Assert.IsFalse(world.List.Refresh(), "nothing changed");
            Assert.AreEqual(version, world.List.Version);
            coins.Value = 99;

            Assert.IsTrue(world.List.Refresh());
            Assert.AreEqual("99", world.List.Cell(0, 0));
            Assert.AreEqual(version + 1, world.List.Version);
        }

        [Test]
        public void S2_5_TheTwoArgumentIdiom_AFolderRenamedAfterItWasParented_IsFound()
        {
            using World world = new();
            RbxInstance folder = world.Create("Folder", "Folder", world.Player);
            world.List.Refresh();
            Assert.IsFalse(world.List.HasStats);

            folder.Name = "leaderstats";
            world.Value<RbxIntValue>("IntValue", "Coins", folder).Value = 3;
            world.List.Refresh();

            CollectionAssert.AreEqual(new[] { "Coins" }, world.List.Columns);
            Assert.AreEqual("3", world.List.Cell(0, 0));
        }

        [Test]
        public void S2_5_AStatAddedLaterOrRemoved_ChangesTheColumns()
        {
            using World world = new();
            RbxInstance stats = world.Leaderstats(world.Player);
            world.Value<RbxIntValue>("IntValue", "Coins", stats);
            world.List.Refresh();
            RbxIntValue gems = world.Value<RbxIntValue>("IntValue", "Gems", stats);

            world.List.Refresh();
            CollectionAssert.AreEqual(new[] { "Coins", "Gems" }, world.List.Columns);

            gems.Destroy();
            world.List.Refresh();
            CollectionAssert.AreEqual(new[] { "Coins" }, world.List.Columns);
        }

        [Test]
        public void MorePlayers_AreMoreRows_SortedByTheFirstStatHighestFirst()
        {
            using World world = new();
            world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player)).Value = 5;
            RbxPlayer other = world.Players.EnsureActor(world.Registry, "other-player");
            RbxIntValue otherCoins = world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(other));
            otherCoins.Value = 40;

            world.List.Refresh();

            Assert.AreEqual(2, world.List.RowCount);
            Assert.AreSame(other, world.List.RowPlayer(0));
            Assert.AreEqual("40", world.List.Cell(0, 0));
            otherCoins.Value = 1;
            world.List.Refresh();
            Assert.AreSame(world.Player, world.List.RowPlayer(0), "a value change re-sorts the rows");
        }

        [Test]
        public void S2_5_TheView_DrawsOneHostScreenBelowModScreens_WithTheColumnsAndTheRow()
        {
            using World world = new();
            world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player)).Value = 7;

            world.Frame();

            FakeRbxUiBackend.FakeScreen screen = world.Backend.Get(RbxPlayerListView.ScreenId);
            Assert.IsNotNull(screen);
            Assert.IsTrue(screen.IsVisible);
            Assert.AreEqual(-100, screen.SortOrder, "mod screens (0 by default) draw over the list");
            Assert.AreEqual(string.Empty, screen.Owner, "the list belongs to the host, not to a mod");
            Assert.AreEqual("Coins", screen.Find("header-0").Text);
            Assert.AreEqual(world.Player.DisplayName, screen.Find("row-0-name").Text);
            Assert.AreEqual("7", screen.Find("row-0-0").Text);
            StringAssert.Contains("top: 56px", screen.UssText);
            StringAssert.Contains("right: 14px", screen.UssText);
            StringAssert.Contains("picking-mode=\"Ignore\"", screen.UxmlText);
            Assert.IsTrue(screen.Find("row-0").HasClass("cai-playerlist__local"));
        }

        [Test]
        public void TheView_DrawsNothing_WhileNoPlayerHasAStat()
        {
            using World world = new();
            world.Leaderstats(world.Player);

            world.Frame();

            Assert.IsNull(world.Backend.Get(RbxPlayerListView.ScreenId));
            Assert.IsFalse(world.View.IsShown);
        }

        [Test]
        public void TheCoreGuiSwitch_HidesTheList_AndTurningItBackOn_ShowsTheCurrentValues()
        {
            using World world = new();
            RbxIntValue coins = world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player));
            world.Frame();
            Assert.IsTrue(world.View.IsShown);

            world.StarterGui.SetCoreGuiEnabled(0, false);
            coins.Value = 8;
            world.Frame();
            Assert.IsFalse(world.View.IsShown);

            world.StarterGui.SetCoreGuiEnabled(0, true);
            world.Frame();
            Assert.IsTrue(world.View.IsShown);
            Assert.AreEqual("8", world.Backend.Get(RbxPlayerListView.ScreenId).Find("row-0-0").Text);

            world.StarterGui.SetCoreGuiEnabled(4, false);
            world.Frame();
            Assert.IsFalse(world.View.IsShown, "CoreGuiType.All switches the list too");
        }

        [Test]
        public void TheView_RebuildsTheMarkupOnlyWhenTheShapeChanges()
        {
            using World world = new();
            RbxIntValue coins = world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player));
            world.Frame();
            FakeRbxUiBackend.FakeScreen screen = world.Backend.Get(RbxPlayerListView.ScreenId);
            int builds = screen.BuildCount;

            coins.Value = 9;
            world.Frame();
            Assert.AreEqual(builds, screen.BuildCount, "a value change sets one text");
            Assert.AreEqual("9", screen.Find("row-0-0").Text);

            RbxPlayer other = world.Players.EnsureActor(world.Registry, "other-player");
            world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(other));
            world.Frame();
            Assert.AreEqual(builds + 1, screen.BuildCount, "a new row is a new shape");
            Assert.IsNotNull(screen.Find("row-1-name"));
        }

        [Test]
        public void ManyPlayers_ShowTheRowCapAndOneMoreLine()
        {
            using World world = new(new RbxPlayerListOptions { MaxRows = 2 });
            for (int index = 0; index < 4; index++)
            {
                RbxPlayer player = index == 0 ? world.Player : world.Players.EnsureActor(world.Registry, "p" + index);
                world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(player)).Value = index;
            }

            world.Frame();

            FakeRbxUiBackend.FakeScreen screen = world.Backend.Get(RbxPlayerListView.ScreenId);
            Assert.IsNotNull(screen.Find("row-1-name"));
            Assert.IsNull(screen.Find("row-2-name"));
            Assert.AreEqual("+2 more", screen.Find("more").Text);
        }

        [Test]
        public void AnUnchangedList_AllocatesNothingPerFrame()
        {
            using World world = new();
            world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player)).Value = 1;
            world.View.Update();

            AllocationMeter.AssertWithinBudget("unchanged player list frame", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    world.View.Update();
                }
            }, 1000, 0);
        }

        [Test]
        public void AnUnchangedList_AllocationTwin_BreaksTheSameBudget()
        {
            using World world = new();
            world.Value<RbxIntValue>("IntValue", "Coins", world.Leaderstats(world.Player)).Value = 1;
            world.View.Update();

            AllocationMeter.AssertTwinBreaksBudget("unchanged player list frame", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    world.View.Update();
                }
            }, 1000, 0);
        }

        private sealed class World : IDisposable
        {
            public World(RbxPlayerListOptions options = null)
            {
                Registry = new InstanceRegistry();
                Game = DataModelBootstrap.CreateGame(Registry);
                Players = (RbxPlayers)Game.GetService("Players");
                Players.CharacterAutoLoads = false;
                Player = Players.EnsureActor(Registry, "local-player");
                StarterGui = (RbxStarterGui)Game.FindFirstChildOfClass("StarterGui");
                Scheduler = new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
                Backend = new FakeRbxUiBackend();
                List = new RbxPlayerList(Registry, Players);
                View = new RbxPlayerListView(List, Backend, Scheduler, () => StarterGui.GetCoreGuiEnabled(0),
                    () => Player, options);
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public RbxPlayers Players { get; }

            public RbxPlayer Player { get; }

            public RbxStarterGui StarterGui { get; }

            public ModScheduler Scheduler { get; }

            public FakeRbxUiBackend Backend { get; }

            public RbxPlayerList List { get; }

            public RbxPlayerListView View { get; }

            public RbxInstance Create(string className, string name, RbxInstance parent)
            {
                RbxInstance instance = Registry.Create(className, "stats-mod");
                instance.Name = name;
                instance.Parent = parent;
                return instance;
            }

            public RbxInstance Leaderstats(RbxPlayer player)
            {
                return Create("Folder", RbxPlayerList.LeaderstatsName, player);
            }

            public T Value<T>(string className, string name, RbxInstance parent) where T : RbxInstance
            {
                return (T)Create(className, name, parent);
            }

            public void Frame()
            {
                Scheduler.Advance(1d / 60d);
            }

            public void Dispose()
            {
                View.Dispose();
                List.Dispose();
            }

            private sealed class NoThreadFactory : IRbxScriptThreadFactory
            {
                public IRbxScriptThread Create(string ownerModId, object callable)
                {
                    throw new InvalidOperationException("these tests connect no Lua handlers");
                }
            }
        }
    }
}

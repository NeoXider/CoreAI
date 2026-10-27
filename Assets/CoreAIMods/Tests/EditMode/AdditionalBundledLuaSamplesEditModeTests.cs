using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Runs the actual additional bundled files through the production Lua VM and scheduler.</summary>
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class AdditionalBundledLuaSamplesEditModeTests
    {
        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previousContext);

        private sealed class StateStore : ILuaModStore
        {
            private readonly Dictionary<(string Mod, string Key), string> _values = new();
            public string Get(string modId, string key) => _values.TryGetValue((modId, key), out string value) ? value : "";
            public void Set(string modId, string key, string value) => _values[(modId, key)] = value;
            public void Clear(string modId)
            {
                foreach ((string Mod, string Key) key in _values.Keys.Where(key => key.Mod == modId).ToArray())
                    _values.Remove(key);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal readonly LuaCsRbxApiBindings Api = new(registry: new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion));
            internal readonly StateStore Store = new();
            internal readonly LuaCsModStack Stack;
            internal RbxInstance Workspace => Api.Game.FindFirstChildOfClass("Workspace");

            internal Harness()
            {
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    RbxApi = Api, ModStore = Store, Log = NullLog.Instance,
                    Capabilities = LuaCapabilities.All, OneOffCapabilities = LuaCapabilities.All,
                    RegisterWorldEditBuildBindings = false
                });
            }

            internal RbxInstance Load(string id, string root)
            {
                Stack.Runtime.LoadMod(id, Source(id), LuaCapabilities.All, persistToStore: false);
                return Required(Workspace, root);
            }

            internal void Advance(int frames = 1)
            {
                for (int index = 0; index < frames; index++) Api.Scheduler.Advance(0.1f);
            }

            internal void Click(RbxInstance part)
            {
                RbxClickDetector detector = (RbxClickDetector)part.FindFirstChildOfClass("ClickDetector");
                Assert.IsNotNull(detector);
                detector.MouseClick.Fire();
                Api.Scheduler.Advance(0f);
            }

            public void Dispose()
            {
                try { Stack.Runtime.ShutdownWithoutPersistence(); }
                finally { Api.Dispose(); }
            }
        }

        private static string Source(string id)
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
                {
                    string path = Path.Combine(directory.FullName, "Assets", "CoreAIMods", "Runtime", "Resources", "CoreAIMods", id + ".lua");
                    if (File.Exists(path)) return File.ReadAllText(path);
                }
            }
            throw new FileNotFoundException("The actual bundled sample was not found: " + id);
        }

        private static RbxInstance Required(RbxInstance parent, string name)
        {
            RbxInstance result = parent.FindFirstChild(name);
            Assert.IsNotNull(result, name);
            return result;
        }

        private static double Number(RbxInstance instance, string key) => Convert.ToDouble(instance.GetAttribute(key));

        [TestCase("sample_orbit_gallery", "OrbitGallery")]
        [TestCase("sample_waypoint_platform", "WaypointPlatform")]
        [TestCase("sample_tween_gate", "TweenGate")]
        [TestCase("sample_respawning_coins", "RespawningCoins")]
        [TestCase("sample_saved_score", "SavedScore")]
        [TestCase("sample_round_clock", "RoundClock")]
        [TestCase("sample_recycling_conveyor", "RecyclingConveyor")]
        [TestCase("sample_maze_solver", "LuaMazeSolver")]
        public void ActualBundledSource_IsOptInAndRunsWithoutGrowthOrQuarantine_ThenUnloads(string id, string rootName)
        {
            LuaModHeader header = LuaModHeader.Parse(Source(id), id);
            Assert.AreEqual(id, header.Id);
            Assert.IsFalse(header.Active, "New examples must not change a game before the user enables them.");
            using Harness harness = new();
            RbxInstance root = harness.Load(id, rootName);
            int descendants = root.GetDescendants().Count;
            harness.Advance(120);
            Assert.AreEqual(descendants, root.GetDescendants().Count, "Frame updates must reuse the authored objects.");
            Assert.IsFalse(harness.Stack.Runtime.ListMods().Single(mod => mod.Id == id).Quarantined);
            harness.Stack.Runtime.UnloadMod(id);
            harness.Advance(3);
            Assert.IsNull(harness.Workspace.FindFirstChild(rootName), "Unload must retire this sample's owned objects.");
        }

        [Test]
        public void MazeSearch_RespectsItsFrameBudget_AndReusesTheBoardAcrossReachableAndBlockedSearches()
        {
            using Harness harness = new();
            RbxInstance root = harness.Load("sample_maze_solver", "LuaMazeSolver");
            int descendants = root.GetDescendants().Count;
            for (int frame = 0; frame < 20; frame++)
            {
                harness.Advance();
                Assert.LessOrEqual(Number(root, "LastFrameWork"), 24);
                Assert.LessOrEqual(Number(root, "Expanded"), 160);
            }
            Assert.AreEqual(true, root.GetAttribute("Finished"));
            Assert.AreEqual(true, root.GetAttribute("Solved"));
            double initialLength = Number(root, "PathLength");
            Assert.Greater(initialLength, 1);
            Assert.LessOrEqual(initialLength, 160);
            harness.Click(Required(root, "GateSwitch"));
            harness.Advance(20);
            Assert.AreEqual(false, root.GetAttribute("GateOpen"));
            Assert.AreEqual(true, root.GetAttribute("Finished"));
            Assert.AreEqual(false, root.GetAttribute("Solved"), "The closed column has no traversable passage.");
            Assert.AreEqual(0, Number(root, "PathLength"));
            harness.Click(Required(root, "GateSwitch"));
            harness.Advance(20);
            Assert.AreEqual(true, root.GetAttribute("Solved"));
            Assert.AreEqual(initialLength, Number(root, "PathLength"));
            Assert.AreEqual(descendants, root.GetDescendants().Count);
        }

        [Test]
        public void Score_ReloadUsesTheSameModState_AndKeepsExactlyOneOwnedRoot()
        {
            using Harness harness = new();
            RbxInstance root = harness.Load("sample_saved_score", "SavedScore");
            harness.Click(Required(root, "ScoreButton"));
            harness.Click(Required(root, "ScoreButton"));
            Assert.AreEqual(2, Number(root, "Score"));
            harness.Stack.Runtime.ReloadMod("sample_saved_score", Source("sample_saved_score"));
            RbxInstance replacement = Required(harness.Workspace, "SavedScore");
            Assert.AreNotSame(root, replacement);
            Assert.AreEqual(2, Number(replacement, "Score"));
            Assert.AreEqual(1, harness.Workspace.GetChildren().Count(child => child.Name == "SavedScore"));
        }

        [Test]
        public void Coins_RejectDuplicateCollection_AndRespawnWithoutNewInstances()
        {
            using Harness harness = new();
            RbxInstance root = harness.Load("sample_respawning_coins", "RespawningCoins");
            RbxInstance coin = Required(root, "Coin1");
            harness.Click(coin);
            harness.Click(coin);
            Assert.AreEqual(1, Number(root, "Collected"));
            Assert.AreEqual(7, Number(root, "Available"));
            harness.Advance(25);
            Assert.AreEqual(8, Number(root, "Available"));
            Assert.AreSame(coin, Required(root, "Coin1"));
            harness.Click(coin);
            Assert.AreEqual(2, Number(root, "Collected"));
        }

        [Test]
        public void OrbitPause_StopsItsClock_AndResumesFromTheSameTime()
        {
            using Harness harness = new();
            RbxInstance root = harness.Load("sample_orbit_gallery", "OrbitGallery");
            harness.Advance(5);
            double beforePause = Number(root, "Elapsed");
            Assert.Greater(beforePause, 0);
            harness.Click(Required(root, "PauseButton"));
            harness.Advance(5);
            Assert.AreEqual(beforePause, Number(root, "Elapsed"));
            harness.Click(Required(root, "PauseButton"));
            harness.Advance(5);
            Assert.Greater(Number(root, "Elapsed"), beforePause);
        }

        [Test]
        public void RoundClock_AdvancesThroughEveryPhase_AndStartsAnotherRound()
        {
            using Harness harness = new();
            RbxInstance root = harness.Load("sample_round_clock", "RoundClock");
            Assert.AreEqual("warmup", root.GetAttribute("Phase"));
            harness.Advance(25);
            Assert.AreEqual("play", root.GetAttribute("Phase"));
            harness.Advance(50);
            Assert.AreEqual("rest", root.GetAttribute("Phase"));
            harness.Advance(25);
            Assert.GreaterOrEqual(Number(root, "Round"), 2);
        }
    }
}

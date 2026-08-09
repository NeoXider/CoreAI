using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression for the shipped demo mods that called the withheld <c>coreai_world_*</c> build
    /// APIs and threw the stub error under the production composition
    /// (<see cref="LuaCsModStackOptions.RegisterWorldEditBuildBindings"/> = false, as
    /// CoreAiModsInstaller configures it). The ported mods — the Wave Director file mod and the
    /// FullAccess Tetris embedded in LuaPlatformExampleController — are loaded through
    /// <see cref="LuaCsModRuntimeFactory"/> in exactly that production configuration and driven
    /// headlessly over the Rbx API, proving they run without routing a single world command.
    /// Lua-side failures are caught via the non-generic <see cref="Assert.Catch(TestDelegate)"/>
    /// and surfaced through the runtime's load/quarantine state.
    /// </summary>
    public sealed class DemoModProductionSurfaceEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>See <see cref="LuaCsModRuntimeEditModeTests"/>: the runtime blocks on its async
        /// VM, so the Unity main-thread SynchronizationContext must be detached to avoid deadlocks.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        private sealed class FakeCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>Builds the stack exactly as the production composition does: the Rbx surface
        /// registered, the coreai_world_* build bindings withheld.</summary>
        private static LuaCsModStack BuildProductionStack(
            LuaCsRbxApiBindings rbxApi, FakeCommandSink sink, ILuaModStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = rbxApi,
                RegisterWorldEditBuildBindings = false
            });
        }

        private static RbxInstance Workspace(LuaCsRbxApiBindings rbxApi)
        {
            return rbxApi.Game.FindFirstChildOfClass("Workspace");
        }

        private static string WaveDirectorSource()
        {
            string path = Path.Combine(Application.dataPath, "CoreAI.Demos/LuaMods/WaveDirectorMod.lua.txt");
            Assert.IsTrue(File.Exists(path), "Shipped demo mod is missing: " + path);
            return File.ReadAllText(path);
        }

        private static string TetrisSource()
        {
            Type controller = Type.GetType("CoreAI.Demos.LuaPlatformExampleController, CoreAI.Demos");
            if (controller == null)
            {
                Assert.Ignore("The CoreAI.Demos assembly is not available to this test run.");
            }

            FieldInfo field = controller.GetField(
                "TetrisSource", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                Assert.Ignore("COREAI_LUA is disabled; the demo compiles its no-Lua stub.");
            }

            return (string)field.GetValue(null);
        }

        private static void AssertNoWithheldBuildApiCall(string source, string modLabel)
        {
            foreach (string api in LuaCsWorldRuntimeBindings.BuildApiNames)
            {
                StringAssert.DoesNotContain(api, source,
                    modLabel + " must not call the withheld build API " + api + " in production.");
            }

            foreach (string api in LuaCsComponentRuntimeBindings.BuildApiNames)
            {
                StringAssert.DoesNotContain(api, source,
                    modLabel + " must not call the withheld build API " + api + " in production.");
            }
        }

        [Test]
        public void WaveDirector_SourceAvoidsWithheldBuildApis_KeepsReadTierExists()
        {
            string source = WaveDirectorSource();
            AssertNoWithheldBuildApiCall(source, "WaveDirectorMod.lua.txt");
            StringAssert.Contains("coreai_world_exists", source,
                "The Boss guard is a Read-tier API and must stay in the mod.");
        }

        [Test]
        public void WaveDirector_LoadsAndRuns_UnderProductionComposition()
        {
            LuaCsRbxApiBindings rbxApi = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildProductionStack(rbxApi, sink, new MemoryStore());

            stack.Runtime.LoadMod("wave_director", WaveDirectorSource(),
                LuaCapabilities.Read | LuaCapabilities.WorldEdit, persistToStore: false);
            Assert.IsTrue(stack.Runtime.IsLoaded("wave_director"));

            stack.Runtime.EmitEvent("wave_started", "1");
            stack.Runtime.Tick(0);

            RbxInstance workspace = Workspace(rbxApi);
            Assert.IsNotNull(workspace.FindFirstChild("wave1_enemy1"),
                "Wave 1 must spawn its first enemy as an Rbx part.");
            Assert.IsNotNull(workspace.FindFirstChild("wave1_enemy3"),
                "Wave 1 spawns 2 + 1 = 3 enemies.");
            Assert.IsNull(workspace.FindFirstChild("wave1_enemy4"));
            Assert.AreEqual(0, sink.Commands.Count,
                "The ported mod must never route a coreai_world_* command.");

            // WHY: no scene Boss exists yet, so the recolor timer must stay a no-op.
            stack.Runtime.Tick(4.0);
            Assert.IsNull(workspace.FindFirstChild("Boss"),
                "Without a scene Boss the recolor half stays inert.");

            GameObject sceneBoss = new("Boss");
            try
            {
                stack.Runtime.Tick(4.0);
                RbxInstance overlay = workspace.FindFirstChild("Boss");
                Assert.IsNotNull(overlay,
                    "With a scene Boss present the mod lays down its Rbx overlay part.");
                Assert.IsTrue(overlay.IsA("BasePart"));
                Assert.IsTrue(rbxApi.PartSink.TryGetPartProperties(overlay.Id, out PartProperties props),
                    "The overlay recolor must reach the part-property sink.");
                Assert.AreEqual(RbxColor3.FromHex("#ffaa00"), props.Color,
                    "Wave 1 picks colors[(1 % 4) + 1] = #ffaa00.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sceneBoss);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("wave_director"),
                "No handler may have errored into quarantine.");
            Assert.AreEqual(0, sink.Commands.Count);
        }

        [Test]
        public void Tetris_SourceAvoidsWithheldBuildApis()
        {
            AssertNoWithheldBuildApiCall(TetrisSource(), "LuaPlatformExampleController.TetrisSource");
        }

        [Test]
        public void Tetris_LoadsAndPlaysItself_UnderProductionComposition()
        {
            LuaCsRbxApiBindings rbxApi = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildProductionStack(rbxApi, sink, new MemoryStore());

            stack.Runtime.LoadMod("tetris3d", TetrisSource(),
                LuaCapabilities.All, persistToStore: false);
            Assert.IsTrue(stack.Runtime.IsLoaded("tetris3d"));

            RbxInstance workspace = Workspace(rbxApi);
            RbxInstance root = workspace.FindFirstChild("TetrisRoot_g1");
            Assert.IsNotNull(root, "The playfield root folder must be built on load.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wl1"), "Left wall of row 1 is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wr14"), "Right wall of row 14 is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wf1"), "Floor is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_a1"), "Active-piece cube 1 is missing.");

            // Autopilot gravity: 14 falls to land the first piece; 120 x 0.1 s ticks cover it.
            for (int i = 0; i < 120; i++)
            {
                stack.Runtime.Tick(0.1);
            }

            bool lockedCube = false;
            foreach (RbxInstance child in root.GetChildren())
            {
                if (child.Name.StartsWith("tz1_c", StringComparison.Ordinal))
                {
                    lockedCube = true;
                    break;
                }
            }

            Assert.IsTrue(lockedCube, "The autopilot must lock at least one piece onto the board.");
            Assert.IsTrue(stack.Runtime.IsLoaded("tetris3d"),
                "No handler may have errored into quarantine.");
            Assert.AreEqual(0, sink.Commands.Count,
                "The ported mod must never route a coreai_world_* command.");
        }
    }
}

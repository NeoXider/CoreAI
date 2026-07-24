using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.LuaBindings
{
    /// <summary>
    /// Proof that mod-owned signal connections are Disconnected on teardown: a Heartbeat handler that
    /// a mod connects fires while the mod is loaded and STOPS firing after <c>UnloadMod</c>, so the
    /// composition's <c>ModTearingDown</c> sweep (connections disconnected before the instance sweep)
    /// cleans up the connection instead of leaving it to fire one more frame against the torn-down mod.
    /// </summary>
    [TestFixture]
    public sealed class RobloxSignalConnectionTeardownEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Detach Unity's SynchronizationContext so VM continuations complete on the thread
        /// pool (same sync-over-async hazard as the sibling RunService fixture).</summary>
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

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
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

        [Test]
        public void Heartbeat_Connection_StopsFiring_AfterModUnload()
        {
            var connections = new ModConnectionRegistry();
            // WHY: wire the bindings to a shared connection ledger exactly like the composition does, so
            // Connect records the handle and teardown can disconnect it.
            var roblox = new LuaCsRobloxApiBindings(connections: connections);
            var store = new MemoryStore();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RobloxApi = roblox
            });

            // WHY: mirror the CoreAiModsInstaller teardown wiring — disconnect a mod's connections on
            // every teardown reason.
            stack.Runtime.ModTearingDown += (modId, _) => connections.DisconnectOwnedBy(modId);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local n = 0
                rs.Heartbeat:Connect(function()
                    n = n + 1
                    store_set('n', tostring(n))
                end)");

            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);
            Assert.AreEqual("3", store.Get("m", "n"), "connected Heartbeat handler runs once per frame");
            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the mod's Heartbeat connection is live while loaded");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");

            // WHY: the teardown sweep must have disconnected the mod's Heartbeat handler.
            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading the mod disconnects its Heartbeat connection");

            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);

            // WHY: no further increments — the disconnected handler never fires against the torn-down mod.
            Assert.AreEqual("3", store.Get("m", "n"),
                "Heartbeat must not fire after the mod is unloaded");
        }
    }
}

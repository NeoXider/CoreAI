using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// Proof that mod-owned signal connections are Disconnected on teardown: a Heartbeat handler that
    /// a mod connects fires while the mod is loaded and STOPS firing after <c>UnloadMod</c>, so the
    /// composition's <c>ModTearingDown</c> sweep (connections disconnected before the instance sweep)
    /// cleans up the connection instead of leaving it to fire one more frame against the torn-down mod.
    /// </summary>
    [TestFixture]
    public sealed class RbxSignalConnectionTeardownEditModeTests
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

        /// <summary>
        /// Builds a stack wired to a shared connection ledger and the same ModTearingDown teardown the
        /// CoreAiModsInstaller installs: disconnect a mod's connections on every reason, KEEPING the
        /// current generation on Reload (the replacement chunk has already re-Connected by then).
        /// </summary>
        private static LuaCsModStack BuildWiredStack(out LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            ModConnectionRegistry connections = new();
            roblox = new LuaCsRbxApiBindings(connections: connections);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });

            stack.Runtime.ModTearingDown += (modId, reason) => connections.DisconnectOwnedBy(
                modId, reason == LuaModTeardownReason.Reload);
            return stack;
        }

        [Test]
        public void Heartbeat_Connection_StopsFiring_AfterModUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

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

            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading the mod disconnects its Heartbeat connection");

            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);

            Assert.AreEqual("3", store.Get("m", "n"),
                "Heartbeat must not fire after the mod is unloaded");
        }

        [Test]
        public void Reload_KeepsNewConnection_AndDropsOldGeneration()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

            // WHY: each handler read-modify-writes the SHARED store key, so two live handlers advance it
            // by two per frame and one by exactly one — the value alone distinguishes "old gen still
            // firing" (double-count) from "new gen inert" (no growth) from correct (one per frame).
            const string bump = @"
                local rs = game:GetService('RunService')
                rs.Heartbeat:Connect(function()
                    store_set('n', tostring((tonumber(store_get('n')) or 0) + 1))
                end)";

            // WHY: generation 1 — the outgoing chunk. Its Heartbeat handler must be gone after reload.
            stack.Runtime.LoadMod("m", bump);

            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);
            Assert.AreEqual("2", store.Get("m", "n"), "gen-1 handler fires once per frame while loaded");

            // WHY: reload with a chunk that ALSO connects Heartbeat (generation 2). The reload teardown
            // must disconnect only gen-1 and keep gen-2 live, so the game loop keeps running.
            stack.Runtime.ReloadMod("m", bump);

            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the reloaded chunk's Heartbeat connection survives the reload teardown");

            roblox.PumpFrame(0.1f);
            roblox.PumpFrame(0.1f);

            // WHY: n grows by exactly one per frame after reload — the reloaded connection STILL fires
            // (would stay at 2 if the fix disconnected the new chunk's own connection), and the old
            // generation does NOT also fire (would jump by two per frame if it were double-counted).
            Assert.AreEqual("4", store.Get("m", "n"),
                "the reloaded mod's Heartbeat keeps firing exactly once per frame");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");
            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading after a reload disconnects the surviving connection too");

            roblox.PumpFrame(0.1f);
            Assert.AreEqual("4", store.Get("m", "n"), "no Heartbeat fires after the final unload");
        }
    }
}

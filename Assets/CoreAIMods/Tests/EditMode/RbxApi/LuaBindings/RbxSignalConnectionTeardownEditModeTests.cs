using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

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

        private sealed class TeardownProbe
        {
            public int KilledThreads { get; set; } = -1;
        }

        /// <summary>
        /// Builds a stack wired to the same ModTearingDown cleanup as CoreAiModsInstaller: scheduler
        /// threads die beside connections, while Reload keeps the current connection generation.
        /// </summary>
        private static LuaCsModStack BuildWiredStack(out LuaCsRbxApiBindings roblox,
            MemoryStore store, IInputSource inputSource = null,
            TeardownProbe teardownProbe = null)
        {
            ModConnectionRegistry connections = new();
            LuaCsRbxApiBindings bindings = new(
                connections: connections, inputSource: inputSource);
            roblox = bindings;
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });

            stack.Runtime.ModTearingDown += (modId, reason) =>
            {
                int killedThreads = reason == LuaModTeardownReason.Reload
                    ? bindings.KillOutgoingScheduledGenerations(modId)
                    : bindings.KillAllScheduledOwnedBy(modId);
                if (teardownProbe != null)
                {
                    teardownProbe.KilledThreads = killedThreads;
                }

                connections.DisconnectOwnedBy(
                    modId, reason == LuaModTeardownReason.Reload);
            };
            return stack;
        }

        private static LuaModRuntimeTickDriver CreateFrameDriver(
            LuaCsModStack stack, LuaCsRbxApiBindings roblox)
        {
            GameObject driverObject = new("LuaModRuntimeTickDriver");
            LuaModRuntimeTickDriver driver = driverObject.AddComponent<LuaModRuntimeTickDriver>();
            ActorContext actorContext = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            driver.Initialize(stack.Runtime, actorContext, roblox.Scheduler,
                roblox.PumpPreSimulation, roblox.PumpHeartbeat, roblox.PumpPreRender);
            return driver;
        }

        [Test]
        public void ProductionDriver_EmitsObservableR4PhaseOrder()
        {
            InMemoryInputSource inputSource = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(
                out LuaCsRbxApiBindings roblox, store, inputSource);
            LuaModRuntimeTickDriver driver = CreateFrameDriver(stack, roblox);
            try
            {
                stack.Runtime.LoadMod("m", @"
                    local run_service = game:GetService('RunService')
                    local input_service = game:GetService('UserInputService')
                    run_service.Stepped:Connect(function()
                        store_set('order', store_get('order') .. 'S')
                    end)
                    run_service.Heartbeat:Connect(function()
                        store_set('order', store_get('order') .. 'H')
                    end)
                    run_service.RenderStepped:Connect(function()
                        store_set('order', store_get('order') .. 'R')
                    end)
                    input_service.InputBegan:Connect(function()
                        store_set('order', store_get('order') .. 'I')
                    end)
                    task.delay(0, function()
                        store_set('order', store_get('order') .. 'D')
                    end)");

                inputSource.SetMouseButton(0, true);
                driver.PumpFrame(0.1f);
                Assert.AreEqual("SDHIR", store.Get("m", "order"));
                driver.PumpFrame(0.1f);

                Assert.AreEqual("SDHIRSHR", store.Get("m", "order"));
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
        }

        [Test]
        public void HostFramePump_TaskWaitResumesAfterEnoughScaledFrames()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);
            LuaModRuntimeTickDriver driver = CreateFrameDriver(stack, roblox);
            try
            {
                stack.Runtime.LoadMod("m", @"
                    task.spawn(function()
                        store_set('wait_state', 'waiting')
                        task.wait(0.2)
                        store_set('wait_state', 'resumed')
                    end)");

                Assert.AreEqual("waiting", store.Get("m", "wait_state"));
                driver.PumpFrame(0.1f);
                Assert.AreEqual("waiting", store.Get("m", "wait_state"));
                driver.PumpFrame(0.1f);
                Assert.AreEqual("resumed", store.Get("m", "wait_state"));
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
        }

        [Test]
        public void HostFramePump_TaskDelayFiresAfterEnoughScaledFrames()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);
            LuaModRuntimeTickDriver driver = CreateFrameDriver(stack, roblox);
            try
            {
                stack.Runtime.LoadMod("m", @"
                    local run_service = game:GetService('RunService')
                    run_service.Heartbeat:Connect(function()
                        store_set('order', store_get('order') .. 'H')
                    end)
                    task.delay(0.25, function()
                        store_set('delay_fired', 'yes')
                        store_set('order', store_get('order') .. 'D')
                    end)");

                driver.PumpFrame(0.1f);
                driver.PumpFrame(0.1f);
                Assert.AreEqual("", store.Get("m", "delay_fired"));
                Assert.AreEqual("HH", store.Get("m", "order"));
                driver.PumpFrame(0.1f);
                Assert.AreEqual("yes", store.Get("m", "delay_fired"));
                Assert.AreEqual("HHDH", store.Get("m", "order"));
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
        }

        [Test]
        public void Unload_KillsPendingSchedulerThreadsWithoutTouchingOtherMods()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);
            LuaModRuntimeTickDriver driver = CreateFrameDriver(stack, roblox);
            try
            {
                const string delayedWrite = @"
                    task.delay(1, function()
                        store_set('delay_fired', 'yes')
                    end)";
                stack.Runtime.LoadMod("alpha", delayedWrite);
                stack.Runtime.LoadMod("beta", delayedWrite);

                Assert.IsTrue(stack.Runtime.UnloadMod("alpha"));
                driver.PumpFrame(1f);

                Assert.AreEqual("", store.Get("alpha", "delay_fired"));
                Assert.AreEqual("yes", store.Get("beta", "delay_fired"));
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
        }

        [Test]
        public void Reload_KillsOutgoingSchedulerGenerationAndKeepsReplacement()
        {
            MemoryStore store = new();
            TeardownProbe teardownProbe = new();
            LuaCsModStack stack = BuildWiredStack(
                out LuaCsRbxApiBindings roblox, store,
                teardownProbe: teardownProbe);
            LuaModRuntimeTickDriver driver = CreateFrameDriver(stack, roblox);
            try
            {
                stack.Runtime.LoadMod("m", @"
                    local scheduled = false
                    game:GetService('RunService').Heartbeat:Connect(function()
                        if scheduled then return end
                        scheduled = true
                        task.delay(1, function()
                            store_set('outgoing_fired', 'yes')
                        end)
                    end)");
                driver.PumpFrame(0.1f);
                driver.PumpFrame(0.1f);
                stack.Runtime.ReloadMod("m", @"
                    task.delay(1, function()
                        store_set('replacement_fired', 'yes')
                    end)");

                driver.PumpFrame(1f);

                Assert.AreEqual(1, teardownProbe.KilledThreads);
                Assert.AreEqual("", store.Get("m", "outgoing_fired"));
                Assert.AreEqual("yes", store.Get("m", "replacement_fired"));
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
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
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("3", store.Get("m", "n"), "connected Heartbeat handler runs once per frame");
            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the mod's Heartbeat connection is live while loaded");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");

            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading the mod disconnects its Heartbeat connection");

            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);

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
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("2", store.Get("m", "n"), "gen-1 handler fires once per frame while loaded");

            // WHY: reload with a chunk that ALSO connects Heartbeat (generation 2). The reload teardown
            // must disconnect only gen-1 and keep gen-2 live, so the game loop keeps running.
            stack.Runtime.ReloadMod("m", bump);

            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the reloaded chunk's Heartbeat connection survives the reload teardown");

            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);

            // WHY: n grows by exactly one per frame after reload — the reloaded connection STILL fires
            // (would stay at 2 if the fix disconnected the new chunk's own connection), and the old
            // generation does NOT also fire (would jump by two per frame if it were double-counted).
            Assert.AreEqual("4", store.Get("m", "n"),
                "the reloaded mod's Heartbeat keeps firing exactly once per frame");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");
            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading after a reload disconnects the surviving connection too");

            roblox.PumpFrame(0.1f);
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("4", store.Get("m", "n"), "no Heartbeat fires after the final unload");
        }
    }
}

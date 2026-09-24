using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
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
            TeardownProbe teardownProbe = null, System.Action<string> log = null)
        {
            ModConnectionRegistry connections = new();
            LuaCsRbxApiBindings bindings = new(
                connections: connections, inputSource: inputSource, log: log);
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
            // WHY the driver gets the scheduler and no per-phase pumps: the bindings that own this
            // scheduler already fire every phase from PhaseReached. Handing the driver the same
            // Pump* methods made it a SECOND subscriber, so each phase fired twice per frame.
            driver.Initialize(stack.Runtime, actorContext, roblox.Scheduler);
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
        public void M8_22_Dispose_DetachesTweenService_SoTheOldSchedulerStepsNoTween()
        {
            LuaCsRbxApiBindings roblox = new();
            RbxInstance part = roblox.Registry.Create("Part");
            part.Parent = roblox.Registry.WorldRoot;
            RbxTween tween = roblox.TweenService.Create(part,
                new RbxTweenInfo(1d, RbxEasingStyle.Linear, RbxEasingDirection.Out, 0, false, 0d),
                new[] { new KeyValuePair<string, object>("Transparency", 1d) },
                new TweenCaller("teardown-host", true, roblox.Registry.WorldId));
            tween.Play();
            roblox.Scheduler.Advance(0.25d);
            float beforeDispose = roblox.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency;
            Assert.AreEqual(0.25f, beforeDispose, 0.001f, "the tween steps while the bindings live");

            roblox.Dispose();
            // WHY the old scheduler is advanced on purpose: after a session swap nothing should still be
            // subscribed to it, and a service left attached kept writing into a world these bindings no
            // longer front.
            roblox.Scheduler.Advance(0.25d);

            Assert.AreEqual(beforeDispose,
                roblox.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency,
                "a disposed world's TweenService no longer steps its tweens");
        }

        [Test]
        public void KillAllScheduledOwnedBy_AlsoDestroysTheTweensTheModCreated()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);
            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Name = 'Tweened'
                part.Transparency = 0
                part.Parent = workspace
                local tweenService = game:GetService('TweenService')
                tweenService:Create(part, TweenInfo.new(1), {Transparency = 1}):Play()");
            roblox.Scheduler.Advance(0.25d);
            Assert.AreEqual(1, roblox.TweenService.ActiveTweenCount);
            RbxInstance part = roblox.Registry.WorldRoot.FindFirstChild("Tweened");
            float killedAt = roblox.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency;

            // WHY the kill path alone: it is what unload and quarantine both run, and the mod's tweens
            // must stop there even when nothing else sweeps the mod's instances.
            roblox.KillAllScheduledOwnedBy("m");
            roblox.Scheduler.Advance(0.25d);

            Assert.AreEqual(0, roblox.TweenService.ActiveTweenCount,
                "the killed mod's tweens stop with it instead of playing on");
            Assert.AreEqual(0, roblox.TweenService.LiveTweenCount);
            Assert.AreEqual(killedAt, roblox.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency,
                "the part keeps the value it had when the mod was killed");
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

            // WHY one Advance per frame and no separate PumpFrame: the scheduler walks the phase
            // pipeline and the bindings fire Heartbeat from PhaseReached, so a frame is exactly one
            // Advance. Pumping as well would fire the handler twice per frame.
            roblox.Scheduler.Advance(0.1d);
            roblox.Scheduler.Advance(0.1d);
            roblox.Scheduler.Advance(0.1d);
            Assert.AreEqual("3", store.Get("m", "n"), "connected Heartbeat handler runs once per frame");
            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the mod's Heartbeat connection is live while loaded");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");

            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading the mod disconnects its Heartbeat connection");

            roblox.Scheduler.Advance(0.1d);
            roblox.Scheduler.Advance(0.1d);

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

            // WHY one Advance per frame: see the sibling unload fixture — the bindings fire Heartbeat
            // from the scheduler's own phase, so an extra PumpFrame would double every count below.
            roblox.Scheduler.Advance(0.1d);
            roblox.Scheduler.Advance(0.1d);
            Assert.AreEqual("2", store.Get("m", "n"), "gen-1 handler fires once per frame while loaded");

            // WHY: reload with a chunk that ALSO connects Heartbeat (generation 2). The reload teardown
            // must disconnect only gen-1 and keep gen-2 live, so the game loop keeps running.
            stack.Runtime.ReloadMod("m", bump);

            Assert.IsTrue(roblox.RunService.Heartbeat.HasConnections,
                "the reloaded chunk's Heartbeat connection survives the reload teardown");

            roblox.Scheduler.Advance(0.1d);
            roblox.Scheduler.Advance(0.1d);

            // WHY: n grows by exactly one per frame after reload — the reloaded connection STILL fires
            // (would stay at 2 if the fix disconnected the new chunk's own connection), and the old
            // generation does NOT also fire (would jump by two per frame if it were double-counted).
            Assert.AreEqual("4", store.Get("m", "n"),
                "the reloaded mod's Heartbeat keeps firing exactly once per frame");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");
            Assert.IsFalse(roblox.RunService.Heartbeat.HasConnections,
                "unloading after a reload disconnects the surviving connection too");

            roblox.Scheduler.Advance(0.1d);
            Assert.AreEqual("4", store.Get("m", "n"), "no Heartbeat fires after the final unload");
        }

        [Test]
        public void ProxyCache_DestroyedInstances_ArePrunedOnUnregistered()
        {
            // WHY (M1-12): every instance a mod context ever wrapped stayed strongly reachable for
            // the context's lifetime, so a spawner mod pinned every part it had ever destroyed.
            // Counted, never GC-observed: the prune is driven by the registry's Unregistered event.
            LuaCsRbxApiBindings bindings = new();
            LuaCsRbxModContext context = CreateHostContext(bindings, "prune-mod");
            int baseline = context.ProxyCacheCount;
            List<RbxInstance> spawned = new();
            for (int index = 0; index < 1000; index++)
            {
                RbxInstance folder = bindings.Registry.Create("Folder");
                spawned.Add(folder);
                context.WrapInstance(folder);
            }

            Assert.AreEqual(baseline + 1000, context.ProxyCacheCount);

            for (int index = 0; index < spawned.Count; index++)
            {
                spawned[index].Destroy();
            }

            Assert.AreEqual(baseline, context.ProxyCacheCount,
                "every destroyed instance left the strong proxy cache");
        }

        [Test]
        public void ProxyCache_HeldProxyOfADestroyedInstance_KeepsItsIdentity()
        {
            // WHY: the twin of the prune — identity must survive it while the proxy is still
            // referenced, because deferred handlers (PlayerRemoving, ChildRemoved) receive a
            // destroyed instance after its unregister and compare it with the one a script stored
            // earlier.
            LuaCsRbxApiBindings bindings = new();
            LuaCsRbxModContext context = CreateHostContext(bindings, "identity-mod");
            RbxInstance folder = bindings.Registry.Create("Folder");
            LuaValue held = context.WrapInstance(folder);
            Assert.IsTrue(LuaCsRbxLua.TryGetInstance(held, out LuaCsRbxInstanceProxy heldProxy));
            int baseline = context.ProxyCacheCount;

            folder.Destroy();

            Assert.AreEqual(baseline - 1, context.ProxyCacheCount);
            LuaValue rewrapped = context.WrapInstance(folder);
            Assert.IsTrue(LuaCsRbxLua.TryGetInstance(rewrapped, out LuaCsRbxInstanceProxy rewrappedProxy));
            Assert.AreSame(heldProxy, rewrappedProxy,
                "a destroyed instance whose proxy Lua still holds re-wraps to that same proxy");
            Assert.AreEqual(baseline - 1, context.ProxyCacheCount,
                "re-wrapping a destroyed instance does not pin it in the strong cache again");
        }

        [Test]
        public void ProxyCache_DeferredChildRemoved_FindsTheTableKeyStoredBeforeTheDestroy()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

            stack.Runtime.LoadMod("m", @"
                local folder = Instance.new('Folder')
                folder.Parent = workspace
                local part = Instance.new('Part')
                part.Parent = folder
                local tracked = {}
                tracked[part] = 'payload'
                folder.ChildRemoved:Connect(function(child)
                    store_set('seen', tostring(tracked[child]))
                    store_set('same', tostring(child == part))
                end)
                part:Destroy()");
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("payload", store.Get("m", "seen"),
                "the destroyed child is the same table key the script stored");
            Assert.AreEqual("true", store.Get("m", "same"));
        }

        [Test]
        public void B1_04_Destroy_DisconnectsWhatAScriptConnectedToAnUnmodelledPropertySignal()
        {
            // WHY: the signal GetPropertyChangedSignal hands out for a real property CoreAI does not
            // model belonged to nothing, so Destroy never reached it: every character a footstep
            // script watched (Humanoid.FloorMaterial) left its connection, and the destroyed
            // character its handler captured, in the mod's ledger until the mod unloaded (B1-04).
            List<string> log = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store, log: log.Add);

            stack.Runtime.LoadMod("footsteps", @"
                local kept = Instance.new('Model')
                kept.Parent = workspace
                kept:GetPropertyChangedSignal('LevelOfDetail'):Connect(function()
                    store_set('fired', kept.Name)
                end)
                for index = 1, 50 do
                    local model = Instance.new('Model')
                    model.Parent = workspace
                    model:GetPropertyChangedSignal('LevelOfDetail'):Connect(function()
                        store_set('fired', model.Name)
                    end)
                    local part = Instance.new('Part')
                    part.Parent = model
                    part:GetPropertyChangedSignal('LocalTransparencyModifier'):Connect(function()
                        store_set('fired', part.Name)
                    end)
                    model:Destroy()
                end
                local late = Instance.new('Model')
                late.Parent = workspace
                local held = late:GetPropertyChangedSignal('LevelOfDetail')
                for index = 1, 200 do
                    late:GetPropertyChangedSignal('Unmodelled' .. index)
                end
                held:Connect(function()
                    store_set('fired', late.Name)
                end)
                late:Destroy()
                store_set('loaded', 'true')");
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("true", store.Get("footsteps", "loaded"));
            Assert.AreEqual(1, log.FindAll(line =>
                    line.Contains("CoreAI does not model Model.LevelOfDetail")).Count,
                "the watched property is one CoreAI does not model, which is the case under test");
            Assert.AreEqual(1, roblox.Connections.GetOwnedBy("footsteps").Count,
                "a destroyed instance's unmodelled-property connections are gone, its descendants' and a "
                + "signal taken before 200 other names included; the live instance's one stays");
            Assert.AreEqual("", store.Get("footsteps", "fired"), "an unmodelled property's signal never fires");
        }

        [Test]
        public void B1_04_Negative_Destroy_StillDisconnectsAModelledPropertySignal()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

            stack.Runtime.LoadMod("footsteps", @"
                for index = 1, 50 do
                    local model = Instance.new('Model')
                    model.Parent = workspace
                    model:GetPropertyChangedSignal('Name'):Connect(function()
                        store_set('fired', model.Name)
                    end)
                    model:Destroy()
                end
                store_set('loaded', 'true')");
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("true", store.Get("footsteps", "loaded"));
            Assert.AreEqual(0, roblox.Connections.GetOwnedBy("footsteps").Count,
                "Destroy disconnects a modelled property's signal, as it always did");
        }

        private static LuaCsRbxModContext CreateHostContext(LuaCsRbxApiBindings bindings,
            string modId)
        {
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            return new LuaCsRbxModContext(bindings, LuaCapabilities.All, modId,
                OriginTag.FromMod(modId), host);
        }
    }
}

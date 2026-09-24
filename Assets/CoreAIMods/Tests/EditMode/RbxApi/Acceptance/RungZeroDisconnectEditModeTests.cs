using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Lua;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Production teardown and bounded actor-churn coverage.</summary>
    [TestFixture]
    public sealed class RungZeroDisconnectEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        [Test]
        public void ConnectDisconnect_UsesProductionSeam_IsIdempotentAndIsolated()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            Dictionary<string, int> added = new(StringComparer.Ordinal);
            Dictionary<string, int> removing = new(StringComparer.Ordinal);
            RbxEnumItem removalReason = null;
            harness.Bindings.Players.PlayerAdded.Connect(
                (Action<object[]>)(arguments =>
                {
                    RbxPlayer player = (RbxPlayer)arguments[0];
                    Increment(added, player.NetworkActorId);
                }));
            harness.Bindings.Players.PlayerRemoving.Connect(
                (Action<object[]>)(arguments =>
                {
                    RbxPlayer player = (RbxPlayer)arguments[0];
                    removalReason = (RbxEnumItem)arguments[1];
                    Increment(removing, player.NetworkActorId);
                }));

            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Bindings.Scheduler.Advance(0d);

            RbxRemoteEvent remote =
                (RbxRemoteEvent)harness.Registry.Create("RemoteEvent");
            remote.GetOnClientEvent(actorA.ActorId);
            remote.GetOnClientEvent(actorB.ActorId);
            harness.SendClientEvent(remote, actorA.ActorId);
            harness.SendClientEvent(remote, actorB.ActorId);
            harness.Stack.Runtime.LoadMod(actorA, "disconnect-mod-a", @"
                task.spawn(function()
                    task.wait(1000)
                end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "disconnect-mod-b", @"
                task.spawn(function()
                    task.wait(1000)
                end)", persistToStore: false);
            int threadsBeforeDisconnect =
                harness.Bindings.Scheduler.LiveThreadCount;

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, added[actorA.ActorId]);
            Assert.AreEqual(1, removing[actorA.ActorId]);
            Assert.AreEqual("PlayerExitReason", removalReason.EnumType.Name);
            Assert.AreEqual("Unknown", removalReason.Name);
            Assert.IsFalse(harness.Bindings.Players.TryGetByActorId(
                actorA.ActorId, out _));
            Assert.IsTrue(harness.Bindings.Players.TryGetByActorId(
                actorB.ActorId, out _));
            CollectionAssert.DoesNotContain(
                harness.Bridge.ActorIds, actorA.ActorId);
            CollectionAssert.Contains(harness.Bridge.ActorIds, actorB.ActorId);
            Assert.AreEqual(1, remote.ClientSignalCount);
            Assert.IsFalse(remote.HasActor(actorA.ActorId));
            Assert.IsTrue(remote.HasActor(actorB.ActorId));
            Assert.AreEqual(1, harness.Bridge.RateWindowCount);
            Assert.Less(
                harness.Bindings.Scheduler.LiveThreadCount,
                threadsBeforeDisconnect);
            Assert.Greater(harness.Bindings.Scheduler.LiveThreadCount, 0);
            Assert.AreEqual(1, harness.ChatFactory.ReleaseCount);

            Assert.IsFalse(harness.Bindings.DisconnectActor(actorA));
            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual(1, removing[actorA.ActorId]);
            Assert.AreEqual(1, harness.ChatFactory.ReleaseCount);
            Assert.IsTrue(harness.Bindings.Players.TryGetByActorId(
                actorB.ActorId, out _));
        }

        [Test]
        public void RepeatedConnectDisconnect200Actors_LeavesNoRuntimeGrowth()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxRemoteEvent remote =
                (RbxRemoteEvent)harness.Registry.Create("RemoteEvent");

            for (int index = 0; index < 200; index++)
            {
                ActorContext actor = harness.Actor("churn-" + index);
                harness.Bindings.ConnectActor(actor);
                remote.GetOnClientEvent(actor.ActorId);
                harness.SendClientEvent(remote, actor.ActorId);
                Assert.IsTrue(harness.Bindings.DisconnectActor(actor));
            }

            harness.Bindings.Scheduler.Advance(0d);
            Assert.IsEmpty(harness.Bridge.ActorIds);
            Assert.AreEqual(0, harness.Bridge.RateWindowCount);
            Assert.AreEqual(0, remote.ClientSignalCount);
            Assert.AreEqual(0, harness.Bindings.Scheduler.LiveThreadCount);
            Assert.IsEmpty(harness.Bindings.Players.GetPlayers());
            Assert.AreEqual(200, harness.ChatFactory.ReleaseCount);
        }

        [Test]
        public void DisconnectActor_RaisesActorModsDisconnected_WithOnlyThatActorsMods()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "released-mod-a",
                "task.spawn(function() task.wait(1000) end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "kept-mod-b",
                "task.spawn(function() task.wait(1000) end)", persistToStore: false);
            List<string> raised = new();
            harness.Bindings.ActorModsDisconnected += (actorId, mods) =>
                raised.Add(actorId + ":" + string.Join(",", mods));

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            CollectionAssert.AreEqual(new[] { "disconnect-a:released-mod-a" }, raised,
                "the runtime learns exactly which mods ran as the departed actor");
            Assert.IsFalse(harness.Bindings.DisconnectActor(actorA));
            Assert.AreEqual(1, raised.Count, "a second disconnect of a released actor raises nothing");
        }

        [Test]
        public void DisconnectActor_ASubscriberThrowingAheadOfTheRuntime_DoesNotKeepTheReleasedModsLoaded()
        {
            // WHY a throwing subscriber registered before the runtime's own: the actor is gone by the
            // time the event runs, and one failing listener must not keep the runtime from releasing
            // the actor's mods.
            bool brokenListenerRan = false;
            using ProductionHarness harness = new ProductionHarness(beforeRuntime: bindings =>
                bindings.ActorModsDisconnected += (actorId, mods) =>
                {
                    brokenListenerRan = true;
                    throw new InvalidOperationException("a broken listener");
                });
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "released-mod-a",
                "task.spawn(function() task.wait(1000) end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "kept-mod-b",
                "task.spawn(function() task.wait(1000) end)", persistToStore: false);

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsTrue(brokenListenerRan, "precondition: the broken listener ran first");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("released-mod-a"),
                "the departed actor's mod no longer dispatches at all (M2-24)");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("kept-mod-b"),
                "another actor's mod is untouched");
        }

        [Test]
        public void DisconnectActor_UnloadsThatActorsMods_ThroughTheProductionRuntime_AndLeavesTheOthersRunning()
        {
            // WHY (M2-24): a departed actor's mods can never dispatch again, so left loaded they only
            // held the actor's and the world's mod quota and failed NOT_AUTHORITY on every call.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "released-mod-a", PingCountingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorA, "released-mod-a2", PingCountingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "kept-mod-b", PingCountingMod, persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);
            RbxInstance releasedPart = harness.Registry.WorldRoot.FindFirstChild("released-mod-a-part");
            RbxInstance keptPart = harness.Registry.WorldRoot.FindFirstChild("kept-mod-b-part");
            Assert.IsNotNull(releasedPart, "precondition: the released mod built its part");
            Assert.IsNotNull(keptPart, "precondition: the kept mod built its part");

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("released-mod-a"),
                "the departed actor's mod is unloaded as the disconnect returns");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("released-mod-a2"),
                "every mod loaded for the departed actor goes");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("kept-mod-b"), "another actor's mod stays");
            Assert.IsTrue(releasedPart.IsDestroyed,
                "the unload swept the instances the departed actor's mod created");
            Assert.AreEqual(0, harness.Registry.GetTeardownOwnedBy("released-mod-a").Count);
            Assert.IsFalse(keptPart.IsDestroyed, "another actor's instances are untouched");

            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual("1", harness.Store.Get("kept-mod-b", "pings"), "the remaining mod dispatches");
            Assert.AreEqual("", harness.Store.Get("released-mod-a", "pings"),
                "the unloaded mod received nothing");
            CollectionAssert.IsEmpty(harness.Log.Errors,
                "releasing a departed actor's mods is not an error");
            CollectionAssert.IsEmpty(harness.Stack.Runtime.GetRecentHandlerErrors(),
                "no call of a released mod failed as its departed actor");
            CollectionAssert.IsEmpty(
                harness.ModLog.Query(new LuaLogQuery { MinLevel = LuaLogLevel.Error }),
                "the mod log holds no failure either");
        }

        [Test]
        public void DisconnectActor_LeavesAModLoadedWithHostAuthority_Loaded()
        {
            // WHY the negative twin: a mod loaded with host authority runs as the host, not as the
            // actor that loaded it, so it is never refused NOT_AUTHORITY and that actor leaving is no
            // reason to unload it.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext actorA = harness.Actor("disconnect-a");
            harness.Bindings.ConnectActor(actorA);
            harness.Stack.Runtime.LoadMod(host, "host-mod", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorA, "actor-mod-a", TimerAndPingMod, persistToStore: false);
            Assert.AreEqual(host.ActorId, harness.Stack.Runtime.GetModOwnerActorId("host-mod"),
                "precondition: the host mod was loaded for the host's actor id");
            List<string> released = new();
            harness.Bindings.ActorModsDisconnected += (actorId, mods) => released.AddRange(mods);

            Assert.IsTrue(harness.Bindings.DisconnectActor(host));
            harness.Stack.Runtime.Tick(0.1d);

            CollectionAssert.AreEqual(new[] { "host-mod" }, released,
                "precondition: the runtime was told the host mod ran as the departed actor");

            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("host-mod"));
            Assert.AreEqual("yes", harness.Store.Get("host-mod", "timer"), "and it keeps dispatching");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("actor-mod-a"), "another actor's mod stays");
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void DisconnectActor_KeepsTheStoredPackageActive_SoTheModRehydratesWhenTheActorRejoins()
        {
            // WHY: nobody chose to stop a departed actor's mod, and a world package saves the store's
            // active flags; a dormant mark would keep the mod from ever starting again for that actor.
            MemorySourceStore sources = new MemorySourceStore();
            using ProductionHarness harness = new ProductionHarness(sources);
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "released-mod-a", PingCountingMod);
            harness.Stack.Runtime.LoadMod(actorB, "kept-mod-b", PingCountingMod);
            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("1", harness.Store.Get("released-mod-a", "pings"), "precondition");

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("released-mod-a"));
            Assert.IsTrue(sources.TryLoad("released-mod-a", out string storedSource,
                    out LuaModManifest storedManifest),
                "the departed actor's package is still stored");
            Assert.AreEqual(PingCountingMod, storedSource);
            Assert.IsTrue(storedManifest.Active, "and still marked active");
            Assert.AreEqual("disconnect-a", storedManifest.OwnerActorId);

            harness.Bindings.ConnectActor(actorA);
            Assert.AreEqual(1, harness.Stack.Runtime.RehydrateFromStore(Capabilities),
                "a rehydrate after the actor rejoins starts exactly the released mod");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("released-mod-a"));
            Assert.AreEqual("disconnect-a", harness.Stack.Runtime.GetModOwnerActorId("released-mod-a"),
                "it runs as its actor again");
            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("2", harness.Store.Get("released-mod-a", "pings"),
                "and resumes from the data it stored before its actor left");
            CollectionAssert.IsEmpty(harness.Log.Errors);

            Assert.IsTrue(harness.Stack.Runtime.UnloadMod("kept-mod-b"));
            Assert.IsTrue(sources.TryLoad("kept-mod-b", out _, out LuaModManifest unloadedManifest));
            Assert.IsFalse(unloadedManifest.Active,
                "the negative twin: an unload someone asked for still marks the package dormant");
        }

        [Test]
        public void DisconnectActor_ReachedFromAModHook_ReleasesTheActorsModsOnlyOnceTheHookReturned()
        {
            // WHY: a kick ends the connection synchronously, so a mod's own hook can disconnect its
            // actor. Unloaded under its feet, the rest of the hook ran with its actor forgotten, and a
            // thread it scheduled on the way out later resumed as the host.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("kick-a");
            ActorContext actorB = harness.Actor("kick-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "kicker-a", @"
                hooks_every(0.05, function()
                    kick_actor('kick-a')
                    store_set('loaded_after_kick', tostring(is_loaded('kicker-a')))
                    task.defer(function() store_set('deferred_ran', 'yes') end)
                    store_set('finished', 'yes')
                end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorA, "later-a", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "bystander-b", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.EmitEvent("ping");

            harness.Stack.Runtime.Tick(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("true", harness.Store.Get("kicker-a", "loaded_after_kick"),
                "the hook that disconnected its own actor ran to its end with its mod still loaded");
            Assert.AreEqual("yes", harness.Store.Get("kicker-a", "finished"));
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("kicker-a"));
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("later-a"));
            Assert.AreEqual("", harness.Store.Get("kicker-a", "deferred_ran"),
                "the thread scheduled after the kick died with its mod instead of resuming as the host");
            Assert.AreEqual("", harness.Store.Get("later-a", "timer"),
                "a released mod later in the same tick runs no timer");
            Assert.AreEqual("", harness.Store.Get("later-a", "pinged"),
                "and no queued event");
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "timer"));
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "pinged"));
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("bystander-b"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
            CollectionAssert.IsEmpty(harness.Stack.Runtime.GetRecentHandlerErrors());
        }

        [Test]
        public void DisconnectActor_ReachedFromAnExportCalledOnAnotherModsThread_ReleasesTheModOnlyAtTheNextTick()
        {
            // WHY: a kick ends the connection synchronously, and the thread running at that moment may
            // belong to another mod: here actor B's thread calls into actor A's export, which kicks
            // actor A. Unloaded right then, the rest of the export ran with its actor forgotten, and the
            // thread it scheduled on the way out resumed later as the host.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("export-a");
            ActorContext actorB = harness.Actor("caller-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "exporter-a", @"
                mods_export('leave', function()
                    kick_actor('export-a')
                    store_set('loaded_after_kick', tostring(is_loaded('exporter-a')))
                    task.defer(function() store_set('deferred_ran', 'yes') end)
                    store_set('finished', 'yes')
                end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "caller-b", @"
                task.spawn(function()
                    task.wait(0.05)
                    mods_call('exporter-a', 'leave')
                    store_set('call_returned', 'yes')
                end)", persistToStore: false);

            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Stack.Runtime.Tick(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual("true", harness.Store.Get("exporter-a", "loaded_after_kick"),
                "the export that disconnected its own actor ran to its end with its mod still loaded");
            Assert.AreEqual("yes", harness.Store.Get("exporter-a", "finished"));
            Assert.AreEqual("yes", harness.Store.Get("caller-b", "call_returned"),
                "the calling thread of another actor carried on");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("exporter-a"),
                "the next tick released the departed actor's mod");
            Assert.AreEqual("", harness.Store.Get("exporter-a", "deferred_ran"),
                "the thread the export scheduled after the kick never ran as the host");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("caller-b"));
        }

        [Test]
        public void DisconnectActor_RaisedWhileAnotherActorsModsAreTornDown_IsReleasedAfterThem()
        {
            // WHY: an unload's teardown runs host listeners that may disconnect another actor; tearing
            // that actor's mods down inside the first teardown would take a second mod down halfway
            // through the first one.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("nested-a");
            ActorContext actorB = harness.Actor("nested-b");
            ActorContext actorC = harness.Actor("nested-c");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Bindings.ConnectActor(actorC);
            harness.Stack.Runtime.LoadMod(actorA, "mod-a1", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorA, "mod-a2", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorC, "mod-c", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "mod-b", TimerAndPingMod, persistToStore: false);
            List<string> order = new();
            harness.Stack.Runtime.ModTearingDown += (modId, reason) =>
            {
                order.Add(modId);
                if (modId == "mod-a1")
                {
                    Assert.IsTrue(harness.Bindings.DisconnectActor(actorC));
                    order.Add("nested-disconnect-returned");
                }
            };

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            CollectionAssert.AreEqual(
                new[] { "mod-a1", "nested-disconnect-returned", "mod-a2", "mod-c" }, order,
                "each teardown finishes before the next begins, and the nested actor's mods follow");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("mod-c"));
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("mod-b"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void DisconnectActor_NeverListsAModWhoseFirstLoadFailed_ButStillListsOneAFailedReloadKept()
        {
            // WHY: a load records which actor the mod runs as before its chunk runs, and the rollback
            // of a failed first load left that record behind, so the actor's disconnect listed a mod
            // that was never loaded. The failed reload is the negative twin: the mod it keeps is
            // still loaded for that actor and must still be listed.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("failed-load-a");
            harness.Bindings.ConnectActor(actorA);
            harness.Stack.Runtime.LoadMod(actorA, "kept-a", TimerAndPingMod, persistToStore: false);
            Assert.Catch(() => harness.Stack.Runtime.LoadMod(actorA, "never-loaded-a",
                "error('the first load fails')", persistToStore: false));
            Assert.Catch(() => harness.Stack.Runtime.ReloadMod("kept-a",
                "error('the reload fails')"));
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("never-loaded-a"), "precondition");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("kept-a"),
                "precondition: a failed reload keeps the loaded mod");
            List<string> released = new();
            harness.Bindings.ActorModsDisconnected += (actorId, mods) => released.AddRange(mods);

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            CollectionAssert.AreEqual(new[] { "kept-a" }, released,
                "the disconnect lists the mod that stayed loaded, never the one whose load failed");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("kept-a"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void DisconnectActor_UnloadsItsQuarantinedMod_AndLeavesAnotherActorsQuarantinedModAlone()
        {
            // WHY: a quarantine releases the mod's scheduled work through the same kill an unload
            // uses, and that kill dropped the record of the actor the mod was loaded for. The
            // quarantined mod stays loaded, so its actor's disconnect neither listed nor unloaded
            // it, and it held a slot of the actor's and the world's mod quota for good.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("quarantine-a");
            ActorContext actorB = harness.Actor("quarantine-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "broken-a", BrokenTimerMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "broken-b", BrokenTimerMod, persistToStore: false);
            for (int tick = 0; tick < harness.Stack.Runtime.MaxErrorsBeforeQuarantine; tick++)
            {
                harness.Stack.Runtime.Tick(0.1d);
            }

            Assert.IsTrue(IsQuarantined(harness, "broken-a"), "precondition: the mod is quarantined");
            Assert.IsTrue(IsQuarantined(harness, "broken-b"), "precondition");
            Assert.IsTrue(harness.Bindings.ActorLedger.TryGetLoad("broken-a", out string owner),
                "a quarantined mod is still loaded, so the bindings still know whose it is");
            Assert.AreEqual("quarantine-a", owner);
            RbxInstance brokenPart = harness.Registry.WorldRoot.FindFirstChild("broken-a-part");
            Assert.IsNotNull(brokenPart, "precondition: the quarantined mod built its part");
            List<string> released = new();
            harness.Bindings.ActorModsDisconnected += (actorId, mods) => released.AddRange(mods);

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            CollectionAssert.AreEqual(new[] { "broken-a" }, released,
                "the disconnect lists the departed actor's quarantined mod");
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("broken-a"),
                "and the runtime unloads it with the actor's other mods");
            Assert.IsTrue(brokenPart.IsDestroyed, "the unload swept the instances it created");
            Assert.IsFalse(harness.Bindings.ActorLedger.TryGetLoad("broken-a", out _),
                "the unload ended the record");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("broken-b"),
                "another actor's quarantined mod stays");
            Assert.IsTrue(IsQuarantined(harness, "broken-b"), "and stays quarantined");
        }

        [Test]
        public void LoadMod_WhoseChunkDisconnectsItsOwnActor_FailsWithAClearError_AndLeavesNothingLoaded()
        {
            // WHY: a kick ends the connection synchronously, and the disconnect kills every thread of
            // the actor's mods, the load's own main chunk among them; LoadMod then surfaced the VM's
            // raw cancellation exception, which names neither the mod nor why it stopped.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("load-kick-a");
            harness.Bindings.ConnectActor(actorA);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                harness.Stack.Runtime.LoadMod(actorA, "load-kicker", @"
                    hooks_on('ping', function() store_set('pinged', 'yes') end)
                    hooks_every(0.05, function() store_set('timer', 'yes') end)
                    kick_actor('load-kick-a')
                    store_set('after_kick', 'yes')", persistToStore: false));

            StringAssert.Contains("'load-kicker'", error.Message);
            StringAssert.Contains("'load-kick-a'", error.Message);
            StringAssert.Contains("disconnected while its main chunk ran", error.Message);
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("load-kicker"));
            Assert.AreEqual("", harness.Store.Get("load-kicker", "after_kick"),
                "the chunk stopped at the kick");
            Assert.IsFalse(harness.Bindings.ActorLedger.TryGetLoad("load-kicker", out _),
                "no record of the actor the failed load ran as is left");
            StringAssert.Contains("disconnected while its main chunk ran",
                LastModLogMessage(harness, "load-kicker"),
                "the mod log explains the failed load the same way");

            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("", harness.Store.Get("load-kicker", "pinged"),
                "a hook the stopped chunk registered never runs");
            Assert.AreEqual("", harness.Store.Get("load-kicker", "timer"));
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("load-kicker"));
            Assert.AreEqual(0, harness.Bindings.Scheduler.LiveThreadCount);
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void LoadMod_WhoseChunkEndsWithTheKick_OrCatchesItWithPcall_FailsTheSameWay()
        {
            // WHY: a chunk whose last statement is the kick, or one that wraps the kick in pcall to
            // carry on, must not load either: the actor it would run as is gone.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext lastActor = harness.Actor("last-kick-a");
            ActorContext pcallActor = harness.Actor("pcall-kick-b");
            harness.Bindings.ConnectActor(lastActor);
            harness.Bindings.ConnectActor(pcallActor);

            InvalidOperationException last = Assert.Throws<InvalidOperationException>(() =>
                harness.Stack.Runtime.LoadMod(lastActor, "last-kicker", @"
                    hooks_every(0.05, function() store_set('timer', 'yes') end)
                    kick_actor('last-kick-a')", persistToStore: false));
            InvalidOperationException caught = Assert.Throws<InvalidOperationException>(() =>
                harness.Stack.Runtime.LoadMod(pcallActor, "pcall-kicker", @"
                    hooks_every(0.05, function() store_set('timer', 'yes') end)
                    local ok, err = pcall(kick_actor, 'pcall-kick-b')
                    store_set('pcall', tostring(ok) .. '|' .. tostring(err))", persistToStore: false));

            StringAssert.Contains("'last-kicker'", last.Message);
            StringAssert.Contains("disconnected while its main chunk ran", last.Message);
            StringAssert.Contains("'pcall-kicker'", caught.Message);
            StringAssert.Contains("disconnected while its main chunk ran", caught.Message);
            Assert.AreEqual("", harness.Store.Get("pcall-kicker", "pcall"),
                "pcall does not let the stopped chunk carry on");
            foreach (string modId in new[] { "last-kicker", "pcall-kicker" })
            {
                Assert.IsFalse(harness.Stack.Runtime.IsLoaded(modId), modId);
                Assert.IsFalse(harness.Bindings.ActorLedger.TryGetLoad(modId, out _), modId);
            }

            harness.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("", harness.Store.Get("last-kicker", "timer"));
            Assert.AreEqual("", harness.Store.Get("pcall-kicker", "timer"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void ReloadMod_WhoseChunkDisconnectsItsOwnActor_FailsWithTheSameError_AndTheKeptModGoesWithItsActor()
        {
            // WHY: the same kill stops a replacement chunk; the instance the failed reload keeps was
            // loaded for the departed actor, so it is released like the actor's other mods.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("reload-kick-a");
            ActorContext actorB = harness.Actor("reload-kick-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "reload-kicker", TimerAndPingMod, persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "bystander-b", TimerAndPingMod, persistToStore: false);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                harness.Stack.Runtime.ReloadMod("reload-kicker", @"
                    kick_actor('reload-kick-a')
                    store_set('after_kick', 'yes')"));

            StringAssert.Contains("'reload-kicker'", error.Message);
            StringAssert.Contains("disconnected while its main chunk ran", error.Message);
            Assert.AreEqual("", harness.Store.Get("reload-kicker", "after_kick"));

            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);

            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("reload-kicker"),
                "the kept instance belonged to the departed actor and went with it");
            Assert.AreEqual("", harness.Store.Get("reload-kicker", "timer"));
            Assert.AreEqual("", harness.Store.Get("reload-kicker", "pinged"));
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("bystander-b"));
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "timer"));
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "pinged"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
        }

        [Test]
        public void DisconnectActor_ReachedFromALogicSlotFormula_ReleasesTheModOnlyOnceTheFormulaReturned()
        {
            // WHY: host code calls a logic slot directly, outside any hook or scheduler thread, so the
            // runtime did not count the formula as running mod code. A formula that kicked its own
            // player had its mod unloaded under its feet: the rest of it ran with its actor
            // forgotten, and a thread it scheduled on the way out later resumed as the host.
            using ProductionHarness harness = new ProductionHarness(
                capabilities: Capabilities | LuaCapabilities.LogicOverride);
            LuaCsLogicSlots slots = harness.Stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("damage");
            ActorContext actorA = harness.Actor("slot-a");
            ActorContext actorB = harness.Actor("slot-b");
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Stack.Runtime.LoadMod(actorA, "slot-kicker", @"
                logic_define('damage', function(base)
                    kick_actor('slot-a')
                    store_set('loaded_after_kick', tostring(is_loaded('slot-kicker')))
                    task.defer(function() store_set('deferred_ran', 'yes') end)
                    store_set('finished', 'yes')
                    return base * 2
                end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "bystander-b", TimerAndPingMod, persistToStore: false);
            Assert.IsTrue(slots.IsOverridden("damage"), "precondition: the formula is installed");

            Assert.IsTrue(slots.TryInvokeNumber("damage", out double damage, 21d));

            Assert.AreEqual(42d, damage, "the formula's own result reaches the host");
            Assert.AreEqual("true", harness.Store.Get("slot-kicker", "loaded_after_kick"),
                "the formula that disconnected its own actor ran to its end with its mod still loaded");
            Assert.AreEqual("yes", harness.Store.Get("slot-kicker", "finished"));
            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("slot-kicker"),
                "the mod is released as soon as the formula returned");
            Assert.IsFalse(slots.IsOverridden("damage"), "and its formula goes with it");

            harness.Bindings.Scheduler.Advance(0.1d);
            harness.Stack.Runtime.EmitEvent("ping");
            harness.Stack.Runtime.Tick(0.1d);
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual("", harness.Store.Get("slot-kicker", "deferred_ran"),
                "the thread scheduled after the kick died with its mod instead of resuming as the host");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("bystander-b"));
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "timer"));
            Assert.AreEqual("yes", harness.Store.Get("bystander-b", "pinged"));
            CollectionAssert.IsEmpty(harness.Log.Errors);
            CollectionAssert.IsEmpty(harness.Stack.Runtime.GetRecentHandlerErrors());
        }

        [Test]
        public void LogicSlotFormulas_ThatReturnOrFail_LeaveNoModCodeRunning_SoALaterDisconnectReleasesAtOnce()
        {
            // WHY the negative twin: a formula counts as running mod code only while it runs. One
            // that returned, or failed and was reset, must not leave the runtime waiting to release
            // a departed actor's mods.
            using ProductionHarness harness = new ProductionHarness(
                capabilities: Capabilities | LuaCapabilities.LogicOverride);
            LuaCsLogicSlots slots = harness.Stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("damage");
            slots.DeclareSlot("broken");
            ActorContext actorA = harness.Actor("slot-twin-a");
            harness.Bindings.ConnectActor(actorA);
            harness.Stack.Runtime.LoadMod(actorA, "slot-twin", @"
                logic_define('damage', function(base) return base * 2 end)
                logic_define('broken', function() error('the formula is broken') end)",
                persistToStore: false);

            Assert.IsTrue(slots.TryInvokeNumber("damage", out double damage, 21d));
            Assert.AreEqual(42d, damage);
            Assert.IsFalse(slots.TryInvokeNumber("broken", out _), "a failing formula falls back");
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded("slot-twin"), "precondition");

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));

            Assert.IsFalse(harness.Stack.Runtime.IsLoaded("slot-twin"),
                "a disconnect after the formulas returned releases the actor's mod at once");
            Assert.IsFalse(slots.IsOverridden("damage"));
        }

        private const string BrokenTimerMod = @"
            local part = Instance.new('Part')
            part.Name = mod_id() .. '-part'
            part.Parent = workspace
            hooks_every(0.05, function() error('the timer is broken') end)";

        private static bool IsQuarantined(ProductionHarness harness, string modId)
        {
            foreach (LuaModInfo mod in harness.Stack.Runtime.ListMods())
            {
                if (string.Equals(mod.Id, modId, StringComparison.Ordinal))
                {
                    return mod.Quarantined;
                }
            }

            return false;
        }

        private static string LastModLogMessage(ProductionHarness harness, string modId)
        {
            string last = "";
            foreach (LuaLogEntry entry in harness.ModLog.Query(new LuaLogQuery { ModId = modId }))
            {
                last = entry.Message;
            }

            return last;
        }

        private const string PingCountingMod = @"
            local part = Instance.new('Part')
            part.Name = mod_id() .. '-part'
            part.Parent = workspace
            hooks_on('ping', function()
                store_set('pings', tostring((tonumber(store_get('pings')) or 0) + 1))
            end)
            task.spawn(function() task.wait(1000) end)";

        private const string TimerAndPingMod = @"
            hooks_every(0.05, function() store_set('timer', 'yes') end)
            hooks_on('ping', function() store_set('pinged', 'yes') end)";

        private static void Increment(Dictionary<string, int> counts, string actorId)
        {
            counts[actorId] = counts.TryGetValue(actorId, out int count)
                ? count + 1
                : 1;
        }

        /// <summary>
        /// The production stack (<see cref="LuaCsModRuntimeFactory"/>) over one Rbx world, with the
        /// installer's ModTearingDown cleanup and two host bindings a mod can call: <c>kick_actor(id)</c>
        /// disconnects an actor synchronously, as a transport reporting a kick's drop does, and
        /// <c>is_loaded(id)</c> reads the runtime.
        /// </summary>
        private sealed class ProductionHarness : IDisposable
        {
            /// <param name="sourceStore">The package store the runtime persists to; null keeps none.</param>
            /// <param name="beforeRuntime">Runs on the bindings before the runtime is built.</param>
            /// <param name="capabilities">The host's capability ceiling for mods and one-off scripts.</param>
            public ProductionHarness(ILuaModSourceStore sourceStore = null,
                Action<LuaCsRbxApiBindings> beforeRuntime = null,
                LuaCapabilities capabilities = Capabilities)
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "disconnect-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bridge = new NullNetworkBridge();
                Bindings = new LuaCsRbxApiBindings(
                    Registry, game, networkBridge: Bridge);
                ChatFactory = new RecordingChatFactory();
                Bindings.AttachChatFactory(ChatFactory);
                beforeRuntime?.Invoke(Bindings);
                Store = new MemoryStore();
                Log = new RecordingLog();
                ModLog = new LuaLogService();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    ModSourceStore = sourceStore,
                    Log = Log,
                    LogService = ModLog,
                    Capabilities = capabilities,
                    OneOffCapabilities = capabilities,
                    RbxApi = Bindings,
                    AdditionalGameplayBindings = (registry, capabilities) =>
                    {
                        registry.Register("kick_actor", new Func<string, bool>(actorId =>
                            Bindings.DisconnectActor(Actor(actorId))));
                        registry.Register("is_loaded", new Func<string, bool>(modId =>
                            Stack.Runtime.IsLoaded(modId)));
                    }
                });
                WireInstallerTeardown();
            }

            public InstanceRegistry Registry { get; }

            public NullNetworkBridge Bridge { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public RecordingChatFactory ChatFactory { get; }

            public MemoryStore Store { get; }

            public RecordingLog Log { get; }

            public LuaLogService ModLog { get; }

            public LuaCsModStack Stack { get; }

            public ActorContext Actor(string actorId)
            {
                return new LocalActorIdentityProvider(
                        actorId,
                        "session-" + actorId,
                        Registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            public void SendClientEvent(RbxRemoteEvent remote, string actorId)
            {
                LuaCsRbxNetworkCodec codec = new(
                    Registry, RbxEnumRegistry.CreateWithBuiltins(), null);
                byte[] payload = codec.EncodeArguments(new List<LuaValue>());
                remote.FireServer(Bridge, actorId, payload);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }

            /// <summary>The same ModTearingDown cleanup CoreAiModsInstaller wires in production.</summary>
            private void WireInstallerTeardown()
            {
                Stack.Runtime.ModTearingDown += (modId, reason) =>
                {
                    if (reason == LuaModTeardownReason.Reload)
                    {
                        Bindings.KillOutgoingScheduledGenerations(modId);
                    }
                    else
                    {
                        Bindings.KillAllScheduledOwnedBy(modId);
                    }

                    Bindings.Connections.DisconnectOwnedBy(modId, reason == LuaModTeardownReason.Reload);
                    if (reason != LuaModTeardownReason.Unload)
                    {
                        return;
                    }

                    foreach (RbxInstance owned in Registry.GetTeardownOwnedBy(modId))
                    {
                        owned?.Destroy();
                    }
                };
            }
        }

        private sealed class RecordingLog : ILog
        {
            public List<string> Errors { get; } = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message);
            }
        }

        private sealed class MemorySourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, KeyValuePair<string, LuaModManifest>> _packages =
                new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _packages[id] = new KeyValuePair<string, LuaModManifest>(source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_packages.TryGetValue(id, out KeyValuePair<string, LuaModManifest> package))
                {
                    source = package.Key;
                    manifest = package.Value;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach (KeyValuePair<string, LuaModManifest> package in _packages.Values)
                {
                    manifests.Add(package.Value);
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_packages.TryGetValue(id, out KeyValuePair<string, LuaModManifest> package)
                    && package.Value != null)
                {
                    package.Value.Active = active;
                }
            }

            public void Delete(string id)
            {
                _packages.Remove(id);
            }
        }

        private sealed class RecordingChatFactory : IInGameLlmChatServiceFactory
        {
            public int ReleaseCount { get; private set; }

            public IInGameLlmChatService Resolve(ActorContext actorContext)
            {
                return null;
            }

            public bool ReleaseActor(ActorContext actorContext)
            {
                ReleaseCount++;
                return true;
            }
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(
                    (modId, key), out string value) ? value : "";
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
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId,
                            StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}

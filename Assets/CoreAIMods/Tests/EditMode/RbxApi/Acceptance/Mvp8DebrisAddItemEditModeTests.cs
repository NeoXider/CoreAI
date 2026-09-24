using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>MVP2.5 slice 8.0 gate: Debris:AddItem through production composition.</summary>
    [TestFixture]
    public sealed class Mvp8DebrisAddItemEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        private SynchronizationContext _savedContext;

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

        [Test]
        public void DebrisService_ResolvesToRbxDebris()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("svc-actor");

            // WHY: on the stub build this resolves to RbxStubService, so the gate is red until
            // the slice lands (member access would raise NOT_IMPLEMENTED there).
            Assert.IsInstanceOf<RbxDebris>(harness.Bindings.Game.GetService("Debris"));

            harness.Stack.Runtime.LoadMod(actor, "svc-resolve",
                "store_set('debris_class', game:GetService('Debris').ClassName)",
                persistToStore: false);

            Assert.AreEqual("Debris", harness.Store.Get("svc-resolve", "debris_class"));
        }

        [Test]
        public void AddItem_DestroysAfterLifetimeThroughEnvelope()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("lifetime-a");
            harness.Stack.Runtime.LoadMod(actor, "lifetime-setup", @"
                local part = Instance.new('Part')
                part.Name = 'LifetimePart'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("LifetimePart");
            Assert.IsNotNull(part);
            InstanceId id = part.Id;
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);
            Assert.IsTrue(harness.Binder.IsMaterialized(id));
            int mutationsBefore = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0.25);
            harness.Bindings.Scheduler.Advance(0.25);

            Assert.IsNull(part.Parent);
            Assert.AreEqual(1, destroyingCount);
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
            // WHY the binder is asked about THIS id instead of about its total count: an actor
            // joining this harness auto-loads a character, and that Model materializes during the
            // same two Advances the debris deadline needs — a total-count assertion measures that
            // unrelated world traffic, not whether the debris item released its backing object.
            Assert.IsFalse(harness.Binder.IsMaterialized(id));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, mutationsBefore);
        }

        [Test]
        public void AddItem_BeforeDeadline_PartStillLive()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("lifetime-neg");
            harness.Stack.Runtime.LoadMod(actor, "lifetime-neg-setup", @"
                local part = Instance.new('Part')
                part.Name = 'EarlyPart'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("EarlyPart");
            Assert.IsNotNull(part);
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Bindings.Scheduler.Advance(0.49);

            Assert.AreSame(harness.Registry.WorldRoot, part.Parent);
            Assert.AreEqual(0, destroyingCount);
            Assert.IsTrue(harness.Registry.TryGet(part.Id, out _));
        }

        [Test]
        public void AddItem_DefaultLifetime_AliveAt999_GoneAt10()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("default-a");
            harness.Stack.Runtime.LoadMod(actor, "default-setup", @"
                local part = Instance.new('Part')
                part.Name = 'DefaultPart'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("DefaultPart");
            Assert.IsNotNull(part);
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Bindings.Scheduler.Advance(9.99);
            Assert.AreSame(harness.Registry.WorldRoot, part.Parent);
            Assert.AreEqual(0, destroyingCount);

            harness.Bindings.Scheduler.Advance(0.01);
            Assert.IsNull(part.Parent);
            Assert.AreEqual(1, destroyingCount);
            Assert.IsFalse(harness.Registry.TryGet(part.Id, out _));
        }

        [Test]
        public void AddItem_ZeroScaledDelta_FreezesEverything()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("freeze-a");
            harness.Stack.Runtime.LoadMod(actor, "freeze-setup", @"
                local part = Instance.new('Part')
                part.Name = 'FrozenDebrisPart'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("FrozenDebrisPart");
            Assert.IsNotNull(part);
            InstanceId id = part.Id;
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            // WHY: mirror Mvp8TweenServiceEditModeTests.Tween_ZeroScaledDelta_FreezesEverything —
            // a timeScale-0 frame driver feeds delta 0 into Advance; Debris must not consume
            // scheduler time or destroy the item, proving its lifetime is scaled, not wall-clock.
            for (int frame = 0; frame < 5; frame++)
            {
                harness.Bindings.Scheduler.Advance(0d);
                Assert.AreSame(harness.Registry.WorldRoot, part.Parent,
                    "frame " + frame + " at scaled delta 0 must not destroy the item");
            }

            Assert.AreEqual(0d, harness.Bindings.Scheduler.CurrentTime);
            Assert.AreEqual(0, destroyingCount);
            Assert.IsTrue(harness.Registry.TryGet(id, out _));

            harness.Bindings.Scheduler.Advance(0.5d);

            Assert.IsNull(part.Parent);
            Assert.AreEqual(1, destroyingCount);
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
        }

        [Test]
        public void AddItem_Cap1k_EvictsOldestInstantly_OthersUntouched()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext setupActor = harness.Actor("cap-setup");
            harness.Stack.Runtime.LoadMod(setupActor, "cap-first", @"
                local first = Instance.new('Part')
                first.Name = 'Cap0'
                first.Parent = workspace
                game:GetService('Debris'):AddItem(first)", persistToStore: false);

            RbxInstance first = harness.Registry.WorldRoot.FindFirstChild("Cap0");
            Assert.IsNotNull(first);
            InstanceId firstId = first.Id;
            int firstDestroying = 0;
            harness.ConnectDestroying(first, () => firstDestroying++);
            int mutationsBeforeLoop = harness.Registry.RetainedMutationOperationCount;

            ActorContext loopActor = harness.Actor("cap-loop");
            harness.Stack.Runtime.LoadMod(loopActor, "cap-loop", @"
                for i = 1, 1000 do
                    local p = Instance.new('Part')
                    p.Name = 'Cap' .. i
                    p.Parent = workspace
                    game:GetService('Debris'):AddItem(p)
                end", persistToStore: false);

            Assert.AreEqual(1000, harness.Bindings.Debris.PendingCount);
            Assert.IsNull(first.Parent, "the oldest entry is destroyed instantly on the 1,001st call");
            Assert.IsFalse(harness.Registry.TryGet(firstId, out _));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, mutationsBeforeLoop);
            Assert.AreEqual(0, firstDestroying, "Destroying handlers run deferred, not at the call site");

            Dictionary<ulong, long> revisions = new();
            for (int index = 1; index <= 1000; index++)
            {
                RbxInstance survivor =
                    harness.Registry.WorldRoot.FindFirstChild("Cap" + index);
                Assert.IsNotNull(survivor, "Cap" + index + " is untouched by the eviction");
                Assert.AreSame(harness.Registry.WorldRoot, survivor.Parent);
                Assert.IsTrue(harness.Registry.TryGetRecord(survivor.Id, out InstanceRecord record));
                revisions[survivor.Id.Value] = record.Revision;
            }

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, firstDestroying);
            for (int index = 1; index <= 1000; index++)
            {
                RbxInstance survivor =
                    harness.Registry.WorldRoot.FindFirstChild("Cap" + index);
                Assert.IsNotNull(survivor);
                Assert.AreSame(harness.Registry.WorldRoot, survivor.Parent);
                Assert.IsTrue(harness.Registry.TryGetRecord(survivor.Id, out InstanceRecord record));
                Assert.AreEqual(revisions[survivor.Id.Value], record.Revision);
            }
        }

        [Test]
        public void AddItem_ManuallyDestroyedBeforeFire_DropsSilently()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("early-a");
            harness.Stack.Runtime.LoadMod(actor, "early-setup", @"
                local part = Instance.new('Part')
                part.Name = 'EarlyGone'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("EarlyGone");
            Assert.IsNotNull(part);
            InstanceId id = part.Id;
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Bindings.Scheduler.Advance(0.2);
            harness.Stack.Runtime.LoadMod(actor, "early-destroy",
                "workspace:FindFirstChild('EarlyGone'):Destroy()", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual(1, destroyingCount);
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            harness.Bindings.Scheduler.Advance(5d);

            Assert.AreEqual(1, destroyingCount, "no second Destroying from the dead timer");
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
            Assert.AreEqual(0, CountDebrisLogs(harness), "no log line beyond the manual destroy");
        }

        [Test]
        public void AddItem_OwnerSchedulesOwnPart_Destroys()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("owner-a");
            harness.Stack.Runtime.LoadMod(actor, "owner-setup", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedMine'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("OwnedMine");
            Assert.IsNotNull(part);
            Assert.IsTrue(harness.Registry.TryGetRecord(part.Id, out InstanceRecord record));
            Assert.AreEqual(InstanceAccessScope.Owned, record.AccessScope);
            Assert.AreEqual("owner-a", record.OwnerActorId);
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.IsNull(part.Parent);
            Assert.AreEqual(1, destroyingCount);
        }

        [Test]
        public void AddItem_CrossActorRefusedAtCallTime_PartUntouched()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("cross-a");
            harness.Stack.Runtime.LoadMod(actorA, "cross-setup", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedByA'
                part.Parent = workspace", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("OwnedByA");
            Assert.IsNotNull(part);
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            ActorContext actorB = harness.Actor("cross-b");
            harness.Stack.Runtime.LoadMod(actorB, "cross-attempt", @"
                local target = workspace:FindFirstChild('OwnedByA')
                local ok, err = pcall(function()
                    return game:GetService('Debris'):AddItem(target, 5)
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("cross-attempt", "ok"));
            string error = harness.Store.Get("cross-attempt", "err");
            StringAssert.Contains("actor 'cross-b'", error);
            StringAssert.Contains("Owned by actor 'cross-a'", error);
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            harness.Bindings.Scheduler.Advance(10d);

            Assert.AreSame(harness.Registry.WorldRoot, part.Parent);
            Assert.AreEqual(0, destroyingCount);
            Assert.IsTrue(harness.Registry.TryGet(part.Id, out _));
        }

        [Test]
        public void AddItem_BadArguments_ScheduleNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("badarg-a");
            harness.Stack.Runtime.LoadMod(actor, "badarg-setup", @"
                local part = Instance.new('Part')
                part.Name = 'BadArgPart'
                part.Parent = workspace
                local debris = game:GetService('Debris')
                local r1, e1 = pcall(function() return debris:AddItem(5, 1) end)
                local r2, e2 = pcall(function() return debris:AddItem(part, 0/0) end)
                local r3, e3 = pcall(function() return debris:AddItem(part, math.huge) end)
                store_set('r1', tostring(r1)); store_set('e1', tostring(e1))
                store_set('r2', tostring(r2)); store_set('e2', tostring(e2))
                store_set('r3', tostring(r3)); store_set('e3', tostring(e3))",
                persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r1"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r2"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r3"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "e1"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "e2"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "e3"));
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("BadArgPart");
            Assert.IsNotNull(part);
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);
            harness.Bindings.Scheduler.Advance(1d);

            Assert.AreSame(harness.Registry.WorldRoot, part.Parent);
            Assert.AreEqual(0, destroyingCount);
        }

        [Test]
        public void AddItem_OwnershipChangedAfterScheduling_FireDroppedWithOneLogLine()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("change-a");
            harness.Stack.Runtime.LoadMod(actor, "change-setup", @"
                local part = Instance.new('Part')
                part.Name = 'ChangedPart'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.5)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("ChangedPart");
            Assert.IsNotNull(part);
            Assert.IsTrue(harness.Registry.TryGetRecord(part.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Registry.SetAccessControl(
                part, "change-b", InstanceAccessScope.Owned, false);
            int debrisLogsBefore = CountDebrisLogs(harness);
            int mutationsBefore = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.AreSame(harness.Registry.WorldRoot, part.Parent);
            Assert.AreEqual(0, destroyingCount);
            Assert.IsTrue(harness.Registry.TryGet(part.Id, out _));
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);
            Assert.IsTrue(harness.Registry.TryGetRecord(part.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore, after.Revision);
            Assert.AreEqual(mutationsBefore, harness.Registry.RetainedMutationOperationCount);
            Assert.AreEqual(debrisLogsBefore + 1, CountDebrisLogs(harness));
            StringAssert.Contains("actor 'change-a'", LastDebrisLog(harness));
            StringAssert.Contains("Owned by actor 'change-b'", LastDebrisLog(harness));
        }

        [Test]
        public void AddItem_TimerSurvivesModUnloadAndThreadKill()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance worldPart = harness.Registry.Create("Part");
            worldPart.Name = "WorldPart";
            worldPart.Parent = harness.Registry.WorldRoot;
            Assert.IsTrue(harness.Registry.TryGetRecord(worldPart.Id, out InstanceRecord record));
            Assert.AreEqual(InstanceAccessScope.SharedWritable, record.AccessScope);

            ActorContext host = CoreAI.Composition.CoreServicesInstaller
                .DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            harness.Stack.Runtime.LoadMod(host, "unload-host", @"
                game:GetService('Debris'):AddItem(workspace:FindFirstChild('WorldPart'), 0.3)",
                persistToStore: false);

            InstanceId id = worldPart.Id;
            int destroyingCount = 0;
            harness.ConnectDestroying(worldPart, () => destroyingCount++);
            int mutationsBefore = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.KillAllScheduledOwnedBy("unload-host");
            Assert.IsTrue(harness.Stack.Runtime.UnloadMod(host, "unload-host"));

            harness.Bindings.Scheduler.Advance(0.35);

            Assert.IsNull(worldPart.Parent);
            Assert.AreEqual(1, destroyingCount);
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, mutationsBefore);
        }

        [Test]
        public void AddItem_UnloadTornDownPart_StaysSilent()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("unload-a");
            harness.Stack.Runtime.LoadMod(actor, "unload-owned", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedGone'
                part.Parent = workspace
                game:GetService('Debris'):AddItem(part, 0.3)", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("OwnedGone");
            Assert.IsNotNull(part);
            InstanceId id = part.Id;
            int destroyingCount = 0;
            harness.ConnectDestroying(part, () => destroyingCount++);

            harness.Bindings.KillAllScheduledOwnedBy("unload-owned");
            foreach (RbxInstance owned in harness.Registry.GetTeardownOwnedBy("unload-owned"))
            {
                owned.Destroy();
            }

            Assert.IsTrue(harness.Stack.Runtime.UnloadMod(actor, "unload-owned"));

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.AreEqual(1, destroyingCount, "only the teardown destroy fired");
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);
            Assert.AreEqual(0, CountDebrisLogs(harness));
        }

        [Test]
        public void AddItem_ReAddedItem_KeepsItsOriginalInsertionOrderForEviction()
        {
            // WHY (M8-17): the documented rule is "a re-add keeps its original insertion order for
            // cap eviction". The old queue appended a replacement node at the back and skipped the
            // stale front node, so the re-added P0 survived and P1 was evicted instead.
            using ProductionHarness harness = new ProductionHarness();
            RbxDebris debris = harness.AttachDebrisHost();
            DebrisCaller caller = harness.HostCaller();
            List<RbxInstance> parts = new();
            for (int index = 0; index < RbxDebris.MaxItems; index++)
            {
                RbxInstance part = harness.WorldPart("Evict" + index);
                parts.Add(part);
                debris.AddItem(part, 10d, caller);
            }

            debris.AddItem(parts[0], 20d, caller);
            Assert.AreEqual(RbxDebris.MaxItems, debris.PendingCount, "a re-add is not a new item");

            RbxInstance overflow = harness.WorldPart("EvictOverflow");
            debris.AddItem(overflow, 10d, caller);

            Assert.IsTrue(parts[0].IsDestroyed,
                "the oldest item by ORIGINAL insertion order is evicted, even after a re-add");
            Assert.IsFalse(parts[1].IsDestroyed, "the second-oldest item is untouched");
            Assert.IsFalse(overflow.IsDestroyed);
            Assert.AreEqual(RbxDebris.MaxItems, debris.PendingCount);
            Assert.AreEqual(RbxDebris.MaxItems, debris.DeadlineHeapCount);
            Assert.AreEqual(RbxDebris.MaxItems, debris.InsertionQueueCount);
        }

        [Test]
        public void AddItem_ChurnAndReAdds_KeepInternalQueuesAndSchedulerCallbacksBounded()
        {
            // WHY (M8-09): the 1,000-item cap was measured on live entries only, while every re-add
            // and every destroyed item left a FIFO node, a heap node and a scheduler host callback
            // behind; 200,000 iterations grew them to 400,000 entries with PendingCount at 1.
            using ProductionHarness harness = new ProductionHarness();
            RbxDebris debris = harness.AttachDebrisHost();
            DebrisCaller caller = harness.HostCaller();
            const int iterations = 10000;
            const int callbackBound = RbxDebris.MaxArmedTimers + 1;

            for (int index = 0; index < iterations; index++)
            {
                RbxInstance part = harness.WorldPart("Churn");
                debris.AddItem(part, 1e6d, caller);
                part.Destroy();
            }

            Assert.AreEqual(0, debris.PendingCount);
            Assert.AreEqual(0, debris.DeadlineHeapCount, "an early destroy leaves no heap node");
            Assert.AreEqual(0, debris.InsertionQueueCount, "an early destroy leaves no FIFO node");
            Assert.LessOrEqual(debris.ArmedTimerCount, callbackBound);

            RbxInstance same = harness.WorldPart("ReAdded");
            for (int index = 0; index < iterations; index++)
            {
                // WHY ever-shorter lifetimes: each re-add is due EARLIER than every armed callback,
                // the worst case for a scheduler that cannot cancel a host callback.
                debris.AddItem(same, 1e6d - index, caller);
            }

            Assert.AreEqual(1, debris.PendingCount);
            Assert.AreEqual(1, debris.DeadlineHeapCount, "a re-add updates the heap in place");
            Assert.AreEqual(1, debris.InsertionQueueCount, "a re-add keeps its one FIFO node");
            Assert.LessOrEqual(debris.ArmedTimerCount, callbackBound,
                "armed scheduler callbacks stay bounded however many re-adds arrive");

            harness.Bindings.Scheduler.Advance(1e6d);

            Assert.IsTrue(same.IsDestroyed, "the bounded timers still fire the latest deadline");
            Assert.AreEqual(0, debris.PendingCount);
            Assert.AreEqual(0, debris.ArmedTimerCount, "every armed callback drained");
        }

        [Test]
        public void AddItem_ManyItemsWithOneDeadline_ShareOneSchedulerCallback()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxDebris debris = harness.AttachDebrisHost();
            DebrisCaller caller = harness.HostCaller();
            List<RbxInstance> parts = new();
            for (int index = 0; index < 50; index++)
            {
                RbxInstance part = harness.WorldPart("Shared" + index);
                parts.Add(part);
                debris.AddItem(part, 0.5d, caller);
            }

            Assert.AreEqual(1, debris.ArmedTimerCount,
                "items due no earlier than an armed callback add no callback of their own");

            harness.Bindings.Scheduler.Advance(0.49d);
            Assert.IsFalse(parts[0].IsDestroyed);
            harness.Bindings.Scheduler.Advance(0.01d);

            for (int index = 0; index < parts.Count; index++)
            {
                Assert.IsTrue(parts[index].IsDestroyed, "Shared" + index + " fires with the batch");
            }

            Assert.AreEqual(0, debris.PendingCount);
        }

        [Test]
        public void AddItem_LaterItemsKeepTheirOwnDeadlines_AfterTheFirstFires()
        {
            // WHY: the negative twin of sharing one callback — an item due later is re-armed for
            // its own deadline, never destroyed early with the batch that fired before it.
            using ProductionHarness harness = new ProductionHarness();
            RbxDebris debris = harness.AttachDebrisHost();
            DebrisCaller caller = harness.HostCaller();
            RbxInstance early = harness.WorldPart("Early");
            RbxInstance late = harness.WorldPart("Late");
            debris.AddItem(early, 0.5d, caller);
            debris.AddItem(late, 10.25d, caller);

            harness.Bindings.Scheduler.Advance(0.5d);
            Assert.IsTrue(early.IsDestroyed);
            Assert.IsFalse(late.IsDestroyed);

            harness.Bindings.Scheduler.Advance(9.5d);
            Assert.IsFalse(late.IsDestroyed, "10 s is still before the 10.25 s lifetime");

            harness.Bindings.Scheduler.Advance(0.25d);
            Assert.IsTrue(late.IsDestroyed, "the re-armed callback fires on the frame the lifetime ends");
        }

        [Test]
        public void AddItem_CrossOwnerDescendant_RefusedAtCallTime_SubtreeUntouched()
        {
            // WHY (M8-06): Debris authorized the root only, so actor B's Model containing actor A's
            // part was refused by M:Destroy() but destroyed whole by Debris:AddItem(M, 0).
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorB = harness.Actor("subtree-b");
            harness.Stack.Runtime.LoadMod(actorB, "subtree-setup", @"
                local crate = Instance.new('Model')
                crate.Name = 'Crate'
                crate.Parent = workspace
                local lid = Instance.new('Part')
                lid.Name = 'Lid'
                lid.Parent = crate", persistToStore: false);

            RbxInstance crate = harness.Registry.WorldRoot.FindFirstChild("Crate");
            Assert.IsNotNull(crate);
            RbxInstance lid = crate.FindFirstChild("Lid");
            Assert.IsNotNull(lid);
            harness.Registry.SetAccessControl(lid, "subtree-a", InstanceAccessScope.Owned, false);

            harness.Stack.Runtime.LoadMod(actorB, "subtree-attempt", @"
                local ok, err = pcall(function()
                    return game:GetService('Debris'):AddItem(workspace:FindFirstChild('Crate'), 0)
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("subtree-attempt", "ok"));
            string error = harness.Store.Get("subtree-attempt", "err");
            StringAssert.Contains("actor 'subtree-b'", error);
            StringAssert.Contains("Owned by actor 'subtree-a'", error);
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            harness.Bindings.Scheduler.Advance(10d);

            Assert.IsFalse(crate.IsDestroyed);
            Assert.IsFalse(lid.IsDestroyed, "actor A's part survives actor B's Debris call");
            Assert.AreSame(crate, lid.Parent);
        }

        [Test]
        public void AddItem_DescendantReownedAfterScheduling_FireDroppedWithOneLogLine()
        {
            // WHY (M8-06): the fire-time re-check covered the root only, so a descendant that
            // changed hands after scheduling was destroyed with it.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorB = harness.Actor("reown-b");
            harness.Stack.Runtime.LoadMod(actorB, "reown-setup", @"
                local crate = Instance.new('Model')
                crate.Name = 'ReownCrate'
                crate.Parent = workspace
                local lid = Instance.new('Part')
                lid.Name = 'ReownLid'
                lid.Parent = crate
                game:GetService('Debris'):AddItem(crate, 0.5)", persistToStore: false);

            RbxInstance crate = harness.Registry.WorldRoot.FindFirstChild("ReownCrate");
            Assert.IsNotNull(crate);
            RbxInstance lid = crate.FindFirstChild("ReownLid");
            Assert.IsNotNull(lid);
            Assert.AreEqual(1, harness.Bindings.Debris.PendingCount);
            harness.Registry.SetAccessControl(lid, "reown-a", InstanceAccessScope.Owned, false);
            int debrisLogsBefore = CountDebrisLogs(harness);

            harness.Bindings.Scheduler.Advance(0.5d);

            Assert.IsFalse(crate.IsDestroyed);
            Assert.IsFalse(lid.IsDestroyed);
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);
            Assert.AreEqual(debrisLogsBefore + 1, CountDebrisLogs(harness));
            StringAssert.Contains("actor 'reown-b'", LastDebrisLog(harness));
            StringAssert.Contains("Owned by actor 'reown-a'", LastDebrisLog(harness));
        }

        [Test]
        public void AddItem_OwnSubtree_DestroysEveryDescendant()
        {
            // WHY: the positive twin of the subtree walk — an actor that owns the whole subtree
            // still gets it destroyed, children included.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("ownsub-a");
            harness.Stack.Runtime.LoadMod(actor, "ownsub-setup", @"
                local crate = Instance.new('Model')
                crate.Name = 'OwnCrate'
                crate.Parent = workspace
                local lid = Instance.new('Part')
                lid.Name = 'OwnLid'
                lid.Parent = crate
                game:GetService('Debris'):AddItem(crate, 0.5)", persistToStore: false);

            RbxInstance crate = harness.Registry.WorldRoot.FindFirstChild("OwnCrate");
            Assert.IsNotNull(crate);
            RbxInstance lid = crate.FindFirstChild("OwnLid");

            harness.Bindings.Scheduler.Advance(0.5d);

            Assert.IsTrue(crate.IsDestroyed);
            Assert.IsTrue(lid.IsDestroyed);
        }

        [Test]
        public void AddItem_Player_IsRefusedWithAKickHint()
        {
            // WHY (M8-12): a Player is Owned by its actor, so the ACL let that actor schedule its
            // own Player's destruction — which skipped PlayerRemoving and left a ghost Player.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("debris-player");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            harness.Stack.Runtime.LoadMod(actor, "debris-player-attempt", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                local ok, err = pcall(function()
                    return game:GetService('Debris'):AddItem(me, 0)
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("debris-player-attempt", "ok"));
            string error = harness.Store.Get("debris-player-attempt", "err");
            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("Player:Kick()", error);
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            harness.Bindings.Scheduler.Advance(1d);

            Assert.IsFalse(player.IsDestroyed);
            Assert.AreEqual(1, harness.Bindings.Players.GetPlayers().Count);
        }

        [Test]
        public void AddItem_NonAclWorld_ProtectedSingletonsAreRefused()
        {
            // WHY (M8-06): in a legacy (ACL-off) world the ACL demand returns early, so Debris
            // destroyed Lighting or the camera that the direct Destroy path protects explicitly.
            using ProductionHarness harness = new ProductionHarness(worldAcl: false);
            Assert.IsFalse(harness.Registry.IsWorldAclEnabled);
            harness.Stack.Runtime.LoadMod("legacy-singletons", @"
                local debris = game:GetService('Debris')
                local function try(label, target)
                    local ok, err = pcall(function() return debris:AddItem(target, 0) end)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('lighting', game:GetService('Lighting'))
                try('camera', workspace.CurrentCamera)
                try('workspace', workspace)
                try('game', game)", Capabilities, persistToStore: false);

            foreach (string label in new[] { "lighting", "camera", "workspace", "game" })
            {
                string result = harness.Store.Get("legacy-singletons", label);
                StringAssert.StartsWith("false|", result, label + " must be refused");
                StringAssert.Contains("singleton", result, label);
            }

            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);
            RbxInstance lighting = harness.Bindings.Game.GetService("Lighting");
            RbxInstance camera = harness.Registry.WorldRoot.FindFirstChildOfClass("Camera");
            Assert.IsNotNull(camera);

            harness.Bindings.Scheduler.Advance(1d);

            Assert.IsFalse(lighting.IsDestroyed);
            Assert.IsFalse(camera.IsDestroyed);
            Assert.IsFalse(harness.Registry.WorldRoot.IsDestroyed);
            Assert.IsFalse(harness.Bindings.Game.IsDestroyed);
        }

        [Test]
        public void AddItem_ReadOnlyMod_IsRefusedForMissingWorldEdit()
        {
            // WHY (M8-06): AddItem skipped the WorldEdit capability, so a mod masked to Read could
            // destroy anything its actor may destroy.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("readonly-debris");
            harness.Stack.Runtime.LoadMod(actor, "readonly-setup", @"
                local part = Instance.new('Part')
                part.Name = 'ReadOnlyTarget'
                part.Parent = workspace", persistToStore: false);
            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("ReadOnlyTarget");
            Assert.IsNotNull(part);

            harness.Stack.Runtime.LoadMod(actor, "readonly-attempt", @"
                local ok, err = pcall(function()
                    return game:GetService('Debris'):AddItem(workspace:FindFirstChild('ReadOnlyTarget'), 0)
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", LuaCapabilities.Read, persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("readonly-attempt", "ok"));
            StringAssert.Contains("WorldEdit", harness.Store.Get("readonly-attempt", "err"));
            Assert.AreEqual(0, harness.Bindings.Debris.PendingCount);

            harness.Bindings.Scheduler.Advance(1d);

            Assert.IsFalse(part.IsDestroyed);
        }

        private static int CountDebrisLogs(ProductionHarness harness)
        {
            int count = 0;
            foreach (string line in harness.LogLines)
            {
                if (line.Contains("Debris"))
                {
                    count++;
                }
            }

            return count;
        }

        private static string LastDebrisLog(ProductionHarness harness)
        {
            string last = "";
            foreach (string line in harness.LogLines)
            {
                if (line.Contains("Debris"))
                {
                    last = line;
                }
            }

            return last;
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness(bool worldAcl = true)
            {
                LogLines = new List<string>();
                Binder = new InMemoryInstanceBackingBinder();
                Registry = new InstanceRegistry(
                    binder: Binder,
                    worldAclVersion: worldAcl ? InstanceRegistry.CurrentWorldAclVersion : (int?)null,
                    worldId: "debris-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game, log: LogLines.Add);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public List<string> LogLines { get; }

            public InMemoryInstanceBackingBinder Binder { get; }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public MemoryStore Store { get; }

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

            /// <summary>Attaches the Debris timer host the way the first Lua AddItem does.</summary>
            public RbxDebris AttachDebrisHost()
            {
                RbxDebris debris = Bindings.Debris;
                debris.EnsureHost(Bindings.Scheduler, LogLines.Add);
                return debris;
            }

            /// <summary>An unrestricted caller for C#-driven AddItem calls.</summary>
            public DebrisCaller HostCaller()
            {
                return new DebrisCaller("debris-host", true, Registry.WorldId);
            }

            /// <summary>A world part created outside Lua (SharedWritable, no owner).</summary>
            public RbxInstance WorldPart(string name)
            {
                RbxInstance part = Registry.Create("Part");
                part.Name = name;
                part.Parent = Registry.WorldRoot;
                return part;
            }

            /// <summary>Binds an instance signal to the harness scheduler, then counts fires.</summary>
            public void ConnectDestroying(RbxInstance part, Action onFired)
            {
                part.Destroying.BindScheduler(Bindings.Scheduler);
                part.Destroying.Connect((Action<object[]>)(_ => onFired()));
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
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
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, StringComparison.Ordinal))
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

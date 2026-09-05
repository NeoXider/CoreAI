using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
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
            int binderBefore = harness.Binder.Materialized.Count;
            int mutationsBefore = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0.25);
            harness.Bindings.Scheduler.Advance(0.25);

            Assert.IsNull(part.Parent);
            Assert.AreEqual(1, destroyingCount);
            Assert.IsFalse(harness.Registry.TryGet(id, out _));
            Assert.AreEqual(binderBefore - 1, harness.Binder.Materialized.Count);
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
            public ProductionHarness()
            {
                LogLines = new List<string>();
                Binder = new InMemoryInstanceBackingBinder();
                Registry = new InstanceRegistry(
                    binder: Binder,
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
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

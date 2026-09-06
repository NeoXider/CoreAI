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
    /// <summary>MVP2.5 slice 8.4 gate: TweenService through production composition.</summary>
    [TestFixture]
    public sealed class Mvp8TweenServiceEditModeTests
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
        public void TweenService_ResolvesToRbxTweenService()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("svc-actor");

            // WHY: on the stub build this resolves to RbxStubService, so the gate is red until
            // the slice lands (member access would raise NOT_IMPLEMENTED there).
            Assert.IsInstanceOf<RbxTweenService>(harness.Bindings.Game.GetService("TweenService"));

            harness.Stack.Runtime.LoadMod(actor, "svc-resolve",
                "store_set('tween_class', game:GetService('TweenService').ClassName)",
                persistToStore: false);

            Assert.AreEqual("TweenService", harness.Store.Get("svc-resolve", "tween_class"));
        }

        [Test]
        public void Tween_MovesTransparencyOverScaledTime_LandsExactlyOnGoal()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("move-a");
            harness.Stack.Runtime.LoadMod(actor, "move-setup", @"
                local part = Instance.new('Part')
                part.Name = 'TweenPart'
                part.Transparency = 0
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part,
                    TweenInfo.new(1, Enum.EasingStyle.Linear, Enum.EasingDirection.Out),
                    {Transparency = 1})
                local count = 0
                local last = nil
                tw.Completed:Connect(function(state) count = count + 1 last = state end)
                tw:Play()
                -- WHY: a resumed wait runs before that frame's Heartbeat step, so every read
                -- lags one wait behind the step it observes; the C# sink asserts cover the
                -- same-frame value instead.
                task.wait(0.5)
                task.wait(0.5)
                store_set('mid_v', tostring(part.Transparency))
                store_set('mid_pb', tostring(tw.PlaybackState))
                task.wait(0.5)
                store_set('count', tostring(count))
                store_set('last', tostring(last))
                store_set('end_pb', tostring(tw.PlaybackState))",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("TweenPart");
            Assert.IsNotNull(part);

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.AreEqual(0.5f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
            Assert.AreEqual(1, harness.Bindings.TweenService.ActiveTweenCount);

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.AreEqual("0.5", harness.Store.Get("move-setup", "mid_v"));
            Assert.AreEqual("Enum.PlaybackState.Playing",
                harness.Store.Get("move-setup", "mid_pb"));

            harness.Bindings.Scheduler.Advance(0.5);

            Assert.AreEqual(1f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
            Assert.AreEqual("Enum.PlaybackState.Completed",
                harness.Store.Get("move-setup", "end_pb"));
            Assert.AreEqual("1", harness.Store.Get("move-setup", "count"));
            Assert.AreEqual("Enum.PlaybackState.Completed",
                harness.Store.Get("move-setup", "last"));
            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);
        }

        [Test]
        public void Tween_ZeroScaledDelta_FreezesEverything()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("freeze-a");
            harness.Stack.Runtime.LoadMod(actor, "freeze-setup", @"
                local part = Instance.new('Part')
                part.Name = 'FrozenPart'
                part.Transparency = 0
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part,
                    TweenInfo.new(5, Enum.EasingStyle.Linear, Enum.EasingDirection.Out),
                    {Transparency = 1})
                local count = 0
                tw.Completed:Connect(function() count = count + 1 end)
                tw:Play()
                task.wait(5)
                task.wait(0.1)
                store_set('count', tostring(count))
                store_set('end_full', tostring(part.Transparency == 1))",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("FrozenPart");
            Assert.IsNotNull(part);

            // WHY: a timeScale-0 frame driver feeds delta 0 into Advance; the tween must not
            // move, complete, or consume scheduler time — this is the scaled-not-wall proof.
            for (int frame = 0; frame < 5; frame++)
            {
                harness.Bindings.Scheduler.Advance(0d);
                Assert.AreEqual(0f,
                    harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency,
                    "frame " + frame + " at scaled delta 0 must not move the property");
            }

            Assert.AreEqual(0d, harness.Bindings.Scheduler.CurrentTime);
            Assert.AreEqual(1, harness.Bindings.TweenService.ActiveTweenCount);

            harness.Bindings.Scheduler.Advance(5d);
            harness.Bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual(1f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
            Assert.AreEqual("1", harness.Store.Get("freeze-setup", "count"));
            Assert.AreEqual("true", harness.Store.Get("freeze-setup", "end_full"));
        }

        [Test]
        public void Tween_DestroyedTarget_NeverFiresCompleted_SurvivingTwinDoes()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("doomed-a");
            harness.Stack.Runtime.LoadMod(actor, "doomed-setup", @"
                local doomed = Instance.new('Part')
                doomed.Name = 'DoomedPart'
                doomed.Transparency = 0
                doomed.Parent = workspace
                local surviving = Instance.new('Part')
                surviving.Name = 'SurvivingPart'
                surviving.Transparency = 0
                surviving.Parent = workspace
                local ts = game:GetService('TweenService')
                local twDoomed = ts:Create(doomed, TweenInfo.new(1), {Transparency = 1})
                local countDoomed = 0
                twDoomed.Completed:Connect(function() countDoomed = countDoomed + 1 end)
                twDoomed:Play()
                local twSurviving = ts:Create(surviving, TweenInfo.new(1), {Transparency = 1})
                local countSurviving = 0
                twSurviving.Completed:Connect(function() countSurviving = countSurviving + 1 end)
                twSurviving:Play()
                task.wait(0.5)
                doomed:Destroy()
                task.wait(0.5)
                task.wait(0.1)
                store_set('count_doomed', tostring(countDoomed))
                store_set('count_surviving', tostring(countSurviving))",
                persistToStore: false);

            // WHY: this is P8.4's negative twin — a destroyed tween target must never report
            // completion and must never throw, but the surviving twin proves the harness would
            // still catch a build where Completed never fires at all.
            harness.Bindings.Scheduler.Advance(0.5);
            harness.Bindings.Scheduler.Advance(0.5);
            harness.Bindings.Scheduler.Advance(0.1);

            Assert.AreEqual("0", harness.Store.Get("doomed-setup", "count_doomed"),
                "Completed never fires for a destroyed tween target");
            Assert.AreEqual("1", harness.Store.Get("doomed-setup", "count_surviving"),
                "the surviving twin still fires Completed exactly once");
            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);
        }

        [Test]
        public void Tween_Cancel_FiresCompletedWithCancelled_AndFreezesValue()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("cancel-a");
            harness.Stack.Runtime.LoadMod(actor, "cancel-setup", @"
                local part = Instance.new('Part')
                part.Name = 'CancelPart'
                part.Transparency = 0
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part,
                    TweenInfo.new(1, Enum.EasingStyle.Linear, Enum.EasingDirection.Out),
                    {Transparency = 1})
                local count = 0
                local last = nil
                tw.Completed:Connect(function(state) count = count + 1 last = state end)
                tw:Play()
                task.wait(0.4)
                task.wait(0.1)
                store_set('mid', tostring(part.Transparency > 0 and part.Transparency < 1))
                tw:Cancel()
                store_set('pb', tostring(tw.PlaybackState))
                store_set('vc', tostring(part.Transparency))
                task.wait(0.1)
                task.wait(0.1)
                store_set('count', tostring(count))
                store_set('last', tostring(last))
                task.wait(1)
                store_set('count2', tostring(count))
                store_set('v2', tostring(part.Transparency))",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0.4);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(1d);

            // WHY: mirror — Cancel fires Completed (with Cancelled) but leaves the properties
            // where they are instead of resetting them.
            Assert.AreEqual("true", harness.Store.Get("cancel-setup", "mid"));
            Assert.AreEqual("Enum.PlaybackState.Cancelled",
                harness.Store.Get("cancel-setup", "pb"));
            Assert.AreEqual("1", harness.Store.Get("cancel-setup", "count"));
            Assert.AreEqual("Enum.PlaybackState.Cancelled",
                harness.Store.Get("cancel-setup", "last"));
            Assert.AreEqual("1", harness.Store.Get("cancel-setup", "count2"),
                "Completed fires exactly once");
            Assert.AreEqual(harness.Store.Get("cancel-setup", "vc"),
                harness.Store.Get("cancel-setup", "v2"),
                "the property stays frozen where Cancel left it");
            Assert.AreNotEqual("1", harness.Store.Get("cancel-setup", "v2"),
                "Cancel does not reset properties to their goals");
        }

        [Test]
        public void Tween_Pause_FiresNothing_AndResumesFromProgress()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("pause-a");
            harness.Stack.Runtime.LoadMod(actor, "pause-setup", @"
                local part = Instance.new('Part')
                part.Name = 'PausePart'
                part.Transparency = 0
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part,
                    TweenInfo.new(1, Enum.EasingStyle.Linear, Enum.EasingDirection.Out),
                    {Transparency = 1})
                local count = 0
                local last = nil
                tw.Completed:Connect(function(state) count = count + 1 last = state end)
                tw:Play()
                task.wait(0.4)
                task.wait(0.1)
                tw:Pause()
                store_set('pb', tostring(tw.PlaybackState))
                store_set('vp', tostring(part.Transparency))
                task.wait(0.2)
                task.wait(0.1)
                store_set('count_mid', tostring(count))
                store_set('vp2', tostring(part.Transparency))
                tw:Play()
                task.wait(0.6)
                task.wait(0.1)
                store_set('count', tostring(count))
                store_set('last', tostring(last))
                store_set('end_full', tostring(part.Transparency == 1))",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0.4);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.2);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.6);
            harness.Bindings.Scheduler.Advance(0.1);

            // WHY: mirror — Pause fires no Completed and keeps progress, so Play resumes from
            // the pause point (0.4 + 0.6 = full duration completes the tween).
            Assert.AreEqual("Enum.PlaybackState.Paused",
                harness.Store.Get("pause-setup", "pb"));
            Assert.AreEqual(harness.Store.Get("pause-setup", "vp"),
                harness.Store.Get("pause-setup", "vp2"),
                "the property stays frozen while paused");
            Assert.AreEqual("0", harness.Store.Get("pause-setup", "count_mid"),
                "Pause fires no Completed");
            Assert.AreEqual("1", harness.Store.Get("pause-setup", "count"));
            Assert.AreEqual("Enum.PlaybackState.Completed",
                harness.Store.Get("pause-setup", "last"));
            Assert.AreEqual("true", harness.Store.Get("pause-setup", "end_full"));
            Assert.AreEqual(1f, harness.Bindings.PartSink.GetPartPropertiesOrDefault(
                harness.Registry.WorldRoot.FindFirstChild("PausePart").Id).Transparency);
        }

        [Test]
        public void TweenInfo_New_Defaults()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("info-a");
            harness.Stack.Runtime.LoadMod(actor, "info-setup", @"
                local info = TweenInfo.new()
                store_set('t', tostring(info.Time == 1))
                store_set('s', tostring(info.EasingStyle))
                store_set('d', tostring(info.EasingDirection))
                store_set('r', tostring(info.RepeatCount == 0))
                store_set('v', tostring(info.Reverses))
                store_set('l', tostring(info.DelayTime == 0))",
                persistToStore: false);

            // WHY: mirror TweenInfo.new defaults in order: 1, Quad, Out, 0, false, 0.
            Assert.AreEqual("true", harness.Store.Get("info-setup", "t"));
            Assert.AreEqual("Enum.EasingStyle.Quad", harness.Store.Get("info-setup", "s"));
            Assert.AreEqual("Enum.EasingDirection.Out", harness.Store.Get("info-setup", "d"));
            Assert.AreEqual("true", harness.Store.Get("info-setup", "r"));
            Assert.AreEqual("false", harness.Store.Get("info-setup", "v"));
            Assert.AreEqual("true", harness.Store.Get("info-setup", "l"));
        }

        [Test]
        public void Create_BadArguments_StartNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("badarg-a");
            harness.Stack.Runtime.LoadMod(actor, "badarg-setup", @"
                local part = Instance.new('Part')
                part.Name = 'BadPart'
                part.Transparency = 0
                part.Parent = workspace
                local ts = game:GetService('TweenService')
                local info = TweenInfo.new(1)
                local function grab(fn, key)
                    local ok, err = pcall(fn)
                    store_set(key .. '_ok', tostring(ok))
                    store_set(key .. '_err', tostring(err))
                end
                grab(function() return ts:Create(5, info, {Transparency = 1}) end, 'r1')
                grab(function() return ts:Create(part, info, {Nope = 1}) end, 'r2')
                grab(function() return ts:Create(part, TweenInfo.new(0/0), {Transparency = 1}) end, 'r3')
                grab(function() return ts:Create(part, info, {}) end, 'r4')
                grab(function() return ts:Create(part, info, {Transparency = true}) end, 'r5')
                grab(function() return ts:SmoothDamp(0, 0, 0, 0, nil, 0) end, 'r6')",
                persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r1_ok"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r2_ok"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r3_ok"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r4_ok"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r5_ok"));
            Assert.AreEqual("false", harness.Store.Get("badarg-setup", "r6_ok"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "r1_err"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "r2_err"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "r3_err"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "r4_err"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("badarg-setup", "r5_err"));
            StringAssert.Contains("NOT_IMPLEMENTED", harness.Store.Get("badarg-setup", "r6_err"));
            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("BadPart");
            Assert.IsNotNull(part);
            harness.Bindings.Scheduler.Advance(2d);

            Assert.AreEqual(0f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
        }

        [Test]
        public void Create_CrossActorRefusedAtCallTime_PartUntouched()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("acl-a");
            harness.Stack.Runtime.LoadMod(actorA, "acl-setup", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedByA'
                part.Transparency = 0
                part.Parent = workspace", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("OwnedByA");
            Assert.IsNotNull(part);

            ActorContext actorB = harness.Actor("acl-b");
            harness.Stack.Runtime.LoadMod(actorB, "acl-attempt", @"
                local target = workspace:FindFirstChild('OwnedByA')
                local ok, err = pcall(function()
                    return game:GetService('TweenService'):Create(target,
                        TweenInfo.new(1), {Transparency = 1})
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("acl-attempt", "ok"));
            string error = harness.Store.Get("acl-attempt", "err");
            StringAssert.Contains("actor 'acl-b'", error);
            StringAssert.Contains("Owned by actor 'acl-a'", error);
            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);

            harness.Bindings.Scheduler.Advance(10d);

            Assert.AreEqual(0f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
            Assert.IsTrue(harness.Registry.TryGet(part.Id, out _));
        }

        [Test]
        public void Tween_Conflict_SecondPlayCancelsFirst()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("conflict-a");
            harness.Stack.Runtime.LoadMod(actor, "conflict-setup", @"
                local part = Instance.new('Part')
                part.Name = 'ConflictPart'
                part.Transparency = 0
                part.Parent = workspace
                local ts = game:GetService('TweenService')
                local t1 = ts:Create(part, TweenInfo.new(1), {Transparency = 1})
                local c1 = 0
                local last1 = nil
                t1.Completed:Connect(function(state) c1 = c1 + 1 last1 = state end)
                t1:Play()
                task.wait(0.5)
                task.wait(0.1)
                local t2 = ts:Create(part, TweenInfo.new(2), {Transparency = 0})
                local c2 = 0
                local last2 = nil
                t2.Completed:Connect(function(state) c2 = c2 + 1 last2 = state end)
                t2:Play()
                store_set('t1pb', tostring(t1.PlaybackState))
                task.wait(0.1)
                task.wait(0.1)
                store_set('c1', tostring(c1))
                store_set('last1', tostring(last1))
                task.wait(1.9)
                task.wait(0.1)
                store_set('c1b', tostring(c1))
                store_set('c2', tostring(c2))
                store_set('last2', tostring(last2))",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0.5);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(1.9);
            harness.Bindings.Scheduler.Advance(0.1);

            // WHY: mirror — the initial tween is cancelled and overwritten by the most recent
            // tween, so t1 reports Cancelled exactly once and the property lands on t2's goal.
            Assert.AreEqual("Enum.PlaybackState.Cancelled",
                harness.Store.Get("conflict-setup", "t1pb"));
            Assert.AreEqual("1", harness.Store.Get("conflict-setup", "c1"));
            Assert.AreEqual("Enum.PlaybackState.Cancelled",
                harness.Store.Get("conflict-setup", "last1"));
            Assert.AreEqual("1", harness.Store.Get("conflict-setup", "c1b"));
            Assert.AreEqual("1", harness.Store.Get("conflict-setup", "c2"));
            Assert.AreEqual("Enum.PlaybackState.Completed",
                harness.Store.Get("conflict-setup", "last2"));

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("ConflictPart");
            Assert.IsNotNull(part);
            Assert.AreEqual(0f,
                harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency);
        }

        [Test]
        public void TweenService_GetValue_EasingMath()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("getvalue-a");
            harness.Stack.Runtime.LoadMod(actor, "getvalue-setup", @"
                local ts = game:GetService('TweenService')
                store_set('q', tostring(ts:GetValue(0.5, Enum.EasingStyle.Quad, Enum.EasingDirection.Out)))
                store_set('l', tostring(ts:GetValue(0.25, Enum.EasingStyle.Linear, Enum.EasingDirection.In)))
                store_set('hi', tostring(ts:GetValue(2, Enum.EasingStyle.Quad, Enum.EasingDirection.Out) == 1))
                store_set('lo', tostring(ts:GetValue(-1, Enum.EasingStyle.Quad, Enum.EasingDirection.Out) == 0))",
                persistToStore: false);

            Assert.AreEqual("0.75", harness.Store.Get("getvalue-setup", "q"));
            Assert.AreEqual("0.25", harness.Store.Get("getvalue-setup", "l"));
            Assert.AreEqual("true", harness.Store.Get("getvalue-setup", "hi"));
            Assert.AreEqual("true", harness.Store.Get("getvalue-setup", "lo"));
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
                    worldId: "tween-world");
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

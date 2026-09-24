using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
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

        [Test]
        public void Tween_ReversesFlashIdiom_ReturnsToTheStartAndCompletesOnce()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("flash-a");
            harness.Stack.Runtime.LoadMod(actor, "flash-setup", @"
                local part = Instance.new('Part')
                part.Name = 'FlashPart'
                part.Transparency = 0
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part,
                    TweenInfo.new(1, Enum.EasingStyle.Linear, Enum.EasingDirection.Out, 0, true),
                    {Transparency = 1})
                local count = 0
                tw.Completed:Connect(function(state)
                    count = count + 1
                    store_set('count', tostring(count))
                    store_set('last', tostring(state))
                end)
                tw:Play()",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("FlashPart");
            Assert.IsNotNull(part);

            // WHY: mirror TweenInfo.reverses — "reverse to the starting values once it reaches
            // its targets"; the island tutorial's hit flash is exactly (T, ..., 0, true), so
            // RepeatCount 0 must still play the reverse leg and end on the start value.
            float[] expected = { 0.5f, 1f, 0.5f, 0f };
            for (int step = 0; step < expected.Length; step++)
            {
                harness.Bindings.Scheduler.Advance(0.5);
                Assert.AreEqual(expected[step],
                    harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Transparency,
                    "Transparency at t=" + ((step + 1) * 0.5d));
            }

            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);
            harness.Bindings.Scheduler.Advance(0.1);
            harness.Bindings.Scheduler.Advance(0.1);

            Assert.AreEqual("1", harness.Store.Get("flash-setup", "count"),
                "Completed fires once, after the reverse leg");
            Assert.AreEqual("Enum.PlaybackState.Completed",
                harness.Store.Get("flash-setup", "last"));
        }

        [Test]
        public void Create_NonFiniteGoalFromLua_RaisesBadArgumentAtCallTime()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("nonfinite-a");
            harness.Stack.Runtime.LoadMod(actor, "nonfinite-setup", @"
                local counter = Instance.new('IntValue')
                counter.Name = 'NonFiniteCounter'
                counter.Parent = workspace
                local part = Instance.new('Part')
                part.Name = 'NonFinitePart'
                part.Parent = workspace
                local ts = game:GetService('TweenService')
                local info = TweenInfo.new(1)
                local function grab(fn, key)
                    local ok, err = pcall(fn)
                    store_set(key .. '_ok', tostring(ok))
                    store_set(key .. '_err', tostring(err))
                end
                grab(function() return ts:Create(counter, info, {Value = 0/0}) end, 'nan')
                grab(function() return ts:Create(counter, info, {Value = math.huge}) end, 'inf')
                grab(function() return ts:Create(part, info, {Transparency = -math.huge}) end, 'ninf')",
                persistToStore: false);

            // WHY: a non-finite IntValue goal used to be accepted here and then throw out of every
            // Heartbeat (IntValue refuses NaN/inf), starving every later Heartbeat subscriber.
            string[] keys = { "nan", "inf", "ninf" };
            string[] described = { "nan", "inf", "-inf" };
            for (int index = 0; index < keys.Length; index++)
            {
                Assert.AreEqual("false", harness.Store.Get("nonfinite-setup", keys[index] + "_ok"),
                    keys[index] + " goal must be refused at Create");
                string error = harness.Store.Get("nonfinite-setup", keys[index] + "_err");
                StringAssert.Contains("BAD_ARGUMENT", error);
                StringAssert.Contains("must be finite, got " + described[index], error);
            }

            Assert.AreEqual(0, harness.Bindings.TweenService.ActiveTweenCount);
            Assert.AreEqual(0, harness.Bindings.TweenService.LiveTweenCount);
            for (int frame = 0; frame < 5; frame++)
            {
                Assert.DoesNotThrow(() => harness.Bindings.Scheduler.Advance(1d / 60d));
            }

            RbxIntValue counterValue =
                (RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("NonFiniteCounter");
            Assert.AreEqual(0L, counterValue.Value);
        }

        [Test]
        public void Tween_CreatedByAMod_IsOwnedByItsActor_NotTheHostBucket()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("owner-a");
            harness.Stack.Runtime.LoadMod(actor, "owner-setup", @"
                local part = Instance.new('Part')
                part.Name = 'OwnerPart'
                part.Parent = workspace
                local tw = game:GetService('TweenService'):Create(part, TweenInfo.new(1),
                    {Transparency = 1})
                store_set('class', tw.ClassName)",
                persistToStore: false);

            Assert.AreEqual("Tween", harness.Store.Get("owner-setup", "class"));
            RbxTween tween = null;
            IReadOnlyList<RbxInstance> live = harness.Registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxTween candidate)
                {
                    Assert.IsNull(tween, "exactly one Tween was created");
                    tween = candidate;
                }
            }

            Assert.IsNotNull(tween);
            Assert.IsTrue(harness.Registry.TryGetRecord(tween.Id, out InstanceRecord record));
            // WHY: an ownerless Tween is charged to the shared "host/system" quota bucket, which
            // create-play-forget tweens used to exhaust for the whole world.
            Assert.AreEqual("owner-a", record.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, record.AccessScope);
            Assert.IsFalse(record.IsRuntimeInfrastructure,
                "a Tween is charged to its actor's quota, so it is not uncharged infrastructure");
        }

        [Test]
        public void CreatePlayComplete_TenThousandCycles_StayWithinTheCreatorsQuota()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance part = harness.Registry.Create("Part");
            part.Name = "QuotaPart";
            part.Parent = harness.Registry.WorldRoot;
            int hostBucketTweens = 0;
            harness.Registry.Registered += record =>
            {
                if (record.Instance is RbxTween
                    && (string.IsNullOrWhiteSpace(record.OwnerActorId)
                        || record.IsRuntimeInfrastructure))
                {
                    hostBucketTweens++;
                }
            };

            RbxTweenService service = harness.Bindings.TweenService;
            TweenCaller caller = new TweenCaller("quota-a", false, harness.Registry.WorldId);
            RbxTweenInfo info = new RbxTweenInfo(0.01d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);

            // WHY 10,000: the runtime's per-actor quota is 2,048 registered instances, so an
            // unreleased tween per cycle fails Create long before the loop ends.
            for (int cycle = 0; cycle < 10000; cycle++)
            {
                RbxTween tween = service.Create(part, info, new[]
                {
                    new KeyValuePair<string, object>("Transparency", (double)(cycle % 2))
                }, caller);
                tween.Play();
                harness.Bindings.Scheduler.Advance(0.02d);
                if (tween.PlaybackState != RbxTweenPlaybackState.Completed)
                {
                    Assert.Fail("cycle " + cycle + " did not complete: " + tween.PlaybackState);
                }
            }

            Assert.AreEqual(0, hostBucketTweens, "no tween is ever charged to host/system");
            Assert.AreEqual(RbxTweenService.MaxIdleTweensPerActor,
                service.IdleTweenCount("quota-a"));
            Assert.LessOrEqual(service.LiveTweenCount, RbxTweenService.MaxIdleTweensPerActor);
        }

        [Test]
        public void Create_UntweenableGoalFromLua_RaisesTheServiceStub_TypeMistakesStayBadArgument()
        {
            // WHY (M8-14): the binding refused every boolean/EnumItem goal itself with a
            // BAD_ARGUMENT, so `{CanCollide = false}` — valid Roblox that CoreAI cannot tween yet —
            // read as a mistake and never counted as a stub hit. Only the service samples the
            // member, so only it can tell a known-but-untweenable member from a type mistake.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("stub-goal-a");
            harness.Stack.Runtime.LoadMod(actor, "stub-goal-setup", @"
                local part = Instance.new('Part')
                part.Name = 'StubGoalPart'
                part.Parent = workspace
                local ts = game:GetService('TweenService')
                local info = TweenInfo.new(1)
                local function grab(fn, key)
                    local ok, err = pcall(fn)
                    store_set(key .. '_ok', tostring(ok))
                    store_set(key .. '_err', tostring(err))
                end
                grab(function() return ts:Create(part, info, {CanCollide = false}) end, 'boolean')
                grab(function() return ts:Create(part, info, {Material = Enum.Material.Neon}) end, 'enum')
                grab(function() return ts:Create(part, info, {Transparency = true}) end, 'mismatch')
                grab(function() return ts:Create(part, info, {Nope = true}) end, 'unknown')
                grab(function() return ts:Create(part, info, {Transparency = 0/0}) end, 'nan')",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("StubGoalPart");
            Assert.IsNotNull(part);
            TweenCaller caller = new TweenCaller("stub-goal-a", false, harness.Registry.WorldId);
            foreach ((string key, string property, object goal) in new[]
                     {
                         ("boolean", "CanCollide", (object)false),
                         ("enum", "Material", (object)ResolveEnumItem(harness, "Material", "Neon"))
                     })
            {
                RbxError expected = Assert.Throws<RbxError>(() => harness.Bindings.TweenService.Create(
                    part, new RbxTweenInfo(), new[] { new KeyValuePair<string, object>(property, goal) },
                    caller));
                Assert.AreEqual(RbxErrorCode.NotImplemented, expected.Code, key);
                Assert.AreEqual("false", harness.Store.Get("stub-goal-setup", key + "_ok"), key);
                StringAssert.Contains(
                    "NOT_IMPLEMENTED: " + expected.RawMessage + " | fix: " + expected.Fix,
                    harness.Store.Get("stub-goal-setup", key + "_err"),
                    key + ": a script sees the service's own stub, word for word");
            }

            StringAssert.Contains("TweenService:Create tweening Part.CanCollide (boolean)",
                harness.Store.Get("stub-goal-setup", "boolean_err"));
            StringAssert.Contains("TweenService:Create tweening Part.Material (EnumItem)",
                harness.Store.Get("stub-goal-setup", "enum_err"));
            string mismatch = harness.Store.Get("stub-goal-setup", "mismatch_err");
            StringAssert.Contains("BAD_ARGUMENT", mismatch);
            StringAssert.Contains("goal for 'Transparency' expects number, got boolean", mismatch);
            string unknown = harness.Store.Get("stub-goal-setup", "unknown_err");
            StringAssert.Contains("BAD_ARGUMENT", unknown);
            StringAssert.Contains("Nope is not a valid member of Part", unknown);
            string nan = harness.Store.Get("stub-goal-setup", "nan_err");
            StringAssert.Contains("BAD_ARGUMENT", nan);
            StringAssert.Contains("must be finite, got nan", nan);
            Assert.AreEqual(0, harness.Bindings.TweenService.LiveTweenCount,
                "no refused Create leaves a tween behind");
        }

        [Test]
        public void Tween_FromLua_BelongsToItsModsTeardown_AndPlayOrPauseByAnotherActorIsRefused()
        {
            // WHY (M8-20): whoever calls Play/Pause must hold the write right over the target, not
            // the actor that created the tween, and the creating mod must own the tween so its
            // unload tears it down; both come from the calling context, never a Lua argument.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("tween-owner");
            harness.Stack.Runtime.LoadMod(owner, "tween-owner-mod", @"
                local part = Instance.new('Part')
                part.Name = 'OwnerModTarget'
                part.Parent = workspace
                local ts = game:GetService('TweenService')
                local playing = ts:Create(part, TweenInfo.new(10), {Transparency = 1})
                playing:Play()
                local idle = ts:Create(part, TweenInfo.new(10), {Transparency = 0.5})
                local function publish(name, tween)
                    local ref = Instance.new('ObjectValue')
                    ref.Name = name
                    ref.Value = tween
                    ref.Parent = workspace
                end
                publish('PlayingTweenRef', playing)
                publish('IdleTweenRef', idle)", persistToStore: false);

            RbxTween playing = (harness.Registry.WorldRoot.FindFirstChild("PlayingTweenRef")
                as RbxObjectValue)?.Value as RbxTween;
            RbxTween idle = (harness.Registry.WorldRoot.FindFirstChild("IdleTweenRef")
                as RbxObjectValue)?.Value as RbxTween;
            Assert.IsNotNull(playing);
            Assert.IsNotNull(idle);
            Assert.IsTrue(harness.Registry.TryGetRecord(playing.Id, out InstanceRecord record));
            Assert.AreEqual("tween-owner-mod", record.OwnerModId,
                "the Lua-created tween is torn down with the mod that created it");
            Assert.AreEqual(RbxTweenPlaybackState.Playing, playing.PlaybackState);

            ActorContext intruder = harness.Actor("tween-intruder");
            harness.Stack.Runtime.LoadMod(intruder, "tween-intruder-mod", @"
                local playing = workspace:FindFirstChild('PlayingTweenRef').Value
                local idle = workspace:FindFirstChild('IdleTweenRef').Value
                local pauseOk, pauseErr = pcall(function() playing:Pause() end)
                store_set('pause', tostring(pauseOk) .. '|' .. tostring(pauseErr))
                local playOk, playErr = pcall(function() idle:Play() end)
                store_set('play', tostring(playOk) .. '|' .. tostring(playErr))",
                persistToStore: false);

            string pause = harness.Store.Get("tween-intruder-mod", "pause");
            StringAssert.StartsWith("false|", pause);
            StringAssert.Contains("actor 'tween-intruder' cannot pause a tween", pause);
            string play = harness.Store.Get("tween-intruder-mod", "play");
            StringAssert.StartsWith("false|", play);
            StringAssert.Contains("actor 'tween-intruder'", play);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, playing.PlaybackState,
                "a refused Pause leaves the owner's tween playing");
            Assert.AreEqual(RbxTweenPlaybackState.Begin, idle.PlaybackState,
                "a refused Play starts nothing");
            Assert.AreEqual(2, harness.Bindings.TweenService.CancelAndReleaseOwnedBy("tween-owner-mod"),
                "both Lua-created tweens belong to the creating mod's teardown");
        }

        private static RbxEnumItem ResolveEnumItem(ProductionHarness harness, string enumName,
            string itemName)
        {
            Assert.IsTrue(harness.Bindings.Enums.TryGet(enumName, out RbxEnum enumType));
            Assert.IsTrue(enumType.TryGetItem(itemName, out RbxEnumItem item));
            return item;
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

    /// <summary>
    /// Engine-free TweenService regressions from the MVP8 audit (M8-01, M8-02, M8-03, M8-07,
    /// M8-08, M8-20): the service, the tween and the scheduler over a recording property host,
    /// with no Lua and no scene, so every assertion counts writes and frames instead of wall time.
    /// </summary>
    [TestFixture]
    public sealed class Mvp8TweenServiceEngineFreeEditModeTests
    {
        private const string ActorA = "tween-actor-a";
        private const string ActorB = "tween-actor-b";
        private const string WorldId = "tween-engine-free-world";
        private const double Frame = 1d / 60d;

        [TestCase(1e-300)]
        [TestCase(1e-9)]
        public void Step_TinyDurationForeverRepeat_WritesOncePerFrame_AndCarriesTheRemainder(
            double time)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("TinyPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(time, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, -1, false, 0d), ActorA, ("Transparency", 1d));
            // WHY a write budget: the old per-boundary loop never returned at 1e-300 s, so
            // the host throws after a few dozen writes instead of letting the test hang.
            world.Host.WriteBudget = 64;
            tween.Play();

            double elapsed = 0d;
            for (int frame = 0; frame < 3; frame++)
            {
                int writesBefore = world.Host.Writes;
                world.Scheduler.Advance(Frame);
                elapsed = (elapsed + Frame) % time;

                Assert.AreEqual(1, world.Host.Writes - writesBefore,
                    "frame " + frame + " writes the one goal exactly once");
                Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);
                Assert.AreEqual(RbxTweenService.GetValue(elapsed / time, RbxEasingStyle.Linear,
                        RbxEasingDirection.Out), world.Host.Number(part, "Transparency"),
                    "frame " + frame + " samples the remainder modulo the duration");
            }

            Assert.AreEqual(1, world.Service.ActiveTweenCount);
        }

        [TestCase(1e-300)]
        [TestCase(1e-9)]
        public void Step_TinyDurationForeverReverse_WritesOncePerFrame(double time)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("TinyReversePart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(time, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, -1, true, 0d), ActorA, ("Transparency", 1d));
            world.Host.WriteBudget = 64;
            tween.Play();

            for (int frame = 0; frame < 3; frame++)
            {
                int writesBefore = world.Host.Writes;
                world.Scheduler.Advance(Frame);

                Assert.AreEqual(1, world.Host.Writes - writesBefore);
                Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);
                double value = world.Host.Number(part, "Transparency");
                Assert.IsTrue(value >= 0d && value <= 1d, "value " + value + " stays in [0, 1]");
            }
        }

        [TestCase(false, 1d)]
        [TestCase(true, 0d)]
        public void Step_TinyDurationFiniteRepeats_FinishesInOneFrameOnTheExactRunEnd(
            bool reverses, double expectedEnd)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("TinyFinitePart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1e-300, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, int.MaxValue, reverses, 0d), ActorA, ("Transparency", 1d));
            List<string> ends = world.RecordCompleted(tween);
            world.Host.WriteBudget = 64;
            tween.Play();

            world.Scheduler.Advance(Frame);
            world.Scheduler.Advance(0d);

            Assert.AreEqual(1, world.Host.Writes, "the whole run lands with one exact write");
            Assert.AreEqual(expectedEnd, world.Host.Number(part, "Transparency"));
            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
            CollectionAssert.AreEqual(new[] { "Completed" }, ends);
            Assert.AreEqual(0, world.Service.ActiveTweenCount);
        }

        [Test]
        public void Step_ShortDurationManyRepeats_KeepsPlayingWithOneWritePerFrame()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("ManyRepeatsPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1e-9, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, int.MaxValue, false, 0d), ActorA, ("Transparency", 1d));
            world.Host.WriteBudget = 64;
            tween.Play();

            for (int frame = 0; frame < 5; frame++)
            {
                world.Scheduler.Advance(Frame);
            }

            // WHY still playing: 2^31 legs of 1e-9 s last about 2.1 s of scaled time.
            Assert.AreEqual(5, world.Host.Writes);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);
        }

        [Test]
        public void Step_ZeroDurationForeverRepeat_CompletesOnTheGoalInsteadOfLooping()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("ZeroPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(0d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, -1, false, 0d), ActorA, ("Transparency", 1d));
            List<string> ends = world.RecordCompleted(tween);
            world.Host.WriteBudget = 64;
            tween.Play();

            world.Scheduler.Advance(Frame);
            world.Scheduler.Advance(0d);

            Assert.AreEqual(1, world.Host.Writes);
            Assert.AreEqual(1d, world.Host.Number(part, "Transparency"));
            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
            CollectionAssert.AreEqual(new[] { "Completed" }, ends);
        }

        [Test]
        public void Step_ZeroDurationReversing_EndsOnTheStartValue()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("ZeroReversePart");
            world.Host.Set(part, "Transparency", 0.25d);
            RbxTween tween = world.Create(part, new RbxTweenInfo(0d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, true, 0d), ActorA, ("Transparency", 1d));
            tween.Play();

            world.Scheduler.Advance(Frame);

            Assert.AreEqual(1, world.Host.Writes);
            Assert.AreEqual(0.25d, world.Host.Number(part, "Transparency"));
            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Reverses_EachRepeatPlaysAForwardAndAReverseLeg(int repeatCount)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("LegPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, repeatCount, true, 0d), ActorA, ("Transparency", 1d));
            List<string> ends = world.RecordCompleted(tween);
            tween.Play();

            // WHY: mirror TweenInfo.reverses — each repeat goes to the targets and back, so
            // RepeatCount r plays 2(r+1) legs of 1 s and the run ends on the start value.
            double[] pattern = { 0.5d, 1d, 0.5d, 0d };
            int halfSteps = 4 * (repeatCount + 1);
            for (int step = 1; step <= halfSteps; step++)
            {
                world.Scheduler.Advance(0.5d);
                Assert.AreEqual(pattern[(step - 1) % 4], world.Host.Number(part, "Transparency"),
                    "Transparency at t=" + (step * 0.5d));
                Assert.AreEqual(step < halfSteps
                        ? RbxTweenPlaybackState.Playing
                        : RbxTweenPlaybackState.Completed, tween.PlaybackState,
                    "state at t=" + (step * 0.5d));
            }

            world.Scheduler.Advance(0d);
            CollectionAssert.AreEqual(new[] { "Completed" }, ends,
                "Completed fires once, after the last reverse leg");
        }

        [Test]
        public void Repeat_WithoutReverses_SnapsBackToTheStartAndEndsOnTheGoal()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("RepeatPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 1, false, 0d), ActorA, ("Transparency", 1d));
            tween.Play();

            double[] expected = { 0.5d, 0d, 0.5d, 1d };
            for (int step = 0; step < expected.Length; step++)
            {
                world.Scheduler.Advance(0.5d);
                Assert.AreEqual(expected[step], world.Host.Number(part, "Transparency"),
                    "Transparency at t=" + ((step + 1) * 0.5d));
            }

            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
        }

        [TestCase(double.NaN, "nan")]
        [TestCase(double.PositiveInfinity, "inf")]
        [TestCase(double.NegativeInfinity, "-inf")]
        public void Create_NonFiniteNumberGoal_IsRefusedAtCreate(double goal, string described)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("NonFinitePart");
            int registered = world.Registry.Count;

            RbxError error = Assert.Throws<RbxError>(() => world.Create(part,
                new RbxTweenInfo(), ActorA, ("Transparency", goal)));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("must be finite, got " + described, error.Message);
            Assert.AreEqual(registered, world.Registry.Count, "a refused Create registers nothing");
            Assert.AreEqual(0, world.Service.LiveTweenCount);
        }

        [TestCase("Position", "a component nan")]
        [TestCase("Color", "a component inf")]
        [TestCase("CFrame", "a CFrame component nan")]
        public void Create_NonFiniteComponentGoal_IsRefusedAtCreate(string property,
            string described)
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("NonFiniteVectorPart");
            object goal;
            switch (property)
            {
                case "Position":
                    goal = new RbxVector3(float.NaN, 0f, 0f);
                    break;
                case "Color":
                    goal = new RbxColor3(0f, float.PositiveInfinity, 0f);
                    break;
                default:
                    goal = RbxCFrame.FromPosition(new RbxVector3(0f, float.NaN, 0f));
                    break;
            }

            RbxError error = Assert.Throws<RbxError>(() => world.Create(part,
                new RbxTweenInfo(), ActorA, (property, goal)));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("must be finite, got " + described, error.Message);
            Assert.AreEqual(0, world.Service.LiveTweenCount);
        }

        [Test]
        public void Create_KnownButUntweenableMember_RaisesTheLoudStub_UnknownStaysBadArgument()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("StubPart");

            RbxError stub = Assert.Throws<RbxError>(() => world.Create(part,
                new RbxTweenInfo(), ActorA, ("CanCollide", 1d)));
            RbxError unknown = Assert.Throws<RbxError>(() => world.Create(part,
                new RbxTweenInfo(), ActorA, ("Nope", 1d)));

            // WHY: the member exists in Roblox and the script is valid; only CoreAI cannot tween
            // it yet, so it is the NOT_IMPLEMENTED stub, not a "wrong argument" diagnosis.
            Assert.AreEqual(RbxErrorCode.NotImplemented, stub.Code);
            StringAssert.Contains("TweenService:Create tweening Part.CanCollide (boolean)",
                stub.Message);
            Assert.AreEqual(RbxErrorCode.BadArgument, unknown.Code);
            StringAssert.Contains("Nope is not a valid member of Part", unknown.Message);
        }

        [Test]
        public void Heartbeat_ThrowingWrite_DropsOnlyThatTween_AndReportsItOnce()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance faulty = world.NewPart("FaultyPart");
            RbxInstance healthy = world.NewPart("HealthyPart");
            int laterHeartbeats = 0;
            world.Scheduler.PhaseReached += (phase, delta) =>
            {
                if (phase == SchedulerPhase.Heartbeat)
                {
                    laterHeartbeats++;
                }
            };

            RbxTweenInfo info = new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            RbxTween broken = world.Create(faulty, info, ActorA, ("Transparency", 1d));
            RbxTween working = world.Create(healthy, info, ActorA, ("Transparency", 1d));
            List<string> brokenEnds = world.RecordCompleted(broken);
            world.Host.ThrowOnWriteFor = faulty.Id;
            broken.Play();
            working.Play();

            for (int frame = 0; frame < 3; frame++)
            {
                Assert.DoesNotThrow(() => world.Scheduler.Advance(0.25d),
                    "a throwing tween never escapes the Heartbeat phase");
            }

            world.Scheduler.Advance(0d);

            Assert.AreEqual(4, laterHeartbeats, "later Heartbeat subscribers ran every frame");
            Assert.AreEqual(RbxTweenPlaybackState.Cancelled, broken.PlaybackState);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, working.PlaybackState);
            Assert.AreEqual(0.75d, world.Host.Number(healthy, "Transparency"));
            Assert.AreEqual(1, world.Service.ActiveTweenCount);
            Assert.AreEqual(1, world.Service.FaultedTweenCount);
            Assert.AreEqual(1, world.LogLines.Count, "the fault is reported exactly once");
            StringAssert.Contains("FaultyPart", world.LogLines[0]);
            StringAssert.Contains("simulated setter failure", world.LogLines[0]);
            CollectionAssert.AreEqual(new[] { "Cancelled" }, brokenEnds,
                "a waiter on the dropped tween is released with Cancelled");
        }

        [Test]
        public void CreatePlayComplete_TenThousandCycles_KeepsAtMost256IdleTweensPerActor()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("CyclePart");
            int baseline = world.Registry.Count;
            int hostBucketTweens = 0;
            world.Registry.Registered += record =>
            {
                if (record.Instance is RbxTween
                    && (string.IsNullOrWhiteSpace(record.OwnerActorId)
                        || record.IsRuntimeInfrastructure))
                {
                    hostBucketTweens++;
                }
            };

            RbxTweenInfo info = new RbxTweenInfo(0.01d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            RbxTween first = null;
            for (int cycle = 0; cycle < 10000; cycle++)
            {
                RbxTween tween = world.Create(part, info, ActorA,
                    ("Transparency", (double)(cycle % 2)));
                first ??= tween;
                tween.Play();
                world.Scheduler.Advance(0.02d);
                if (tween.PlaybackState != RbxTweenPlaybackState.Completed)
                {
                    Assert.Fail("cycle " + cycle + " did not complete: " + tween.PlaybackState);
                }

                if (world.Service.LiveTweenCount > RbxTweenService.MaxIdleTweensPerActor)
                {
                    Assert.Fail("cycle " + cycle + " keeps " + world.Service.LiveTweenCount
                                + " live tweens");
                }
            }

            Assert.AreEqual(0, hostBucketTweens, "no tween is ever charged to host/system");
            Assert.AreEqual(RbxTweenService.MaxIdleTweensPerActor,
                world.Service.IdleTweenCount(ActorA));
            Assert.AreEqual(baseline + RbxTweenService.MaxIdleTweensPerActor,
                world.Registry.Count);
            Assert.IsTrue(first.IsDestroyed, "the longest-idle tween was released");
        }

        [Test]
        public void IdleCap_IsCountedPerActor()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("PerActorPart");
            RbxTweenInfo info = new RbxTweenInfo(0.01d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            List<RbxTween> fromB = new List<RbxTween>();
            for (int cycle = 0; cycle < 5; cycle++)
            {
                RbxTween tween = world.Create(part, info, ActorB, ("Transparency", 1d));
                fromB.Add(tween);
                tween.Play();
                world.Scheduler.Advance(0.02d);
            }

            for (int cycle = 0; cycle < RbxTweenService.MaxIdleTweensPerActor + 10; cycle++)
            {
                RbxTween tween = world.Create(part, info, ActorA, ("Transparency", 0d));
                tween.Play();
                world.Scheduler.Advance(0.02d);
            }

            Assert.AreEqual(RbxTweenService.MaxIdleTweensPerActor,
                world.Service.IdleTweenCount(ActorA));
            Assert.AreEqual(5, world.Service.IdleTweenCount(ActorB));
            for (int index = 0; index < fromB.Count; index++)
            {
                Assert.IsFalse(fromB[index].IsDestroyed,
                    "another actor's finished tweens never pay for this actor's");
            }
        }

        [Test]
        public void Tween_IsOwnedByItsActor_AndBelongsToItsModsTeardown()
        {
            TweenWorld world = new TweenWorld(aclEnabled: true);
            RbxInstance part = world.NewPart("OwnedTweenPart");
            RbxTween tween = world.Service.Create(part, new RbxTweenInfo(),
                Goals(("Transparency", 1d)), new TweenCaller(ActorA, false, WorldId, "mod-a"));

            Assert.IsTrue(world.Registry.TryGetRecord(tween.Id, out InstanceRecord record));
            Assert.AreEqual(ActorA, record.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, record.AccessScope);
            Assert.IsFalse(record.IsRuntimeInfrastructure);
            Assert.AreEqual("mod-a", record.OwnerModId);
            CollectionAssert.Contains(world.Registry.GetTeardownOwnedBy("mod-a"), tween,
                "unloading the mod destroys its tweens with its other instances");

            Assert.Throws<RbxError>(() => WorldAclAuthorizer.Demand(world.Registry, ActorB,
                false, WorldId, tween, WorldAclDecision.Destroy, "destroy"));
            Assert.DoesNotThrow(() => WorldAclAuthorizer.Demand(world.Registry, ActorA,
                false, WorldId, tween, WorldAclDecision.Destroy, "destroy"));
        }

        [Test]
        public void Replay_OfARecentlyCompletedTween_PlaysTheFullRunAgain()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("ReplayPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d), ActorA, ("Transparency", 1d));
            List<string> ends = world.RecordCompleted(tween);
            tween.Play();
            world.Scheduler.Advance(1d);
            world.Scheduler.Advance(0d);
            Assert.AreEqual(1, world.Service.IdleTweenCount(ActorA));

            world.Host.Set(part, "Transparency", 0d);
            tween.Play();
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);
            Assert.AreEqual(0, world.Service.IdleTweenCount(ActorA), "a replayed tween is not idle");
            world.Scheduler.Advance(0.5d);
            Assert.AreEqual(0.5d, world.Host.Number(part, "Transparency"));
            world.Scheduler.Advance(0.5d);
            world.Scheduler.Advance(0d);

            Assert.AreEqual(1d, world.Host.Number(part, "Transparency"));
            CollectionAssert.AreEqual(new[] { "Completed", "Completed" }, ends);
            Assert.IsFalse(tween.IsDestroyed);
        }

        [Test]
        public void Replay_OfAReleasedTween_RaisesInstanceDestroyed()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("ReleasedPart");
            RbxTweenInfo info = new RbxTweenInfo(0.01d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            RbxTween oldest = world.Create(part, info, ActorA, ("Transparency", 1d));
            oldest.Play();
            world.Scheduler.Advance(0.02d);
            for (int cycle = 0; cycle < RbxTweenService.MaxIdleTweensPerActor; cycle++)
            {
                RbxTween tween = world.Create(part, info, ActorA, ("Transparency", 0d));
                tween.Play();
                world.Scheduler.Advance(0.02d);
            }

            Assert.IsTrue(oldest.IsDestroyed);
            RbxError error = Assert.Throws<RbxError>(() => oldest.Play());
            Assert.AreEqual(RbxErrorCode.InstanceDestroyed, error.Code);
            StringAssert.Contains("released", error.Message);
        }

        [Test]
        public void DestroyedTween_StopsWritingAndNeverFiresCompleted()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance part = world.NewPart("DestroyedTweenPart");
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d), ActorA, ("Transparency", 1d));
            List<string> ends = world.RecordCompleted(tween);
            tween.Play();
            world.Scheduler.Advance(0.25d);

            tween.Destroy();
            world.Scheduler.Advance(0.5d);
            world.Scheduler.Advance(0.5d);
            world.Scheduler.Advance(0d);

            // WHY: mirror Instance:Destroy "disconnects all connections"; a destroyed tween
            // must stop animating and must never report completion.
            Assert.AreEqual(0.25d, world.Host.Number(part, "Transparency"));
            CollectionAssert.IsEmpty(ends);
            Assert.AreEqual(0, world.Service.ActiveTweenCount);
            Assert.AreEqual(0, world.Service.LiveTweenCount);
        }

        [Test]
        public void CancelAndReleaseOwnedBy_StopsOnlyThatModsTweens_Silently()
        {
            TweenWorld world = new TweenWorld();
            RbxInstance partA = world.NewPart("ModAPart");
            RbxInstance partB = world.NewPart("ModBPart");
            RbxTweenInfo info = new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            RbxTween fromModA = world.Service.Create(partA, info, Goals(("Transparency", 1d)),
                new TweenCaller(ActorA, false, WorldId, "mod-a"));
            RbxTween fromModB = world.Service.Create(partB, info, Goals(("Transparency", 1d)),
                new TweenCaller(ActorA, false, WorldId, "mod-b"));
            List<string> endsA = world.RecordCompleted(fromModA);
            fromModA.Play();
            fromModB.Play();
            world.Scheduler.Advance(0.5d);

            Assert.AreEqual(1, world.Service.CancelAndReleaseOwnedBy("mod-a"));
            world.Scheduler.Advance(0.25d);
            world.Scheduler.Advance(0d);

            Assert.IsTrue(fromModA.IsDestroyed);
            Assert.AreEqual(0.5d, world.Host.Number(partA, "Transparency"));
            CollectionAssert.IsEmpty(endsA);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, fromModB.PlaybackState);
            Assert.AreEqual(0.75d, world.Host.Number(partB, "Transparency"));
            Assert.AreEqual(0, world.Service.CancelAndReleaseOwnedBy("mod-a"));
        }

        [Test]
        public void CancelAndPause_ByAnActorWithoutWriteAccess_AreRefused_TheTweenKeepsPlaying()
        {
            TweenWorld world = new TweenWorld(aclEnabled: true);
            RbxInstance part = world.NewPart("GuardedPart", ActorA);
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d), ActorA, ("Transparency", 1d));
            tween.Play();
            world.Scheduler.Advance(0.25d);

            TweenCaller intruder = new TweenCaller(ActorB, false, WorldId);
            RbxError cancelRefusal = Assert.Throws<RbxError>(() => tween.Cancel(intruder));
            RbxError pauseRefusal = Assert.Throws<RbxError>(() => tween.Pause(intruder));

            StringAssert.Contains("actor '" + ActorB + "' cannot cancel a tween",
                cancelRefusal.Message);
            StringAssert.Contains("actor '" + ActorB + "' cannot pause a tween",
                pauseRefusal.Message);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);

            tween.Cancel(new TweenCaller(ActorA, false, WorldId));
            Assert.AreEqual(RbxTweenPlaybackState.Cancelled, tween.PlaybackState);
        }

        [Test]
        public void Play_ByARevokedCreator_CancelsNoConflictingTween()
        {
            TweenWorld world = new TweenWorld(aclEnabled: true);
            RbxInstance part = world.NewPart("ContestedPart");
            RbxTweenInfo info = new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d);
            RbxTween fromB = world.Create(part, info, ActorB, ("Transparency", 0d));
            world.Registry.SetAccessControl(part, ActorA, InstanceAccessScope.Owned, false);
            RbxTween fromA = world.Create(part, info, ActorA, ("Transparency", 1d));
            List<string> endsA = world.RecordCompleted(fromA);
            fromA.Play();
            world.Scheduler.Advance(0.25d);

            RbxError refusal = Assert.Throws<RbxError>(() => fromB.Play());
            world.Scheduler.Advance(0d);

            // WHY: the conflict rule used to run before the authorization, so a refused Play
            // still cancelled the rightful owner's running tween.
            StringAssert.Contains("Owned by actor '" + ActorA + "'", refusal.Message);
            Assert.AreEqual(RbxTweenPlaybackState.Playing, fromA.PlaybackState);
            Assert.AreEqual(RbxTweenPlaybackState.Begin, fromB.PlaybackState);
            CollectionAssert.IsEmpty(endsA);
        }

        [Test]
        public void Resume_AfterAnOwnershipChange_IsReauthorized()
        {
            TweenWorld world = new TweenWorld(aclEnabled: true);
            RbxInstance part = world.NewPart("ResumePart");
            RbxTween fromB = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d), ActorB, ("Transparency", 1d));
            fromB.Play();
            world.Scheduler.Advance(0.25d);
            fromB.Pause();
            world.Registry.SetAccessControl(part, ActorA, InstanceAccessScope.Owned, false);

            Assert.Throws<RbxError>(() => fromB.Play());
            world.Scheduler.Advance(0.5d);

            Assert.AreEqual(RbxTweenPlaybackState.Paused, fromB.PlaybackState);
            Assert.AreEqual(0.25d, world.Host.Number(part, "Transparency"));
        }

        [Test]
        public void Play_WithACaller_AuthorizesTheCallerNotTheCreator()
        {
            TweenWorld world = new TweenWorld(aclEnabled: true);
            RbxInstance part = world.NewPart("CallerPart", ActorA);
            RbxTween tween = world.Create(part, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                RbxEasingDirection.Out, 0, false, 0d), ActorA, ("Transparency", 1d));

            RbxError refusal = Assert.Throws<RbxError>(() =>
                tween.Play(new TweenCaller(ActorB, false, WorldId)));
            StringAssert.Contains("actor '" + ActorB + "'", refusal.Message);
            Assert.AreEqual(RbxTweenPlaybackState.Begin, tween.PlaybackState);

            tween.Play(new TweenCaller("tween-host", true, WorldId));
            Assert.AreEqual(RbxTweenPlaybackState.Playing, tween.PlaybackState);
        }

        private static List<KeyValuePair<string, object>> Goals(
            params (string Property, object Goal)[] goals)
        {
            List<KeyValuePair<string, object>> list = new List<KeyValuePair<string, object>>();
            for (int index = 0; index < goals.Length; index++)
            {
                list.Add(new KeyValuePair<string, object>(goals[index].Property,
                    goals[index].Goal));
            }

            return list;
        }

        private sealed class TweenWorld
        {
            private readonly RbxEnumRegistry _enums = RbxEnumRegistry.CreateWithBuiltins();

            public TweenWorld(bool aclEnabled = false)
            {
                Registry = aclEnabled
                    ? new InstanceRegistry(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                        worldId: WorldId)
                    : new InstanceRegistry(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Workspace = game.FindFirstChildOfClass("Workspace");
                Service = (RbxTweenService)game.FindFirstChildOfClass("TweenService");
                Scheduler = new ModScheduler(new NoScripts(), new RbxAccumulatingTimeSource());
                Host = new RecordingHost();
                Service.AttachHost(Scheduler, Host, ResolveState, LogLines.Add);
            }

            public InstanceRegistry Registry { get; }

            public RbxInstance Workspace { get; }

            public RbxTweenService Service { get; }

            public ModScheduler Scheduler { get; }

            public RecordingHost Host { get; }

            public List<string> LogLines { get; } = new List<string>();

            public RbxInstance NewPart(string name, string ownerActorId = null)
            {
                RbxInstance part = Registry.Create("Part", ownerActorId: ownerActorId);
                part.Name = name;
                part.Parent = Workspace;
                return part;
            }

            public RbxTween Create(RbxInstance target, RbxTweenInfo info, string actorId,
                params (string Property, object Goal)[] goals)
            {
                return Service.Create(target, info, Goals(goals),
                    new TweenCaller(actorId, false, WorldId));
            }

            public List<string> RecordCompleted(RbxTween tween)
            {
                List<string> ends = new List<string>();
                tween.Completed.Connect(new Action<object[]>(arguments =>
                    ends.Add(((RbxEnumItem)arguments[0]).Name)));
                return ends;
            }

            private RbxEnumItem ResolveState(RbxTweenPlaybackState state)
            {
                Assert.IsTrue(_enums.TryGet("PlaybackState", out RbxEnum playbackState));
                Assert.IsTrue(playbackState.TryGetItem(state.ToString(), out RbxEnumItem item));
                return item;
            }
        }

        private sealed class RecordingHost : ITweenPropertyHost
        {
            private readonly Dictionary<(ulong Id, string Property), object> _values =
                new Dictionary<(ulong Id, string Property), object>();

            public int Writes { get; private set; }

            public int WriteBudget { get; set; } = int.MaxValue;

            public InstanceId? ThrowOnWriteFor { get; set; }

            public TweenPropertySample Sample(RbxInstance target, string propertyName)
            {
                switch (propertyName)
                {
                    case "Transparency":
                        return TweenPropertySample.SupportedValue(
                            Read(target, propertyName, 0d), "number");
                    case "Position":
                        return TweenPropertySample.SupportedValue(
                            Read(target, propertyName, RbxVector3.Zero), "Vector3");
                    case "Color":
                        return TweenPropertySample.SupportedValue(
                            Read(target, propertyName, new RbxColor3(0f, 0f, 0f)), "Color3");
                    case "CFrame":
                        return TweenPropertySample.SupportedValue(
                            Read(target, propertyName, RbxCFrame.Identity), "CFrame");
                    case "CanCollide":
                        return TweenPropertySample.Unsupported("boolean");
                    default:
                        return TweenPropertySample.Unknown();
                }
            }

            public void Write(RbxInstance target, string propertyName, object value)
            {
                Writes++;
                if (Writes > WriteBudget)
                {
                    throw new InvalidOperationException(
                        "tween write budget of " + WriteBudget + " writes exceeded");
                }

                if (ThrowOnWriteFor.HasValue && target.Id == ThrowOnWriteFor.Value)
                {
                    throw new InvalidOperationException("simulated setter failure");
                }

                _values[(target.Id.Value, propertyName)] = value;
            }

            public void Set(RbxInstance target, string propertyName, object value)
            {
                _values[(target.Id.Value, propertyName)] = value;
            }

            public double Number(RbxInstance target, string propertyName)
            {
                return (double)Read(target, propertyName, 0d);
            }

            private object Read(RbxInstance target, string propertyName, object fallback)
            {
                return _values.TryGetValue((target.Id.Value, propertyName), out object value)
                    ? value
                    : fallback;
            }
        }

        private sealed class NoScripts : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("no scripts run in this test");
            }
        }
    }

    /// <summary>
    /// The Lua-facing tween property host (M8-02 IntValue guard, M8-14 Camera/Humanoid members,
    /// M8-21 teleport note) over the in-memory sink, camera rig and a contact-raising physics
    /// port, driven through the real TweenService.
    /// </summary>
    [TestFixture]
    public sealed class Mvp8TweenPropertyHostEditModeTests
    {
        private const string WorldId = "tween-host-world";
        private const string ActorA = "tween-host-actor";

        [TestCase(1e30, long.MaxValue)]
        [TestCase(-1e30, long.MinValue)]
        [TestCase(double.PositiveInfinity, long.MaxValue)]
        [TestCase(double.NegativeInfinity, long.MinValue)]
        [TestCase(2.5, 3L)]
        [TestCase(-2.5, -3L)]
        public void IntValueWrite_RoundsInRange_AndSaturatesBeyondInt64(double written,
            long expected)
        {
            HostWorld world = new HostWorld();
            RbxIntValue value = (RbxIntValue)world.Registry.Create("IntValue");

            Assert.DoesNotThrow(() => world.Host.Write(value, "Value", written),
                "a tweened IntValue write never throws out of Heartbeat");
            Assert.AreEqual(expected, value.Value);
        }

        [Test]
        public void IntValueWrite_OfNaN_LeavesTheValueAlone()
        {
            HostWorld world = new HostWorld();
            RbxIntValue value = (RbxIntValue)world.Registry.Create("IntValue");
            value.Value = 7L;

            Assert.DoesNotThrow(() => world.Host.Write(value, "Value", double.NaN));
            Assert.AreEqual(7L, value.Value);
        }

        [TestCase("Position")]
        [TestCase("CFrame")]
        [TestCase("Orientation")]
        [TestCase("Rotation")]
        public void TweenedSpatialWrite_NotesATeleport_SoTheOverlapFiresNoTouched(string property)
        {
            HostWorld world = new HostWorld();
            RbxBasePart mover = world.NewPart("Mover");
            RbxBasePart wall = world.NewPart("Wall");
            List<string> touches = world.RecordTouches(wall);

            object value = property == "CFrame"
                ? (object)RbxCFrame.FromPosition(new RbxVector3(0f, 5f, 0f))
                : new RbxVector3(0f, 5f, 0f);
            world.Host.Write(mover, property, value);
            world.Physics.BeginPhysicsStep();
            world.Port.RaiseBegan(mover.Id, wall.Id);
            world.Scheduler.Advance(0d);

            // WHY: mirror Touched "will not fire if the CFrame property was changed such that
            // the part overlaps another part"; a tween moves the part by assignment exactly like
            // the scripted Position/CFrame writes, which note the same teleport.
            CollectionAssert.IsEmpty(touches);
        }

        [Test]
        public void TweenedAppearanceWrite_LeavesContactsAlone()
        {
            HostWorld world = new HostWorld();
            RbxBasePart mover = world.NewPart("Fader");
            RbxBasePart wall = world.NewPart("Wall");
            List<string> touches = world.RecordTouches(wall);

            world.Host.Write(mover, "Transparency", 0.5d);
            world.Physics.BeginPhysicsStep();
            world.Port.RaiseBegan(mover.Id, wall.Id);
            world.Scheduler.Advance(0d);

            CollectionAssert.AreEqual(new[] { "Fader" }, touches,
                "a fade is not a move, so a real contact still reports");
        }

        [Test]
        public void CameraCFrameTween_DrivesTheRig_AndLandsExactlyOnTheGoal()
        {
            HostWorld world = new HostWorld();
            RbxInstance camera = world.Workspace.FindFirstChildOfClass("Camera");
            Assert.IsNotNull(camera);
            RbxCFrame goal = RbxCFrame.FromPosition(new RbxVector3(10f, 20f, 30f));
            RbxTween tween = world.Create(camera, ("CFrame", goal));
            tween.Play();

            world.Scheduler.Advance(0.5d);
            Assert.AreNotEqual(goal, world.Rig.GetCFrame(), "the camera is midway at t=0.5");
            world.Scheduler.Advance(0.5d);

            Assert.AreEqual(goal, world.Rig.GetCFrame());
            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
        }

        [Test]
        public void HumanoidWalkSpeedTween_ReachesItsGoal()
        {
            HostWorld world = new HostWorld();
            RbxHumanoid humanoid = (RbxHumanoid)world.Registry.Create("Humanoid");
            humanoid.WalkSpeed = 16d;
            RbxTween tween = world.Create(humanoid, ("WalkSpeed", 0d));
            tween.Play();

            world.Scheduler.Advance(0.5d);
            Assert.AreEqual(8d, humanoid.WalkSpeed);
            world.Scheduler.Advance(0.5d);

            Assert.AreEqual(0d, humanoid.WalkSpeed);
            Assert.AreEqual(RbxTweenPlaybackState.Completed, tween.PlaybackState);
        }

        [TestCase("Camera", "FieldOfView", "Camera.FieldOfView (number)")]
        [TestCase("Part", "CanCollide", "Part.CanCollide (boolean)")]
        [TestCase("Humanoid", "UseJumpPower", "Humanoid.UseJumpPower (boolean)")]
        public void UntweenableKnownMember_RaisesTheLoudStub(string className, string property,
            string described)
        {
            HostWorld world = new HostWorld();
            RbxInstance target = className == "Camera"
                ? world.Workspace.FindFirstChildOfClass("Camera")
                : className == "Part"
                    ? world.NewPart("StubPart")
                    : world.Registry.Create(className);

            RbxError stub = Assert.Throws<RbxError>(() => world.Create(target, (property, 1d)));

            Assert.AreEqual(RbxErrorCode.NotImplemented, stub.Code);
            StringAssert.Contains("TweenService:Create tweening " + described, stub.Message);
        }

        [Test]
        public void TweenedPartAndCameraWrites_FireChangedAndTheirPropertySignals()
        {
            // WHY (M1-03): a tween writes through the part sink and the camera rig, which live
            // outside the instance, so nothing fired Changed or GetPropertyChangedSignal for a
            // tweened move — a script watching a door's Position never saw it open.
            HostWorld world = new HostWorld();
            RbxBasePart part = world.NewPart("Watched");
            RbxInstance camera = world.Workspace.FindFirstChildOfClass("Camera");
            Assert.IsNotNull(camera);
            List<string> partChanges = world.RecordChanged(part);
            List<string> cameraChanges = world.RecordChanged(camera);
            Func<int> positions = world.CountPropertySignal(part, "Position");

            world.Host.Write(part, "CFrame", RbxCFrame.FromPosition(new RbxVector3(0f, 5f, 0f)));
            world.Host.Write(part, "Transparency", 0.5d);
            world.Host.Write(part, "Transparency", 0.5d);
            world.Host.Write(camera, "CFrame", RbxCFrame.FromPosition(new RbxVector3(1f, 2f, 3f)));
            Assert.AreEqual(0, positions(), "signals are deferred until the scheduler resumes");
            world.Scheduler.Advance(0d);

            CollectionAssert.AreEqual(new[] { "CFrame", "Position", "Transparency" }, partChanges,
                "the tweened member first, then what it moved; an unchanged write fires nothing");
            Assert.AreEqual(1, positions(), "a tweened CFrame move fires Position's signal");
            CollectionAssert.AreEqual(new[] { "CFrame" }, cameraChanges);
        }

        private sealed class HostWorld
        {
            private readonly RbxEnumRegistry _enums = RbxEnumRegistry.CreateWithBuiltins();

            public HostWorld()
            {
                Registry = new InstanceRegistry(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Workspace = game.FindFirstChildOfClass("Workspace");
                Service = (RbxTweenService)game.FindFirstChildOfClass("TweenService");
                Scheduler = new ModScheduler(new NoScripts(), new RbxAccumulatingTimeSource());
                Rig = new InMemoryCameraRig();
                Physics = new RbxWorldPhysics(Registry);
                Port = new ContactPort();
                Physics.AttachPort(Port);
                RbxWorldPhysics physics = Physics;
                Host = new LuaCsTweenPropertyHost(new InMemoryPartPropertySink(), Registry,
                    () => physics, Rig);
                Service.AttachHost(Scheduler, Host, ResolveState);
            }

            public InstanceRegistry Registry { get; }

            public RbxInstance Workspace { get; }

            public RbxTweenService Service { get; }

            public ModScheduler Scheduler { get; }

            public InMemoryCameraRig Rig { get; }

            public RbxWorldPhysics Physics { get; }

            public ContactPort Port { get; }

            public LuaCsTweenPropertyHost Host { get; }

            public RbxBasePart NewPart(string name)
            {
                RbxBasePart part = (RbxBasePart)Registry.Create("Part");
                part.Name = name;
                part.Parent = Workspace;
                return part;
            }

            public RbxTween Create(RbxInstance target, params (string Property, object Goal)[] goals)
            {
                List<KeyValuePair<string, object>> list = new List<KeyValuePair<string, object>>();
                for (int index = 0; index < goals.Length; index++)
                {
                    list.Add(new KeyValuePair<string, object>(goals[index].Property,
                        goals[index].Goal));
                }

                return Service.Create(target, new RbxTweenInfo(1d, RbxEasingStyle.Linear,
                    RbxEasingDirection.Out, 0, false, 0d), list,
                    new TweenCaller(ActorA, false, WorldId));
            }

            public List<string> RecordChanged(RbxInstance instance)
            {
                List<string> names = new List<string>();
                instance.Changed.BindScheduler(Scheduler);
                instance.Changed.Connect(new Action<object[]>(arguments =>
                    names.Add((string)arguments[0])));
                return names;
            }

            public Func<int> CountPropertySignal(RbxInstance instance, string property)
            {
                int fires = 0;
                RbxScriptSignal signal = instance.GetPropertyChangedSignal(property);
                signal.BindScheduler(Scheduler);
                signal.Connect(new Action<object[]>(_ => fires++));
                return () => fires;
            }

            public List<string> RecordTouches(RbxBasePart part)
            {
                List<string> touches = new List<string>();
                part.Touched.BindScheduler(Scheduler);
                part.Touched.Connect(new Action<object[]>(arguments =>
                    touches.Add(((RbxInstance)arguments[0]).Name)));
                return touches;
            }

            private RbxEnumItem ResolveState(RbxTweenPlaybackState state)
            {
                Assert.IsTrue(_enums.TryGet("PlaybackState", out RbxEnum playbackState));
                Assert.IsTrue(playbackState.TryGetItem(state.ToString(), out RbxEnumItem item));
                return item;
            }
        }

        private sealed class ContactPort : IRbxPhysicsPort
        {
            public event Action<InstanceId, InstanceId> ContactBegan;

            public event Action<InstanceId, InstanceId> ContactEnded;

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
                bool respectCanCollide, Func<InstanceId, bool> isEligible,
                out RbxPhysicsRaycastHit hit)
            {
                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }

            public void RaiseBegan(InstanceId first, InstanceId second)
            {
                ContactBegan?.Invoke(first, second);
            }

            public void RaiseEnded(InstanceId first, InstanceId second)
            {
                ContactEnded?.Invoke(first, second);
            }
        }

        private sealed class NoScripts : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("no scripts run in this test");
            }
        }
    }
}

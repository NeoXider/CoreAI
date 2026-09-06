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
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>MVP2.5 slice 8.1 gate: Value objects + leaderstats through production composition.</summary>
    [TestFixture]
    public sealed class Mvp8ValueObjectsEditModeTests
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
        public void Values_InstanceNew_Succeeds_And_Catalog_Reports_Creatable()
        {
            using ProductionHarness harness = new ProductionHarness();
            string[] classes =
            {
                "IntValue", "NumberValue", "StringValue", "BoolValue", "ObjectValue",
                "Vector3Value", "CFrameValue", "Color3Value"
            };
            foreach (string className in classes)
            {
                Assert.IsTrue(
                    harness.Registry.Catalog.TryGet(className, out ClassDescriptor descriptor),
                    className + " is registered");
                Assert.IsTrue(descriptor.IsCreatable, className + " is creatable");
            }

            Assert.IsTrue(
                harness.Registry.Catalog.TryGet("ValueBase", out ClassDescriptor valueBase));
            Assert.IsTrue(valueBase.IsAbstract, "ValueBase is abstract like the mirror");
            Assert.IsFalse(valueBase.IsCreatable, "ValueBase is NotCreatable like the mirror");

            ActorContext actor = harness.Actor("new-actor");
            harness.Stack.Runtime.LoadMod(actor, "new-values", @"
                local made = {}
                for _, className in ipairs({'IntValue','NumberValue','StringValue','BoolValue',
                        'ObjectValue','Vector3Value','CFrameValue','Color3Value'}) do
                    local v = Instance.new(className)
                    v.Name = className .. 'Inst'
                    v.Parent = workspace
                    made[className] = v
                end
                store_set('int_is_valuebase', tostring(made['IntValue']:IsA('ValueBase')))
                store_set('int_is_instance', tostring(made['IntValue']:IsA('Instance')))
                store_set('int_class', made['IntValue'].ClassName)
                local ok, err = pcall(function() return Instance.new('ValueBase') end)
                store_set('base_ok', tostring(ok))
                store_set('base_err', tostring(err))",
                persistToStore: false);

            // WHY: on the pre-slice build Instance.new('IntValue') raises unknown-class, so every
            // assert below is red until the slice lands.
            Assert.AreEqual("true", harness.Store.Get("new-values", "int_is_valuebase"));
            Assert.AreEqual("true", harness.Store.Get("new-values", "int_is_instance"));
            Assert.AreEqual("IntValue", harness.Store.Get("new-values", "int_class"));
            Assert.AreEqual("false", harness.Store.Get("new-values", "base_ok"));
            StringAssert.Contains("ValueBase", harness.Store.Get("new-values", "base_err"));
        }

        [Test]
        public void ScalarValues_RoundTripThroughLua_WithDefaults()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("scalar-a");
            harness.Stack.Runtime.LoadMod(actor, "scalar-setup", @"
                local i = Instance.new('IntValue'); i.Name = 'SInt'; i.Parent = workspace
                local n = Instance.new('NumberValue'); n.Name = 'SNum'; n.Parent = workspace
                local s = Instance.new('StringValue'); s.Name = 'SStr'; s.Parent = workspace
                local b = Instance.new('BoolValue'); b.Name = 'SBool'; b.Parent = workspace
                store_set('i0', tostring(i.Value == 0))
                store_set('n0', tostring(n.Value == 0))
                store_set('s0', s.Value)
                store_set('b0', tostring(b.Value))
                i.Value = 42; n.Value = 3.5; s.Value = 'hi'; b.Value = true",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("scalar-setup", "i0"));
            Assert.AreEqual("true", harness.Store.Get("scalar-setup", "n0"));
            Assert.AreEqual("", harness.Store.Get("scalar-setup", "s0"));
            Assert.AreEqual("false", harness.Store.Get("scalar-setup", "b0"));

            harness.Stack.Runtime.LoadMod(actor, "scalar-read", @"
                store_set('i', tostring(workspace:FindFirstChild('SInt').Value == 42))
                store_set('n', tostring(workspace:FindFirstChild('SNum').Value == 3.5))
                store_set('s', workspace:FindFirstChild('SStr').Value)
                store_set('b', tostring(workspace:FindFirstChild('SBool').Value))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("scalar-read", "i"));
            Assert.AreEqual("true", harness.Store.Get("scalar-read", "n"));
            Assert.AreEqual("hi", harness.Store.Get("scalar-read", "s"));
            Assert.AreEqual("true", harness.Store.Get("scalar-read", "b"));
        }

        [Test]
        public void ScalarValues_WrongTypeWrite_Rejected_ValueUnchanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("mistype-a");
            harness.Stack.Runtime.LoadMod(actor, "mistype-setup", @"
                local i = Instance.new('IntValue'); i.Name = 'MInt'; i.Value = 7; i.Parent = workspace
                local n = Instance.new('NumberValue'); n.Name = 'MNum'; n.Value = 1.5; n.Parent = workspace
                local s = Instance.new('StringValue'); s.Name = 'MStr'; s.Value = 'ok'; s.Parent = workspace
                local b = Instance.new('BoolValue'); b.Name = 'MBool'; b.Value = true; b.Parent = workspace
                local r1, e1 = pcall(function() i.Value = 'nope' end)
                local r2, e2 = pcall(function() i.Value = 0/0 end)
                local r3, e3 = pcall(function() n.Value = true end)
                local r4, e4 = pcall(function() s.Value = 5 end)
                local r5, e5 = pcall(function() b.Value = 1 end)
                store_set('r1', tostring(r1)); store_set('e1', tostring(e1))
                store_set('r2', tostring(r2)); store_set('e2', tostring(e2))
                store_set('r3', tostring(r3)); store_set('e3', tostring(e3))
                store_set('r4', tostring(r4)); store_set('e4', tostring(e4))
                store_set('r5', tostring(r5)); store_set('e5', tostring(e5))",
                persistToStore: false);

            for (int index = 1; index <= 5; index++)
            {
                Assert.AreEqual("false", harness.Store.Get("mistype-setup", "r" + index));
                StringAssert.Contains(
                    "BAD_ARGUMENT", harness.Store.Get("mistype-setup", "e" + index));
            }

            // WHY: a build that wrote through on mistyped assignment (or fired Changed there)
            // fails here: canonical values and revisions must be exactly as before.
            Assert.AreEqual(7L, ((RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("MInt")).Value);
            Assert.AreEqual(1.5d, ((RbxNumberValue)harness.Registry.WorldRoot.FindFirstChild("MNum")).Value);
            Assert.AreEqual("ok", ((RbxStringValue)harness.Registry.WorldRoot.FindFirstChild("MStr")).Value);
            Assert.IsTrue(((RbxBoolValue)harness.Registry.WorldRoot.FindFirstChild("MBool")).Value);
        }

        [Test]
        public void IntValue_RoundsHalfAwayFromZero()
        {
            // Mirror IntValue: "rounding of values to the nearest integer, with halfway cases
            // rounded away from 0".
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("round-a");
            harness.Stack.Runtime.LoadMod(actor, "round-setup", @"
                local v = Instance.new('IntValue'); v.Name = 'RInt'; v.Parent = workspace
                v.Value = 2.5; store_set('p_half', tostring(v.Value == 3))
                v.Value = -2.5; store_set('n_half', tostring(v.Value == -3))
                v.Value = 2.4; store_set('p_down', tostring(v.Value == 2))
                v.Value = -2.4; store_set('n_down', tostring(v.Value == -2))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("round-setup", "p_half"));
            Assert.AreEqual("true", harness.Store.Get("round-setup", "n_half"));
            Assert.AreEqual("true", harness.Store.Get("round-setup", "p_down"));
            Assert.AreEqual("true", harness.Store.Get("round-setup", "n_down"));
        }

        [Test]
        public void StringValue_TooLong_Rejected_ValueUnchanged()
        {
            // Mirror StringValue: "can't be more than 200,000 characters; anything longer causes
            // a `String too long` error".
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("long-a");
            harness.Stack.Runtime.LoadMod(actor, "long-setup", @"
                local v = Instance.new('StringValue'); v.Name = 'LStr'; v.Value = 'kept'
                v.Parent = workspace
                local ok, err = pcall(function() v.Value = string.rep('x', 200001) end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))",
                persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("long-setup", "ok"));
            StringAssert.Contains("String too long", harness.Store.Get("long-setup", "err"));
            Assert.AreEqual(
                "kept",
                ((RbxStringValue)harness.Registry.WorldRoot.FindFirstChild("LStr")).Value);
        }

        [Test]
        public void Changed_FiresOnceWithNewValue()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("changed-a");
            harness.Stack.Runtime.LoadMod(actor, "changed-setup", @"
                local v = Instance.new('IntValue'); v.Name = 'CInt'; v.Parent = workspace",
                persistToStore: false);

            RbxIntValue value = (RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("CInt");
            List<object[]> fired = new();
            value.Changed.BindScheduler(harness.Bindings.Scheduler);
            value.Changed.Connect((Action<object[]>)(args => fired.Add(args)));
            int propertyFires = 0;
            value.GetPropertyChangedSignal("Value").BindScheduler(harness.Bindings.Scheduler);
            value.GetPropertyChangedSignal("Value").Connect(
                (Action<object[]>)(_ => propertyFires++));

            harness.Stack.Runtime.LoadMod(actor, "changed-write", @"
                workspace:FindFirstChild('CInt').Value = 7
                workspace:FindFirstChild('CInt').Value = 7", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, fired.Count);
            Assert.AreEqual(1, fired[0].Length);
            Assert.AreEqual(7L, fired[0][0]);
            Assert.AreEqual(1, propertyFires);

            harness.Stack.Runtime.LoadMod(actor, "changed-lua", @"
                workspace:FindFirstChild('CInt').Changed:Connect(function(nv)
                    store_set('changed', tostring(nv == 9))
                end)
                workspace:FindFirstChild('CInt').Value = 9",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("true", harness.Store.Get("changed-lua", "changed"));
        }

        [Test]
        public void RealValueWrite_AdvancesTheRevisionExactlyOnce()
        {
            // WHY exactly once, not "at least once": the binding used to call RecordMutation on top of
            // the setter's own advance, so a real write moved the revision by TWO and a no-op write by
            // ONE. Only the no-op case failed a test; the double count on the real path was invisible.
            // Revision drives stale-write rejection and the MVP12 dirty set, so both are wrong.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("rev-a");
            harness.Stack.Runtime.LoadMod(actor, "rev-setup", @"
                local v = Instance.new('IntValue'); v.Name = 'RevInt'; v.Value = 5
                v.Parent = workspace",
                persistToStore: false);

            RbxIntValue value = (RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("RevInt");
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;

            harness.Stack.Runtime.LoadMod(actor, "rev-write", @"
                workspace:FindFirstChild('RevInt').Value = 6", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore + 1, after.Revision,
                "a single real value write must move the revision by exactly one");
        }

        [Test]
        public void Changed_SameValueAssignment_DoesNotFire()
        {
            // Mirror: "Fires whenever the `Class.IntValue.Value` is changed" — the mirror is
            // silent on assigning the SAME value, so this slice pins OURS: an equal assignment
            // is not a change (same guard as Name/Archivable). A build that fires unconditionally
            // fails here.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("same-a");
            harness.Stack.Runtime.LoadMod(actor, "same-setup", @"
                local v = Instance.new('IntValue'); v.Name = 'SameInt'; v.Value = 5
                v.Parent = workspace",
                persistToStore: false);

            RbxIntValue value = (RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("SameInt");
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;
            int fires = 0;
            value.Changed.BindScheduler(harness.Bindings.Scheduler);
            value.Changed.Connect((Action<object[]>)(_ => fires++));

            harness.Stack.Runtime.LoadMod(actor, "same-write", @"
                workspace:FindFirstChild('SameInt').Value = 5", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(0, fires);
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore, after.Revision);
        }

        [Test]
        public void ObjectValue_DefaultNil_SetReadClear()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("obj-a");
            harness.Stack.Runtime.LoadMod(actor, "obj-setup", @"
                local target = Instance.new('Part'); target.Name = 'ObjTarget'; target.Parent = workspace
                local v = Instance.new('ObjectValue'); v.Name = 'OVal'; v.Parent = workspace
                store_set('dflt', tostring(v.Value))
                v.Value = target
                store_set('same', tostring(v.Value == target))
                store_set('class', v.Value.ClassName)
                v.Value = nil
                store_set('cleared', tostring(v.Value))",
                persistToStore: false);

            Assert.AreEqual("nil", harness.Store.Get("obj-setup", "dflt"));
            Assert.AreEqual("true", harness.Store.Get("obj-setup", "same"));
            Assert.AreEqual("Part", harness.Store.Get("obj-setup", "class"));
            Assert.AreEqual("nil", harness.Store.Get("obj-setup", "cleared"));
        }

        [Test]
        public void DatatypeValues_RoundTripThroughLua_WithDefaults()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("dt-a");
            harness.Stack.Runtime.LoadMod(actor, "dt-setup", @"
                local v3 = Instance.new('Vector3Value'); v3.Name = 'DV3'; v3.Parent = workspace
                local cf = Instance.new('CFrameValue'); cf.Name = 'DCF'; cf.Parent = workspace
                local c3 = Instance.new('Color3Value'); c3.Name = 'DC3'; c3.Parent = workspace
                store_set('v3x0', tostring(v3.Value.X == 0))
                store_set('cfx0', tostring(cf.Value.X == 0))
                store_set('c3r0', tostring(c3.Value.R == 0))
                v3.Value = Vector3.new(1, 2, 3)
                cf.Value = CFrame.new(4, 5, 6)
                c3.Value = Color3.fromRGB(255, 0, 0)
                store_set('v3', tostring(v3.Value.X == 1 and v3.Value.Y == 2 and v3.Value.Z == 3))
                store_set('cf', tostring(cf.Value.Position.X == 4 and cf.Value.Position.Y == 5 and cf.Value.Position.Z == 6))
                store_set('c3', tostring(c3.Value.R == 1 and c3.Value.G == 0 and c3.Value.B == 0))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("dt-setup", "v3x0"));
            Assert.AreEqual("true", harness.Store.Get("dt-setup", "cfx0"));
            // WHY: Color3 default is OURS (black) — the mirror does not specify defaults.
            Assert.AreEqual("true", harness.Store.Get("dt-setup", "c3r0"));
            Assert.AreEqual("true", harness.Store.Get("dt-setup", "v3"));
            Assert.AreEqual("true", harness.Store.Get("dt-setup", "cf"));
            Assert.AreEqual("true", harness.Store.Get("dt-setup", "c3"));
        }

        [Test]
        public void LeaderstatsFolder_UnderPlayer_SurvivesTreeRoundTrip()
        {
            // leaderstats is CONVENTION (no mirror API): a Folder named exactly "leaderstats"
            // under a Player. Player nodes are rejected by the world package, so this round-trips
            // through the tree save/load mechanism every package encodes.
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance player = harness.Registry.Create("Player");
            player.Name = "LeaderPlayer";
            player.Parent = harness.Bindings.Game.GetService("Players");
            RbxInstance folder = harness.Registry.Create("Folder");
            folder.Name = "leaderstats";
            folder.Parent = player;
            RbxIntValue coins = (RbxIntValue)harness.Registry.Create("IntValue");
            coins.Name = "Coins";
            coins.Value = 7;
            coins.Parent = folder;
            RbxStringValue rank = (RbxStringValue)harness.Registry.Create("StringValue");
            rank.Name = "Rank";
            rank.Value = "Pro";
            rank.Parent = folder;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(player);

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxInstance root = InstanceTreeSerializer.Restore(snapshot, fresh);

            Assert.AreEqual("Player", root.ClassName);
            RbxInstance restoredFolder = root.FindFirstChild("leaderstats");
            Assert.IsNotNull(restoredFolder);
            Assert.AreEqual("Folder", restoredFolder.ClassName);
            Assert.AreEqual(7L, ((RbxIntValue)restoredFolder.FindFirstChild("Coins")).Value);
            Assert.AreEqual("Pro", ((RbxStringValue)restoredFolder.FindFirstChild("Rank")).Value);
            Assert.AreEqual(player.Id, root.Id, "stable ids, no remap");
        }

        [Test]
        public void WorldPackage_WithValues_RoundTripsThroughBytes()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxIntValue coins = (RbxIntValue)harness.Registry.Create("IntValue");
            coins.Name = "Coins";
            coins.Value = 42;
            coins.Parent = harness.Registry.WorldRoot;
            RbxObjectValue marker = (RbxObjectValue)harness.Registry.Create("ObjectValue");
            marker.Name = "Marker";
            marker.Value = coins;
            marker.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot tree = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            RbxWorldPackagePayload payload = new(
                DateTime.UtcNow,
                new RbxWorldSettings { WorldId = harness.Registry.WorldId },
                tree,
                new Dictionary<InstanceId, PartProperties>(),
                null,
                Array.Empty<RbxWorldModSource>());
            byte[] bytes = RbxWorldPackageSerializer.WritePackage(payload);
            RbxWorldPackagePayload reloaded =
                RbxWorldPackageSerializer.ReadPackage(bytes);

            InstanceSnapshot coinsNode = null;
            InstanceSnapshot markerNode = null;
            foreach (InstanceSnapshot node in reloaded.Tree.Instances)
            {
                if (node.Id == coins.Id.Value)
                {
                    coinsNode = node;
                }

                if (node.Id == marker.Id.Value)
                {
                    markerNode = node;
                }
            }

            Assert.IsNotNull(coinsNode, "IntValue survives the world package");
            Assert.AreEqual("42", coinsNode.Value.StringValue);
            Assert.IsNotNull(markerNode, "ObjectValue survives the world package");
            Assert.AreEqual(coins.Id.Value, markerNode.Value.ObjectTargetId);
        }

        [Test]
        public void MalformedIntValuePackage_Rejected_RegistryUnchanged()
        {
            // WHY: a build that restores without validating (or coerces "1.5" silently) passes a
            // naive round-trip but fails here: rejection first, canonical state untouched.
            using ProductionHarness harness = new ProductionHarness();
            RbxIntValue coins = (RbxIntValue)harness.Registry.Create("IntValue");
            coins.Name = "Coins";
            coins.Value = 42;
            coins.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == coins.Id.Value)
                {
                    node.Value.StringValue = "1.5";
                }
            }

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Restore(snapshot, fresh));
            StringAssert.Contains("IntValue", error.RawMessage);
            Assert.IsFalse(
                fresh.TryGet(coins.Id, out _),
                "validate-before-mutate: nothing was restored");
        }

        [Test]
        public void ObjectValue_DanglingTarget_Rejected_RegistryUnchanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxIntValue coins = (RbxIntValue)harness.Registry.Create("IntValue");
            coins.Name = "Coins";
            coins.Parent = harness.Registry.WorldRoot;
            RbxObjectValue marker = (RbxObjectValue)harness.Registry.Create("ObjectValue");
            marker.Name = "Marker";
            marker.Value = coins;
            marker.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == marker.Id.Value)
                {
                    node.Value.ObjectTargetId = coins.Id.Value + 1000000UL;
                }
            }

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Restore(snapshot, fresh));
            StringAssert.Contains("ObjectValue", error.RawMessage);
            Assert.IsFalse(fresh.TryGet(marker.Id, out _));
        }

        [Test]
        public void LeaderstatsAndHumanoid_RoundTripThroughTree_AllValuesAndIdsIdentical()
        {
            // Gate P8.6: a Player-less leaderstats folder (IntValue, NumberValue, StringValue,
            // BoolValue, ObjectValue) plus a non-default Humanoid must round-trip to an identical
            // tree — every value and every instance id.
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance root = harness.Registry.Create("Folder");
            root.Name = "NpcRoot";
            root.Parent = harness.Registry.WorldRoot;

            RbxInstance folder = harness.Registry.Create("Folder");
            folder.Name = "leaderstats";
            folder.Parent = root;

            RbxIntValue coins = (RbxIntValue)harness.Registry.Create("IntValue");
            coins.Name = "Coins";
            coins.Value = 7;
            coins.Parent = folder;
            RbxNumberValue score = (RbxNumberValue)harness.Registry.Create("NumberValue");
            score.Name = "Score";
            score.Value = 12.5d;
            score.Parent = folder;
            RbxStringValue rank = (RbxStringValue)harness.Registry.Create("StringValue");
            rank.Name = "Rank";
            rank.Value = "Pro";
            rank.Parent = folder;
            RbxBoolValue vip = (RbxBoolValue)harness.Registry.Create("BoolValue");
            vip.Name = "Vip";
            vip.Value = true;
            vip.Parent = folder;

            RbxHumanoid humanoid = (RbxHumanoid)harness.Registry.Create("Humanoid");
            humanoid.Name = "Humanoid";
            humanoid.MaxHealth = 250d;
            humanoid.Health = 30d;
            humanoid.WalkSpeed = 24d;
            humanoid.JumpPower = 75d;
            humanoid.JumpHeight = 9.5d;
            humanoid.UseJumpPower = false;
            humanoid.DisplayName = "Rex";
            humanoid.Parent = root;

            RbxObjectValue marker = (RbxObjectValue)harness.Registry.Create("ObjectValue");
            marker.Name = "Marker";
            marker.Value = humanoid;
            marker.Parent = folder;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(root);

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxInstance restoredRoot = InstanceTreeSerializer.Restore(snapshot, fresh);

            Assert.AreEqual(root.Id, restoredRoot.Id, "stable ids, no remap");
            RbxInstance restoredFolder = restoredRoot.FindFirstChild("leaderstats");
            Assert.IsNotNull(restoredFolder);
            Assert.AreEqual(folder.Id, restoredFolder.Id);

            RbxInstance restoredCoins = restoredFolder.FindFirstChild("Coins");
            Assert.AreEqual(coins.Id, restoredCoins.Id);
            Assert.AreEqual(7L, ((RbxIntValue)restoredCoins).Value);
            RbxInstance restoredScore = restoredFolder.FindFirstChild("Score");
            Assert.AreEqual(score.Id, restoredScore.Id);
            Assert.AreEqual(12.5d, ((RbxNumberValue)restoredScore).Value);
            RbxInstance restoredRank = restoredFolder.FindFirstChild("Rank");
            Assert.AreEqual(rank.Id, restoredRank.Id);
            Assert.AreEqual("Pro", ((RbxStringValue)restoredRank).Value);
            RbxInstance restoredVip = restoredFolder.FindFirstChild("Vip");
            Assert.AreEqual(vip.Id, restoredVip.Id);
            Assert.IsTrue(((RbxBoolValue)restoredVip).Value);

            RbxHumanoid restoredHumanoid = (RbxHumanoid)restoredRoot.FindFirstChild("Humanoid");
            Assert.IsNotNull(restoredHumanoid);
            Assert.AreEqual(humanoid.Id, restoredHumanoid.Id);
            Assert.AreEqual(250d, restoredHumanoid.MaxHealth);
            Assert.AreEqual(30d, restoredHumanoid.Health);
            Assert.AreEqual(24d, restoredHumanoid.WalkSpeed);
            Assert.AreEqual(75d, restoredHumanoid.JumpPower);
            Assert.AreEqual(9.5d, restoredHumanoid.JumpHeight);
            Assert.IsFalse(restoredHumanoid.UseJumpPower);
            Assert.AreEqual("Rex", restoredHumanoid.DisplayName);

            RbxObjectValue restoredMarker =
                (RbxObjectValue)restoredFolder.FindFirstChild("Marker");
            Assert.AreEqual(marker.Id, restoredMarker.Id);
            Assert.AreSame(restoredHumanoid, restoredMarker.Value);
        }

        [Test]
        public void MalformedHumanoidPackage_HealthAboveMaxHealth_Rejected_RegistryUnchanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = (RbxHumanoid)harness.Registry.Create("Humanoid");
            humanoid.Name = "Npc";
            humanoid.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == humanoid.Id.Value)
                {
                    node.Humanoid.MaxHealth = "100";
                    node.Humanoid.Health = "150";
                }
            }

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Restore(snapshot, fresh));
            StringAssert.Contains("Humanoid", error.RawMessage);
            Assert.IsFalse(
                fresh.TryGet(humanoid.Id, out _),
                "validate-before-mutate: nothing was restored");
        }

        [Test]
        public void MalformedHumanoidPackage_NegativeMaxHealth_Rejected_RegistryUnchanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = (RbxHumanoid)harness.Registry.Create("Humanoid");
            humanoid.Name = "Npc";
            humanoid.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == humanoid.Id.Value)
                {
                    node.Humanoid.MaxHealth = "-1";
                }
            }

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Restore(snapshot, fresh));
            StringAssert.Contains("Humanoid", error.RawMessage);
            Assert.IsFalse(
                fresh.TryGet(humanoid.Id, out _),
                "validate-before-mutate: nothing was restored");
        }

        [Test]
        public void MalformedHumanoidPackage_NonFiniteNumber_Rejected_RegistryUnchanged()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxHumanoid humanoid = (RbxHumanoid)harness.Registry.Create("Humanoid");
            humanoid.Name = "Npc";
            humanoid.Parent = harness.Registry.WorldRoot;

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(harness.Bindings.Game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == humanoid.Id.Value)
                {
                    node.Humanoid.WalkSpeed = "NaN";
                }
            }

            InstanceRegistry fresh = new(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "values-world");
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceTreeSerializer.Restore(snapshot, fresh));
            StringAssert.Contains("Humanoid", error.RawMessage);
            Assert.IsFalse(
                fresh.TryGet(humanoid.Id, out _),
                "validate-before-mutate: nothing was restored");
        }

        [Test]
        public void Acl_ActorWithoutRights_CannotWriteOthersValue()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("acl-a");
            harness.Stack.Runtime.LoadMod(actorA, "acl-setup", @"
                local v = Instance.new('IntValue')
                v.Name = 'OwnedVal'
                v.Value = 3
                v.Parent = workspace", persistToStore: false);

            RbxIntValue value =
                (RbxIntValue)harness.Registry.WorldRoot.FindFirstChild("OwnedVal");
            Assert.IsNotNull(value);
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord record));
            Assert.AreEqual(InstanceAccessScope.Owned, record.AccessScope);
            Assert.AreEqual("acl-a", record.OwnerActorId);
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;
            int fires = 0;
            value.Changed.BindScheduler(harness.Bindings.Scheduler);
            value.Changed.Connect((Action<object[]>)(_ => fires++));

            ActorContext actorB = harness.Actor("acl-b");
            harness.Stack.Runtime.LoadMod(actorB, "acl-attempt", @"
                local target = workspace:FindFirstChild('OwnedVal')
                local ok, err = pcall(function() target.Value = 99 end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("acl-attempt", "ok"));
            string error = harness.Store.Get("acl-attempt", "err");
            StringAssert.Contains("actor 'acl-b'", error);
            StringAssert.Contains("Owned by actor 'acl-a'", error);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(3L, value.Value);
            Assert.AreEqual(0, fires);
            Assert.IsTrue(harness.Registry.TryGetRecord(value.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore, after.Revision);
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
                    worldId: "values-world");
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

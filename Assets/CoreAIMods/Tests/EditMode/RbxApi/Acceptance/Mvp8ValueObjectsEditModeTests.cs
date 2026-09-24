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
using CoreAI.Scripting.LuaCs;
using Lua;
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
                local r4, e4 = pcall(function() s.Value = true end)
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
            // fails here: canonical values and revisions must be exactly as before. StringValue is
            // mistyped with a boolean: a number is converted like Roblox converts it (see
            // RuleTable_RbxSurface_PropertyWritesAndArguments_ConvertLikeRoblox).
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
        public void Clone_ObjectValueAndPrimaryPartPointAtTheCopiedTargets_OutsideTargetsAreKept()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("clone-a");
            harness.Stack.Runtime.LoadMod(actor, "clone-refs", @"
                local outside = Instance.new('Part'); outside.Name = 'Outside'; outside.Parent = workspace
                local rig = Instance.new('Model'); rig.Name = 'Rig'
                local root = Instance.new('Part'); root.Name = 'Root'; root.Parent = rig
                local toRoot = Instance.new('ObjectValue'); toRoot.Name = 'ToRoot'
                toRoot.Value = root; toRoot.Parent = rig
                local toOutside = Instance.new('ObjectValue'); toOutside.Name = 'ToOutside'
                toOutside.Value = outside; toOutside.Parent = rig
                rig.PrimaryPart = root
                rig.Parent = workspace
                local copy = rig:Clone()
                copy.Name = 'RigCopy'
                copy.Parent = workspace
                store_set('inner', tostring(copy.ToRoot.Value == copy.Root))
                store_set('primary', tostring(copy.PrimaryPart == copy.Root))
                store_set('outside', tostring(copy.ToOutside.Value == outside))
                store_set('source', tostring(rig.ToRoot.Value == root and rig.PrimaryPart == root))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("clone-refs", "inner"),
                "Instance.yaml Clone: an ObjectValue whose target was cloned too points at the copy");
            Assert.AreEqual("true", harness.Store.Get("clone-refs", "primary"),
                "a cloned model's PrimaryPart is its own copied part, so PivotTo moves the clone");
            Assert.AreEqual("true", harness.Store.Get("clone-refs", "outside"),
                "a target that was not cloned keeps the same value");
            Assert.AreEqual("true", harness.Store.Get("clone-refs", "source"));
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

        // ---- RBX-COERCE: the argument/property coercion rule table ---------------------------
        // WHY one table for both surfaces: a script written for Roblox must run here unchanged, and
        // Roblox reads a parameter the way Luau's luaL_checklstring/luaL_checknumber do (a number for a
        // string, a numeric string for a number, never a boolean) while an Enum member of an instance
        // also takes the item's Name or Value. The mod-core surface (typed delegates such as store_set,
        // var-args such as hooks_every) reads by the same LuaCsValueMarshaller rules as the Rbx surface.

        [TestCase("5", 5d)]
        [TestCase(" 0x10 ", 16d)]
        [TestCase("1e3", 1000d)]
        [TestCase("\t-2.5\n", -2.5d)]
        [TestCase(".5", 0.5d)]
        [TestCase("5.", 5d)]
        [TestCase("+7", 7d)]
        [TestCase("-0x10", -16d)]
        [TestCase("0X1f", 31d)]
        [TestCase("0x1p4", 16d)]
        [TestCase("0x.8", 0.5d)]
        [TestCase("0x1P-2", 0.25d)]
        [TestCase("1E-2", 0.01d)]
        [TestCase("inf", double.PositiveInfinity)]
        [TestCase("-Infinity", double.NegativeInfinity)]
        [TestCase("1e999", double.PositiveInfinity)]
        [TestCase("5\0x", 5d)]
        [TestCase(" 0x10\0 junk", 16d)]
        public void RuleTable_NumberParameter_TakesWhatLuauTonumberAccepts_OnBothSurfaces(string text,
            double expected)
        {
            LuaValue value = text;

            Assert.IsTrue(LuaCsRbxLua.TryCoerceNumber(value, out double rbx),
                "Rbx surface refused '" + text + "', which Luau's lua_tonumberx converts");
            Assert.AreEqual(expected, rbx, "Rbx surface");
            Assert.AreEqual(expected, (double)LuaCsValueMarshaller.CoerceArgument(value, typeof(double)),
                "mod-core surface");
        }

        [TestCase("nan")]
        [TestCase("NaN")]
        [TestCase("-nan")]
        [TestCase("nan(1)")]
        public void RuleTable_NumberParameter_TakesNaNSpellings_AsStrtodDoes_OnBothSurfaces(string text)
        {
            LuaValue value = text;

            Assert.IsTrue(LuaCsRbxLua.TryCoerceNumber(value, out double rbx), "Rbx surface refused " + text);
            Assert.IsTrue(double.IsNaN(rbx), "Rbx surface");
            Assert.IsTrue(double.IsNaN((double)LuaCsValueMarshaller.CoerceArgument(value, typeof(double))),
                "mod-core surface");
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("abc")]
        [TestCase("1e")]
        [TestCase("1e+")]
        [TestCase("0x")]
        [TestCase("0x1p")]
        [TestCase("5x")]
        [TestCase("1 2")]
        [TestCase("--5")]
        [TestCase("+-5")]
        [TestCase("infin")]
        [TestCase("nan(")]
        [TestCase("1_000")]
        [TestCase("0b101")]
        [TestCase(".")]
        [TestCase("e5")]
        [TestCase("\u00A05")]
        [TestCase("5\u0085")]
        [TestCase("\u00005")]
        [TestCase("5x\0")]
        public void RuleTable_NumberParameter_RefusesAStringTonumberRefuses_OnBothSurfaces(string text)
        {
            LuaValue value = text;

            Assert.IsFalse(LuaCsRbxLua.TryCoerceNumber(value, out _), "Rbx surface accepted '" + text + "'");
            Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument(value, typeof(double)),
                "mod-core surface accepted '" + text + "'");
        }

        [Test]
        public void RuleTable_StringParameter_TakesANumberAsItsTostringText_AndNothingElse_OnBothSurfaces()
        {
            LuaValue five = 5d;
            LuaValue quarter = 0.25d;
            LuaValue flag = true;

            Assert.AreEqual("5", LuaCsRbxLua.ReadAssignedString(five, "Folder", "Name"));
            Assert.AreEqual("5", LuaCsValueMarshaller.CoerceArgument(five, typeof(string)));
            Assert.AreEqual(quarter.ToString(), LuaCsRbxLua.ReadAssignedString(quarter, "Folder", "Name"),
                "the text is exactly what tostring gives the script");
            Assert.AreEqual(quarter.ToString(), LuaCsValueMarshaller.CoerceArgument(quarter, typeof(string)));

            RbxError refused = Assert.Throws<RbxError>(
                () => LuaCsRbxLua.ReadAssignedString(flag, "Folder", "Name"));
            Assert.AreEqual("Folder.Name expects a string, got boolean", refused.RawMessage);
            Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument(flag, typeof(string)),
                "Luau converts only numbers to strings, so a boolean stays a bad argument");
        }

        [Test]
        public void RuleTable_IntegerParameter_TruncatesTowardZero_AndRefusesNoIntegerRepresentation()
        {
            // WHY truncation: Luau's luaL_checkinteger casts with (int), which drops the fraction toward
            // zero; the old Convert.ToInt32 rounded 2.7 to 3 (and 2.5 to 2, banker's rounding).
            LuaValue positive = 2.7d;
            LuaValue negative = -2.7d;
            LuaValue text = "3";
            LuaValue fractionText = "4.9";

            Assert.AreEqual(2, LuaCsValueMarshaller.CoerceArgument(positive, typeof(int)));
            Assert.AreEqual(-2, LuaCsValueMarshaller.CoerceArgument(negative, typeof(int)));
            Assert.AreEqual(3, LuaCsValueMarshaller.CoerceArgument(text, typeof(int)));
            Assert.AreEqual(4L, LuaCsValueMarshaller.CoerceArgument(fractionText, typeof(long)));
            Assert.AreEqual(int.MaxValue,
                LuaCsValueMarshaller.CoerceArgument((LuaValue)2147483647d, typeof(int)));

            foreach (LuaValue refused in new LuaValue[] { 2147483648d, double.NaN, 1e300, true, "three" })
            {
                Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument(refused, typeof(int)),
                    refused + " has no int representation");
            }

            Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument((LuaValue)1e300, typeof(long)));
        }

        [Test]
        public void RuleTable_BooleanParameter_IsNeverConverted_AndAnOmittedOneIsFalse()
        {
            Assert.AreEqual(false, LuaCsValueMarshaller.CoerceArgument(LuaValue.Nil, typeof(bool)));
            Assert.AreEqual(true, LuaCsValueMarshaller.CoerceArgument((LuaValue)true, typeof(bool)));
            Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument((LuaValue)"true", typeof(bool)));
            Assert.Catch(() => LuaCsValueMarshaller.CoerceArgument((LuaValue)1d, typeof(bool)));

            LuaValue text = "true";
            RbxError refused = Assert.Throws<RbxError>(
                () => LuaCsRbxLua.ReadAssignedBoolean(text, "Part", "Anchored"));
            Assert.AreEqual("Part.Anchored expects a boolean, got string", refused.RawMessage);
        }

        [Test]
        public void RuleTable_FullTierReflectedMembers_ConvertByTheSameRules()
        {
            // WHY: the Full-tier unity_* bindings kept rules of their own: a numeric string was refused
            // for a number, 0 was false and a string threw an engine cast error for a boolean, integer
            // casts were unchecked ((int)1e300 depends on the CPU, (uint)-1 wrapped) and an enum name
            // matched in any case (C1-07).
            Assert.AreEqual("5", LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)"5", typeof(string)));
            Assert.AreEqual("5", LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)5d, typeof(string)));
            Assert.AreEqual(((LuaValue)0.25d).ToString(),
                LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)0.25d, typeof(string)));
            Assert.AreEqual(2.5d, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)"2.5", typeof(double)));
            Assert.AreEqual(16f, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)" 0x10 ", typeof(float)));
            Assert.AreEqual(2, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)2.7d, typeof(int)));
            Assert.AreEqual(-2, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)"-2.7", typeof(int)));
            Assert.AreEqual(4294967295u,
                LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)4294967295d, typeof(uint)));
            Assert.AreEqual((byte)255, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)255d, typeof(byte)));
            Assert.AreEqual(true, LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)true, typeof(bool)));
            Assert.AreEqual(false, LuaCsFullUnityRuntimeBindings.ConvertArg(LuaValue.Nil, typeof(bool)));
            Assert.AreEqual(DayOfWeek.Monday,
                LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)"Monday", typeof(DayOfWeek)));
            Assert.AreEqual(DayOfWeek.Monday,
                LuaCsFullUnityRuntimeBindings.ConvertArg((LuaValue)1d, typeof(DayOfWeek)));

            (LuaValue Value, Type Target)[] refused =
            {
                (true, typeof(string)),
                ("two", typeof(double)),
                (true, typeof(double)),
                (1e300, typeof(int)),
                (double.NaN, typeof(int)),
                (2147483648d, typeof(int)),
                (-1d, typeof(uint)),
                (256d, typeof(byte)),
                (1e300, typeof(long)),
                (1d, typeof(bool)),
                (0d, typeof(bool)),
                ("true", typeof(bool)),
                ("monday", typeof(DayOfWeek)),
                ("1", typeof(DayOfWeek))
            };
            foreach ((LuaValue value, Type target) in refused)
            {
                Assert.Throws<ArgumentException>(() => LuaCsFullUnityRuntimeBindings.ConvertArg(value, target),
                    value + " for " + target.Name + " is refused, as on the other surfaces");
            }
        }

        [Test]
        public void RuleTable_RbxSurface_PropertyWritesAndArguments_ConvertLikeRoblox()
        {
            using ProductionHarness harness = new ProductionHarness();
            InMemoryInputSource keys = new();
            harness.Bindings.UserInputService.AttachInputSource(keys);
            keys.PressKey(101);
            ActorContext actor = harness.Actor("coerce-a");
            harness.Stack.Runtime.LoadMod(actor, "coerce", @"
                local function row(label, action)
                    local ok, value = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(value))
                end
                local tween = game:GetService('TweenService')
                local uis = game:GetService('UserInputService')
                local linear, inward = Enum.EasingStyle.Linear, Enum.EasingDirection.In
                local root = Instance.new('Folder'); root.Name = 'CoerceRoot'; root.Parent = workspace
                local five = Instance.new('Folder'); five.Name = '5'; five.Parent = root
                local half = Instance.new('Folder'); half.Name = tostring(0.5); half.Parent = root
                local part = Instance.new('Part'); part.Parent = root
                local sv = Instance.new('StringValue'); sv.Parent = root
                local nv = Instance.new('NumberValue'); nv.Parent = root
                local iv = Instance.new('IntValue'); iv.Parent = root
                local bv = Instance.new('BoolValue'); bv.Parent = root
                local deep = Instance.new('Folder'); deep.Name = 'Deep'; deep.Parent = five
                local http = game:GetService('HttpService')

                row('str_arg_number', function() return root:FindFirstChild(5) == five end)
                row('str_arg_fraction', function() return root:FindFirstChild(0.5) == half end)
                row('str_arg_attribute', function() root:SetAttribute(7, 'seven') return root:GetAttribute('7') end)
                row('str_arg_member_key', function() return root[5] == five end)
                row('str_arg_boolean', function() return root:FindFirstChild(true) end)
                row('str_arg_table', function() return root:FindFirstChild({}) end)
                row('str_arg_function', function() return root:FindFirstChild(function() end) end)
                row('str_arg_nil', function() return root:FindFirstChild(nil) end)

                row('str_prop_number', function() sv.Name = 12 return sv.Name end)
                row('str_prop_value', function() sv.Value = 42 return sv.Value end)
                row('str_prop_fraction', function() sv.Value = 0.25 return sv.Value == tostring(0.25) end)
                row('str_prop_boolean', function() sv.Name = true end)
                row('str_prop_nil', function() sv.Name = nil end)
                row('str_prop_table', function() sv.Value = {} end)

                row('num_arg_string', function() return tween:GetValue('0.5', linear, inward) end)
                row('num_arg_hex', function() return Random.new(1):NextInteger(' 0x10 ', ' 0x10 ') end)
                row('num_arg_exponent', function() return Random.new(1):NextInteger('1e3', '1e3') end)
                row('num_arg_infinity', function() return tween:GetValue('inf', linear, inward) end)
                row('num_arg_text', function() return tween:GetValue('half', linear, inward) end)
                row('num_arg_empty', function() return tween:GetValue('', linear, inward) end)
                row('num_arg_boolean', function() return tween:GetValue(true, linear, inward) end)
                row('num_arg_table', function() return tween:GetValue({}, linear, inward) end)

                row('num_prop_string', function() nv.Value = '2.5' return nv.Value end)
                row('num_prop_hex', function() nv.Value = ' 0x10 ' return nv.Value end)
                row('num_prop_transparency', function() part.Transparency = '0.5' return part.Transparency end)
                row('num_prop_text', function() nv.Value = 'lots' end)
                row('num_prop_boolean', function() nv.Value = true end)

                row('int_prop_string', function() iv.Value = '3' return iv.Value end)
                row('int_prop_half', function() iv.Value = '2.5' return iv.Value end)
                row('int_arg_fraction', function() return Random.new(1):NextInteger(2.7, 2.7) end)
                row('int_arg_fraction_string', function() return Random.new(1):NextInteger('-2.7', '-2.7') end)
                row('int_arg_huge', function() return Random.new(1):NextInteger(1, 1e300) end)
                row('int_prop_nan', function() iv.Value = 'nan' end)
                row('int_prop_lowest', function() iv.Value = -2^63 return iv.Value == -2^63 end)
                row('int_prop_2p63', function() iv.Value = 2^63 end)
                row('int_prop_huge', function() iv.Value = 1e300 end)
                row('int_prop_huge_string', function() iv.Value = '1e300' end)
                row('int_prop_kept', function() return iv.Value == -2^63 end)

                row('bool_prop_string', function() part.Anchored = 'true' end)
                row('bool_prop_number', function() bv.Value = 1 end)
                row('bool_prop_nil', function() part.Archivable = nil end)
                row('bool_arg_omitted', function()
                    local a, b = Vector2.new(1, 0), Vector2.new(0, -1)
                    return a:Angle(b) == a:Angle(b, false)
                end)
                row('bool_arg_string', function() return Vector2.new(1, 0):Angle(Vector2.new(0, -1), 'true') end)
                row('bool_arg_recursive', function() return root:FindFirstChild('Deep', true) == deep end)
                row('bool_arg_recursive_omitted', function() return root:FindFirstChild('Deep') == nil end)
                row('bool_arg_recursive_zero', function() return root:FindFirstChild('Deep', 0) end)
                row('bool_arg_recursive_string', function() return root:FindFirstChild('Deep', 'false') end)
                row('bool_arg_which_is_a', function() return five:FindFirstChildWhichIsA('Folder', false) == deep end)
                row('bool_arg_which_is_a_number', function() return root:FindFirstChildWhichIsA('Folder', 1) end)
                row('bool_arg_guid_omitted', function() return #http:GenerateGUID() end)
                row('bool_arg_guid_false', function() return #http:GenerateGUID(false) end)
                row('bool_arg_guid_number', function() return http:GenerateGUID(0) end)
                row('bool_arg_compress_number', function() return http:PostAsync('https://example.invalid/', 'x', nil, 1) end)

                row('v2_mul_string', function() return Vector2.new(1, 2) * '2' == Vector2.new(2, 4) end)
                row('v2_string_mul', function() return '2' * Vector2.new(1, 2) == Vector2.new(2, 4) end)
                row('v2_div_string', function() return Vector2.new(2, 4) / ' 0x2 ' == Vector2.new(1, 2) end)
                row('v2_mul_text', function() return Vector2.new(1, 2) * 'two' end)
                row('num_arg_nul', function() return tween:GetValue('0.5\0junk', linear, inward) end)

                row('enum_prop_name', function() part.Material = 'Wood' return part.Material == Enum.Material.Wood end)
                row('enum_prop_value', function() part.Material = 816 return part.Material == Enum.Material.Concrete end)
                row('enum_prop_shape_name', function() part.Shape = 'Cylinder' return part.Shape == Enum.PartType.Cylinder end)
                row('enum_prop_shape_value', function() part.Shape = 0 return part.Shape == Enum.PartType.Ball end)
                row('enum_prop_unknown_name', function() part.Material = 'Plastik' end)
                row('enum_prop_numeric_string', function() part.Material = '256' end)
                row('enum_prop_fraction', function() part.Material = 256.5 end)
                row('enum_prop_unknown_value', function() part.Material = 1 end)
                row('enum_prop_other_enum', function() part.Material = Enum.PartType.Ball end)
                row('enum_prop_boolean', function() part.Material = true end)
                row('enum_prop_kept', function() return part.Material == Enum.Material.Concrete end)

                row('enum_arg_names', function() return tween:GetValue(0.5, 'Linear', 'In') end)
                row('enum_arg_values', function() return tween:GetValue(0.5, 0, 0) end)
                row('enum_arg_key_name', function() return uis:IsKeyDown('E') end)
                row('enum_arg_key_value', function() return uis:IsKeyDown(101) end)
                row('enum_arg_unknown', function() return tween:GetValue(0.5, 'Quadratic', 'In') end)
                row('enum_arg_boolean', function() return uis:IsKeyDown(true) end)

                row('core_str_number', function() store_set(5, 'five') return store_get('5') end)
                row('core_str_boolean', function() store_set(true, 'x') end)
                row('core_num_hex', function() return hooks_every(' 0x10 ', function() end) ~= nil end)
                row('core_num_infinity', function() return hooks_every('inf', function() end) ~= nil end)
                row('core_num_boolean', function() hooks_every(true, function() end) end)",
                persistToStore: false);

            (string Label, string Expected)[] accepted =
            {
                ("str_arg_number", "true|true"),
                ("str_arg_fraction", "true|true"),
                ("str_arg_attribute", "true|seven"),
                ("str_arg_member_key", "true|true"),
                ("str_prop_number", "true|12"),
                ("str_prop_value", "true|42"),
                ("str_prop_fraction", "true|true"),
                ("num_arg_string", "true|0.5"),
                ("num_arg_hex", "true|16"),
                ("num_arg_exponent", "true|1000"),
                ("num_prop_string", "true|2.5"),
                ("num_prop_hex", "true|16"),
                ("num_prop_transparency", "true|0.5"),
                ("int_prop_string", "true|3"),
                ("int_prop_half", "true|3"),
                ("int_arg_fraction", "true|2"),
                ("int_arg_fraction_string", "true|-2"),
                ("int_prop_lowest", "true|true"),
                ("int_prop_kept", "true|true"),
                ("bool_arg_omitted", "true|true"),
                ("bool_arg_recursive", "true|true"),
                ("bool_arg_recursive_omitted", "true|true"),
                ("bool_arg_which_is_a", "true|true"),
                ("bool_arg_guid_omitted", "true|38"),
                ("bool_arg_guid_false", "true|36"),
                ("v2_mul_string", "true|true"),
                ("v2_string_mul", "true|true"),
                ("v2_div_string", "true|true"),
                ("num_arg_nul", "true|0.5"),
                ("enum_prop_name", "true|true"),
                ("enum_prop_value", "true|true"),
                ("enum_prop_shape_name", "true|true"),
                ("enum_prop_shape_value", "true|true"),
                ("enum_prop_kept", "true|true"),
                ("enum_arg_names", "true|0.5"),
                ("enum_arg_values", "true|0.5"),
                ("enum_arg_key_name", "true|true"),
                ("enum_arg_key_value", "true|true"),
                ("core_str_number", "true|five"),
                ("core_num_hex", "true|true"),
                ("core_num_infinity", "true|true")
            };
            List<string> mismatches = new();
            foreach ((string label, string expected) in accepted)
            {
                string outcome = harness.Store.Get("coerce", label);
                if (outcome != expected)
                {
                    mismatches.Add(label + ": expected '" + expected + "', got '" + outcome + "'");
                }
            }

            (string Label, string Expected)[] refused =
            {
                ("str_arg_boolean", "Instance:FindFirstChild expects a string at argument 1"),
                ("str_arg_boolean", "got boolean at argument 1"),
                ("str_arg_table", "got table at argument 1"),
                ("str_arg_function", "got function at argument 1"),
                ("str_arg_nil", "got nil at argument 1"),
                ("str_prop_boolean", "StringValue.Name expects a string, got boolean"),
                ("str_prop_nil", "StringValue.Name expects a string, got nil"),
                ("str_prop_table", "StringValue.Value expects a string, got table"),
                ("num_arg_infinity", "TweenService:GetValue expects a finite alpha at argument 1"),
                ("num_arg_text", "TweenService:GetValue expects a number at argument 1"),
                ("num_arg_text", "got string at argument 1"),
                ("num_arg_empty", "TweenService:GetValue expects a number at argument 1"),
                ("num_arg_boolean", "got boolean at argument 1"),
                ("num_arg_table", "got table at argument 1"),
                ("num_prop_text", "NumberValue.Value expects a number, got string"),
                ("num_prop_boolean", "NumberValue.Value expects a number, got boolean"),
                ("int_arg_huge", "Random:NextInteger expects a finite whole number"),
                ("int_prop_nan", "IntValue.Value expects a finite number"),
                ("int_prop_2p63", "IntValue.Value expects a whole number from -2^63 to 2^63 - 1"),
                ("int_prop_huge", "IntValue.Value expects a whole number from -2^63 to 2^63 - 1"),
                ("int_prop_huge_string", "IntValue.Value expects a whole number from -2^63 to 2^63 - 1"),
                ("bool_prop_string", "Part.Anchored expects a boolean, got string"),
                ("bool_prop_number", "BoolValue.Value expects a boolean, got number"),
                ("bool_prop_nil", "Part.Archivable expects a boolean, got nil"),
                ("bool_arg_string", "Vector2:Angle expects a boolean at argument 2"),
                ("bool_arg_recursive_zero", "Instance:FindFirstChild expects a boolean at argument 2"),
                ("bool_arg_recursive_string", "Instance:FindFirstChild expects a boolean at argument 2"),
                ("bool_arg_which_is_a_number", "Instance:FindFirstChildWhichIsA expects a boolean at argument 2"),
                ("bool_arg_guid_number", "HttpService:GenerateGUID expects a boolean at argument 1"),
                ("bool_arg_compress_number", "HttpService:PostAsync expects a boolean at argument 4"),
                ("v2_mul_text", "Vector2 * expects a Vector2 at argument 2"),
                ("enum_prop_unknown_name", "Part.Material expects an Enum.Material item, got string \"Plastik\""),
                ("enum_prop_numeric_string", "Part.Material expects an Enum.Material item, got string \"256\""),
                ("enum_prop_fraction", "Part.Material expects an Enum.Material item, got number 256.5"),
                ("enum_prop_unknown_value", "Part.Material expects an Enum.Material item, got number 1"),
                ("enum_prop_other_enum", "Part.Material expects an Enum.Material item, got EnumItem"),
                ("enum_prop_boolean", "Part.Material expects an Enum.Material item, got boolean"),
                ("enum_arg_unknown", "TweenService:GetValue expects an Enum.EasingStyle item at argument 2"),
                ("enum_arg_unknown", "got string \"Quadratic\" at argument 2"),
                ("enum_arg_boolean", "UserInputService:IsKeyDown expects an Enum.KeyCode item at argument 1"),
                ("core_str_boolean", "bad argument #1 to 'store_set' (string expected, got boolean)"),
                ("core_num_boolean", "bad argument #1 to 'hooks_every' (number expected, got boolean)")
            };
            foreach ((string label, string expected) in refused)
            {
                string outcome = harness.Store.Get("coerce", label);
                if (!outcome.StartsWith("false|", StringComparison.Ordinal)
                    || !outcome.Contains(expected))
                {
                    mismatches.Add(label + ": expected a refusal containing '" + expected + "', got '"
                                   + outcome + "'");
                }
            }

            // WHY every row before failing: one run then shows the whole table's state, not the first row.
            Assert.IsEmpty(mismatches, string.Join("\n", mismatches));

            RbxInstance part = null;
            foreach (RbxInstance child in harness.Registry.WorldRoot.FindFirstChild("CoerceRoot").GetChildren())
            {
                if (child.ClassName == "Part")
                {
                    part = child;
                }
            }

            Assert.IsNotNull(part);
            Assert.AreEqual("Concrete", harness.Bindings.PartSink.GetPartPropertiesOrDefault(part.Id).Material.Name,
                "a refused Material write leaves the last converted one in place");
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

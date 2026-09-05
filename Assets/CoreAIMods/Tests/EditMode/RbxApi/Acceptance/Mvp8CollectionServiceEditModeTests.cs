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
    /// <summary>MVP2.5 slice 8.2 gate: CollectionService through production composition.</summary>
    [TestFixture]
    public sealed class Mvp8CollectionServiceEditModeTests
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
        public void CollectionService_ResolvesToRbxCollectionService()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("svc-actor");

            // WHY: on the stub build this resolves to RbxStubService, so the gate is red until
            // the slice lands (member access would raise NOT_IMPLEMENTED there).
            Assert.IsInstanceOf<RbxCollectionService>(
                harness.Bindings.Game.GetService("CollectionService"));

            harness.Stack.Runtime.LoadMod(actor, "svc-resolve",
                "store_set('cs_class', game:GetService('CollectionService').ClassName)",
                persistToStore: false);

            Assert.AreEqual("CollectionService", harness.Store.Get("svc-resolve", "cs_class"));
        }

        [Test]
        public void AddTag_GetTagged_ReturnsTaggedPart()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("tag-a");
            harness.Stack.Runtime.LoadMod(actor, "tag-setup", @"
                local part = Instance.new('Part')
                part.Name = 'TaggedPart'
                part.Parent = workspace
                local cs = game:GetService('CollectionService')
                cs:AddTag(part, 'x')
                store_set('has', tostring(cs:HasTag(part, 'x')))
                local tags = cs:GetTags(part)
                store_set('tags_n', tostring(#tags))
                store_set('tags_1', tags[1])
                local got = cs:GetTagged('x')
                store_set('n', tostring(#got))
                store_set('first', got[1] and got[1].Name or 'nil')
                store_set('all', table.concat(cs:GetAllTags(), ','))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("tag-setup", "has"));
            Assert.AreEqual("1", harness.Store.Get("tag-setup", "tags_n"));
            Assert.AreEqual("x", harness.Store.Get("tag-setup", "tags_1"));
            Assert.AreEqual("1", harness.Store.Get("tag-setup", "n"));
            Assert.AreEqual("TaggedPart", harness.Store.Get("tag-setup", "first"));
            Assert.AreEqual("x", harness.Store.Get("tag-setup", "all"));
        }

        [Test]
        public void Negative_GetTagged_ExcludesUntaggedAndUnknownTag()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("tag-neg");
            harness.Stack.Runtime.LoadMod(actor, "tag-neg-setup", @"
                local tagged = Instance.new('Part')
                tagged.Name = 'Tagged'
                tagged.Parent = workspace
                local plain = Instance.new('Part')
                plain.Name = 'Plain'
                plain.Parent = workspace
                local cs = game:GetService('CollectionService')
                cs:AddTag(tagged, 'x')
                local got = cs:GetTagged('x')
                store_set('n', tostring(#got))
                store_set('only', got[1] and got[1].Name or 'nil')
                local ok, unknown = pcall(function() return cs:GetTagged('nope') end)
                store_set('ok', tostring(ok))
                store_set('unknown_n', tostring(#unknown))
                local hasPlain = cs:HasTag(plain, 'x')
                store_set('has_plain', tostring(hasPlain))
                local r, e = pcall(function() return cs:AddTag(tagged, '') end)
                store_set('r', tostring(r)); store_set('e', tostring(e))",
                persistToStore: false);

            Assert.AreEqual("1", harness.Store.Get("tag-neg-setup", "n"));
            Assert.AreEqual("Tagged", harness.Store.Get("tag-neg-setup", "only"));
            Assert.AreEqual("true", harness.Store.Get("tag-neg-setup", "ok"));
            Assert.AreEqual("0", harness.Store.Get("tag-neg-setup", "unknown_n"));
            Assert.AreEqual("false", harness.Store.Get("tag-neg-setup", "has_plain"));
            Assert.AreEqual("false", harness.Store.Get("tag-neg-setup", "r"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("tag-neg-setup", "e"));
        }

        [Test]
        public void TagAddedSignal_FiresWithInstance()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("sig-a");
            harness.Stack.Runtime.LoadMod(actor, "sig-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceAddedSignal('kill'):Connect(function(inst)
                    store_set('added_n', tostring((tonumber(store_get('added_n')) or 0) + 1))
                    store_set('added_name', inst.Name)
                end)
                cs.TagAdded:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                    store_set('global_tag', tag)
                end)
                store_set('added_n', '0')
                store_set('global_n', '0')
                local part = Instance.new('Part')
                part.Name = 'KillBrick'
                part.Parent = workspace
                cs:AddTag(part, 'kill')",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("1", harness.Store.Get("sig-setup", "added_n"));
            Assert.AreEqual("KillBrick", harness.Store.Get("sig-setup", "added_name"));
            Assert.AreEqual("1", harness.Store.Get("sig-setup", "global_n"));
            Assert.AreEqual("kill", harness.Store.Get("sig-setup", "global_tag"));
        }

        [Test]
        public void Negative_AddTag_Duplicate_FiresNothingAgain()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("dup-a");
            harness.Stack.Runtime.LoadMod(actor, "dup-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceAddedSignal('k'):Connect(function(inst)
                    store_set('added_n', tostring((tonumber(store_get('added_n')) or 0) + 1))
                end)
                cs.TagAdded:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                end)
                store_set('added_n', '0')
                store_set('global_n', '0')
                local part = Instance.new('Part')
                part.Name = 'Dup'
                part.Parent = workspace
                cs:AddTag(part, 'k')
                -- WHY: mirror pins duplicate AddTag as doing nothing, so neither the per-tag
                -- signal nor the TagAdded global may fire a second time here.
                cs:AddTag(part, 'k')",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("1", harness.Store.Get("dup-setup", "added_n"));
            Assert.AreEqual("1", harness.Store.Get("dup-setup", "global_n"));
            Assert.AreEqual(1, harness.Registry.Tags.GetTagged("k").Count);
        }

        [Test]
        public void TagAddedGlobal_FiresOncePerPlaceFirstUse()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("first-a");
            harness.Stack.Runtime.LoadMod(actor, "first-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceAddedSignal('k'):Connect(function(inst)
                    store_set('added_n', tostring((tonumber(store_get('added_n')) or 0) + 1))
                end)
                cs.TagAdded:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                end)
                store_set('added_n', '0')
                store_set('global_n', '0')
                for i = 1, 2 do
                    local p = Instance.new('Part')
                    p.Name = 'K' .. i
                    p.Parent = workspace
                    cs:AddTag(p, 'k')
                end",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("2", harness.Store.Get("first-setup", "added_n"));
            Assert.AreEqual("1", harness.Store.Get("first-setup", "global_n"));
        }

        [Test]
        public void RemoveTag_FiresRemovedAndDropsFromGetTagged()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("rm-a");
            harness.Stack.Runtime.LoadMod(actor, "rm-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceRemovedSignal('k'):Connect(function(inst)
                    store_set('removed_n', tostring((tonumber(store_get('removed_n')) or 0) + 1))
                    store_set('removed_name', inst.Name)
                end)
                cs.TagRemoved:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                    store_set('global_tag', tag)
                end)
                store_set('removed_n', '0')
                store_set('global_n', '0')
                local part = Instance.new('Part')
                part.Name = 'Gone'
                part.Parent = workspace
                cs:AddTag(part, 'k')
                cs:RemoveTag(part, 'k')
                store_set('left', tostring(#cs:GetTagged('k')))
                store_set('has', tostring(cs:HasTag(part, 'k')))
                store_set('all', table.concat(cs:GetAllTags(), ','))",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("1", harness.Store.Get("rm-setup", "removed_n"));
            Assert.AreEqual("Gone", harness.Store.Get("rm-setup", "removed_name"));
            Assert.AreEqual("1", harness.Store.Get("rm-setup", "global_n"));
            Assert.AreEqual("k", harness.Store.Get("rm-setup", "global_tag"));
            Assert.AreEqual("0", harness.Store.Get("rm-setup", "left"));
            Assert.AreEqual("false", harness.Store.Get("rm-setup", "has"));
            Assert.AreEqual("", harness.Store.Get("rm-setup", "all"));
        }

        [Test]
        public void Negative_RemoveTag_NeverHeld_ChangesNothingFiresNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("rm-neg");
            harness.Stack.Runtime.LoadMod(actor, "rm-neg-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceRemovedSignal('k'):Connect(function(inst)
                    store_set('removed_n', tostring((tonumber(store_get('removed_n')) or 0) + 1))
                end)
                cs.TagRemoved:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                end)
                store_set('removed_n', '0')
                store_set('global_n', '0')
                local part = Instance.new('Part')
                part.Name = 'Untagged'
                part.Parent = workspace
                local ok, err = pcall(function() return cs:RemoveTag(part, 'k') end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("Untagged");
            Assert.IsNotNull(part);
            Assert.IsTrue(harness.Registry.TryGetRecord(part.Id, out InstanceRecord before));
            long revisionBefore = before.Revision;

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("true", harness.Store.Get("rm-neg-setup", "ok"));
            Assert.AreEqual("0", harness.Store.Get("rm-neg-setup", "removed_n"));
            Assert.AreEqual("0", harness.Store.Get("rm-neg-setup", "global_n"));
            Assert.IsTrue(harness.Registry.TryGetRecord(part.Id, out InstanceRecord after));
            Assert.AreEqual(revisionBefore, after.Revision);
        }

        [Test]
        public void TaggedPart_TreeExit_FiresRemoved_TreeEntry_FiresAdded()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("tree-a");
            harness.Stack.Runtime.LoadMod(actor, "tree-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceAddedSignal('k'):Connect(function(inst)
                    store_set('added_n', tostring((tonumber(store_get('added_n')) or 0) + 1))
                end)
                cs:GetInstanceRemovedSignal('k'):Connect(function(inst)
                    store_set('removed_n', tostring((tonumber(store_get('removed_n')) or 0) + 1))
                    store_set('removed_name', inst.Name)
                end)
                store_set('added_n', '0')
                store_set('removed_n', '0')
                local part = Instance.new('Part')
                part.Name = 'Mover'
                part.Parent = workspace
                cs:AddTag(part, 'k')",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual("1", harness.Store.Get("tree-setup", "added_n"));

            RbxInstance mover = harness.Registry.WorldRoot.FindFirstChild("Mover");
            Assert.IsNotNull(mover);

            mover.Parent = null;
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(0, harness.Bindings.CollectionService.GetTagged("k").Count);
            Assert.AreEqual("1", harness.Store.Get("tree-setup", "removed_n"));
            Assert.AreEqual("Mover", harness.Store.Get("tree-setup", "removed_name"));

            mover.Parent = harness.Registry.WorldRoot;
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, harness.Bindings.CollectionService.GetTagged("k").Count);
            Assert.AreEqual("2", harness.Store.Get("tree-setup", "added_n"));
        }

        [Test]
        public void DestroyedInstance_NotReturnedByGetTagged()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("doom-a");
            harness.Stack.Runtime.LoadMod(actor, "doom-setup", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceRemovedSignal('k'):Connect(function(inst)
                    store_set('removed_n', tostring((tonumber(store_get('removed_n')) or 0) + 1))
                end)
                cs.TagRemoved:Connect(function(tag)
                    store_set('global_n', tostring((tonumber(store_get('global_n')) or 0) + 1))
                end)
                store_set('removed_n', '0')
                store_set('global_n', '0')
                local part = Instance.new('Part')
                part.Name = 'Doomed'
                part.Parent = workspace
                cs:AddTag(part, 'k')",
                persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("Doomed");
            Assert.IsNotNull(part);
            InstanceId id = part.Id;

            harness.Stack.Runtime.LoadMod(actor, "doom-fire",
                "workspace:FindFirstChild('Doomed'):Destroy()", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsFalse(harness.Registry.TryGet(id, out _));

            harness.Stack.Runtime.LoadMod(actor, "doom-check", @"
                store_set('left', tostring(#game:GetService('CollectionService'):GetTagged('k')))",
                persistToStore: false);

            Assert.AreEqual("0", harness.Store.Get("doom-check", "left"));
            Assert.AreEqual("1", harness.Store.Get("doom-setup", "removed_n"));
            Assert.AreEqual("1", harness.Store.Get("doom-setup", "global_n"));
        }

        [Test]
        public void Negative_PreExistingTag_DoesNotFireOnConnect()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("pre-a");
            harness.Stack.Runtime.LoadMod(actor, "pre-tag", @"
                local part = Instance.new('Part')
                part.Name = 'Early'
                part.Parent = workspace
                game:GetService('CollectionService'):AddTag(part, 'k')",
                persistToStore: false);

            // WHY: mirror pins pre-existing in-tree instances as never firing the per-tag
            // signal on connect — GetTagged is the way to discover them.
            harness.Stack.Runtime.LoadMod(actor, "pre-connect", @"
                local cs = game:GetService('CollectionService')
                cs:GetInstanceAddedSignal('k'):Connect(function(inst)
                    store_set('added_n', tostring((tonumber(store_get('added_n')) or 0) + 1))
                end)
                store_set('added_n', '0')
                store_set('n', tostring(#cs:GetTagged('k')))",
                persistToStore: false);

            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("0", harness.Store.Get("pre-connect", "added_n"));
            Assert.AreEqual("1", harness.Store.Get("pre-connect", "n"));
        }

        [Test]
        public void Negative_CrossActor_AddTag_RefusedAtCallTime()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("cross-a");
            harness.Stack.Runtime.LoadMod(actorA, "cross-setup", @"
                local part = Instance.new('Part')
                part.Name = 'OwnedByA'
                part.Parent = workspace", persistToStore: false);

            RbxInstance part = harness.Registry.WorldRoot.FindFirstChild("OwnedByA");
            Assert.IsNotNull(part);

            ActorContext actorB = harness.Actor("cross-b");
            harness.Stack.Runtime.LoadMod(actorB, "cross-attempt", @"
                local target = workspace:FindFirstChild('OwnedByA')
                local ok, err = pcall(function()
                    return game:GetService('CollectionService'):AddTag(target, 'x')
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("cross-attempt", "ok"));
            string error = harness.Store.Get("cross-attempt", "err");
            StringAssert.Contains("actor 'cross-b'", error);
            StringAssert.Contains("Owned by actor 'cross-a'", error);
            Assert.IsFalse(part.HasTag("x"));

            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual(0, harness.Registry.Tags.GetTagged("x").Count);
        }

        [Test]
        public void Negative_DeprecatedMembers_StayAbsent()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("depr-a");
            harness.Stack.Runtime.LoadMod(actor, "depr-setup", @"
                local cs = game:GetService('CollectionService')
                local r1, e1 = pcall(function() return cs.GetCollection end)
                local r2, e2 = pcall(function() return cs.ItemAdded end)
                local r3, e3 = pcall(function() return cs.ItemRemoved end)
                store_set('r1', tostring(r1)); store_set('e1', tostring(e1))
                store_set('r2', tostring(r2)); store_set('e2', tostring(e2))
                store_set('r3', tostring(r3)); store_set('e3', tostring(e3))",
                persistToStore: false);

            // WHY: GetCollection/ItemAdded/ItemRemoved are Deprecated in the mirror and stay
            // absent ("not", not even a loud stub) — member access raises unknown-member, so an
            // accidental delivery is caught here rather than surfacing silently in Lua.
            Assert.AreEqual("false", harness.Store.Get("depr-setup", "r1"));
            Assert.AreEqual("false", harness.Store.Get("depr-setup", "r2"));
            Assert.AreEqual("false", harness.Store.Get("depr-setup", "r3"));
            StringAssert.Contains("not a valid member",
                harness.Store.Get("depr-setup", "e1"));
            StringAssert.Contains("GetCollection", harness.Store.Get("depr-setup", "e1"));
            StringAssert.Contains("not a valid member",
                harness.Store.Get("depr-setup", "e2"));
            StringAssert.Contains("not a valid member",
                harness.Store.Get("depr-setup", "e3"));
            StringAssert.DoesNotContain("NOT_IMPLEMENTED",
                harness.Store.Get("depr-setup", "e1")
                + harness.Store.Get("depr-setup", "e2")
                + harness.Store.Get("depr-setup", "e3"));
        }

        [Test]
        public void InstanceSignals_SameObjectOnRepeatCalls()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxCollectionService service = harness.Bindings.CollectionService;
            Assert.IsNotNull(service);

            Assert.AreSame(
                service.GetInstanceAddedSignal("k"), service.GetInstanceAddedSignal("k"));
            Assert.AreSame(
                service.GetInstanceRemovedSignal("k"), service.GetInstanceRemovedSignal("k"));
            Assert.AreNotSame(
                service.GetInstanceAddedSignal("k"), service.GetInstanceAddedSignal("other"));
            Assert.AreNotSame(
                service.GetInstanceAddedSignal("k"), service.GetInstanceRemovedSignal("k"));
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
                    worldId: "tags-world");
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

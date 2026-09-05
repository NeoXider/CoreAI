using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>MVP2.5 slice 8.3 gate: Players/Player completion through production composition.</summary>
    [TestFixture]
    public sealed class Mvp8PlayersCompletionEditModeTests
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
        public void PlayersService_ResolvesToRbxPlayers()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("svc-actor");

            // WHY: before this slice the lookups/profile/Kick surface did not exist, so the gate
            // below is red on a "did nothing" build (unknown-member BAD_ARGUMENT, not nil).
            Assert.IsInstanceOf<RbxPlayers>(harness.Bindings.Game.GetService("Players"));
            Assert.IsInstanceOf<SyntheticPlayerProfileProvider>(
                harness.Bindings.Players.ProfileProvider);

            harness.Stack.Runtime.LoadMod(actor, "svc-resolve",
                "store_set('players_class', game:GetService('Players').ClassName)",
                persistToStore: false);

            Assert.AreEqual("Players", harness.Store.Get("svc-resolve", "players_class"));
        }

        [Test]
        public void GetPlayerByUserId_FindsConnectedPlayer()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("find-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            harness.Stack.Runtime.LoadMod(actor, "find-setup", @"
                local found = game:GetService('Players'):GetPlayerByUserId(" + player.UserId + @")
                store_set('found_name', found.Name)
                store_set('found_uid', tostring(found.UserId))
                store_set('same', tostring(found == game:GetService('Players'):GetPlayers()[1]))",
                persistToStore: false);

            Assert.AreEqual(player.Name, harness.Store.Get("find-setup", "found_name"));
            Assert.AreEqual(player.UserId.ToString(), harness.Store.Get("find-setup", "found_uid"));
            Assert.AreEqual("true", harness.Store.Get("find-setup", "same"));
            Assert.AreSame(player, harness.Bindings.Players.GetPlayerByUserId(player.UserId));
        }

        [Test]
        public void Negative_GetPlayerByUserId_UnknownId_ReturnsNil()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("unknown-a");
            harness.Bindings.ConnectActor(actor);

            // WHY: the mirror says an absent player reads back as nil (not an error), so the
            // pcall must SUCCEED here — a build that errors (or finds a ghost) fails this twin.
            harness.Stack.Runtime.LoadMod(actor, "unknown-setup", @"
                local ok, found = pcall(function()
                    return game:GetService('Players'):GetPlayerByUserId(424242)
                end)
                store_set('ok', tostring(ok))
                store_set('is_nil', tostring(found == nil))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("unknown-setup", "ok"));
            Assert.AreEqual("true", harness.Store.Get("unknown-setup", "is_nil"));
            Assert.IsNull(harness.Bindings.Players.GetPlayerByUserId(424242));
        }

        [Test]
        public void Negative_GetPlayerByUserId_DisconnectedPlayer_ReturnsNil()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("gone-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            long userId = player.UserId;
            Assert.IsNotNull(harness.Bindings.Players.GetPlayerByUserId(userId));

            harness.Stack.Runtime.LoadMod(actor, "gone-kick",
                "game:GetService('Players'):GetPlayers()[1]:Kick()", persistToStore: false);

            Assert.IsTrue(player.IsDestroyed);
            Assert.IsNull(harness.Bindings.Players.GetPlayerByUserId(userId));
            Assert.IsEmpty(harness.Bindings.Players.GetPlayers());

            harness.Stack.Runtime.LoadMod(actor, "gone-lookup", @"
                local found = game:GetService('Players'):GetPlayerByUserId(" + userId + @")
                store_set('is_nil', tostring(found == nil))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("gone-lookup", "is_nil"));
        }

        [Test]
        public void GetPlayerFromCharacter_RoundTripsThroughLua()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            harness.Stack.Runtime.LoadMod(actor, "char-setup", @"
                local Players = game:GetService('Players')
                local me = Players:GetPlayers()[1]
                local model = Instance.new('Model')
                model.Name = 'Avatar'
                model.Parent = workspace
                me.Character = model
                store_set('char_name', me.Character.Name)
                local back = Players:GetPlayerFromCharacter(model)
                store_set('back_uid', tostring(back.UserId))
                store_set('same', tostring(back == me))",
                persistToStore: false);

            Assert.AreEqual("Avatar", harness.Store.Get("char-setup", "char_name"));
            Assert.AreEqual(player.UserId.ToString(), harness.Store.Get("char-setup", "back_uid"));
            Assert.AreEqual("true", harness.Store.Get("char-setup", "same"));
            RbxInstance model = harness.Registry.WorldRoot.FindFirstChild("Avatar");
            Assert.IsNotNull(model);
            Assert.AreSame(player, harness.Bindings.Players.GetPlayerFromCharacter(model));
        }

        [Test]
        public void Negative_GetPlayerFromCharacter_NilAndNonCharacter_ReturnNil()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("charnil-a");
            harness.Bindings.ConnectActor(actor);

            // WHY: the mirror's equivalent loop matches nothing for nil or a non-character model,
            // so both read back as nil inside a SUCCEEDING pcall; a non-Instance argument is a
            // BAD_ARGUMENT instead (mirror types the parameter as Model).
            harness.Stack.Runtime.LoadMod(actor, "charnil-setup", @"
                local Players = game:GetService('Players')
                local ok_nil, r_nil = pcall(function()
                    return Players:GetPlayerFromCharacter(nil)
                end)
                local prop = Instance.new('Model')
                prop.Name = 'Prop'
                prop.Parent = workspace
                local ok_prop, r_prop = pcall(function()
                    return Players:GetPlayerFromCharacter(prop)
                end)
                local ok_num, e_num = pcall(function()
                    return Players:GetPlayerFromCharacter(5)
                end)
                store_set('ok_nil', tostring(ok_nil)); store_set('r_nil', tostring(r_nil == nil))
                store_set('ok_prop', tostring(ok_prop)); store_set('r_prop', tostring(r_prop == nil))
                store_set('ok_num', tostring(ok_num)); store_set('e_num', tostring(e_num))",
                persistToStore: false);

            Assert.AreEqual("true", harness.Store.Get("charnil-setup", "ok_nil"));
            Assert.AreEqual("true", harness.Store.Get("charnil-setup", "r_nil"));
            Assert.AreEqual("true", harness.Store.Get("charnil-setup", "ok_prop"));
            Assert.AreEqual("true", harness.Store.Get("charnil-setup", "r_prop"));
            Assert.AreEqual("false", harness.Store.Get("charnil-setup", "ok_num"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("charnil-setup", "e_num"));
            Assert.IsNull(harness.Bindings.Players.GetPlayerFromCharacter(null));
        }

        [Test]
        public void NameAndDisplayName_ComeFromSyntheticProfileByDefault()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("synth-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            Assert.AreEqual("Player" + player.UserId, player.Name);
            Assert.AreEqual(player.Name, player.DisplayName);

            harness.Stack.Runtime.LoadMod(actor, "synth-setup", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                store_set('name', me.Name)
                store_set('display', me.DisplayName)
                me.DisplayName = 'Custom Display'
                store_set('display_after', me.DisplayName)",
                persistToStore: false);

            Assert.AreEqual("Player" + player.UserId, harness.Store.Get("synth-setup", "name"));
            Assert.AreEqual("Player" + player.UserId, harness.Store.Get("synth-setup", "display"));
            Assert.AreEqual("Custom Display", harness.Store.Get("synth-setup", "display_after"));
            Assert.AreEqual("Custom Display", player.DisplayName);
        }

        [Test]
        public void ProfileProvider_Substitute_ChangesNamesThroughSameLuaCall()
        {
            using ProductionHarness harness = new ProductionHarness();

            // WHY: this is the host seam — the SAME Lua read (player.Name) answers from the
            // substituted provider, proving names are sourced from the port, not the default.
            harness.Bindings.Players.ProfileProvider = new FakeProfileProvider();
            ActorContext actor = harness.Actor("fake-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            Assert.AreEqual("FakeName" + player.UserId, player.Name);
            Assert.AreEqual("Fake Display " + player.UserId, player.DisplayName);

            harness.Stack.Runtime.LoadMod(actor, "fake-setup", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                store_set('name', me.Name)
                store_set('display', me.DisplayName)",
                persistToStore: false);

            Assert.AreEqual("FakeName" + player.UserId, harness.Store.Get("fake-setup", "name"));
            Assert.AreEqual(
                "Fake Display " + player.UserId, harness.Store.Get("fake-setup", "display"));
        }

        [Test]
        public void Kick_RemovesPlayerAndFiresPlayerRemovingWithCreatorKick()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("kick-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            InstanceId playerId = player.Id;
            long userId = player.UserId;

            int removingCount = 0;
            RbxPlayer removedPlayer = null;
            RbxEnumItem removalReason = null;
            harness.Bindings.Players.PlayerRemoving.Connect(
                (Action<object[]>)(arguments =>
                {
                    removingCount++;
                    removedPlayer = (RbxPlayer)arguments[0];
                    removalReason = (RbxEnumItem)arguments[1];
                }));

            harness.Stack.Runtime.LoadMod(actor, "kick-setup", @"
                local Players = game:GetService('Players')
                local me = Players:GetPlayers()[1]
                Players.PlayerRemoving:Connect(function(p, reason)
                    store_set('rm_uid', tostring(p.UserId))
                    store_set('rm_reason', reason.Name)
                    store_set('rm_is_creator', tostring(reason == Enum.PlayerExitReason.CreatorKick))
                end)
                me:Kick('farewell')",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            // WHY: mirror observables for Kick are the disconnect itself — the Player leaves the
            // tree — plus PlayerRemoving firing before removal with the CreatorKick reason.
            Assert.IsNull(player.Parent);
            Assert.IsTrue(player.IsDestroyed);
            Assert.IsFalse(harness.Registry.TryGet(playerId, out _));
            Assert.IsNull(harness.Bindings.Players.GetPlayerByUserId(userId));
            Assert.IsEmpty(harness.Bindings.Players.GetPlayers());
            Assert.AreEqual(1, removingCount);
            Assert.AreSame(player, removedPlayer);
            Assert.IsNotNull(removalReason);
            Assert.AreEqual("PlayerExitReason", removalReason.EnumType.Name);
            Assert.AreEqual("CreatorKick", removalReason.Name);
            Assert.AreEqual(userId.ToString(), harness.Store.Get("kick-setup", "rm_uid"),
                "mod log: " + string.Join(" || ", harness.LogLines));
            Assert.AreEqual("CreatorKick", harness.Store.Get("kick-setup", "rm_reason"));
            Assert.AreEqual("true", harness.Store.Get("kick-setup", "rm_is_creator"));
        }

        [Test]
        public void PlayerRemoving_HandlerReadsTheLeavingPlayersFields()
        {
            // WHY this is a gate and not a detail: the mirror describes PlayerRemoving as firing
            // "right before a Player leaves ... useful for storing player data using a
            // GlobalDataStore", and the DataStore key IS player.UserId. CoreAI defers signal
            // callbacks, so by the time the handler runs the Player is already destroyed and only
            // the destruction tombstone keeps it readable. That tombstone used to cover exactly
            // three members (Name/ClassName/Parent), so the canonical save-on-leave handler died on
            // its first line — and because a faulting callback is reported to the mod's error
            // stream rather than thrown, the failure looked like "the handler wrote nothing".
            using ProductionHarness harness = new ProductionHarness();
            ActorContext observer = harness.Actor("leave-obs");
            ActorContext leaver = harness.Actor("leave-a");
            harness.Bindings.ConnectActor(observer);
            RbxPlayer leaving = harness.Bindings.ConnectActor(leaver);

            harness.Stack.Runtime.LoadMod(observer, "leave-mod", @"
                local Players = game:GetService('Players')
                Players.PlayerRemoving:Connect(function(p, reason)
                    store_set('uid', tostring(p.UserId))
                    store_set('name', p.Name)
                    store_set('display', p.DisplayName)
                    store_set('class', p.ClassName)
                    store_set('reason', reason.Name)
                end)", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            harness.Bindings.Players.KickPlayer(leaving,
                harness.Bindings.Enums.Get("PlayerExitReason")["CreatorKick"]);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(leaving.UserId.ToString(), harness.Store.Get("leave-mod", "uid"),
                "the DataStore key must be readable in the handler; log: "
                + string.Join(" || ", harness.LogLines));
            Assert.AreEqual(leaving.Name, harness.Store.Get("leave-mod", "name"));
            Assert.AreEqual(leaving.DisplayName, harness.Store.Get("leave-mod", "display"));
            Assert.AreEqual("Player", harness.Store.Get("leave-mod", "class"));
            Assert.AreEqual("CreatorKick", harness.Store.Get("leave-mod", "reason"));
        }

        [Test]
        public void Negative_PlayerRemoving_HandlerStillCannotWriteOrCallOnTheLeavingPlayer()
        {
            // The twin of the read widening: the tombstone is a READ exception for the instance the
            // handler was handed, not a resurrection. A write, a method call, and a read of some
            // OTHER destroyed instance all stay refused, or "destroyed" would stop meaning anything.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext observer = harness.Actor("leaveneg-obs");
            ActorContext leaver = harness.Actor("leaveneg-a");
            harness.Bindings.ConnectActor(observer);
            RbxPlayer leaving = harness.Bindings.ConnectActor(leaver);

            harness.Stack.Runtime.LoadMod(observer, "leaveneg-mod", @"
                local Players = game:GetService('Players')
                local other = Instance.new('Part')
                other.Parent = workspace
                other:Destroy()
                Players.PlayerRemoving:Connect(function(p, reason)
                    local okWrite, errWrite = pcall(function() p.Name = 'renamed' end)
                    store_set('write', tostring(okWrite) .. '|' .. tostring(errWrite))
                    local okCall, errCall = pcall(function() return p:GetChildren() end)
                    store_set('call', tostring(okCall) .. '|' .. tostring(errCall))
                    local okOther, errOther = pcall(function() return other.Transparency end)
                    store_set('other', tostring(okOther) .. '|' .. tostring(errOther))
                end)", persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            harness.Bindings.Players.KickPlayer(leaving,
                harness.Bindings.Enums.Get("PlayerExitReason")["CreatorKick"]);
            harness.Bindings.Scheduler.Advance(0d);

            string write = harness.Store.Get("leaveneg-mod", "write");
            StringAssert.StartsWith("false|", write, "a write to a destroyed player must be refused");
            StringAssert.Contains("INSTANCE_DESTROYED", write);
            string call = harness.Store.Get("leaveneg-mod", "call");
            StringAssert.StartsWith("false|", call, "a method call on a destroyed player must be refused");
            StringAssert.Contains("INSTANCE_DESTROYED", call);
            string other = harness.Store.Get("leaveneg-mod", "other");
            StringAssert.StartsWith("false|", other,
                "the tombstone covers the handler's own argument, not every destroyed instance");
            StringAssert.Contains("INSTANCE_DESTROYED", other);
        }

        [Test]
        public void Negative_Kick_AlreadyRemoved_ChangesNothingFiresNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("kickneg-a");
            ActorContext actorB = harness.Actor("kickneg-b");
            RbxPlayer playerA = harness.Bindings.ConnectActor(actorA);
            RbxPlayer playerB = harness.Bindings.ConnectActor(actorB);

            int removingCount = 0;
            RbxEnumItem kickReason = null;
            harness.Bindings.Players.PlayerRemoving.Connect(
                (Action<object[]>)(arguments =>
                {
                    removingCount++;
                    kickReason = (RbxEnumItem)arguments[1];
                }));

            // WHY: the first kick goes through Lua (self-kick resolves CreatorKick internally);
            // the captured reason item then drives the C# seam for the already-removed twins.
            harness.Stack.Runtime.LoadMod(actorA, "kickneg-first",
                "game:GetService('Players'):GetPlayerByUserId(" + playerA.UserId + "):Kick()",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual(1, removingCount);
            Assert.IsNotNull(kickReason);
            Assert.AreEqual("CreatorKick", kickReason.Name);
            int mutationsAfterFirstKick = harness.Registry.RetainedMutationOperationCount;

            // WHY: kicking an already-removed (or never-connected) player is a silent no-op —
            // false, no signal, no tree or ledger change — so a second kick can never double-fire.
            Assert.IsFalse(harness.Bindings.Players.KickPlayer(playerA, kickReason));
            Assert.IsFalse(harness.Bindings.Players.KickPlayer(null, kickReason));
            Assert.AreEqual(1, removingCount);
            Assert.AreEqual(
                mutationsAfterFirstKick, harness.Registry.RetainedMutationOperationCount);
            Assert.AreSame(playerB, harness.Bindings.Players.GetPlayerByUserId(playerB.UserId));
            Assert.AreEqual(1, harness.Bindings.Players.GetPlayers().Count);
        }

        [Test]
        public void Negative_Kick_CrossActor_Refused()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("kickcross-a");
            ActorContext actorB = harness.Actor("kickcross-b");
            RbxPlayer playerA = harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);

            // WHY: Kick authorizes like Destroy of the player's subtree — a plain actor kicks its
            // own player, never another actor's (the host grant kicks anyone).
            harness.Stack.Runtime.LoadMod(actorB, "kickcross-attempt", @"
                local other = game:GetService('Players'):GetPlayerByUserId(" + playerA.UserId + @")
                local ok, err = pcall(function() return other:Kick() end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))",
                persistToStore: false);

            Assert.AreEqual("false", harness.Store.Get("kickcross-attempt", "ok"));
            StringAssert.Contains("kickcross-b", harness.Store.Get("kickcross-attempt", "err"));
            StringAssert.Contains("cannot kick", harness.Store.Get("kickcross-attempt", "err"));
            Assert.AreSame(playerA, harness.Bindings.Players.GetPlayerByUserId(playerA.UserId));
            Assert.IsFalse(playerA.IsDestroyed);
        }

        [Test]
        public void PerPlayerContainers_ExistAndEmptyOnJoin()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("cont-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            harness.Stack.Runtime.LoadMod(actor, "cont-setup", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                store_set('backpack_class', me.Backpack.ClassName)
                store_set('playergui_class', me.PlayerGui.ClassName)
                store_set('playerscripts_class', me.PlayerScripts.ClassName)
                store_set('backpack_n', tostring(#me.Backpack:GetChildren()))
                store_set('playergui_n', tostring(#me.PlayerGui:GetChildren()))
                store_set('playerscripts_n', tostring(#me.PlayerScripts:GetChildren()))
                store_set('backpack_parent', tostring(me.Backpack.Parent == me))
                store_set('find', tostring(me:FindFirstChild('Backpack') == me.Backpack))",
                persistToStore: false);

            Assert.AreEqual("Backpack", harness.Store.Get("cont-setup", "backpack_class"));
            Assert.AreEqual("PlayerGui", harness.Store.Get("cont-setup", "playergui_class"));
            Assert.AreEqual("PlayerScripts", harness.Store.Get("cont-setup", "playerscripts_class"));
            Assert.AreEqual("0", harness.Store.Get("cont-setup", "backpack_n"));
            Assert.AreEqual("0", harness.Store.Get("cont-setup", "playergui_n"));
            Assert.AreEqual("0", harness.Store.Get("cont-setup", "playerscripts_n"));
            Assert.AreEqual("true", harness.Store.Get("cont-setup", "backpack_parent"));
            Assert.AreEqual("true", harness.Store.Get("cont-setup", "find"));
            Assert.AreSame(harness.Bindings.Players, player.Parent);
            Assert.AreSame(
                player,
                harness.Bindings.Game
                    .FindFirstChildOfClass("Players")
                    .FindFirstChild(player.Name));
        }

        [Test]
        public void Negative_UnshippedMembers_StayLoudStubs()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("stub-a");
            harness.Bindings.ConnectActor(actor);

            harness.Stack.Runtime.LoadMod(actor, "stub-setup", @"
                local Players = game:GetService('Players')
                local me = Players:GetPlayers()[1]
                local probes = {
                    Players_BanAsync = function() return Players.BanAsync end,
                    Players_GetFriendsAsync = function() return Players.GetFriendsAsync end,
                    Players_Chat = function() return Players.Chat end,
                    Players_PlayerMembershipChanged = function() return Players.PlayerMembershipChanged end,
                    Player_Team = function() return me.Team end,
                    Player_GetMouse = function() return me.GetMouse end,
                    Player_LoadCharacterAsync = function() return me.LoadCharacterAsync end,
                    Player_DistanceFromCharacter = function() return me.DistanceFromCharacter end,
                    Player_CharacterAdded = function() return me.CharacterAdded end,
                    Player_Chatted = function() return me.Chatted end,
                    Player_StarterGear = function() return me.StarterGear end,
                }
                for key, probe in pairs(probes) do
                    local ok, err = pcall(probe)
                    store_set(key .. '_ok', tostring(ok))
                    store_set(key .. '_err', tostring(err))
                end
                local d1, e1 = pcall(function() return Players.getPlayers end)
                local d2, e2 = pcall(function() return Players.playerFromCharacter end)
                local d3, e3 = pcall(function() return Players.localPlayer end)
                store_set('d1', tostring(d1)); store_set('e1', tostring(e1))
                store_set('d2', tostring(d2)); store_set('e2', tostring(e2))
                store_set('d3', tostring(d3)); store_set('e3', tostring(e3))",
                persistToStore: false);

            // WHY: unshipped mirror members stay loud NOT_IMPLEMENTED stubs — an accidental
            // delivery (a "did something extra" build) flips these from false to true and fails
            // here. Deprecated aliases stay fully absent ("not", never a stub).
            string[] stubKeys =
            {
                "Players_BanAsync", "Players_GetFriendsAsync", "Players_Chat",
                "Players_PlayerMembershipChanged", "Player_Team", "Player_GetMouse",
                "Player_LoadCharacterAsync", "Player_DistanceFromCharacter",
                "Player_CharacterAdded", "Player_Chatted", "Player_StarterGear",
            };
            foreach (string key in stubKeys)
            {
                Assert.AreEqual("false", harness.Store.Get("stub-setup", key + "_ok"), key);
                StringAssert.Contains(
                    "NOT_IMPLEMENTED", harness.Store.Get("stub-setup", key + "_err"), key);
            }

            Assert.AreEqual("false", harness.Store.Get("stub-setup", "d1"));
            Assert.AreEqual("false", harness.Store.Get("stub-setup", "d2"));
            Assert.AreEqual("false", harness.Store.Get("stub-setup", "d3"));
            StringAssert.Contains("not a valid member", harness.Store.Get("stub-setup", "e1"));
            StringAssert.Contains("not a valid member", harness.Store.Get("stub-setup", "e2"));
            StringAssert.Contains("not a valid member", harness.Store.Get("stub-setup", "e3"));
            StringAssert.DoesNotContain("NOT_IMPLEMENTED",
                harness.Store.Get("stub-setup", "e1")
                + harness.Store.Get("stub-setup", "e2")
                + harness.Store.Get("stub-setup", "e3"));
        }

        private sealed class FakeProfileProvider : IRbxPlayerProfileProvider
        {
            public bool TryGetProfile(long userId, out string username, out string displayName)
            {
                username = "FakeName" + userId;
                displayName = "Fake Display " + userId;
                return true;
            }
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
                    worldId: "players-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game, log: LogLines.Add);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new CapturingGameLogger(LogLines),
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

        /// <summary>
        /// Quiet for debug/info, but warnings and errors are recorded.
        /// </summary>
        /// <remarks>
        /// WHY not silent: a Lua fault inside a deferred signal callback is reported through this
        /// logger, not thrown. A fully silent logger turned "the callback errored" into "the
        /// callback wrote nothing", which is the shape a store assertion reports as an empty
        /// string — a diagnosis this file lost an entire run to.
        /// </remarks>
        private sealed class CapturingGameLogger : IGameLogger
        {
            private readonly List<string> _sink;

            public CapturingGameLogger(List<string> sink)
            {
                _sink = sink;
            }

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
                _sink.Add("[warn] " + message);
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
                _sink.Add("[error] " + message);
            }
        }
    }
}

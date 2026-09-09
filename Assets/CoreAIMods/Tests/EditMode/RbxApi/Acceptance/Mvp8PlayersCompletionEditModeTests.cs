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
        public void CharacterSpawn_IsDeferredOneSchedulerAdvance_NotSynchronousWithConnect()
        {
            // WHY this is the F7 gate: EnsureActor used to spawn the character inline, so it was
            // already in Workspace before a script had any chance to see the join happen, let alone
            // flip CharacterAutoLoads in time. Deferring to the scheduler's next drain is what makes
            // the race below winnable.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("defer-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            Assert.IsNull(player.Character,
                "the auto-spawn must not land synchronously inside EnsureActor/ConnectActor.");

            harness.Bindings.Scheduler.Advance(1d / 60d);

            Assert.IsNotNull(player.Character,
                "CharacterAutoLoads is true by default; the deferred spawn must still land on the " +
                "next scheduler drain.");
        }

        [Test]
        public void Negative_CharacterAutoLoads_SetFalseBeforeTheDeferredSpawnLands_SkipsIt()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("defer-b");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            // WHY this is the exact F7 race: a script that flips CharacterAutoLoads off runs
            // synchronously after the actor is already connected (mod-context creation and network
            // dispatch both connect actors before any mod chunk has a chance to run), and must still
            // win against the deferred auto-spawn queued by that connect.
            harness.Bindings.Players.CharacterAutoLoads = false;
            harness.Bindings.Scheduler.Advance(1d / 60d);

            Assert.IsNull(player.Character,
                "flipping CharacterAutoLoads off before the deferred spawn's scheduler slot must " +
                "cancel it — the flag is read at fire time, not at connect time.");
        }

        [Test]
        public void JoiningActor_GetsACharacterWithAHumanoidAndARootPart()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);

            harness.Stack.Runtime.LoadMod(actor, "char-shape", @"
                local Players = game:GetService('Players')
                local me = Players:GetPlayers()[1]
                local character = me.Character
                store_set('has_character', tostring(character ~= nil))
                store_set('name_matches', tostring(character.Name == me.Name))
                store_set('parent_is_workspace', tostring(character.Parent == workspace))
                local humanoid = character:FindFirstChild('Humanoid')
                local root = character:FindFirstChild('HumanoidRootPart')
                store_set('has_humanoid', tostring(humanoid ~= nil))
                store_set('root_is_part', tostring(root ~= nil and root:IsA('BasePart')))
                store_set('root_part_wired', tostring(humanoid.RootPart == root))
                store_set('resolves_back',
                    tostring(Players:GetPlayerFromCharacter(character) == me))");

            Assert.AreEqual("true", harness.Store.Get("char-shape", "has_character"));
            Assert.AreEqual("true", harness.Store.Get("char-shape", "name_matches"));
            Assert.AreEqual("true", harness.Store.Get("char-shape", "parent_is_workspace"));
            Assert.AreEqual("true", harness.Store.Get("char-shape", "has_humanoid"));
            Assert.AreEqual("true", harness.Store.Get("char-shape", "root_is_part"),
                "Humanoid.RootPart must be a BasePart named HumanoidRootPart, not the Model.");
            Assert.AreEqual("true", harness.Store.Get("char-shape", "root_part_wired"));
            Assert.AreEqual("true", harness.Store.Get("char-shape", "resolves_back"));
            Assert.IsNotNull(player.Character);
        }

        [Test]
        public void LoadCharacterAsync_ReplacesTheOldOneInTheMirrorsOrderAndYields()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-b");
            harness.Bindings.ConnectActor(actor);
            // WHY advanced once here: the join's own auto-spawn is now deferred (F7), and this test
            // needs that FIRST character to already exist so LoadCharacterAsync's replacement can be
            // observed (CharacterRemoving for it, then CharacterAdded for the new one).
            harness.Bindings.Scheduler.Advance(1d / 60d);

            // WHY an accumulated string: counters would prove each event happened, which is true in
            // any order. The ORDER is the contract - CharacterRemoving for the outgoing character
            // BEFORE CharacterAdded for its replacement - and the "after" tag proves the call
            // yielded, because a non-yielding LoadCharacterAsync would return before the deferred
            // handlers ran and put "after" first.
            harness.Stack.Runtime.LoadMod(actor, "char-order", @"
                local Players = game:GetService('Players')
                local me = Players:GetPlayers()[1]
                local first = me.Character
                local order = ''
                local function note(tag)
                    return function()
                        order = order .. tag
                        store_set('order', order)
                    end
                end
                me.CharacterRemoving:Connect(note('R'))
                me.CharacterAdded:Connect(note('A'))
                task.spawn(function()
                    local second = me:LoadCharacterAsync()
                    order = order .. 'after'
                    store_set('order', order)
                    store_set('replaced', tostring(second ~= first))
                    store_set('is_current', tostring(me.Character == second))
                end)");

            for (int frame = 0; frame < 4; frame++)
            {
                harness.Bindings.PumpFrame(1f / 60f);
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.AreEqual("RAafter", harness.Store.Get("char-order", "order"),
                "CharacterRemoving fires for the outgoing character, then CharacterAdded for the "
                + "new one, and only then does the yielding call resume.");
            Assert.AreEqual("true", harness.Store.Get("char-order", "replaced"));
            Assert.AreEqual("true", harness.Store.Get("char-order", "is_current"));
        }

        [Test]
        public void Negative_CharacterAutoLoadsOff_LeavesTheJoinerWithoutOne()
        {
            using ProductionHarness harness = new ProductionHarness();
            harness.Bindings.Players.CharacterAutoLoads = false;
            ActorContext actor = harness.Actor("char-c");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            Assert.IsNull(player.Character,
                "CharacterAutoLoads = false means nothing spawns until LoadCharacterAsync is called.");

            harness.Stack.Runtime.LoadMod(actor, "char-manual", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                store_set('before', tostring(me.Character == nil))
                task.spawn(function()
                    me:LoadCharacterAsync()
                    store_set('after', tostring(me.Character ~= nil))
                end)");

            for (int frame = 0; frame < 4; frame++)
            {
                harness.Bindings.PumpFrame(1f / 60f);
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.AreEqual("true", harness.Store.Get("char-manual", "before"));
            Assert.AreEqual("true", harness.Store.Get("char-manual", "after"));
        }

        [Test]
        public void DistanceFromCharacter_IsInStuds_AndZeroWithoutACharacter()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-d");
            harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);

            harness.Stack.Runtime.LoadMod(actor, "char-distance", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                me.Character.HumanoidRootPart.Position = Vector3.new(0, 0, 0)
                store_set('three', tostring(me:DistanceFromCharacter(Vector3.new(3, 0, 0))))
                store_set('five', tostring(me:DistanceFromCharacter(Vector3.new(0, 4, 3))))");

            Assert.AreEqual("3", harness.Store.Get("char-distance", "three"),
                "The distance is in studs, the same unit the position was written in.");
            Assert.AreEqual("5", harness.Store.Get("char-distance", "five"));

            using ProductionHarness bare = new ProductionHarness();
            bare.Bindings.Players.CharacterAutoLoads = false;
            ActorContext bareActor = bare.Actor("char-e");
            bare.Bindings.ConnectActor(bareActor);
            bare.Stack.Runtime.LoadMod(bareActor, "char-none", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                store_set('zero', tostring(me:DistanceFromCharacter(Vector3.new(9, 9, 9))))");

            Assert.AreEqual("0", bare.Store.Get("char-none", "zero"),
                "The mirror returns 0 for a player with no character, not an error.");
        }

        [Test]
        public void LoadCharacter_LogsItsDeprecationOncePerMod_NotOncePerProcess()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-f");
            harness.Bindings.ConnectActor(actor);

            const string source = @"
                local me = game:GetService('Players'):GetPlayers()[1]
                task.spawn(function()
                    me:LoadCharacter()
                    me:LoadCharacter()
                end)";
            harness.Stack.Runtime.LoadMod(actor, "deprecated-one", source);
            harness.Stack.Runtime.LoadMod(actor, "deprecated-two", source);

            for (int frame = 0; frame < 8; frame++)
            {
                harness.Bindings.PumpFrame(1f / 60f);
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            // WHY two mods and four calls: one mod calling twice cannot tell once-per-mod from
            // once-per-process, and a static flag would pass that weaker test. Two mods must
            // produce exactly two notes.
            int notes = 0;
            foreach (string line in harness.LogLines)
            {
                if (line.Contains("LoadCharacter() is deprecated"))
                {
                    notes++;
                }
            }

            Assert.AreEqual(2, notes,
                "One deprecation note per mod: two mods, four calls, two notes.");
        }

        [Test]
        public void Negative_DisconnectTakesTheCharacterWithIt()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("char-g");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance character = player.Character;
            Assert.IsNotNull(character);

            harness.Bindings.DisconnectActor(actor);

            Assert.IsTrue(character.IsDestroyed,
                "A character left standing after its player disconnects is a body nobody drives.");
            Assert.IsFalse(harness.Registry.TryGet(character.Id, out _));
        }

        [Test]
        public void CharacterMotor_AttachesAfterParenting_AndRunsOnlyAtTheFixedStepPump()
        {
            // WHY "only": F10 — the motor must NOT move on PumpPreSimulation (the render-frame
            // pump); it must move only through StepCharacterMotors, which the host calls from
            // FixedUpdate. Velocity-driven walking applied a variable number of times per simulated
            // step would move a character at a rate that depends on frame rate.
            GameObject body = new GameObject("Character motor test");
            try
            {
                Rigidbody rigidbody = body.AddComponent<Rigidbody>();
                rigidbody.useGravity = false;
                using ProductionHarness harness = new ProductionHarness();
                harness.Bindings.AttachCharacterMotorFactory(humanoid =>
                    humanoid.RootPart == null ? null : new UnityRbxCharacterMotor(rigidbody));
                RbxPlayer player = harness.Bindings.ConnectActor(harness.Actor("motor"));
                harness.Bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");
                Assert.AreSame(player.Character.FindFirstChild("HumanoidRootPart"), humanoid.RootPart);

                humanoid.MoveTo(new RbxVector3(20f, 0f, 0f));
                harness.Bindings.PumpPreSimulation(1f / 60f);
                Assert.AreEqual(0f, rigidbody.linearVelocity.magnitude,
                    "the character motor must not step on the render frame (PumpPreSimulation).");

                harness.Bindings.StepCharacterMotors(1f / 60f);
                Assert.Greater(rigidbody.linearVelocity.magnitude, 0f,
                    "StepCharacterMotors is the fixed-step pump; it must actually move the character.");

                player.Character.Destroy();
                Assert.DoesNotThrow(() => harness.Bindings.PumpPreSimulation(1f / 60f));
                Assert.DoesNotThrow(() => harness.Bindings.StepCharacterMotors(1f / 60f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(body);
            }
        }

        [Test]
        public void CharacterMotor_StopsWhenTheHumanoidDiesMidMove()
        {
            // WHY: Humanoid.SetHealth clears the Humanoid's OWN walk target on death, but the
            // motor holds a separate target of its own that nothing else ever cleared — kill a
            // character mid-MoveTo and the corpse kept walking to the destination, forever when
            // CharacterAutoLoads is off. StepCharacterMotors must notice the death and stop
            // driving that motor from then on.
            GameObject body = new GameObject("Dead character motor test");
            try
            {
                Rigidbody rigidbody = body.AddComponent<Rigidbody>();
                rigidbody.useGravity = false;
                using ProductionHarness harness = new ProductionHarness();
                harness.Bindings.AttachCharacterMotorFactory(humanoid =>
                    humanoid.RootPart == null ? null : new UnityRbxCharacterMotor(rigidbody));
                RbxPlayer player = harness.Bindings.ConnectActor(harness.Actor("dead-motor"));
                harness.Bindings.Scheduler.Advance(1d / 60d);
                RbxHumanoid humanoid = (RbxHumanoid)player.Character.FindFirstChild("Humanoid");

                humanoid.MoveTo(new RbxVector3(20f, 0f, 0f));
                harness.Bindings.StepCharacterMotors(1f / 60f);
                Assert.Greater(rigidbody.linearVelocity.magnitude, 0f,
                    "sanity: the walk must actually be under way before the kill");

                humanoid.Health = 0d;
                Assert.IsTrue(humanoid.IsDead);

                // Several more fixed steps — the old bug kept re-chasing the original destination
                // on every one of these, since nothing had cleared the motor's own target.
                for (int step = 0; step < 5; step++)
                {
                    harness.Bindings.StepCharacterMotors(1f / 60f);
                }

                Assert.AreEqual(0f, rigidbody.linearVelocity.x, 1e-6f,
                    "a dead character's motor must stop walking toward its old destination");
                Assert.AreEqual(0f, rigidbody.linearVelocity.z, 1e-6f,
                    "a dead character's motor must stop walking toward its old destination");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(body);
            }
        }

        [Test]
        public void CharacterRemoving_DeferredCallbackCanReadOutgoingName()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("removing-name");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            string name = player.Character.Name;
            harness.Stack.Runtime.LoadMod(actor, "removing-name", @"
                local me = game:GetService('Players'):GetPlayers()[1]
                me.CharacterRemoving:Connect(function(character)
                    store_set('removed_name', character.Name)
                end)
                task.spawn(function() me:LoadCharacterAsync() end)");
            for (int frame = 0; frame < 4; frame++)
            {
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }
            Assert.AreEqual(name, harness.Store.Get("removing-name", "removed_name"));
        }

        [Test]
        public void HumanoidDied_RespawnsAfterRespawnTime_WhenCharacterAutoLoadsStaysTrue()
        {
            // WHY this is the F8 gate: RespawnTime used to have no consumer anywhere in Runtime —
            // a Humanoid could die and nothing ever reloaded the character, no matter what the flag
            // said.
            using ProductionHarness harness = new ProductionHarness();
            harness.Bindings.Players.RespawnTime = 0.1d;
            ActorContext actor = harness.Actor("respawn-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance firstCharacter = player.Character;
            Assert.IsNotNull(firstCharacter);
            RbxHumanoid humanoid = (RbxHumanoid)firstCharacter.FindFirstChild("Humanoid");

            humanoid.TakeDamage(humanoid.MaxHealth + 1d);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            Assert.IsTrue(humanoid.IsDead);

            // WHY still the same (dead) character here: RespawnTime has not elapsed yet — the
            // respawn must wait the full delay, not fire immediately on Died.
            harness.Bindings.Scheduler.Advance(1d / 60d);
            Assert.AreSame(firstCharacter, player.Character);

            for (int frame = 0; frame < 10 && ReferenceEquals(player.Character, firstCharacter);
                 frame++)
            {
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.AreNotSame(firstCharacter, player.Character,
                "RespawnTime elapsed with CharacterAutoLoads still true; a fresh character must load.");
            Assert.IsTrue(firstCharacter.IsDestroyed);
        }

        [Test]
        public void Negative_HumanoidDied_CharacterAutoLoadsFalse_NeverRespawns()
        {
            using ProductionHarness harness = new ProductionHarness();
            harness.Bindings.Players.RespawnTime = 0.05d;
            ActorContext actor = harness.Actor("respawn-b");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance firstCharacter = player.Character;
            RbxHumanoid humanoid = (RbxHumanoid)firstCharacter.FindFirstChild("Humanoid");

            humanoid.TakeDamage(humanoid.MaxHealth + 1d);
            // WHY advanced once, THEN flipped: this drains the Died signal and schedules the
            // respawn timer first, so setting the flag false afterward — before the timer elapses —
            // exercises the re-check at fire time, not a check that merely runs before scheduling.
            harness.Bindings.Scheduler.Advance(1d / 60d);
            harness.Bindings.Players.CharacterAutoLoads = false;

            for (int frame = 0; frame < 20; frame++)
            {
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.AreSame(firstCharacter, player.Character,
                "CharacterAutoLoads=false must cancel the respawn even though it was already queued.");
            Assert.IsFalse(firstCharacter.IsDestroyed);
        }

        [Test]
        public void Negative_HumanoidDiedRespawn_FailureCostsOnlyThatPlayerAndIsReportedNotThrown()
        {
            // WHY this is the death-respawn twin of the join-time gate above: the death-triggered
            // respawn runs from the very same scheduler host-callback slot as the deferred join
            // spawn — outside any mod's dispatch try/catch. Before this fix, only the join spawn
            // was guarded; a failure inside RbxCharacterFactory.Load from a dead Humanoid's
            // respawn timer propagated out of Advance and killed the whole scheduler frame for
            // every mod, not just the player who died.
            using ProductionHarness harness = new ProductionHarness();
            List<string> diagnostics = new();
            harness.Registry.Diagnostics = diagnostics.Add;
            harness.Bindings.Players.RespawnTime = 0.05d;

            string failingName = null;
            int failingPlayerSpawnAttempts = 0;
            harness.Bindings.Players.RootPartSpawnSeeder = (rootPart, size, position) =>
            {
                if (rootPart.Parent == null || failingName == null
                    || !string.Equals(rootPart.Parent.Name, failingName, StringComparison.Ordinal))
                {
                    return;
                }

                // WHY a counted second attempt, not every attempt: the first attempt is this
                // player's ordinary join-time spawn, which must succeed so there is a live,
                // killable Humanoid. Only the second attempt — the death-triggered respawn — is
                // the one under test here.
                failingPlayerSpawnAttempts++;
                if (failingPlayerSpawnAttempts == 2)
                {
                    throw new InvalidOperationException("simulated respawn failure");
                }
            };

            RbxPlayer failingPlayer = harness.Bindings.ConnectActor(harness.Actor("respawn-fail-a"));
            failingName = failingPlayer.Name;
            RbxPlayer healthyPlayer = harness.Bindings.ConnectActor(harness.Actor("respawn-fail-b"));
            harness.Bindings.Scheduler.Advance(1d / 60d);

            RbxInstance failingFirstCharacter = failingPlayer.Character;
            RbxInstance healthyFirstCharacter = healthyPlayer.Character;
            Assert.IsNotNull(failingFirstCharacter);
            Assert.IsNotNull(healthyFirstCharacter);
            RbxHumanoid failingHumanoid =
                (RbxHumanoid)failingFirstCharacter.FindFirstChild("Humanoid");
            RbxHumanoid healthyHumanoid =
                (RbxHumanoid)healthyFirstCharacter.FindFirstChild("Humanoid");

            failingHumanoid.TakeDamage(failingHumanoid.MaxHealth + 1d);
            healthyHumanoid.TakeDamage(healthyHumanoid.MaxHealth + 1d);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            Assert.IsTrue(failingHumanoid.IsDead);
            Assert.IsTrue(healthyHumanoid.IsDead);

            for (int frame = 0; frame < 20; frame++)
            {
                Assert.DoesNotThrow(() => harness.Bindings.Scheduler.Advance(1d / 60d),
                    "a failed death-triggered respawn must not kill the scheduler frame for every " +
                    "mod.");
            }

            Assert.AreSame(failingFirstCharacter, failingPlayer.Character,
                "a failed death-triggered respawn must cost only this player its respawn.");
            Assert.AreNotSame(healthyFirstCharacter, healthyPlayer.Character,
                "one player's failed respawn must not prevent another player's respawn in the " +
                "same frame.");
            Assert.IsTrue(
                diagnostics.Exists(line => line.Contains("simulated respawn failure")),
                "the failure must be reported through the registry's diagnostics; log: "
                + string.Join(" || ", diagnostics));
        }

        [Test]
        public void DisconnectActor_DisconnectsCharacterSignals()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("signal-cleanup");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            RbxScriptConnection added = player.CharacterAdded.Connect((Action<object[]>)(_ => { }));
            RbxScriptConnection removing = player.CharacterRemoving.Connect((Action<object[]>)(_ => { }));
            harness.Bindings.DisconnectActor(actor);
            Assert.IsFalse(added.Connected);
            Assert.IsFalse(removing.Connected);
        }

        [Test]
        public void JoiningActor_RootPartSpawnSeederReceivesASaneSizeAndHeightAboveOrigin()
        {
            // WHY this is the F2b gate: an unseeded root part materializes from the part-property
            // sink's plain Roblox default — a 4x1x2 box, unanchored, collidable, at the origin —
            // so every join used to drop a falling box embedded in whatever sits at (0,0,0).
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance seededRootPart = null;
            RbxVector3 seededSize = default;
            RbxVector3 seededPosition = default;
            harness.Bindings.Players.RootPartSpawnSeeder = (rootPart, size, position) =>
            {
                seededRootPart = rootPart;
                seededSize = size;
                seededPosition = position;
            };

            ActorContext actor = harness.Actor("spawn-shape-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);

            Assert.IsNotNull(seededRootPart,
                "the deferred join-spawn must seed the root part's spawn state.");
            Assert.AreSame(
                player.Character.FindFirstChild(RbxCharacterFactory.RootPartName), seededRootPart);
            Assert.AreEqual(RbxCharacterFactory.RootPartSize, seededSize);
            Assert.AreEqual(RbxCharacterFactory.DefaultSpawnPosition, seededPosition);
            Assert.AreNotEqual(new RbxVector3(4f, 1f, 2f), seededSize,
                "the root part must not keep the generic Part default size (4x1x2).");
            Assert.Greater(seededPosition.Y, 0f,
                "the spawn position must be above the world origin, not embedded in whatever " +
                "sits there.");
        }

        [Test]
        public void JoiningActor_ProductionWiring_PushesTheSpawnTransformIntoThePartSink()
        {
            // WHY this exists next to the seeder test above: that one installs its own seeder and so
            // passes even when nothing wires one in production, which is exactly the state this
            // pipeline was in — the spawn shape was fixed only for tests and every real join still
            // materialized the sink's plain 4x1x2 default at the origin.
            using ProductionHarness harness = new ProductionHarness();
            Assert.IsNotNull(harness.Bindings.Players.RootPartSpawnSeeder,
                "the bindings must wire a root-part spawn seeder; the character factory cannot "
                + "reach the part sink from the engine-free assembly.");

            ActorContext actor = harness.Actor("spawn-wiring");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);
            harness.Bindings.Scheduler.Advance(1d / 60d);

            RbxInstance rootPart = player.Character.FindFirstChild(RbxCharacterFactory.RootPartName);
            Assert.IsTrue(
                harness.Bindings.PartSink.TryGetPartProperties(rootPart.Id, out PartProperties props));
            Assert.AreEqual(RbxCharacterFactory.RootPartSize, props.Size);
            Assert.AreEqual(RbxCharacterFactory.DefaultSpawnPosition, props.Position);
        }

        [Test]
        public void Negative_DeferredAutoLoad_FailureCostsOnlyThatPlayerAndIsReportedNotThrown()
        {
            // WHY this is the F2a gate: the deferred join-spawn runs from the scheduler's host-
            // callback slot, outside any mod's dispatch try/catch. Before this fix, a failure
            // inside RbxCharacterFactory.Load (an instance cap, an ACL refusal) propagated out of
            // Advance and killed the whole scheduler frame for every mod, not just the joiner.
            using ProductionHarness harness = new ProductionHarness();
            List<string> diagnostics = new();
            harness.Registry.Diagnostics = diagnostics.Add;

            string failingName = null;
            harness.Bindings.Players.RootPartSpawnSeeder = (rootPart, size, position) =>
            {
                if (rootPart.Parent != null
                    && string.Equals(rootPart.Parent.Name, failingName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated spawn failure");
                }
            };

            RbxPlayer failingPlayer = harness.Bindings.ConnectActor(harness.Actor("spawn-fail-a"));
            failingName = failingPlayer.Name;
            RbxPlayer healthyPlayer = harness.Bindings.ConnectActor(harness.Actor("spawn-fail-b"));

            Assert.DoesNotThrow(() => harness.Bindings.Scheduler.Advance(1d / 60d),
                "a failed deferred spawn must not kill the scheduler frame for every mod.");

            Assert.IsNull(failingPlayer.Character,
                "a failed deferred spawn must cost only this player its character.");
            Assert.IsNotNull(healthyPlayer.Character,
                "one player's failed spawn must not prevent another player's spawn in the same " +
                "frame.");
            Assert.IsTrue(
                diagnostics.Exists(line => line.Contains("simulated spawn failure")),
                "the failure must be reported through the registry's diagnostics; log: "
                + string.Join(" || ", diagnostics));
        }

        [Test]
        public void Negative_RespawnParentAssignmentFailure_LeavesPlayerCharacterNullNotDetached()
        {
            // WHY this pins the reorder: the Parent setter materializes the subtree synchronously
            // (the backing binder's OnEnteredWorld runs inside it), so it can still refuse after
            // the outgoing character is already gone — a locked parent, a destroyed instance, or
            // (as simulated here) a binder that throws while materializing. Before the reorder,
            // Load assigned player.Character before parenting, so a throw here left Character
            // pointing at a detached Model that never entered the world and never fired
            // CharacterAdded — a leak the caller's own failure log wrongly called "stays nil".
            InMemoryInstanceBackingBinder innerBinder = new();
            RespawnFailingBackingBinder failingBinder = new(innerBinder);
            using ProductionHarness harness = new ProductionHarness(failingBinder);

            RbxPlayer player = harness.Bindings.ConnectActor(harness.Actor("respawn-parent-fail"));
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance firstCharacter = player.Character;
            Assert.IsNotNull(firstCharacter);

            // WHY armed only now: the join-time spawn above must succeed so there is an outgoing
            // character to unload; only the replacement built by the LoadCharacterAsync call below
            // should fail to parent.
            failingBinder.FailOnNextModelEntryNamed = player.Name;

            bool characterAddedFired = false;
            player.CharacterAdded.Connect((Action<object[]>)(_ => characterAddedFired = true));

            Assert.Throws<InvalidOperationException>(() =>
                RbxCharacterFactory.Load(harness.Registry, harness.Registry.WorldRoot, player));

            Assert.IsNull(player.Character,
                "a Parent-assignment failure must leave Character null, matching reality, instead " +
                "of pointing at a detached Model that never entered the world.");
            Assert.IsFalse(characterAddedFired,
                "CharacterAdded must not fire for a character that never genuinely entered the " +
                "world.");
            Assert.IsTrue(firstCharacter.IsDestroyed,
                "the outgoing character is still unloaded even though the replacement failed to " +
                "parent.");

            IReadOnlyList<RbxInstance> liveInstances = harness.Registry.GetLiveInstances();
            bool leakedCharacterModel = false;
            for (int index = 0; index < liveInstances.Count; index++)
            {
                RbxInstance candidate = liveInstances[index];
                if (string.Equals(candidate.ClassName, "Model", StringComparison.Ordinal)
                    && string.Equals(candidate.Name, player.Name, StringComparison.Ordinal))
                {
                    leakedCharacterModel = true;
                    break;
                }
            }

            Assert.IsFalse(leakedCharacterModel,
                "a failed parent assignment must not leak a detached Model the registry still " +
                "tracks.");
        }

        [Test]
        public void LoadCharacter_RejectsForeignRegistryWithoutDestroyingCurrentCharacter()
        {
            using ProductionHarness harness = new ProductionHarness();
            using ProductionHarness foreign = new ProductionHarness();
            RbxPlayer player = harness.Bindings.ConnectActor(harness.Actor("invalid-registry"));
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance original = player.Character;
            Assert.Throws<ArgumentException>(() => RbxCharacterFactory.Load(
                foreign.Registry, foreign.Registry.WorldRoot, player));
            Assert.AreSame(original, player.Character);
            Assert.IsFalse(original.IsDestroyed);
        }

        [Test]
        public void LoadCharacter_RejectsReplacingAnotherActorsCharacter()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext owner = harness.Actor("owner");
            harness.Bindings.ConnectActor(owner);
            RbxPlayer other = harness.Bindings.ConnectActor(harness.Actor("other"));
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance protectedCharacter = other.Character;
            harness.Stack.Runtime.LoadMod(owner, "foreign-character", @"
                local players = game:GetService('Players'):GetPlayers()
                players[1].Character = players[2].Character
                task.spawn(function()
                    local ok = pcall(function() players[1]:LoadCharacterAsync() end)
                    store_set('allowed', tostring(ok))
                end)");
            for (int frame = 0; frame < 4; frame++)
            {
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }
            Assert.AreEqual("false", harness.Store.Get("foreign-character", "allowed"));
            Assert.IsFalse(protectedCharacter.IsDestroyed);
        }

        [Test]
        public void Negative_DisconnectAfterHijackingAnotherPlayersCharacterReference_LeavesTheirsIntact()
        {
            // WHY this is the hostile-audit Finding-A gate: the test above proves
            // LoadCharacterAsync refuses to replace a character it does not own, but that
            // authorization guards the LOAD path only. Character itself carries no such check on
            // plain assignment (no signal, no check — the mirror's own contract), so the hijack
            // read below must still succeed. What must NOT succeed is a disconnect afterwards
            // destroying the character actor A pointed itself at but never owned.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("hijack-disconnect-a");
            ActorContext actorB = harness.Actor("hijack-disconnect-b");
            RbxPlayer playerA = harness.Bindings.ConnectActor(actorA);
            RbxPlayer playerB = harness.Bindings.ConnectActor(actorB);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance charactersA = playerA.Character;
            RbxInstance charactersB = playerB.Character;
            Assert.IsNotNull(charactersA);
            Assert.IsNotNull(charactersB);

            harness.Stack.Runtime.LoadMod(actorA, "hijack-disconnect-setup", @"
                local players = game:GetService('Players'):GetPlayers()
                players[1].Character = players[2].Character",
                persistToStore: false);
            Assert.AreSame(charactersB, playerA.Character,
                "the hijack read itself must succeed — Character assignment carries no ownership " +
                "check.");

            harness.Bindings.DisconnectActor(actorA);

            Assert.IsFalse(charactersB.IsDestroyed,
                "actor A pointing its own Player.Character at B's character must not let A's " +
                "disconnect destroy a character A was never authorized to touch.");
            Assert.AreSame(charactersB, playerB.Character,
                "B's own Character reference must be untouched by A's disconnect.");
            Assert.IsTrue(charactersA.IsDestroyed,
                "A's disconnect must still tear down the character A's OWN lifecycle actually " +
                "built.");
        }

        [Test]
        public void Negative_SelfKickAfterHijackingAnotherPlayersCharacterReference_LeavesTheirsIntact()
        {
            // WHY this is the Kick twin of the disconnect test above: Kick and disconnect both
            // reach RbxCharacterFactory.Unload through RbxPlayers.RemoveActor with no
            // authorization check of their own — Unload has to be the thing that refuses to aim at
            // a foreign character, not a gate at either call site.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("hijack-kick-a");
            ActorContext actorB = harness.Actor("hijack-kick-b");
            harness.Bindings.ConnectActor(actorA);
            RbxPlayer playerB = harness.Bindings.ConnectActor(actorB);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance charactersB = playerB.Character;
            Assert.IsNotNull(charactersB);

            harness.Stack.Runtime.LoadMod(actorA, "hijack-kick-setup", @"
                local players = game:GetService('Players'):GetPlayers()
                players[1].Character = players[2].Character
                players[1]:Kick()",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsFalse(charactersB.IsDestroyed,
                "self-kicking after pointing Character at another player's character must not " +
                "destroy a character this actor was never authorized to touch.");
            Assert.AreSame(charactersB, playerB.Character);
        }

        [Test]
        public void Negative_DeathRespawnAfterHijackingAnotherPlayersCharacterReference_RespawnsTheDyingPlayerNotTheHijacker()
        {
            // WHY this is the death-respawn twin of the two hijack tests above: those close the
            // teardown path (disconnect/kick) by having Unload target LoadedCharacter instead of
            // Character. The death-triggered respawn had the same hole on a different path — it
            // resolved the dying character's owner with GetPlayerFromCharacter, which matches the
            // Lua-writable Character the hijack tests above prove carries no ownership check. Actor
            // A, who joins first and points its OWN Character at B's character, must not be
            // resolved as the owner when B's humanoid dies: B must still respawn, and A must not
            // receive a character it never earned by dying.
            using ProductionHarness harness = new ProductionHarness();
            harness.Bindings.Players.RespawnTime = 0.05d;
            ActorContext actorA = harness.Actor("death-hijack-a");
            ActorContext actorB = harness.Actor("death-hijack-b");
            RbxPlayer playerA = harness.Bindings.ConnectActor(actorA);
            RbxPlayer playerB = harness.Bindings.ConnectActor(actorB);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            RbxInstance characterA = playerA.Character;
            RbxInstance characterB = playerB.Character;
            Assert.IsNotNull(characterA);
            Assert.IsNotNull(characterB);

            harness.Stack.Runtime.LoadMod(actorA, "death-hijack-setup", @"
                local players = game:GetService('Players'):GetPlayers()
                players[1].Character = players[2].Character",
                persistToStore: false);
            Assert.AreSame(characterB, playerA.Character,
                "the hijack read itself must succeed — Character assignment carries no ownership " +
                "check.");

            RbxHumanoid humanoidB = (RbxHumanoid)characterB.FindFirstChild("Humanoid");
            humanoidB.TakeDamage(humanoidB.MaxHealth + 1d);
            harness.Bindings.Scheduler.Advance(1d / 60d);
            Assert.IsTrue(humanoidB.IsDead);

            for (int frame = 0; frame < 20 && ReferenceEquals(playerB.Character, characterB);
                 frame++)
            {
                harness.Bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.AreNotSame(characterB, playerB.Character,
                "B's own dead Humanoid must still respawn B — actor A hijacking A's own Character " +
                "reference must not cancel or redirect B's respawn.");
            Assert.IsTrue(characterB.IsDestroyed,
                "B's dead character must be torn down by B's own respawn, exactly as an unhijacked " +
                "death would.");
            Assert.IsFalse(characterA.IsDestroyed,
                "A never died, so A's own character must not be destroyed by a respawn timer that " +
                "was never A's to begin with.");
            Assert.AreSame(characterB, playerA.Character,
                "A must not receive a fresh character it never earned by dying — A's Character " +
                "field stays exactly what A pointed it at.");
        }

        [Test]
        public void SynchronousWorldEntryObserver_ResolvesTheOwningPlayer()
        {
            // WHY this is the hostile-audit Finding-B gate: Parent assignment materializes the
            // character subtree synchronously and fires InstanceRegistry.SceneMembershipChanged
            // (the same event LuaCsRbxApiBindings' own character-motor refresh listens to) from
            // inside that call, before RbxCharacterFactory.Load returns. A host reacting to the
            // character entering the world from that synchronous callback must already be able to
            // resolve Players:GetPlayerFromCharacter — which requires Player.Character to be
            // published BEFORE Parent is assigned, not after.
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("sync-observer-a");
            RbxPlayer player = harness.Bindings.ConnectActor(actor);

            bool observedEntry = false;
            RbxPlayer resolvedDuringEntry = null;
            harness.Registry.SceneMembershipChanged += (instance, entered) =>
            {
                if (!entered || observedEntry
                    || !string.Equals(instance.ClassName, "Model", StringComparison.Ordinal)
                    || !string.Equals(instance.Name, player.Name, StringComparison.Ordinal))
                {
                    return;
                }

                observedEntry = true;
                resolvedDuringEntry = harness.Bindings.Players.GetPlayerFromCharacter(instance);
            };

            harness.Bindings.Scheduler.Advance(1d / 60d);

            Assert.IsTrue(observedEntry, "the character model must synchronously enter the scene.");
            Assert.AreSame(player, resolvedDuringEntry,
                "a synchronous observer of the character entering the world must already be able " +
                "to resolve the owning player — Character has to be published before Parent is " +
                "assigned.");
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
                "Player_Chatted", "Player_StarterGear",
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

        /// <summary>
        /// Test double for the Finding-B parent-assignment-failure gate: delegates to a plain
        /// <see cref="InMemoryInstanceBackingBinder"/> but throws out of the second
        /// <c>OnEnteredWorld</c> call for a "Model" named <see cref="FailOnSecondModelEntryNamed"/>
        /// — the second Model-named-like-this entry is a respawn's replacement character, not the
        /// join-time original, matching where <see cref="RbxCharacterFactory.Load"/> actually
        /// materializes the subtree (the Parent setter, synchronously).
        /// </summary>
        private sealed class RespawnFailingBackingBinder : IInstanceBackingBinder
        {
            private readonly IInstanceBackingBinder _inner;

            public RespawnFailingBackingBinder(IInstanceBackingBinder inner)
            {
                _inner = inner;
            }

            /// <summary>
            /// Name of the character Model whose NEXT entry into the world must fail. Armed after the
            /// join-time spawn has already succeeded, and disarmed as it fires, so exactly one
            /// materialization is refused.
            /// </summary>
            public string FailOnNextModelEntryNamed { get; set; }

            public void OnEnteredWorld(InstanceRecord record)
            {
                if (FailOnNextModelEntryNamed != null
                    && string.Equals(record.Instance.ClassName, "Model", StringComparison.Ordinal)
                    && string.Equals(record.Instance.Name, FailOnNextModelEntryNamed,
                        StringComparison.Ordinal))
                {
                    FailOnNextModelEntryNamed = null;
                    throw new InvalidOperationException(
                        "simulated parent materialization failure");
                }

                _inner.OnEnteredWorld(record);
            }

            public void OnLeftWorld(InstanceRecord record) => _inner.OnLeftWorld(record);

            public void OnDestroyed(InstanceRecord record) => _inner.OnDestroyed(record);

            public void OnReparented(InstanceRecord record) => _inner.OnReparented(record);

            public void OnNameChanged(InstanceRecord record) => _inner.OnNameChanged(record);

            public void CopyBackingState(InstanceId sourceId, InstanceId destinationId) =>
                _inner.CopyBackingState(sourceId, destinationId);
        }

        private sealed class ProductionHarness : IDisposable
        {
            // WHY an optional seam rather than a second harness type: every existing caller wants
            // the plain in-memory binder, and only the Finding-B parent-assignment-failure test
            // needs one that can refuse mid-materialization — a default keeps this call site
            // unchanged everywhere else.
            public ProductionHarness(IInstanceBackingBinder binder = null)
            {
                LogLines = new List<string>();
                Binder = new InMemoryInstanceBackingBinder();
                Registry = new InstanceRegistry(
                    binder: binder ?? Binder,
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

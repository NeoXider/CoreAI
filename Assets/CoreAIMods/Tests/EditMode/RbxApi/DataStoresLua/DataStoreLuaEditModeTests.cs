using System;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.DataStores;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.DataStoresLua
{
    /// <summary>
    /// Lua-tier conformance of DataStoreService (MVP16, single player): the five store members yield the calling
    /// thread and resume it on a later step (S1.4-S1.13), work inside pcall, refuse what Roblox refuses with Roblox's
    /// codes (S1.3, S1.14, S1.15), UpdateAsync's nil abort and no-yield rule (S1.8, S1.10), pacing a script can retry,
    /// the Data capability, data kept across a restart, outside world packages and outside undo, and the canonical
    /// coins game with a leaderstats column. Storage is the in-memory port with hand-driven clocks.
    /// </summary>
    [TestFixture]
    public sealed class DataStoreLuaEditModeTests
    {
        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void S1_4_GetAsync_YieldsTheThread_ResumesOnALaterStep_AndReturnsNilNilForANewKey()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                workspace:SetAttribute('before', true)
                local value, info = store:GetAsync('Player_1')
                workspace:SetAttribute('valueNil', value == nil)
                workspace:SetAttribute('infoNil', info == nil)
                workspace:SetAttribute('after', true)");

            Assert.AreEqual(true, world.Attribute("before"));
            Assert.IsNull(world.Attribute("after"), "the call yields; the chunk does not run on in the same step");
            world.AdvanceUntil(() => world.Attribute("after") != null);
            Assert.AreEqual(true, world.Attribute("valueNil"));
            Assert.AreEqual(true, world.Attribute("infoNil"));
        }

        [Test]
        public void S1_6_SetAsync_ThenGetAsync_RoundTripsATable_WithItsKeyInfo()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('PlayerData')
                local version = store:SetAsync('Player_1', { coins = 12, items = { 'sword', 'shield' }, vip = true }, { 1 })
                local data, info = store:GetAsync('Player_1')
                workspace:SetAttribute('version', version)
                workspace:SetAttribute('coins', data.coins)
                workspace:SetAttribute('item2', data.items[2])
                workspace:SetAttribute('vip', data.vip)
                workspace:SetAttribute('infoClass', info.ClassName)
                workspace:SetAttribute('infoVersion', info.Version)
                workspace:SetAttribute('isKeyInfo', info:IsA('DataStoreKeyInfo'))
                workspace:SetAttribute('created', info.CreatedTime)
                workspace:SetAttribute('userId', info:GetUserIds()[1])
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual("1", world.Attribute("version"));
            Assert.AreEqual(12d, Convert.ToDouble(world.Attribute("coins")));
            Assert.AreEqual("shield", world.Attribute("item2"));
            Assert.AreEqual(true, world.Attribute("vip"));
            Assert.AreEqual("DataStoreKeyInfo", world.Attribute("infoClass"));
            Assert.AreEqual("1", world.Attribute("infoVersion"));
            Assert.AreEqual(true, world.Attribute("isKeyInfo"));
            Assert.AreEqual((double)world.WallNow, Convert.ToDouble(world.Attribute("created")));
            Assert.AreEqual(1d, Convert.ToDouble(world.Attribute("userId")));
        }

        [Test]
        public void S1_4_ThePcallIdioms_ReturnTrueAndTheValues()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                local okSet = pcall(function() store:SetAsync('k', 5) end)
                local ok, value, info = pcall(function() return store:GetAsync('k') end)
                local okMethod, direct = pcall(store.GetAsync, store, 'k')
                workspace:SetAttribute('okSet', okSet)
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('value', value)
                workspace:SetAttribute('hasInfo', info ~= nil)
                workspace:SetAttribute('okMethod', okMethod)
                workspace:SetAttribute('direct', direct)
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(true, world.Attribute("okSet"));
            Assert.AreEqual(true, world.Attribute("ok"));
            Assert.AreEqual(5d, Convert.ToDouble(world.Attribute("value")));
            Assert.AreEqual(true, world.Attribute("hasInfo"));
            Assert.AreEqual(true, world.Attribute("okMethod"));
            Assert.AreEqual(5d, Convert.ToDouble(world.Attribute("direct")));
        }

        [Test]
        public void S1_3_AKeyOneCharacterTooLong_FailsInsidePcallWith102_AndTheLimitKeyWorks()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                local okLimit = pcall(function() store:SetAsync(string.rep('k', 50), 1) end)
                local ok, err = pcall(function() store:SetAsync(string.rep('k', 51), 1) end)
                workspace:SetAttribute('okLimit', okLimit)
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('err', tostring(err))
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(true, world.Attribute("okLimit"));
            Assert.AreEqual(false, world.Attribute("ok"));
            StringAssert.Contains("102: Key name exceeds the 50 character limit.", (string)world.Attribute("err"));
            StringAssert.Contains("BAD_ARGUMENT", (string)world.Attribute("err"));
        }

        [Test]
        public void S1_15_AValueOneCharacterTooLargeAsJson_IsRefusedWith105_AndOneAtTheLimitIsStored()
        {
            using World world = new();
            // WHY a table of five strings: the sandbox caps one string.rep at 1,000,000 characters. Four strings of
            // 1,000,000 and one of r encode to 2 brackets + 4 commas + 10 quotes + 4,000,000 + r characters, so
            // r = 194,288 is exactly the limit.
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Big')
                local million = string.rep('x', 1000000)
                local function value(rest) return { million, million, million, million, string.rep('y', rest) } end
                local okLimit, errLimit = pcall(function() store:SetAsync('fits', value(194288)) end)
                local ok, err = pcall(function() store:SetAsync('over', value(194289)) end)
                workspace:SetAttribute('okLimit', okLimit)
                workspace:SetAttribute('errLimit', tostring(errLimit))
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('err', tostring(err))
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(true, world.Attribute("okLimit"), (string)world.Attribute("errLimit"));
            Assert.AreEqual(false, world.Attribute("ok"));
            StringAssert.Contains("105: Serialized value exceeds 4MB limit.", (string)world.Attribute("err"));
            Assert.IsNull(world.Backend.Database("").Read("Big", "global", "over"));
            Assert.AreEqual(RbxDataStoreOptions.MirrorValueLimit,
                world.Backend.Database("").Read("Big", "global", "fits").ValueJson.Length);
        }

        [Test]
        public void S1_14_WhatCannotBeStored_IsRefusedWithRobloxCodes_AndNothingIsWritten()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Bad')
                local function try(value)
                    local ok, err = pcall(function() store:SetAsync('k', value) end)
                    return tostring(ok) .. '|' .. tostring(err)
                end
                workspace:SetAttribute('func', try(function() end))
                workspace:SetAttribute('instance', try(workspace))
                workspace:SetAttribute('vector', try(Vector3.new(1, 2, 3)))
                workspace:SetAttribute('mixed', try({ 1, 2, name = 'x' }))
                workspace:SetAttribute('nested', try({ part = workspace }))
                local cyclic = {}
                cyclic.self = cyclic
                workspace:SetAttribute('cyclic', try(cyclic))
                workspace:SetAttribute('nilValue', try(nil))
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            StringAssert.StartsWith("false|", (string)world.Attribute("func"));
            StringAssert.Contains("104: Can't store function in DataStore.", (string)world.Attribute("func"));
            StringAssert.Contains("104: Can't store Instance in DataStore.", (string)world.Attribute("instance"));
            StringAssert.Contains("104: Can't store Vector3 in DataStore.", (string)world.Attribute("vector"));
            StringAssert.Contains("104: Can't store a mixed table", (string)world.Attribute("mixed"));
            StringAssert.Contains("(at $.part)", (string)world.Attribute("nested"));
            StringAssert.Contains("104: Can't store a cyclic table", (string)world.Attribute("cyclic"));
            StringAssert.Contains("Argument 2 missing or nil", (string)world.Attribute("nilValue"));
            Assert.IsNull(world.Backend.Database("").Read("Bad", "global", "k"));
        }

        [Test]
        public void S1_8_UpdateAsyncNilAborts_TheValueStays_AndTheCallReturnsNil()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                store:SetAsync('k', 5)
                local seen
                local result = store:UpdateAsync('k', function(old) seen = old return nil end)
                local after = store:GetAsync('k')
                workspace:SetAttribute('seen', seen)
                workspace:SetAttribute('resultNil', result == nil)
                workspace:SetAttribute('after', after)
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(5d, Convert.ToDouble(world.Attribute("seen")));
            Assert.AreEqual(true, world.Attribute("resultNil"));
            Assert.AreEqual(5d, Convert.ToDouble(world.Attribute("after")));
            Assert.AreEqual(1L, world.Backend.Database("").Read("Coins", "global", "k").Version, "no new version");
        }

        [Test]
        public void S1_7_UpdateAsync_ReadsTheCurrentValue_WritesTheReturnedOne_WithUserIdsAndMetadata()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('PlayerData')
                local newValue, info = store:UpdateAsync('Player_1', function(current, keyInfo)
                    current = current or { coins = 0 }
                    current.coins = current.coins + 50
                    return current, { 1 }, { origin = 'quest' }
                end)
                local again = store:UpdateAsync('Player_1', function(current, keyInfo)
                    current.coins = current.coins + 1
                    return current, keyInfo:GetUserIds(), keyInfo:GetMetadata()
                end)
                local _, latest = store:GetAsync('Player_1')
                workspace:SetAttribute('first', newValue.coins)
                workspace:SetAttribute('firstVersion', info.Version)
                workspace:SetAttribute('second', again.coins)
                workspace:SetAttribute('origin', latest:GetMetadata().origin)
                workspace:SetAttribute('userId', latest:GetUserIds()[1])
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(50d, Convert.ToDouble(world.Attribute("first")));
            Assert.AreEqual("1", world.Attribute("firstVersion"));
            Assert.AreEqual(51d, Convert.ToDouble(world.Attribute("second")));
            Assert.AreEqual("quest", world.Attribute("origin"));
            Assert.AreEqual(1d, Convert.ToDouble(world.Attribute("userId")));
        }

        [Test]
        public void S1_10_AnUpdateFunctionThatYields_FailsTheCall_AndWritesNothing()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                store:SetAsync('k', 1)
                local okWait, errWait = pcall(function()
                    return store:UpdateAsync('k', function(old) task.wait(1) return old + 1 end)
                end)
                local okYield, errYield = pcall(function()
                    return store:UpdateAsync('k', function(old) coroutine.yield() return old + 1 end)
                end)
                local okThrow, errThrow = pcall(function()
                    return store:UpdateAsync('k', function(old) error('boom', 0) end)
                end)
                workspace:SetAttribute('okWait', okWait)
                workspace:SetAttribute('okYield', okYield)
                workspace:SetAttribute('errYield', tostring(errYield))
                workspace:SetAttribute('okThrow', okThrow)
                workspace:SetAttribute('errThrow', tostring(errThrow))
                workspace:SetAttribute('value', store:GetAsync('k'))
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(false, world.Attribute("okWait"));
            Assert.AreEqual(false, world.Attribute("okYield"));
            StringAssert.Contains("the update function cannot yield", (string)world.Attribute("errYield"));
            Assert.AreEqual(false, world.Attribute("okThrow"));
            StringAssert.Contains("boom", (string)world.Attribute("errThrow"));
            Assert.AreEqual(1d, Convert.ToDouble(world.Attribute("value")));
        }

        [Test]
        public void S1_12_IncrementAsync_AddsIntegers_DefaultsToOne_AndRefusesAFraction()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Counters')
                local a = store:IncrementAsync('visits')
                local b = store:IncrementAsync('visits', 10)
                local ok, err = pcall(function() return store:IncrementAsync('visits', 1.5) end)
                workspace:SetAttribute('a', a)
                workspace:SetAttribute('b', b)
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('err', tostring(err))
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(1d, Convert.ToDouble(world.Attribute("a")));
            Assert.AreEqual(11d, Convert.ToDouble(world.Attribute("b")));
            Assert.AreEqual(false, world.Attribute("ok"));
            StringAssert.Contains("delta must be an integer", (string)world.Attribute("err"));
        }

        [Test]
        public void S1_13_RemoveAsync_ReturnsWhatWasStored_AndTheKeyIsGoneAfterwards()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                store:SetAsync('k', 'hello')
                local old, info = store:RemoveAsync('k')
                local after = store:GetAsync('k')
                local again = store:RemoveAsync('k')
                workspace:SetAttribute('old', old)
                workspace:SetAttribute('hadInfo', info ~= nil)
                workspace:SetAttribute('afterNil', after == nil)
                workspace:SetAttribute('againNil', again == nil)
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual("hello", world.Attribute("old"));
            Assert.AreEqual(true, world.Attribute("hadInfo"));
            Assert.AreEqual(true, world.Attribute("afterNil"));
            Assert.AreEqual(true, world.Attribute("againNil"));
        }

        [Test]
        public void S1_24_AWriteRefusedByPacing_ReadsLikeTheThrottleError_AndStandardRetryCodeSucceedsAfterTheRefill()
        {
            using World world = new(new RbxDataStoreOptions { WriteBase = 1, WritePerPlayer = 0, QueueSize = 0 });
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                local DataStoreService = game:GetService('DataStoreService')
                store:SetAsync('a', 1)
                workspace:SetAttribute('budget', DataStoreService:GetRequestBudgetForRequestType(Enum.DataStoreRequestType.SetIncrementAsync))
                local ok, err = pcall(function() store:SetAsync('b', 2) end)
                workspace:SetAttribute('firstOk', ok)
                workspace:SetAttribute('firstErr', tostring(err))
                local attempts = 1
                while not ok do
                    task.wait(1)
                    attempts = attempts + 1
                    ok, err = pcall(function() store:SetAsync('b', 2) end)
                end
                workspace:SetAttribute('attempts', attempts)
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("firstErr") != null);

            Assert.AreEqual(0d, Convert.ToDouble(world.Attribute("budget")));
            Assert.AreEqual(false, world.Attribute("firstOk"));
            StringAssert.Contains("302: SetAsync request dropped. Request was throttled but queue was full.",
                (string)world.Attribute("firstErr"));
            StringAssert.Contains("BUDGET_EXCEEDED", (string)world.Attribute("firstErr"));

            world.Now += 60d;
            world.AdvanceUntil(() => world.Attribute("done") != null, 1d);
            Assert.Greater(Convert.ToDouble(world.Attribute("attempts")), 1d);
            Assert.AreEqual("2", world.Backend.Database("").Read("Coins", "global", "b").ValueJson);
        }

        [Test]
        public void AModWithoutTheDataCapability_IsRefused_AndTheSameModWithIt_Works()
        {
            using World world = new();
            world.Load(@"
                local ok, err = pcall(function() return game:GetService('DataStoreService'):GetDataStore('Coins') end)
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('err', tostring(err))", LuaCapabilities.All & ~LuaCapabilities.Data, "no-data");
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                store:SetAsync('k', 3)
                workspace:SetAttribute('granted', store:GetAsync('k'))", LuaCapabilities.All, "with-data");
            world.AdvanceUntil(() => world.Attribute("granted") != null);

            Assert.AreEqual(false, world.Attribute("ok"));
            StringAssert.Contains("NOT_AUTHORITY", (string)world.Attribute("err"));
            StringAssert.Contains("requires the Data capability", (string)world.Attribute("err"));
            Assert.AreEqual(3d, Convert.ToDouble(world.Attribute("granted")));
        }

        [Test]
        public void S1_1_StoresAreSharedBetweenMods_ButAnotherNameOrScopeSeesNothing()
        {
            using World world = new();
            world.Load(@"
                game:GetService('DataStoreService'):GetDataStore('Coins'):SetAsync('Player_1', 40)
                workspace:SetAttribute('written', true)", LuaCapabilities.All, "writer");
            world.AdvanceUntil(() => world.Attribute("written") != null);
            world.Load(@"
                local DataStoreService = game:GetService('DataStoreService')
                workspace:SetAttribute('same', DataStoreService:GetDataStore('Coins'):GetAsync('Player_1'))
                workspace:SetAttribute('otherName', DataStoreService:GetDataStore('Gems'):GetAsync('Player_1') == nil)
                workspace:SetAttribute('otherScope', DataStoreService:GetDataStore('Coins', 'gold'):GetAsync('Player_1') == nil)
                workspace:SetAttribute('done', true)", LuaCapabilities.All, "reader");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(40d, Convert.ToDouble(world.Attribute("same")));
            Assert.AreEqual(true, world.Attribute("otherName"));
            Assert.AreEqual(true, world.Attribute("otherScope"));
        }

        [Test]
        public void ARestart_ReadsWhatTheLastSessionWrote()
        {
            InMemoryRbxDataStorePersistence storage = new();
            using (World first = new(null, storage))
            {
                first.Load(@"
                    game:GetService('DataStoreService'):GetDataStore('PlayerData'):SetAsync('Player_1', { coins = 77 })
                    workspace:SetAttribute('saved', true)");
                first.AdvanceUntil(() => first.Attribute("saved") != null);
            }

            using World second = new(null, storage);
            second.Load(@"
                local data = game:GetService('DataStoreService'):GetDataStore('PlayerData'):GetAsync('Player_1')
                workspace:SetAttribute('coins', data and data.coins)");
            second.AdvanceUntil(() => second.Attribute("coins") != null);

            Assert.AreEqual(77d, Convert.ToDouble(second.Attribute("coins")));
        }

        [Test]
        public void DataStoreData_IsNotPartOfAWorldPackage_AndRestoringAPackageLeavesItAsItIs()
        {
            using World world = new();
            world.Load(@"
                game:GetService('DataStoreService'):GetDataStore('Coins'):SetAsync('Player_1', 'marker-4242')
                local part = Instance.new('Part')
                part.Name = 'Built'
                part.Parent = workspace
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                world.Rbx.Registry, world.Rbx.Game, world.Rbx.PartSink, new RbxWorldSettings()));
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            Assert.IsFalse(ContainsText(package, "marker-4242"), "no DataStore value is in the package");
            Assert.IsFalse(ContainsText(package, "DataStoreService"), "the detached service is not in the tree");
            Assert.IsNotNull(read);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            using World next = new(null, world.Storage, restored, world.Backend);
            next.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                local ok, value = pcall(function() return store:GetAsync('Player_1') end)
                store:SetAsync('Seen', tostring(ok) .. '|' .. tostring(value))");
            next.AdvanceUntil(() => next.Backend.Database("").Read("Coins", "global", "Seen") != null);
            Assert.AreEqual("\"true|marker-4242\"", next.Backend.Database("").Read("Coins", "global", "Seen").ValueJson,
                "the restored world reads the data the world before it wrote");
        }

        [Test]
        public void ADataStoreWrite_LeavesTheWorldMarkUnchanged_SoUndoNeitherSeesNorRevertsIt()
        {
            using World world = new();
            world.Load(@"
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                workspace:SetAttribute('ready', true)
                task.wait(1)
                store:SetAsync('Player_1', 9)
                workspace:SetAttribute('saved', true)");
            world.AdvanceUntil(() => world.Attribute("ready") != null);
            RbxWorldStateMark before = RbxWorldStateMark.Of(world.Rbx.Registry, 0L);

            world.AdvanceUntil(() => world.Attribute("saved") != null, 0.5d);
            RbxWorldStateMark after = RbxWorldStateMark.Of(world.Rbx.Registry, 0L);

            Assert.AreEqual(before, after, "undo compares this mark; a data write is not a world change it can revert");
            Assert.AreEqual("9", world.Backend.Database("").Read("Coins", "global", "Player_1").ValueJson);
        }

        [Test]
        public void TheDataStoreObjects_AreReadOnly_AndTheirPlannedMembersNameTheirRung()
        {
            using World world = new();
            world.Load(@"
                local DataStoreService = game:GetService('DataStoreService')
                local store = DataStoreService:GetDataStore('Coins')
                local okParent, errParent = pcall(function() store.Parent = workspace end)
                local okOrdered, errOrdered = pcall(function() return DataStoreService:GetOrderedDataStore('Top') end)
                local okList, errList = pcall(function() return store:ListKeysAsync() end)
                local okOptions, errOptions = pcall(function() return Instance.new('DataStoreSetOptions') end)
                workspace:SetAttribute('isGlobal', store:IsA('GlobalDataStore'))
                workspace:SetAttribute('okParent', okParent)
                workspace:SetAttribute('errParent', tostring(errParent))
                workspace:SetAttribute('errOrdered', tostring(errOrdered))
                workspace:SetAttribute('errList', tostring(errList))
                workspace:SetAttribute('errOptions', tostring(errOptions))
                workspace:SetAttribute('serviceParentNil', DataStoreService.Parent == nil)
                workspace:SetAttribute('done', true)");
            world.AdvanceUntil(() => world.Attribute("done") != null);

            Assert.AreEqual(true, world.Attribute("isGlobal"));
            Assert.AreEqual(false, world.Attribute("okParent"));
            StringAssert.Contains("can't be set", (string)world.Attribute("errParent"));
            StringAssert.Contains("DataStoreService:GetOrderedDataStore is planned for MVP16", (string)world.Attribute("errOrdered"));
            StringAssert.Contains("DataStore:ListKeysAsync is planned for MVP16", (string)world.Attribute("errList"));
            StringAssert.Contains("is planned for MVP16", (string)world.Attribute("errOptions"));
            Assert.AreEqual(true, world.Attribute("serviceParentNil"));
        }

        [Test]
        public void TheCoinsGame_RemembersTheCoins_AndShowsThemInTheLeaderstatsColumn()
        {
            const string Source = @"
                local Players = game:GetService('Players')
                local store = game:GetService('DataStoreService'):GetDataStore('Coins')
                Players.PlayerAdded:Connect(function(player)
                    local leaderstats = Instance.new('Folder')
                    leaderstats.Name = 'leaderstats'
                    leaderstats.Parent = player
                    local coins = Instance.new('IntValue')
                    coins.Name = 'Coins'
                    coins.Parent = leaderstats
                    local ok, saved = pcall(function() return store:GetAsync('Player_' .. player.UserId) end)
                    if ok and saved then coins.Value = saved end
                    coins.Changed:Connect(function(value)
                        pcall(function() store:SetAsync('Player_' .. player.UserId, value) end)
                    end)
                    workspace:SetAttribute('loaded', coins.Value)
                end)";
            InMemoryRbxDataStorePersistence storage = new();
            using (World first = new(null, storage))
            {
                FakeRbxUiBackend ui = new();
                RbxPlayerListView view = CoreAiPlayerListBindings.AttachTo(first.Rbx, ui);
                first.Load(Source);
                RbxPlayer player = first.Rbx.Players.EnsureActor(first.Rbx.Registry, "player-1");
                first.AdvanceUntil(() => first.Attribute("loaded") != null);
                RbxIntValue coins = (RbxIntValue)player.FindFirstChild("leaderstats").FindFirstChild("Coins");
                coins.Value = 25;
                first.AdvanceUntil(() => first.Backend.Database("").Read("Coins", "global", "Player_" + player.UserId) != null);
                first.Advance();

                Assert.AreEqual(0d, Convert.ToDouble(first.Attribute("loaded")));
                FakeRbxUiBackend.FakeScreen screen = (FakeRbxUiBackend.FakeScreen)view.Screen;
                Assert.IsNotNull(screen, "the leaderstats folder shows the player list");
                Assert.AreEqual("Coins", screen.Find("header-0").Text);
                Assert.AreEqual("25", screen.Find("row-0-0").Text);
            }

            using World second = new(null, storage);
            second.Load(Source);
            second.Rbx.Players.EnsureActor(second.Rbx.Registry, "player-1");
            second.AdvanceUntil(() => second.Attribute("loaded") != null);

            Assert.AreEqual(25d, Convert.ToDouble(second.Attribute("loaded")), "the next session remembers the coins");
        }

        private static bool ContainsText(byte[] package, string text)
        {
            using System.IO.MemoryStream stream = new(package);
            using System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Read);
            foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
            {
                using System.IO.Stream entryStream = entry.Open();
                using System.IO.StreamReader reader = new(entryStream, Encoding.UTF8);
                if (reader.ReadToEnd().IndexOf(text, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class World : IDisposable
        {
            private readonly bool _ownsBackend;

            public World(RbxDataStoreOptions options = null, InMemoryRbxDataStorePersistence storage = null,
                RbxWorldPackageRestoreResult restored = null, RbxDataStoreBackend sharedBackend = null)
            {
                Storage = storage ?? new InMemoryRbxDataStorePersistence();
                Rbx = restored == null
                    ? new LuaCsRbxApiBindings()
                    : new LuaCsRbxApiBindings(restored.Registry, restored.Game);
                Rbx.Players.CharacterAutoLoads = false;
                _ownsBackend = sharedBackend == null;
                Backend = sharedBackend ?? new RbxDataStoreBackend(Storage, "", options, () => Now, () => WallNow);
                Action<LuaCsApiRegistry, LuaCapabilities> hook =
                    CoreAiLuaDataStoreBindings.Attach(Rbx, Backend, null, out LuaCsRbxDataStoreAdapter adapter);
                Adapter = adapter;
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    RbxApi = Rbx,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    AdditionalGameplayBindings = hook
                });
            }

            public double Now { get; set; } = 10d;

            public long WallNow { get; } = 1_731_300_000_000L;

            public InMemoryRbxDataStorePersistence Storage { get; }

            public RbxDataStoreBackend Backend { get; }

            public LuaCsRbxApiBindings Rbx { get; }

            public LuaCsRbxDataStoreAdapter Adapter { get; }

            public LuaCsModStack Stack { get; }

            public void Load(string source, LuaCapabilities capabilities = LuaCapabilities.All, string modId = "coins")
            {
                Stack.Runtime.LoadMod(modId, source, capabilities, false);
            }

            public object Attribute(string name)
            {
                return Rbx.Game.FindFirstChildOfClass("Workspace").GetAttribute(name);
            }

            public void Advance(double seconds = 0d)
            {
                Rbx.Scheduler.Advance(seconds);
            }

            public void AdvanceUntil(Func<bool> done, double seconds = 0d)
            {
                for (int frame = 0; frame < 400 && !done(); frame++)
                {
                    Advance(seconds);
                }

                Assert.IsTrue(done(), "The world did not settle within 400 frames.");
            }

            public void Dispose()
            {
                Rbx.Dispose();
                if (_ownsBackend)
                {
                    Backend.Dispose();
                }
            }
        }
    }
}

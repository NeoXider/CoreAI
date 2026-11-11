using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.DataStores;
using CoreAI.Tests.EditMode.Performance;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.DataStores
{
    /// <summary>
    /// The engine-free DataStoreService (MVP16): the mirror's limits and error texts (S1.3, S1.15, the 1xx and 3xx
    /// codes), the request budgets and their queue (S1.24 and the limits page), the namespace rule, the coalesced
    /// writes over the storage port, a restart reading what was written, an unreadable store and a failing disk, and
    /// the allocation budget of an idle service. Each behaviour has its refused twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxDataStoreEngineFreeEditModeTests
    {
        private double _now;
        private long _wallNow;

        [SetUp]
        public void ResetClocks()
        {
            _now = 100d;
            _wallNow = 1_731_300_000_000L;
        }

        [Test]
        public void S1_3_AKeyOf50Characters_IsAccepted_And51_IsRefusedWithCode102()
        {
            RbxDataStoreService service = Service(Backend());
            RbxGlobalDataStore store = service.GetDataStore("PlayerData");

            Assert.DoesNotThrow(() => service.Submit(Request(RbxDataStoreOperation.Get, store, new string('k', 50))));
            RbxError tooLong = Assert.Throws<RbxError>(() =>
                service.Submit(Request(RbxDataStoreOperation.Get, store, new string('k', 51))));
            RbxError empty = Assert.Throws<RbxError>(() => service.Submit(Request(RbxDataStoreOperation.Get, store, "")));

            Assert.AreEqual(RbxErrorCode.BadArgument, tooLong.Code);
            StringAssert.StartsWith("102: Key name exceeds the 50 character limit.", tooLong.RawMessage);
            StringAssert.StartsWith("101: Key name can't be empty.", empty.RawMessage);
        }

        [Test]
        public void S1_3_ANameAndAScopeOf50Characters_AreAccepted_And51_AreRefused()
        {
            RbxDataStoreService service = Service(Backend());

            Assert.DoesNotThrow(() => service.GetDataStore(new string('n', 50), new string('s', 50)));
            RbxError name = Assert.Throws<RbxError>(() => service.GetDataStore(new string('n', 51)));
            RbxError scope = Assert.Throws<RbxError>(() => service.GetDataStore("ok", new string('s', 51)));
            RbxError empty = Assert.Throws<RbxError>(() => service.GetDataStore(""));

            StringAssert.Contains("name exceeds the 50 character limit", name.RawMessage);
            StringAssert.Contains("scope exceeds the 50 character limit", scope.RawMessage);
            StringAssert.Contains("can't be empty", empty.RawMessage);
        }

        [Test]
        public void S1_1_TheSameNameAndScope_IsTheSameStore_AndTheDefaultScopeIsGlobal()
        {
            RbxDataStoreService service = Service(Backend());

            Assert.AreSame(service.GetDataStore("Coins"), service.GetDataStore("Coins", "global"));
            Assert.AreNotSame(service.GetDataStore("Coins"), service.GetDataStore("Coins", "gold"));
            Assert.AreEqual("global", service.GetDataStore("Coins").Scope);
            Assert.AreEqual("u", service.GetGlobalDataStore().Scope);
            Assert.AreEqual("Coins", service.GetDataStore("Coins").Name);
            Assert.AreEqual("DataStore", service.GetDataStore("Coins").ClassName);
        }

        [Test]
        public void S1_15_AValueOfTheLimit_IsWritten_AndOneCharacterMore_IsRefusedWithCode105()
        {
            RbxDataStoreBackend backend = Backend();
            RbxDataStoreService service = Service(backend);
            RbxGlobalDataStore store = service.GetDataStore("Big");
            string atLimit = new('x', RbxDataStoreOptions.MirrorValueLimit);

            Assert.DoesNotThrow(() => service.RequireValueSize(atLimit));
            RbxError refused = Assert.Throws<RbxError>(() => service.RequireValueSize(atLimit + "x"));

            Assert.AreEqual(RbxErrorCode.PayloadTooLarge, refused.Code);
            Assert.AreEqual("105: Serialized value exceeds 4MB limit.", refused.RawMessage);
            RbxDataStoreRequest set = service.Submit(Set(store, "k", atLimit + "x"));
            Assert.AreEqual(RbxDataStoreRequestState.Failed, set.State, "a too-large write fails, it is never stored");
            Assert.IsNull(service.Database.Read("Big", "global", "k"));
        }

        [Test]
        public void S1_24_AWriteRefusedByPacing_IsAcceptedAfterTheBudgetRefills()
        {
            RbxDataStoreOptions options = new() { WriteBase = 2, WritePerPlayer = 0, QueueSize = 0 };
            RbxDataStoreService service = Service(Backend(options));
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(store, "a", "1")).State);
            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(store, "b", "2")).State);
            Assert.AreEqual(0, service.GetRequestBudget("SetIncrementAsync"));
            RbxError refused = Assert.Throws<RbxError>(() => service.Submit(Set(store, "c", "3")));
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, refused.Code);
            Assert.AreEqual("302: SetAsync request dropped. Request was throttled but queue was full.", refused.RawMessage);
            Assert.IsNull(service.Database.Read("Coins", "global", "c"));

            _now += 30d;

            Assert.AreEqual(1, service.GetRequestBudget("SetIncrementAsync"), "2 per minute is one every 30 seconds");
            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(store, "c", "3")).State);
            Assert.AreEqual("3", service.Database.Read("Coins", "global", "c").ValueJson);
        }

        [Test]
        public void S1_24_AnExhaustedBudget_QueuesTheRequest_WhichRunsWhenItRefills()
        {
            RbxDataStoreOptions options = new() { ReadBase = 1, ReadPerPlayer = 0, QueueSize = 2 };
            RbxDataStoreService service = Service(Backend(options));
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            RbxDataStoreRequest first = service.Submit(Request(RbxDataStoreOperation.Get, store, "a"));
            RbxDataStoreRequest second = service.Submit(Request(RbxDataStoreOperation.Get, store, "b"));
            RbxDataStoreRequest third = service.Submit(Request(RbxDataStoreOperation.Get, store, "c"));
            RbxError dropped = Assert.Throws<RbxError>(() => service.Submit(Request(RbxDataStoreOperation.Get, store, "d")));

            Assert.AreEqual(RbxDataStoreRequestState.Done, first.State);
            Assert.AreEqual(RbxDataStoreRequestState.Queued, second.State);
            Assert.AreEqual(2, service.QueuedCount);
            StringAssert.StartsWith("301: GetAsync request dropped. Request was throttled but queue was full.",
                dropped.RawMessage);

            service.Pump();
            Assert.AreEqual(RbxDataStoreRequestState.Queued, second.State, "nothing refilled yet");

            _now += 60d;
            service.Pump();

            Assert.AreEqual(RbxDataStoreRequestState.Done, second.State);
            Assert.AreEqual(RbxDataStoreRequestState.Queued, third.State, "one per minute: the next waits again");
            _now += 60d;
            service.Pump();
            Assert.AreEqual(RbxDataStoreRequestState.Done, third.State);
            Assert.AreEqual(0, service.QueuedCount);
        }

        [Test]
        public void OneModCannotHoldMoreWaitingRequestsThanItsShare_AnotherModStillQueues()
        {
            RbxDataStoreOptions options = new() { ReadBase = 0, ReadPerPlayer = 0, QueueSize = 30, WaitingRequestsPerMod = 2 };
            RbxDataStoreService service = Service(Backend(options));
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            service.Submit(Request(RbxDataStoreOperation.Get, store, "a", "spammer"));
            service.Submit(Request(RbxDataStoreOperation.Get, store, "b", "spammer"));
            RbxError refused = Assert.Throws<RbxError>(() =>
                service.Submit(Request(RbxDataStoreOperation.Get, store, "c", "spammer")));
            RbxDataStoreRequest other = service.Submit(Request(RbxDataStoreOperation.Get, store, "d", "other"));

            StringAssert.StartsWith("301: GetAsync request dropped.", refused.RawMessage);
            StringAssert.Contains("mod 'spammer' already has 2 requests waiting", refused.RawMessage);
            Assert.AreEqual(RbxDataStoreRequestState.Queued, other.State);
        }

        [Test]
        public void S1_24_TheBudgetOfEachRequestType_IsTheMirrorServerDefaultForOnePlayer()
        {
            RbxDataStoreService service = Service(Backend());

            Assert.AreEqual(100, service.GetRequestBudget("GetAsync"));
            Assert.AreEqual(100, service.GetRequestBudget("StandardRead"));
            Assert.AreEqual(100, service.GetRequestBudget("SetIncrementAsync"));
            Assert.AreEqual(100, service.GetRequestBudget("UpdateAsync"));
            Assert.AreEqual(100, service.GetRequestBudget("StandardRemove"));
            Assert.AreEqual(7, service.GetRequestBudget("StandardList"));
            Assert.AreEqual(0, service.GetRequestBudget("GetSortedAsync"), "ordered stores are not served");
            Assert.AreEqual(0, service.GetRequestBudget("OnUpdate"));
        }

        [Test]
        public void S1_7_UpdateAsync_TakesAReadAndAWrite_AndACancelledWriteGivesTheWriteBack()
        {
            RbxDataStoreService service = Service(Backend());
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            RbxDataStoreRequest update = service.Submit(Request(RbxDataStoreOperation.Update, store, "k"));
            Assert.AreEqual(RbxDataStoreRequestState.Granted, update.State);
            Assert.AreEqual(99, service.GetRequestBudget("GetAsync"));
            Assert.AreEqual(99, service.GetRequestBudget("SetIncrementAsync"));
            Assert.IsNull(service.ReadForUpdate(update));

            Assert.IsNull(service.CommitUpdate(update, false, null, null, null));

            Assert.AreEqual(100, service.GetRequestBudget("SetIncrementAsync"), "S1.8: nil aborts and writes nothing");
            Assert.IsNull(service.Database.Read("Coins", "global", "k"));
            Assert.Throws<RbxError>(() => service.CommitUpdate(update, true, "1", null, null), "a ticket commits once");
        }

        [Test]
        public void S1_12_Increment_AddsToAnInteger_TreatsAMissingKeyAsZero_AndRefusesAnything_Else()
        {
            RbxDataStoreService service = Service(Backend());
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            RbxDataStoreRequest first = service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Increment, store, "n", "m")
                { Delta = 5 });
            RbxDataStoreRequest second = service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Increment, store, "n", "m")
                { Delta = -2 });
            service.Submit(Set(store, "text", "\"abc\""));
            RbxDataStoreRequest refused = service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Increment, store,
                "text", "m"));

            Assert.AreEqual("5", first.Result.ValueJson);
            Assert.AreEqual("3", second.Result.ValueJson);
            Assert.AreEqual(RbxDataStoreRequestState.Failed, refused.State);
            StringAssert.Contains("its value is not an integer", refused.Error.RawMessage);
            Assert.AreEqual("\"abc\"", service.Database.Read("Coins", "global", "text").ValueJson);
        }

        [Test]
        public void S1_19_EveryWrite_MovesTheVersionAndTheUpdatedTime_AndKeepsTheCreatedTime()
        {
            RbxDataStoreService service = Service(Backend());
            RbxGlobalDataStore store = service.GetDataStore("Coins");

            RbxDataStoreEntry first = service.Submit(Set(store, "k", "1")).Result;
            _wallNow += 5000L;
            RbxDataStoreEntry second = service.Submit(Set(store, "k", "2")).Result;
            RbxDataStoreKeyInfo info = new(ClassCatalog.CreateMvp1(), second);

            Assert.AreEqual(1L, first.Version);
            Assert.AreEqual(2L, second.Version);
            Assert.AreEqual(first.CreatedMilliseconds, second.CreatedMilliseconds);
            Assert.AreEqual(first.UpdatedMilliseconds + 5000L, second.UpdatedMilliseconds);
            Assert.AreEqual("2", info.Version);
            Assert.AreEqual("DataStoreKeyInfo", info.ClassName);
        }

        [Test]
        public void DataWrittenUnderOneStoreName_IsInvisibleUnderAnother_AndUnderAnotherScope()
        {
            RbxDataStoreService service = Service(Backend());
            service.Submit(Set(service.GetDataStore("Coins"), "Player_1", "50"));

            Assert.AreEqual("50", service.Database.Read("Coins", "global", "Player_1").ValueJson);
            Assert.IsNull(service.Submit(Request(RbxDataStoreOperation.Get, service.GetDataStore("Gems"), "Player_1")).Result);
            Assert.IsNull(service.Submit(Request(RbxDataStoreOperation.Get, service.GetDataStore("Coins", "gold"), "Player_1"))
                .Result);
        }

        [Test]
        public void TheNamespace_SeparatesGamesAndWorldsWithAnId_AndWorldsWithoutAnIdShareTheGamesData()
        {
            InMemoryRbxDataStorePersistence storage = new();
            RbxDataStoreBackend game = Backend(null, storage, "block-world-demo");
            RbxDataStoreBackend otherGame = Backend(null, storage, "");
            RbxDataStoreService defaultWorld = Service(game, "");
            RbxDataStoreService restoredDefault = Service(game, "");
            RbxDataStoreService namedWorld = Service(game, "survival-2");
            RbxDataStoreService otherGameWorld = Service(otherGame, "");

            defaultWorld.Submit(Set(defaultWorld.GetDataStore("Coins"), "Player_1", "7"));

            Assert.AreEqual("7", Read(restoredDefault, "Coins", "Player_1"), "a world saved from the default world shares its data");
            Assert.IsNull(Read(namedWorld, "Coins", "Player_1"), "a world with its own id keeps its own data");
            Assert.IsNull(Read(otherGameWorld, "Coins", "Player_1"), "another composition is another game");
            Assert.AreEqual("block-world-demo", defaultWorld.Namespace);
            Assert.AreNotEqual(defaultWorld.Namespace, namedWorld.Namespace);
        }

        [Test]
        public void TheHost_ReplacesTheNamespaceRule()
        {
            RbxDataStoreOptions options = new() { Namespace = (gameNamespace, worldId) => "slot-3" };
            RbxDataStoreBackend backend = Backend(options);

            Assert.AreEqual("slot-3", Service(backend, "any-world").Namespace);
            Assert.AreEqual("slot-3", Service(backend, "").Namespace);
        }

        [Test]
        public void ARestart_ReadsWhatWasWritten_AfterThePendingWritesWereFlushed()
        {
            InMemoryRbxDataStorePersistence storage = new();
            RbxDataStoreBackend first = Backend(null, storage);
            RbxDataStoreService before = Service(first);
            before.Submit(Set(before.GetDataStore("PlayerData"), "Player_1", "{\"coins\":12}"));
            Assert.AreEqual(0, storage.SaveCount, "writes are coalesced, not saved at once");
            first.Dispose();

            RbxDataStoreBackend second = Backend(null, storage);
            RbxDataStoreService after = Service(second);

            Assert.AreEqual(1, storage.SaveCount);
            Assert.AreEqual("{\"coins\":12}", Read(after, "PlayerData", "Player_1"));
            Assert.AreEqual(1L, after.Database.Read("PlayerData", "global", "Player_1").Version);
        }

        [Test]
        public void Writes_AreCoalesced_UntilTheDelay_AndAtMostTheFrameLimitIsWrittenAtOnce()
        {
            InMemoryRbxDataStorePersistence storage = new();
            RbxDataStoreOptions options = new() { WriteDelaySeconds = 1d, MaxFilesWrittenPerFrame = 1 };
            RbxDataStoreService service = Service(Backend(options, storage));
            for (int index = 0; index < 5; index++)
            {
                service.Submit(Set(service.GetDataStore("A"), "k", index.ToString()));
            }

            service.Submit(Set(service.GetDataStore("B"), "k", "1"));
            service.FlushDue();
            Assert.AreEqual(0, storage.SaveCount, "the delay has not passed");

            _now += 1d;
            service.FlushDue();
            Assert.AreEqual(1, storage.SaveCount, "one file a frame");
            service.FlushDue();
            Assert.AreEqual(2, storage.SaveCount);
            service.FlushDue();
            Assert.AreEqual(2, storage.SaveCount, "nothing is left to write");
            Assert.AreEqual("4", storage.Peek("", "A", "global").Entries[0].ValueJson, "five writes became one");
        }

        [Test]
        public void AnUnreadableStore_FailsItsFirstRequestWith501_IsReported_AndThenWorksEmpty()
        {
            InMemoryRbxDataStorePersistence storage = new();
            storage.Corrupt("", "PlayerData", "global", "PlayerData.json could not be read (bad JSON)");
            List<string> reports = new();
            RbxDataStoreBackend backend = new(storage, "", null, () => _now, () => _wallNow, reports.Add);
            RbxDataStoreService service = Service(backend);
            RbxGlobalDataStore store = service.GetDataStore("PlayerData");

            RbxDataStoreRequest first = service.Submit(Request(RbxDataStoreOperation.Get, store, "Player_1"));
            RbxDataStoreRequest second = service.Submit(Request(RbxDataStoreOperation.Get, store, "Player_1"));
            RbxDataStoreRequest write = service.Submit(Set(store, "Player_1", "1"));

            Assert.AreEqual(RbxDataStoreRequestState.Failed, first.State);
            StringAssert.StartsWith("501: Can't parse response because data might be corrupted.", first.Error.RawMessage);
            Assert.AreEqual(RbxDataStoreRequestState.Done, second.State);
            Assert.IsNull(second.Result);
            Assert.AreEqual(RbxDataStoreRequestState.Done, write.State);
            Assert.AreEqual(1, reports.Count);
            StringAssert.Contains("bad JSON", reports[0]);
            Assert.AreEqual(1, backend.Problems.Count);
        }

        [Test]
        public void AFailingDisk_RefusesLaterWritesWith502_KeepsTheData_AndRecoversOnRetry()
        {
            InMemoryRbxDataStorePersistence storage = new() { FailSavesWith = "disk full" };
            RbxDataStoreOptions options = new() { WriteDelaySeconds = 0d, RetryDelaySeconds = 5d };
            RbxDataStoreService service = Service(Backend(options, storage));
            RbxGlobalDataStore store = service.GetDataStore("Coins");
            service.Submit(Set(store, "a", "1"));
            service.FlushDue();

            RbxDataStoreRequest refused = service.Submit(Set(store, "b", "2"));
            Assert.AreEqual(RbxDataStoreRequestState.Failed, refused.State);
            StringAssert.StartsWith("502: API Services rejected the request with error:", refused.Error.RawMessage);
            StringAssert.Contains("disk full", refused.Error.RawMessage);
            Assert.AreEqual("1", Read(service, "Coins", "a"), "the data written so far stays readable");

            storage.FailSavesWith = null;
            _now += 5d;
            service.FlushDue();

            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(store, "b", "2")).State);
            Assert.AreEqual("1", storage.Peek("", "Coins", "global").Entries[0].ValueJson);
        }

        [Test]
        public void TheWorldsStorageCaps_RefuseTheWriteThatPassesThem_AndAcceptTheOneThatFits()
        {
            RbxDataStoreOptions options = new() { MaxKeysPerWorld = 2, MaxStoresPerWorld = 2, MaxStoredCharactersPerWorld = 400 };
            RbxDataStoreService service = Service(Backend(options));

            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(service.GetDataStore("A"), "k1", "1")).State);
            Assert.AreEqual(RbxDataStoreRequestState.Done, service.Submit(Set(service.GetDataStore("B"), "k1", "1")).State);
            RbxDataStoreRequest thirdStore = service.Submit(Set(service.GetDataStore("C"), "k1", "1"));
            RbxDataStoreRequest thirdKey = service.Submit(Set(service.GetDataStore("A"), "k2", "1"));
            RbxDataStoreRequest tooBig = service.Submit(Set(service.GetDataStore("A"), "k1", "\"" + new string('x', 400) + "\""));
            RbxDataStoreRequest overwrite = service.Submit(Set(service.GetDataStore("A"), "k1", "2"));

            Assert.AreEqual(RbxDataStoreRequestState.Failed, thirdStore.State);
            Assert.AreEqual(RbxDataStoreRequestState.Failed, thirdKey.State);
            Assert.AreEqual(RbxDataStoreRequestState.Failed, tooBig.State);
            StringAssert.Contains("at most 2 keys", thirdKey.Error.RawMessage);
            StringAssert.Contains("at most 400 characters", tooBig.Error.RawMessage);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, tooBig.Error.Code);
            Assert.AreEqual(RbxDataStoreRequestState.Done, overwrite.State, "an overwrite adds no key");
        }

        [Test]
        public void ClosingTheWorld_FailsWaitingRequestsWith401_AndFlushesEveryPendingWrite()
        {
            InMemoryRbxDataStorePersistence storage = new();
            RbxDataStoreOptions options = new() { ReadBase = 0, ReadPerPlayer = 0, WriteDelaySeconds = 60d };
            RbxDataStoreService service = Service(Backend(options, storage));
            RbxGlobalDataStore store = service.GetDataStore("Coins");
            service.Submit(Set(store, "a", "1"));
            RbxDataStoreRequest waiting = service.Submit(Request(RbxDataStoreOperation.Get, store, "a"));

            service.Close();

            Assert.AreEqual(RbxDataStoreRequestState.Failed, waiting.State);
            StringAssert.StartsWith("401: Request failed.", waiting.Error.RawMessage);
            Assert.AreEqual(1, storage.SaveCount, "closing writes what was pending");
            Assert.Throws<RbxError>(() => service.Submit(Request(RbxDataStoreOperation.Get, store, "a")));
        }

        [Test]
        public void AnIdleService_AllocatesNothingPerFrame()
        {
            RbxDataStoreService service = Service(Backend());
            service.Submit(Set(service.GetDataStore("Coins"), "k", "1"));
            _now += 10d;
            service.FlushDue();

            AllocationMeter.AssertWithinBudget("idle DataStoreService frame", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    service.Pump();
                    service.FlushDue();
                }
            }, 1000, 0);
        }

        [Test]
        public void AnIdleService_AllocationTwin_BreaksTheSameBudget()
        {
            RbxDataStoreService service = Service(Backend());

            AllocationMeter.AssertTwinBreaksBudget("idle DataStoreService frame", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    service.Pump();
                    service.FlushDue();
                }
            }, 1000, 0);
        }

        private RbxDataStoreBackend Backend(RbxDataStoreOptions options = null,
            InMemoryRbxDataStorePersistence storage = null, string gameNamespace = "")
        {
            return new RbxDataStoreBackend(storage ?? new InMemoryRbxDataStorePersistence(), gameNamespace, options,
                () => _now, () => _wallNow);
        }

        private static RbxDataStoreService Service(RbxDataStoreBackend backend, string worldId = "")
        {
            return new RbxDataStoreService(ClassCatalog.CreateMvp1(), backend, worldId);
        }

        private static RbxDataStoreRequest Request(RbxDataStoreOperation operation, RbxGlobalDataStore store, string key,
            string modId = "m")
        {
            return new RbxDataStoreRequest(operation, store, key, modId);
        }

        private static RbxDataStoreRequest Set(RbxGlobalDataStore store, string key, string json)
        {
            return new RbxDataStoreRequest(RbxDataStoreOperation.Set, store, key, "m") { ValueJson = json };
        }

        private static string Read(RbxDataStoreService service, string name, string key)
        {
            return service.Database.Read(name, "global", key)?.ValueJson;
        }
    }
}

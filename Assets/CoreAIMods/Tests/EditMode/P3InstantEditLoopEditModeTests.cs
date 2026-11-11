using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// P3, the instant edit loop (Docs/CoreAIMods/ROBLOX_API_ROADMAP.md): the write-behind pre-mutation
    /// backup, its crash report, the configurable rate limit on AI world changes and the debounced record
    /// of the world that opens on the next start. Engine-free; linked into the portable Lua-tier runner.
    /// </summary>
    [TestFixture]
    public sealed class P3InstantEditLoopEditModeTests
    {
        private readonly List<string> _directories = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            foreach (string directory in _directories)
            {
                P3Fixtures.DeleteDirectory(directory);
            }

            _directories.Clear();
        }

        // ==================== Write-behind gate ====================

        [Test]
        public async Task WriteBehind_TheMutationRunsBeforeItsBackupIsWritten()
        {
            P3MemoryPackageStore store = new() { HoldNextCommit = true };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload("Before")), store, true);
            P3RecordingBindings bindings = new(() => store.Log.Add("mutation"));
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("new-tree", bindings.TreeState, "the change is visible when the tool returns");
            Assert.IsTrue(gate.WritesBehind);
            Assert.IsTrue(gate.HasPendingBackup, "the durable write is still in flight behind the mutation");
            CollectionAssert.AreEqual(
                new[] { "prepare:execute_lua", "mutation", "commit:execute_lua" },
                store.Log,
                "capture and encode in front, the mutation, then the write behind it");

            store.ReleaseHeldCommit();
            RbxWorldPackageWriteResult written = await gate.WaitForPendingBackupAsync();

            Assert.IsTrue(written.Success, written.Error);
            Assert.IsFalse(gate.HasPendingBackup);
            Assert.AreEqual(1, store.CommittedPackages.Count);
            Assert.IsNotNull(FindFolder(RbxWorldPackageSerializer.ReadPackage(store.CommittedPackages[0]), "Before"),
                "the autosave holds the world as it was before the mutation");
        }

        [Test]
        public async Task WriteBehind_ASecondMutationWaitsOnlyForThePendingWrite()
        {
            P3MemoryPackageStore store = new() { HoldNextCommit = true };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            P3RecordingBindings bindings = new(() => store.Log.Add("mutation"));
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult first = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);
            Task<LuaTool.LuaResult> second = executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsFalse(second.IsCompleted, "the second mutation waits while the first write is pending");
            Assert.AreEqual(1, bindings.Mutations);
            Assert.AreEqual(1, CountEntries(store.Log, "prepare:"), "nothing of the second call ran yet");

            store.ReleaseHeldCommit();
            LuaTool.LuaResult secondResult = await second;
            await gate.WaitForPendingBackupAsync();

            Assert.IsTrue(secondResult.Success, secondResult.Error);
            CollectionAssert.AreEqual(
                new[]
                {
                    "prepare:execute_lua", "mutation", "commit:execute_lua", "commit-done:execute_lua",
                    "prepare:execute_lua", "mutation", "commit:execute_lua", "commit-done:execute_lua"
                },
                store.Log);
        }

        [Test]
        public async Task WriteBehind_AFailedWrite_MakesTheNextMutationBackUpDurablyFirst_AndSaysSo()
        {
            P3MemoryPackageStore store = new() { FailCommits = true };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LuaModsLlmTool tool = CreateModsTool(runtime, "behind-failure-actor", gate, null);

            JObject first = JObject.Parse(await tool.ExecuteAsync("load", "mod-a", "local a = 1"));
            RbxWorldPackageWriteResult lost = await gate.WaitForPendingBackupAsync();
            store.FailCommits = false;
            JObject second = JObject.Parse(await tool.ExecuteAsync("load", "mod-b", "local b = 1"));
            JObject third = JObject.Parse(await tool.ExecuteAsync("load", "mod-c", "local c = 1"));
            await gate.WaitForPendingBackupAsync();

            Assert.IsTrue(first.Value<bool>("success"), first.ToString());
            Assert.IsFalse(lost.Success);
            StringAssert.Contains("Injected write-behind durability refusal", gate.LastBackupFailure == ""
                ? lost.Error
                : gate.LastBackupFailure);
            Assert.IsTrue(second.Value<bool>("success"), second.ToString());
            StringAssert.Contains("could not be written behind it",
                second.Value<string>(ConfirmedWorldMutationGate.BackupNoteField));
            Assert.IsNull(third[ConfirmedWorldMutationGate.BackupNoteField], "said once, by the next call");
            CollectionAssert.AreEqual(
                new[]
                {
                    "prepare:manage_mods-load", "commit:manage_mods-load", "commit-done:manage_mods-load",
                    "durable:manage_mods-load",
                    "prepare:manage_mods-load", "commit:manage_mods-load", "commit-done:manage_mods-load"
                },
                store.Log,
                "after a lost write the next backup is durable before its mutation, then write-behind resumes");
            Assert.AreEqual("", gate.LastBackupFailure, "a later backup succeeded");
        }

        [Test]
        public async Task WriteBehind_AFailedWriteAndAFailedDurableRetry_RefusesTheNextMutation()
        {
            P3MemoryPackageStore store = new() { FailCommits = true, FailDurableAutoSaves = true };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult first = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);
            LuaTool.LuaResult second = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsFalse(second.Success, second.Output);
            StringAssert.Contains("Confirmed pre-mutation backup 'execute_lua' failed", second.Error);
            Assert.AreEqual(1, bindings.Mutations, "a durability failure that persists keeps refusing, as before");
        }

        [Test]
        public async Task WriteBehind_APayloadThatCannotBeEncoded_RefusesExecuteLua_AndSaysWhatToDo()
        {
            const string refusal = "World package entry 'world.json' is 19555897 bytes; limit is 16777216.";
            P3MemoryPackageStore store = new() { EncodingRefusal = refusal };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsFalse(result.Success, result.Output);
            StringAssert.Contains("Confirmed pre-mutation backup 'execute_lua' failed", result.Error);
            StringAssert.Contains("world.json", result.Error);
            StringAssert.Contains("this change was NOT made", result.Error);
            StringAssert.Contains("undo the last AI change", result.Error);
            Assert.AreEqual(0, bindings.Mutations);
            Assert.AreEqual(0, CountEntries(store.Log, "commit:"), "nothing is written for a refused mutation");
        }

        [Test]
        public async Task WriteBehind_APayloadThatCannotBeEncoded_StillRunsForget_WithItsNote()
        {
            const string refusal = "World package entry 'world.json' is 19555897 bytes; limit is 16777216.";
            P3MemoryPackageStore store = new() { EncodingRefusal = refusal };
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            P3MemorySourceStore sources = new();
            LuaCsModRuntime runtime = new(sourceStore: sources, versionStore: new MemoryLuaScriptVersionStore());
            sources.Save("stale-mod", "local stale = 1", new LuaModManifest
            {
                Id = "stale-mod",
                Name = "stale-mod",
                OwnerActorId = "forget-actor",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = false
            });
            LuaModsLlmTool tool = CreateModsTool(runtime, "forget-actor", gate, null);

            JObject forget = JObject.Parse(await tool.ExecuteAsync("forget", "stale-mod"));
            JObject load = JObject.Parse(await tool.ExecuteAsync("load", "new-mod", "local value = 1"));

            Assert.IsTrue(forget.Value<bool>("success"), forget.ToString());
            StringAssert.Contains("No pre-mutation backup was written for '" + LuaModsLlmTool.ForgetBackupTrigger + "'",
                forget.Value<string>(ConfirmedWorldMutationGate.BackupNoteField));
            Assert.IsFalse(sources.TryLoad("stale-mod", out _, out _));
            Assert.IsFalse(load.Value<bool>("success"), "every other mutation stays refused: " + load);
        }

        [Test]
        public async Task WriteBehind_ACaptureThatHitsAFormatLimit_IsRefusedForExecuteLua_AsBefore()
        {
            P3MemoryPackageStore store = new();
            ConfirmedWorldMutationGate gate = new(
                _ => throw new RbxWorldPackageFormatLimitException("Injected format limit."), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsFalse(result.Success, result.Output);
            StringAssert.Contains("Injected format limit", result.Error);
            Assert.AreEqual(0, bindings.Mutations);
            CollectionAssert.IsEmpty(store.Log);
        }

        [Test]
        public async Task WriteBehind_AStoreWithoutTheTwoPhaseFace_KeepsTheDurableFirstOrder()
        {
            List<string> sequence = new();
            DurableOnlyStore store = new(trigger => sequence.Add("durable:" + trigger));
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload()), store, true);
            P3RecordingBindings bindings = new(() => sequence.Add("mutation"));
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.IsTrue(gate.WriteBehindBackups);
            Assert.IsFalse(gate.WritesBehind, "write-behind needs the store's two-phase face");
            CollectionAssert.AreEqual(new[] { "durable:execute_lua", "mutation" }, sequence);
        }

        [Test]
        public async Task WriteBehind_ObserversGetTheActorTheSnapshotAndTheBackupCost()
        {
            P3MemoryPackageStore store = new();
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload("Seen")), store, true);
            RecordingObserver observer = new();
            gate.AddObserver(observer);
            gate.AddMetricsObserver(observer);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LuaModsLlmTool tool = CreateModsTool(runtime, "observed-actor", gate, null);

            JObject loaded = JObject.Parse(await tool.ExecuteAsync("load", "observed-mod", "local value = 1"));
            await gate.WaitForPendingBackupAsync();

            Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            CollectionAssert.AreEqual(new[] { "start:manage_mods-load:observed-actor", "finish:manage_mods-load:True" },
                observer.Events);
            Assert.IsNotNull(observer.LastBefore);
            Assert.IsTrue(observer.LastBefore.HoldsEncodedPackage, "a write-behind snapshot keeps the compact package");
            Assert.IsNotNull(FindFolder(observer.LastBefore.ToPayload(), "Seen"));
            Assert.AreEqual(1, observer.Metrics.Count);
            RbxWorldBackupMetrics metrics = observer.Metrics[0];
            Assert.IsTrue(metrics.Succeeded, metrics.Error);
            Assert.IsTrue(metrics.WrittenBehind);
            Assert.AreEqual(store.CommittedPackages[0].LongLength, metrics.PackageBytes);
            Assert.GreaterOrEqual(metrics.CaptureMilliseconds, 0d);
            Assert.AreEqual(metrics.CaptureMilliseconds + metrics.EncodeMilliseconds,
                metrics.MillisecondsInFrontOfTheMutation, 1e-9);
            Assert.AreSame(metrics, gate.LatestBackupMetrics);
        }

        [Test]
        public async Task WriteBehind_AConfirmedWorldLoad_KeepsItsSafetyAutosaveDurableFirst()
        {
            P3MemoryPackageStore store = new();
            using P3HeadlessSession session = new(store);

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(P3Fixtures.CapturePayload("Loaded"));

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.IsTrue(session.Gate.WritesBehind);
            CollectionAssert.AreEqual(new[] { "durable:" + RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger },
                store.Log, "the outgoing world is on disk before a player-confirmed load replaces it");
        }

        // ==================== File store: two-phase autosave and crash report ====================

        [Test]
        public async Task FileStore_PreparedAndCommittedAutoSave_HoldsExactlyTheEncodedPreMutationWorld()
        {
            string root = NewDirectory();
            FileRbxWorldPackageStore store = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => P3Fixtures.CapturedAtUtc);
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Golden");

            RbxPreparedAutoSave prepared = await store.PrepareAutoAsync("execute_lua", payload);
            Assert.IsTrue(prepared.Success, prepared.Error);
            CollectionAssert.IsEmpty(store.ListAutoFiles(), "preparing touches no storage");
            RbxWorldPackageWriteResult written = await store.CommitAutoAsync(prepared);

            Assert.IsTrue(written.Success, written.Error);
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(1, autosaves.Count);
            Assert.AreEqual("20261111T093000000Z-0000-execute_lua.world", autosaves[0].FileName);
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(payload),
                File.ReadAllBytes(Path.Combine(root, "Auto", autosaves[0].FileName)));
            CollectionAssert.IsEmpty(Directory.GetFiles(Path.Combine(root, "Auto"), "*.pending"),
                "a finished write leaves no pending marker");
        }

        /// <summary>
        /// DoD (P3): a crash between the mutation and its write is reported at the next start as one missing
        /// backup, never as a corrupt ring. The crash is a write that stops half-way and never returns.
        /// </summary>
        [Test]
        public async Task FileStore_ACrashBetweenMutationAndWrite_IsOneMissingBackupAtTheNextStart_AndTheRingIsIntact()
        {
            string root = NewDirectory();
            FileRbxWorldPackageStore firstRun = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => P3Fixtures.CapturedAtUtc);
            RbxWorldPackageWriteResult earlier = await firstRun.CreateAutoAsync("earlier", P3Fixtures.CapturePayload("Earlier"));
            Assert.IsTrue(earlier.Success, earlier.Error);
            FileRbxWorldPackageStore crashingRun = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => P3Fixtures.CapturedAtUtc.AddSeconds(5d), fileSystem: new P3CrashingFileSystem());
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(P3Fixtures.CapturePayload("Before")), crashingRun, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("new-tree", bindings.TreeState, "the mutation ran");
            Assert.IsTrue(gate.HasPendingBackup, "precondition: the process 'dies' with the write unfinished");

            FileRbxWorldPackageStore nextStart = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => P3Fixtures.CapturedAtUtc.AddMinutes(1d));

            Assert.AreEqual(1, nextStart.MissingAutoSavesAtOpen.Count);
            Assert.AreEqual(LuaCsGameToolExecutor.ExecuteLuaBackupTrigger, nextStart.MissingAutoSavesAtOpen[0].Trigger);
            Assert.AreEqual(P3Fixtures.CapturedAtUtc.AddSeconds(5d), nextStart.MissingAutoSavesAtOpen[0].TimestampUtc);
            StringAssert.Contains("One autosave was still being written when the app stopped",
                RbxMissingAutoSave.Describe(nextStart.MissingAutoSavesAtOpen));
            CollectionAssert.AreEqual(new[] { Path.GetFileName(earlier.Path) }, nextStart.ListAutoFiles(),
                "the ring holds only finished autosaves");
            Assert.IsNotNull(FindFolder(
                RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(earlier.Path)), "Earlier"),
                "the earlier autosave still reads as a valid package");
            CollectionAssert.IsEmpty(Directory.GetFiles(Path.Combine(root, "Auto"), "*.tmp"),
                "the half-written temporary file was swept");

            nextStart.AcknowledgeMissingAutoSaves();
            FileRbxWorldPackageStore startAfterThat = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true));

            CollectionAssert.IsEmpty(startAfterThat.MissingAutoSavesAtOpen, "a reported missing backup is reported once");
        }

        [Test]
        public void FileStore_AnUnacknowledgedMissingBackup_IsReportedAgainAtTheFollowingStart()
        {
            string root = NewDirectory();
            string auto = Path.Combine(root, "Auto");
            Directory.CreateDirectory(auto);
            File.WriteAllBytes(Path.Combine(auto, "20261111T093005000Z-execute_lua." + Guid.NewGuid().ToString("N") + ".pending"),
                Array.Empty<byte>());

            FileRbxWorldPackageStore toolContainer = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true));
            FileRbxWorldPackageStore game = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true));

            Assert.AreEqual(1, toolContainer.MissingAutoSavesAtOpen.Count);
            Assert.AreEqual(1, game.MissingAutoSavesAtOpen.Count,
                "a store nobody acknowledged (a test or tool container) cannot swallow the game's report");
        }

        [Test]
        public void FileStore_AMarkerWhoseAutoSaveWasInstalled_IsRemovedWithoutAReport()
        {
            string root = NewDirectory();
            string auto = Path.Combine(root, "Auto");
            Directory.CreateDirectory(auto);
            File.WriteAllBytes(Path.Combine(auto, "20261111T093005000Z-0000-execute_lua.world"),
                RbxWorldPackageSerializer.WritePackage(P3Fixtures.CapturePayload()));
            string marker = Path.Combine(auto, "20261111T093005000Z-execute_lua." + Guid.NewGuid().ToString("N") + ".pending");
            File.WriteAllBytes(marker, Array.Empty<byte>());

            FileRbxWorldPackageStore store = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true));

            CollectionAssert.IsEmpty(store.MissingAutoSavesAtOpen, "only the marker's removal was lost");
            Assert.IsFalse(File.Exists(marker));
        }

        [TestCase("20261111T093005000Z-execute_lua.0123456789abcdef0123456789abcdef.pending", true)]
        [TestCase("20261111T093005000Z-manage_mods-load.0123456789abcdef0123456789abcdef.pending", true)]
        [TestCase("20261111T093005000Z-execute_lua.pending", false)]
        [TestCase("not-a-stamp-execute_lua.0123456789abcdef0123456789abcdef.pending", false)]
        [TestCase("20261111T093005000Z-execute_lua.0123456789ABCDEF0123456789abcdef.pending", false)]
        public void FileStore_PendingMarkerNames_AreParsedExactly(string fileName, bool expected)
        {
            Assert.AreEqual(expected, FileRbxWorldPackageStore.TryParsePendingMarker(fileName, out _, out _));
        }

        // ==================== Rate limit ====================

        [Test]
        public void RateLimit_ThreePerTenSeconds_TheFourthIsRefusedWithTheResetTime()
        {
            double now = 100d;
            LuaGenerationRateLimiter limiter = new(3, 10d, () => now);

            Assert.IsTrue(limiter.TryAcquire(out _));
            now = 102d;
            Assert.IsTrue(limiter.TryAcquire(out _));
            Assert.IsTrue(limiter.TryAcquire(out LuaRateLimitBudget third));
            Assert.AreEqual(0, third.Remaining);
            bool fourth = limiter.TryAcquire(out LuaRateLimitBudget refused);

            Assert.IsFalse(fourth);
            Assert.AreEqual(8d, refused.SecondsUntilNextSlot, 1e-9, "the first call leaves the window at t=110");
            StringAssert.Contains("next one is accepted in 8.0 s", refused.Describe());
            now = 110d;
            Assert.IsTrue(limiter.TryAcquire(out _), "a slot frees when the oldest call leaves the window");
        }

        [Test]
        public void RateLimit_SwitchedOff_AHundredCallsPass()
        {
            LuaGenerationRateLimiter limiter = new(3, 10d, () => 0d);
            limiter.Reconfigure(false, 3, 10d);

            for (int index = 0; index < 100; index++)
            {
                Assert.IsTrue(limiter.TryAcquire(out LuaRateLimitBudget budget));
                Assert.IsFalse(budget.IsLimiting);
            }

            Assert.AreEqual(0, limiter.TotalRejected);
            StringAssert.Contains("not rate limited", limiter.GetBudget().Describe());
        }

        [Test]
        public void RateLimit_ComposedWithAClock_IgnoresEveryCallersOwnTime()
        {
            double now = 50d;
            LuaGenerationRateLimiter limiter = new(2, 10d, () => now);

            Assert.IsTrue(limiter.TryAcquire(0d));
            Assert.IsTrue(limiter.TryAcquire(123456d), "a caller on another stopwatch cannot open a fresh window");
            Assert.IsFalse(limiter.TryAcquire(out _));
            Assert.AreEqual(2, limiter.GetBudget().Used);
            now = 61d;
            Assert.AreEqual(2, limiter.GetBudget().Remaining);
        }

        [Test]
        public void RateLimit_Reconfigured_AppliesTheNewWindowAndCount()
        {
            double now = 0d;
            LuaGenerationRateLimiter limiter = new(1, 60d, () => now);
            Assert.IsTrue(limiter.TryAcquire(out _));
            Assert.IsFalse(limiter.TryAcquire(out _));

            limiter.Reconfigure(true, 2, 5d);

            Assert.IsTrue(limiter.TryAcquire(out _), "the count rose to two");
            Assert.IsFalse(limiter.TryAcquire(out _));
            now = 5d;
            Assert.AreEqual(2, limiter.GetBudget().Remaining, "the window shrank to five seconds");
        }

        [Test]
        public async Task ManageMods_MutatingActionsSpendTheSharedLimit_ReadOnlyActionsDoNot()
        {
            double now = 0d;
            LuaGenerationRateLimiter limiter = new(3, 10d, () => now);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LuaModsLlmTool tool = CreateModsTool(runtime, "limited-actor", null, limiter);

            for (int index = 0; index < 3; index++)
            {
                JObject loaded = JObject.Parse(await tool.ExecuteAsync("load", "mod-" + index, "local value = " + index));
                Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            }

            JObject listed = JObject.Parse(await tool.ExecuteAsync("list"));
            JObject refused = JObject.Parse(await tool.ExecuteAsync("load", "mod-3", "local value = 3"));

            Assert.IsTrue(listed.Value<bool>("success"), "read-only actions are never limited: " + listed);
            Assert.IsFalse(refused.Value<bool>("success"), refused.ToString());
            Assert.AreEqual("rate_limited", refused.Value<string>("status"));
            Assert.AreEqual(10d, refused.Value<double>("retry_after_seconds"), 1e-9);
            StringAssert.Contains("was NOT executed", refused.Value<string>("message"));
            Assert.IsFalse(runtime.IsLoaded(
                new LocalActorIdentityProvider("limited-actor").GetActorContext(BuiltInAgentRoleIds.Programmer), "mod-3"));
            Assert.AreSame(limiter, tool.RateLimiter);
        }

        [Test]
        public async Task ManageMods_WithTheLimitOff_AHundredMutationsPass_AndARefusalNeverReachesTheGate()
        {
            LuaGenerationRateLimiter limiter = new(1, 10d, () => 0d);
            int captures = 0;
            P3MemoryPackageStore store = new();
            ConfirmedWorldMutationGate gate = new(_ =>
            {
                captures++;
                return UniTask.FromResult(P3Fixtures.CapturePayload());
            }, store, true);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LuaModsLlmTool tool = CreateModsTool(runtime, "solo-actor", gate, limiter);

            JObject first = JObject.Parse(await tool.ExecuteAsync("load", "solo-0", "local value = 0"));
            JObject refused = JObject.Parse(await tool.ExecuteAsync("load", "solo-1", "local value = 1"));
            Assert.IsTrue(first.Value<bool>("success"), first.ToString());
            Assert.AreEqual("rate_limited", refused.Value<string>("status"));
            Assert.AreEqual(1, captures, "a refused call takes no snapshot and writes nothing");

            limiter.Reconfigure(false, 1, 10d);
            for (int index = 1; index <= 100; index++)
            {
                JObject loaded = JObject.Parse(await tool.ExecuteAsync("reload", "solo-0", "local value = " + index));
                Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            }

            await gate.WaitForPendingBackupAsync();
            Assert.AreEqual(101, captures);
        }

        // ==================== Debounced startup record ====================

        [Test]
        public async Task StartupRefresh_Debounced_ABurstOfAiEditsIsRecordedOnce_WhenItIsQuiet()
        {
            DateTime now = P3Fixtures.CapturedAtUtc;
            P3MemoryPackageStore store = new(P3Fixtures.CapturePayload("StartupWorld"));
            using P3HeadlessSession session = new(store);
            session.Controller.ConfigurePendingLoadClockForTests(() => now, TimeSpan.FromMinutes(2d));
            session.Controller.StartupRefreshDebounce = TimeSpan.FromSeconds(1d);
            ActorContext actor = new LocalActorIdentityProvider("burst-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
            RbxWorldStartupRestoreResult restored = await session.Controller.RestoreStartupSelectionAsync();
            Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);

            for (int index = 0; index < 3; index++)
            {
                LuaTool.LuaResult built = await session.ExecuteAsAsync(actor,
                    "local f = Instance.new('Folder') f.Name = 'Burst" + index + "' f.Parent = workspace return true");
                Assert.IsTrue(built.Success, built.Error);
            }

            Assert.AreEqual(0, store.SelectedPayloads.Count, "nothing is recorded inside the burst");
            now = now.AddSeconds(0.5d);
            session.Controller.PumpFrame(actor, 0.016f);
            await session.Controller.StartupRefreshForTests;
            Assert.AreEqual(0, store.SelectedPayloads.Count, "still inside the quiet time");

            now = now.AddSeconds(1d);
            session.Controller.PumpFrame(actor, 0.016f);
            await session.Controller.StartupRefreshForTests;

            Assert.AreEqual(1, store.SelectedPayloads.Count, "the burst is recorded once");
            for (int index = 0; index < 3; index++)
            {
                Assert.IsNotNull(FindFolder(store.SelectedPayloads[0], "Burst" + index));
            }

            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task StartupRefresh_WithoutDebounce_TheFirstAiEditIsRecordedInsideItsCall_AsBefore()
        {
            P3MemoryPackageStore store = new(P3Fixtures.CapturePayload("StartupWorld"));
            using P3HeadlessSession session = new(store);
            ActorContext actor = new LocalActorIdentityProvider("eager-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
            await session.Controller.RestoreStartupSelectionAsync();

            LuaTool.LuaResult built = await session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'Eager' f.Parent = workspace return true");

            Assert.IsTrue(built.Success, built.Error);
            Assert.AreEqual(TimeSpan.Zero, session.Controller.StartupRefreshDebounce, "the default");
            Assert.AreEqual(1, store.SelectedPayloads.Count);
            Assert.IsNotNull(FindFolder(store.SelectedPayloads[0], "Eager"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public void Session_ReportsMissingBackupsAtTheStartThatFindsThem_AndClearsThemOnItsFirstFrame()
        {
            P3MemoryPackageStore store = new();
            store.AddMissingAutoSave(new RbxMissingAutoSave("execute_lua", P3Fixtures.CapturedAtUtc, "marker.pending"));

            using P3HeadlessSession session = new(store);

            Assert.AreEqual(1, session.Diagnostics.Count, string.Join("\n", session.Diagnostics));
            StringAssert.Contains("One autosave was still being written when the app stopped ('execute_lua'",
                session.Diagnostics[0]);
            Assert.AreEqual(0, store.Acknowledged, "a composition that never runs a frame keeps the record");
            ActorContext host = new LocalActorIdentityProvider("frame-host").GetActorContext(BuiltInAgentRoleIds.Programmer);
            session.Controller.PumpFrame(host, 0.016f);
            session.Controller.PumpFrame(host, 0.016f);
            Assert.AreEqual(1, store.Acknowledged, "the first frame of the game clears it, once");
            Assert.AreEqual(1, session.Controller.MissingAutoSavesAtStart.Count);
            Assert.AreEqual(1, session.Gate.MissingBackupsAtStart.Count);
        }

        // ==================== Helpers ====================

        private string NewDirectory()
        {
            string directory = P3Fixtures.NewTemporaryDirectory();
            _directories.Add(directory);
            return directory;
        }

        private static LuaModsLlmTool CreateModsTool(
            LuaCsModRuntime runtime,
            string actorId,
            IConfirmedWorldMutationGate gate,
            LuaGenerationRateLimiter limiter)
        {
            return new LuaModsLlmTool(
                runtime,
                new CoreAISettingsOptions(),
                NullLog.Instance,
                LuaCapabilities.All,
                true,
                new LocalActorIdentityProvider(actorId),
                BuiltInAgentRoleIds.Programmer,
                gate,
                limiter);
        }

        private static int CountEntries(IEnumerable<string> log, string prefix)
        {
            int count = 0;
            foreach (string entry in log)
            {
                if (entry.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        internal static InstanceSnapshot FindFolder(RbxWorldPackagePayload payload, string name)
        {
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            return null;
        }

        private sealed class RecordingObserver : IRbxWorldMutationObserver, IRbxWorldBackupMetricsObserver
        {
            public List<string> Events { get; } = new();

            public List<RbxWorldBackupMetrics> Metrics { get; } = new();

            public RbxWorldSnapshot LastBefore { get; private set; }

            public void OnMutationStarting(RbxWorldMutationInfo mutation)
            {
                Events.Add("start:" + mutation.Trigger + ":" + mutation.ActorId);
            }

            public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
            {
                Events.Add("finish:" + mutation.Trigger + ":" + completed);
                LastBefore = before;
            }

            public void OnBackupMeasured(RbxWorldBackupMetrics metrics)
            {
                Metrics.Add(metrics);
            }
        }

        /// <summary>A store with only the durable-first face.</summary>
        private sealed class DurableOnlyStore : IRbxWorldPackageStore
        {
            private readonly Action<string> _onAutoSave;

            public DurableOnlyStore(Action<string> onAutoSave)
            {
                _onAutoSave = onAutoSave;
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                _onAutoSave(trigger);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", "not in this fixture"));
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot, CancellationToken cancellationToken = default)
            {
                throw new FileNotFoundException(slot);
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName, CancellationToken cancellationToken = default)
            {
                throw new FileNotFoundException(fileName);
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// P3 undo and redo of AI changes (<see cref="RbxWorldUndoService"/>) over the production session
    /// controller and the write-behind gate. Engine-free; linked into the portable Lua-tier runner.
    /// </summary>
    [TestFixture]
    public sealed class P3WorldUndoEditModeTests
    {
        private const string BuildBase =
            "local f = Instance.new('Folder') f.Name = 'Base' f.Parent = workspace "
            + "local p = Instance.new('Part') p.Name = 'Floor' p.Size = Vector3.new(16, 1, 16) "
            + "p.Position = Vector3.new(0, 0, 0) p.Anchored = true p.Parent = f return true";

        private const string BuildTower =
            "for i = 1, 3 do local p = Instance.new('Part') p.Name = 'Tower' .. i p.Size = Vector3.new(2, 2, 2) "
            + "p.Position = Vector3.new(0, i * 2, 0) p.Anchored = true p.Color = Color3.new(1, 0, 0) "
            + "p.Parent = workspace end return true";

        private static ActorContext Actor(string id)
        {
            return new LocalActorIdentityProvider(id).GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>DoD (P3): /undo after an AI build restores the previous tree byte-identically.</summary>
        [Test]
        public async Task Undo_AfterAnAiBuild_RestoresThePreviousWorldByteIdentically()
        {
            P3MemoryPackageStore store = new();
            using P3HeadlessSession session = new(store);
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildBase)).Success);
            byte[] beforeTower = P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent());

            LuaTool.LuaResult tower = await session.ExecuteAsAsync(builder, BuildTower);
            Assert.IsTrue(tower.Success, tower.Error);
            Assert.IsNotNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.AreEqual(2, status.UndoCount);
            Assert.IsTrue(status.CanUndo, status.Reason);

            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(RbxWorldUndoResult.UndoneStatus, undone.Status);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
            CollectionAssert.AreEqual(beforeTower, P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent()),
                "the restored world encodes to exactly the bytes it had before the AI change");
            Assert.AreEqual(1, undone.UndoCount);
            Assert.AreEqual(1, undone.RedoCount);
            await session.Gate.WaitForPendingBackupAsync();
            CollectionAssert.Contains(store.Log, "commit:" + RbxWorldUndoService.UndoTrigger,
                "the world the undo removed is itself autosaved");
        }

        /// <summary>DoD twin: after a player edit in between, the undo refuses with a reason and changes nothing.</summary>
        [Test]
        public async Task Undo_AfterAPlayerEditInBetween_IsRefusedWithTheReason()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;
            RbxInstance playerMade = live.Create("Folder");
            playerMade.Name = "PlayerMade";
            playerMade.Parent = live.WorldRoot;

            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            RbxWorldUndoResult refused = await session.Undo.UndoAsync(builder);

            Assert.IsFalse(status.CanUndo);
            Assert.IsFalse(refused.Success);
            Assert.AreEqual(RbxWorldUndoResult.WorldChangedStatus, refused.Status);
            StringAssert.Contains("a player, a mod script or the Hub", refused.Message);
            StringAssert.Contains("World page", refused.Message);
            Assert.AreSame(live, session.Controller.CurrentRbxApi.Registry, "the world was not replaced");
            Assert.IsNotNull(live.WorldRoot.FindFirstChild("Tower1"));
            Assert.IsNotNull(live.WorldRoot.FindFirstChild("PlayerMade"));
            Assert.AreEqual(1, refused.UndoCount, "the refused change stays in the history");
        }

        [Test]
        public async Task Undo_AfterAnotherAgentsChange_NamesThatAgent()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext first = Actor("first-agent");
            ActorContext second = Actor("second-agent");
            Assert.IsTrue((await session.ExecuteAsAsync(first, BuildBase)).Success);
            Assert.IsTrue((await session.ExecuteAsAsync(second, BuildTower)).Success);

            RbxWorldUndoResult refused = await session.Undo.UndoAsync(first);
            RbxWorldUndoResult allowed = await session.Undo.UndoAsync(second);

            Assert.AreEqual(RbxWorldUndoResult.WorldChangedStatus, refused.Status);
            StringAssert.Contains("Agent 'second-agent'", refused.Message);
            Assert.IsTrue(allowed.Success, allowed.Message);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
        }

        [Test]
        public async Task Undo_ConsecutiveUndosWalkBackTheChain_AndRedoPutsTheChangesBack()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            byte[] empty = P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent());
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildBase)).Success);
            byte[] withBase = P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent());
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            byte[] withTower = P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent());

            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success);
            CollectionAssert.AreEqual(withBase, P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent()));
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success, "the earlier change is next in the chain");
            CollectionAssert.AreEqual(empty, P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent()));
            RbxWorldUndoResult nothing = await session.Undo.UndoAsync(builder);
            Assert.AreEqual(RbxWorldUndoResult.NothingToUndoStatus, nothing.Status);

            Assert.IsTrue((await session.Undo.RedoAsync(builder)).Success);
            CollectionAssert.AreEqual(withBase, P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent()));
            RbxWorldUndoResult redone = await session.Undo.RedoAsync(builder);
            Assert.IsTrue(redone.Success, redone.Message);
            CollectionAssert.AreEqual(withTower, P3Fixtures.TimelessPackage(session.Controller.CaptureCurrent()));
            Assert.AreEqual(2, redone.UndoCount);
            Assert.AreEqual(0, redone.RedoCount);
            Assert.AreEqual(RbxWorldUndoResult.NothingToRedoStatus, (await session.Undo.RedoAsync(builder)).Status);
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_TheHistoryKeepsTheLastNChangesPerActor()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), undoCapacity: 2);
            ActorContext builder = Actor("builder");
            for (int index = 0; index < 3; index++)
            {
                LuaTool.LuaResult built = await session.ExecuteAsAsync(builder,
                    "local f = Instance.new('Folder') f.Name = 'Step" + index + "' f.Parent = workspace return true");
                Assert.IsTrue(built.Success, built.Error);
            }

            Assert.AreEqual(2, session.Undo.GetStatus(builder.ActorId).UndoCount, "the oldest change was dropped");
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success);
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success);
            Assert.AreEqual(RbxWorldUndoResult.NothingToUndoStatus, (await session.Undo.UndoAsync(builder)).Status);
            InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;
            Assert.IsNotNull(live.WorldRoot.FindFirstChild("Step0"), "the evicted change cannot be undone");
            Assert.IsNull(live.WorldRoot.FindFirstChild("Step1"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_AReadOnlyExecuteLua_LeavesTheHistoryAlone()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);

            LuaTool.LuaResult lookup = await session.ExecuteAsAsync(builder,
                "return workspace:FindFirstChild('Tower1') ~= nil");
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(lookup.Success, lookup.Error);
            Assert.IsTrue(undone.Success, "the lookup is not an AI change: " + undone.Message);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"),
                "undo took back the build, not the lookup");
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_ANewAiChange_ClearsTheRedoHistory()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success);
            Assert.AreEqual(1, session.Undo.GetStatus(builder.ActorId).RedoCount);

            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildBase)).Success);

            Assert.AreEqual(0, session.Undo.GetStatus(builder.ActorId).RedoCount);
            Assert.AreEqual(RbxWorldUndoResult.NothingToRedoStatus, (await session.Undo.RedoAsync(builder)).Status);
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_AModLoadedThroughManageMods_IsTakenBackWithItsSource()
        {
            P3MemorySourceStore sources = new();
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), sources);
            LocalActorIdentityProvider identity = new("mod-builder");
            ActorContext builder = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaModsLlmTool tool = new(session.Controller.Runtime, new CoreAISettingsOptions(), NullLog.Instance,
                P3Fixtures.SessionCapabilities, true, identity, BuiltInAgentRoleIds.Programmer, session.Gate);

            JObject loaded = JObject.Parse(await tool.ExecuteAsync("load", "undo-mod", "local value = 1"));
            Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            Assert.IsTrue(session.Controller.SourceStore.TryLoad("undo-mod", out _, out _));

            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsFalse(session.Controller.SourceStore.TryLoad("undo-mod", out _, out _),
                "the restored world holds the source set it had before the load");
            Assert.IsFalse(session.Controller.Runtime.IsLoaded(builder, "undo-mod"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_OfTheStartupWorld_RecordsTheRestoredWorldForTheNextStart()
        {
            P3MemoryPackageStore store = new(P3Fixtures.CapturePayload("StartupWorld"));
            using P3HeadlessSession session = new(store);
            ActorContext builder = Actor("builder");
            await session.Controller.RestoreStartupSelectionAsync();
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            int recordedBeforeUndo = store.SelectedPayloads.Count;

            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(undone.Success, undone.Message);
            Assert.Greater(store.SelectedPayloads.Count, recordedBeforeUndo, "the undone world is recorded");
            RbxWorldPackagePayload latest = store.SelectedPayloads[store.SelectedPayloads.Count - 1];
            Assert.IsNull(P3InstantEditLoopEditModeTests.FindFolder(latest, "Tower1"));
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(latest, "StartupWorld"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_AnUntrustedCaller_IsRefusedBeforeTheGate()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());

            RbxWorldUndoResult refused = await session.Undo.UndoAsync(default);

            Assert.AreEqual(RbxWorldUndoResult.UntrustedStatus, refused.Status);
        }

        /// <summary>The service is replaceable around its two seams: any observable gate and any host.</summary>
        [Test]
        public async Task Undo_OverAFakeGateAndAFakeHost_UsesOnlyTheirContracts()
        {
            FakeUndoHost host = new();
            FakeObservableGate gate = new();
            using RbxWorldUndoService service = new(gate, host, 3);
            RbxWorldPackagePayload before = P3Fixtures.CapturePayload("BeforeFake");
            RbxWorldSnapshot snapshot = new("execute_lua", P3Fixtures.CapturedAtUtc, before, null);

            await gate.RunAsync("execute_lua", "fake-actor", snapshot, () => host.SourceChanges++);
            Assert.AreEqual(1, service.GetStatus("fake-actor").UndoCount);
            host.SourceChanges++;
            RbxWorldUndoResult refused = await service.UndoAsync(Actor("fake-actor"));
            host.SourceChanges--;
            RbxWorldUndoResult undone = await service.UndoAsync(Actor("fake-actor"));

            Assert.AreEqual(RbxWorldUndoResult.WorldChangedStatus, refused.Status);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreSame(before, host.Restored, "the host is handed the snapshot's payload");
            CollectionAssert.AreEqual(new[] { "execute_lua:fake-actor", RbxWorldUndoService.UndoTrigger + ":fake-actor" },
                gate.Runs);
            Assert.IsTrue(service.IsObservingGate);
        }

        [Test]
        public async Task Undo_OverAGateThatCannotBeObserved_HasNothingToUndo()
        {
            using RbxWorldUndoService service = new(new PlainGate(), new FakeUndoHost());

            RbxWorldUndoResult result = await service.UndoAsync(Actor("plain"));

            Assert.IsFalse(service.IsObservingGate);
            Assert.AreEqual(RbxWorldUndoResult.NothingToUndoStatus, result.Status);
        }

        [Test]
        public void StateMark_ChangesWithAScriptedEdit_AndIgnoresAModsOwnInstances()
        {
            InstanceRegistry registry = new(worldId: P3Fixtures.WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxWorldStateMark start = RbxWorldStateMark.Of(registry, 0);
            RbxInstance modOwned = registry.Create("Folder", ownerModId: "some-mod");
            modOwned.Parent = registry.WorldRoot;
            RbxWorldStateMark afterModEdit = RbxWorldStateMark.Of(registry, 0);
            RbxInstance worldOwned = registry.Create("Folder");
            worldOwned.Parent = registry.WorldRoot;
            RbxWorldStateMark afterWorldEdit = RbxWorldStateMark.Of(registry, 0);

            Assert.AreEqual(start, afterModEdit, "a mod's own instances are not what a package holds");
            Assert.AreNotEqual(afterModEdit, afterWorldEdit);
            Assert.AreNotEqual(afterWorldEdit, RbxWorldStateMark.Of(registry, 1), "a source write changes the mark");
            Assert.IsFalse(default(RbxWorldStateMark).Equals(default(RbxWorldStateMark)), "no world, no match");
            game.Destroy();
        }

        private sealed class FakeUndoHost : IRbxWorldUndoHost
        {
            private readonly InstanceRegistry _registry = new(worldId: P3Fixtures.WorldId);

            public long SourceChanges { get; set; }

            public RbxWorldPackagePayload Restored { get; private set; }

            public RbxWorldStateMark MarkPublishedWorld()
            {
                return RbxWorldStateMark.Of(_registry, SourceChanges);
            }

            public UniTask<RbxWorldLoadResult> RestoreSnapshotInPlaceAsync(
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken)
            {
                Restored = payload;
                SourceChanges += 100;
                return UniTask.FromResult(new RbxWorldLoadResult(true, "", 0));
            }
        }

        /// <summary>A host's own gate: runs each mutation at once and reports it to its observers.</summary>
        private sealed class FakeObservableGate : IAttributedWorldMutationGate, IObservableWorldMutationGate
        {
            private readonly List<IRbxWorldMutationObserver> _observers = new();
            private long _sequence;

            public List<string> Runs { get; } = new();

            public void AddObserver(IRbxWorldMutationObserver observer)
            {
                _observers.Add(observer);
            }

            public void RemoveObserver(IRbxWorldMutationObserver observer)
            {
                _observers.Remove(observer);
            }

            public async Task RunAsync(string trigger, string actorId, RbxWorldSnapshot before, Action mutation)
            {
                await ExecuteAsync(trigger, actorId, _ =>
                {
                    mutation();
                    return Task.FromResult(true);
                }, CancellationToken.None, before);
            }

            public Task<TResult> ExecuteAsync<TResult>(
                string trigger,
                Func<CancellationToken, Task<TResult>> mutationAsync,
                CancellationToken cancellationToken)
            {
                return ExecuteAsync(trigger, "", mutationAsync, cancellationToken);
            }

            public Task<TResult> ExecuteAsync<TResult>(
                string trigger,
                string actorId,
                Func<CancellationToken, Task<TResult>> mutationAsync,
                CancellationToken cancellationToken)
            {
                return ExecuteAsync(trigger, actorId, mutationAsync, cancellationToken,
                    new RbxWorldSnapshot(trigger, P3Fixtures.CapturedAtUtc, P3Fixtures.CapturePayload(), null));
            }

            private async Task<TResult> ExecuteAsync<TResult>(
                string trigger,
                string actorId,
                Func<CancellationToken, Task<TResult>> mutationAsync,
                CancellationToken cancellationToken,
                RbxWorldSnapshot before)
            {
                Runs.Add(trigger + ":" + actorId);
                RbxWorldMutationInfo info = new(trigger, actorId, ++_sequence);
                foreach (IRbxWorldMutationObserver observer in _observers.ToArray())
                {
                    observer.OnMutationStarting(info);
                }

                TResult result = await mutationAsync(cancellationToken);
                foreach (IRbxWorldMutationObserver observer in _observers.ToArray())
                {
                    observer.OnMutationFinished(info, before, true);
                }

                return result;
            }
        }

        private sealed class PlainGate : IConfirmedWorldMutationGate
        {
            public Task<TResult> ExecuteAsync<TResult>(
                string trigger,
                Func<CancellationToken, Task<TResult>> mutationAsync,
                CancellationToken cancellationToken)
            {
                return mutationAsync(cancellationToken);
            }
        }
    }
}

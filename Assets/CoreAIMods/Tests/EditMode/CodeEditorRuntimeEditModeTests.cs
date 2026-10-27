#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.Hub.CodeEditing;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.CodeEditing;
using CoreAI.Composition;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Runs editor commands through the production Hub service, factory, actor, and Lua VM.</summary>
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class CodeEditorRuntimeEditModeTests
    {
        private const string ModId = "editor-runtime";
        private const string MutationSource =
            "store_set('ran','yes') local folder=Instance.new('Folder',workspace) folder.Name='EditorCreated'";
        private const string FiniteSource =
            "local n=0 for i=1,20000 do n=n+i end assert(n==200010000) store_set('finished','yes')";
        private SynchronizationContext _previousContext;

        private static ActorContext Host => CoreServicesInstaller.DefaultLocalHostIdentityProvider
            .GetActorContext(BuiltInAgentRoleIds.Programmer);

        [SetUp]
        public void SetUp()
        {
            // WHY: The production synchronous entry point must not wait on Unity's blocked test context.
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previousContext);

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RunEntryPoints_CompleteBoundedSaveOnTheCallerContext(bool asyncFacade, bool cancelled)
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            if (cancelled) cancellation.Cancel();
            NonPumpingSynchronizationContext context = new NonPumpingSynchronizationContext();
            SynchronizationContext previous = SynchronizationContext.Current;
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            List<int> observerThreads = new List<int>();
            fixture.Editor.StatusChanged += _ => observerThreads.Add(Thread.CurrentThread.ManagedThreadId);
            fixture.Editor.MessageReceived += _ => observerThreads.Add(Thread.CurrentThread.ManagedThreadId);
            SideEffects before = Snapshot(fixture);
            CodeRunRequest request = Request(MutationSource);
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                fixture.Editor.Initialize();
                CodeRunResult result;
                if (asyncFacade)
                {
                    Task<CodeRunResult> completion = fixture.Editor.RunAsync(request, cancellation.Token);
                    Assert.IsTrue(completion.IsCompleted,
                        "The compatibility task must complete before the caller can return to its context.");
                    result = completion.GetAwaiter().GetResult();
                }
                else
                {
                    result = fixture.Editor.Run(request, cancellation.Token);
                }

                AssertResult(result, request, cancelled ? CodeRunStatus.Cancelled : CodeRunStatus.Success,
                    !cancelled, !cancelled);
                Assert.AreSame(context, SynchronizationContext.Current);
                Assert.AreEqual(0, context.PostCalls);
                Assert.AreEqual(0, context.SendCalls);
                Assert.Greater(observerThreads.Count, 0);
                foreach (int observedThread in observerThreads) Assert.AreEqual(callerThread, observedThread);
                Assert.AreEqual(CodeRuntimeStatus.Ready, fixture.Editor.Status);
                Assert.IsFalse(fixture.Editor.Stop(request.RunId));
                if (cancelled)
                {
                    AssertUnchanged(fixture, before);
                }
                else
                {
                    Assert.AreEqual("yes", fixture.State.Get(ModId, "ran"));
                    AssertStoredSource(fixture, MutationSource);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        [Test]
        public void SaveAndRun_ExecutesPersistsAndReloadsThroughTheProductionReceiptPath()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            Assert.AreEqual("lua", fixture.Editor.LanguageId);
            Assert.AreEqual(CodeRuntimeCapabilities.SaveAndRun, fixture.Editor.Capabilities);
            fixture.Editor.Initialize();
            int initialAuthored = fixture.Rbx.Registry.AuthoredCount;
            CodeRunRequest first = Request(MutationSource);
            CodeRunResult loaded = Run(fixture, first);
            AssertResult(loaded, first, CodeRunStatus.Success, true, true);
            StringAssert.Contains("Saved & ran", loaded.Message);
            Assert.AreEqual("yes", fixture.State.Get(ModId, "ran"));
            Assert.AreEqual(initialAuthored + 1, fixture.Rbx.Registry.AuthoredCount);
            AssertStoredSource(fixture, MutationSource);

            string replacement = MutationSource.Replace("'yes'", "'again'").Replace("EditorCreated", "EditorReloaded");
            CodeRunRequest second = Request(replacement, new CodeSourceExpectation(true, MutationSource));
            CodeRunResult reloaded = Run(fixture, second);
            AssertResult(reloaded, second, CodeRunStatus.Success, true, true);
            Assert.AreEqual("again", fixture.State.Get(ModId, "ran"));
            Assert.AreEqual(initialAuthored + 1, fixture.Rbx.Registry.AuthoredCount,
                "The configured clean reload mode must replace the previous run's startup object.");
            AssertStoredSource(fixture, replacement);
            Assert.AreEqual(CodeRuntimeStatus.Ready, fixture.Editor.Status);
        }

        [TestCase("failed", CodeRunStatus.Failed)]
        [TestCase("unconfirmed", CodeRunStatus.Unconfirmed)]
        public void SaveReceiptReportsLiveExecutionSeparatelyFromPersistence(string persistence, CodeRunStatus expected)
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            fixture.Sources.FailStrictSave = persistence == "failed";
            fixture.Sources.SupportsStrictSave = persistence != "unconfirmed";
            CodeRunRequest request = Request("store_set('runs', tostring((tonumber(store_get('runs')) or 0)+1))");
            CodeRunResult result = Run(fixture, request);
            AssertResult(result, request, expected, true, false);
            Assert.AreEqual("1", fixture.State.Get(ModId, "runs"));
            Assert.IsTrue(fixture.Stack.Runtime.IsLoaded(Host, ModId));
            Assert.AreEqual(1, fixture.Sources.WriteCalls);

            if (persistence == "failed")
            {
                fixture.Sources.FailStrictSave = false;
                CodeRunRequest retry = Request(request.Code, new CodeSourceExpectation(true, request.Code));
                CodeRunResult retried = Run(fixture, retry);
                AssertResult(retried, retry, CodeRunStatus.Success, true, true);
                Assert.AreEqual("1", fixture.State.Get(ModId, "runs"),
                    "Retrying the retained persistence receipt must not execute the VM twice.");
                AssertStoredSource(fixture, request.Code);
            }
        }

        [TestCase("python-adapter")]
        [TestCase("python-stub")]
        [TestCase("lua-run-only")]
        public void UnsupportedCommandsHaveNoVmStoreOrWorldSideEffects(string operation)
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            SideEffects before = Snapshot(fixture);
            bool python = operation != "lua-run-only";
            CodeRunRequest request = new CodeRunRequest("document", python ? "python" : "lua", 17,
                ModId, MutationSource, python ? CodeRunOperation.SaveAndRun : CodeRunOperation.Run,
                1000, 500_000, new CodeSourceExpectation(false, ""), "unsupported-" + operation);
            CodeRunResult result = operation == "python-stub"
                ? new PythonCodeInterpreterStub().RunAsync(request).GetAwaiter().GetResult()
                : Run(fixture, request);
            AssertResult(result, request, CodeRunStatus.Unsupported, false, false);
            AssertUnchanged(fixture, before);
        }

        [TestCase("missing-expectation")]
        [TestCase("wrong-existence")]
        [TestCase("wrong-case")]
        [TestCase("different-newline")]
        public void SourcePreconditionRejectsExactMismatchBeforeAnyMutation(string mismatch)
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            const string original = "store_set('original','yes')\n";
            fixture.Service.SaveOrReload(ModId, original, ModReloadMode.CleanStartupObjects);
            CodeSourceExpectation expectation = mismatch == "missing-expectation" ? null :
                mismatch == "wrong-existence" ? new CodeSourceExpectation(false, "") :
                new CodeSourceExpectation(true, mismatch == "wrong-case"
                    ? original.Replace("original", "Original") : original.Replace("\n", "\r\n"));
            CodeRunRequest request = new CodeRunRequest("document", "lua", 21, ModId, MutationSource,
                CodeRunOperation.SaveAndRun, 1000, 500_000, expectation, "conflicting-run");
            SideEffects before = Snapshot(fixture);
            CodeRunResult result = Run(fixture, request);
            AssertResult(result, request, CodeRunStatus.Conflict, false, false);
            AssertUnchanged(fixture, before);
            Assert.IsTrue(fixture.Stack.Runtime.TryGetModSource(Host, ModId, out string live));
            Assert.AreEqual(original, live);
            AssertStoredSource(fixture, original);
        }

        [Test]
        public void StoredOnlySourceAlsoParticipatesInTheExactPrecondition()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            const string stored = "store_set('stored','yes')";
            fixture.Sources.SaveStrict(ModId, stored, new LuaModManifest { Id = ModId, Active = false });
            SideEffects before = Snapshot(fixture);
            CodeRunRequest request = Request(MutationSource, new CodeSourceExpectation(true, stored + " "));
            AssertResult(Run(fixture, request), request, CodeRunStatus.Conflict, false, false);
            AssertUnchanged(fixture, before);
            AssertStoredSource(fixture, stored, active: false);
        }

        [Test]
        public void ShortInitialBudgetTripsInTheRealVm_AndTheNextHealthyRunHasItsOwnPolicy()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            fixture.Editor.Initialize();
            SideEffects before = Snapshot(fixture);
            CodeRunRequest limited = Request(FiniteSource, maxSteps: 1000);
            CodeRunResult failure = Run(fixture, limited);
            AssertResult(failure, limited, CodeRunStatus.BudgetExceeded, false, false);
            Assert.Greater(failure.Diagnostics.Count, 0);
            AssertUnchanged(fixture, before);
            Assert.AreEqual(CodeRuntimeStatus.Ready, fixture.Editor.Status);

            CodeRunRequest healthy = Request(FiniteSource);
            AssertResult(Run(fixture, healthy), healthy, CodeRunStatus.Success, true, true);
            Assert.AreEqual("yes", fixture.State.Get(ModId, "finished"));
            AssertStoredSource(fixture, FiniteSource);
        }

        [Test]
        public void YieldedMainUsesNormalHostPolicyAfterTheExplicitInitialResumeCompletes()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            CodeRunRequest request = Request("task.wait(0.05) " + FiniteSource, maxSteps: 2000);
            AssertResult(Run(fixture, request, cancellation.Token), request, CodeRunStatus.Success, true, true);
            Assert.AreEqual("", fixture.State.Get(ModId, "finished"));
            Assert.IsFalse(fixture.Editor.Stop(request.RunId));
            cancellation.Cancel();
            fixture.Rbx.Scheduler.Advance(0.1f);
            Assert.AreEqual("yes", fixture.State.Get(ModId, "finished"),
                "An editor invocation bounds the initial resume; later resumes use the normal host policy.");
            Assert.IsTrue(fixture.Stack.Runtime.IsLoaded(Host, ModId));
        }

        [Test]
        public void PrecancelledCommandReturnsItsIdsWithoutAnySideEffects()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            fixture.Editor.Initialize();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            SideEffects before = Snapshot(fixture);
            CodeRunRequest request = Request(MutationSource);
            AssertResult(Run(fixture, request, cancellation.Token), request, CodeRunStatus.Cancelled, false, false);
            AssertUnchanged(fixture, before);
            Assert.AreEqual(CodeRuntimeStatus.Ready, fixture.Editor.Status);
        }

        [Test]
        public void StopTargetsTheActiveRunId_ThenAllowsTheNextHealthyRun()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            CodeRunRequest request = Request(MutationSource);
            bool wrongIdStopped = true;
            bool correctIdStopped = false;
            Action<CodeRuntimeStatus> stop = status =>
            {
                if (status != CodeRuntimeStatus.Running) return;
                wrongIdStopped = fixture.Editor.Stop("another-run");
                correctIdStopped = fixture.Editor.Stop(request.RunId);
            };
            fixture.Editor.StatusChanged += stop;
            SideEffects before = Snapshot(fixture);
            CodeRunResult result = Run(fixture, request);
            fixture.Editor.StatusChanged -= stop;
            AssertResult(result, request, CodeRunStatus.Cancelled, false, false);
            Assert.IsFalse(wrongIdStopped);
            Assert.IsTrue(correctIdStopped);
            Assert.IsFalse(fixture.Editor.Stop(request.RunId));
            AssertUnchanged(fixture, before);
            CodeRunRequest next = Request(MutationSource);
            AssertResult(Run(fixture, next), next, CodeRunStatus.Success, true, true);
        }

        [Test]
        public void DisposeDuringRunningStatusCancelsBeforeMutation_AndRejectsFurtherRuns()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            fixture.Editor.StatusChanged += status =>
            {
                if (status == CodeRuntimeStatus.Running) fixture.Editor.Dispose();
            };
            CodeRunRequest request = Request(MutationSource);
            SideEffects before = Snapshot(fixture);
            AssertResult(Run(fixture, request), request, CodeRunStatus.Cancelled, false, false);
            Assert.AreEqual(CodeRuntimeStatus.Disposed, fixture.Editor.Status);
            Assert.IsFalse(fixture.Editor.Stop(request.RunId));
            fixture.Editor.Dispose();
            Assert.Throws<ObjectDisposedException>(() => fixture.Editor.Initialize());
            CodeRunRequest next = Request(MutationSource);
            AssertResult(Run(fixture, next), next, CodeRunStatus.Failed, false, false);
            AssertUnchanged(fixture, before);
        }

        [Test]
        public void ObserverFailuresDoNotHideLaterObserversOrChangeTheRunReceipt()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            List<CodeRuntimeStatus> statuses = new List<CodeRuntimeStatus>();
            List<CodeRuntimeMessage> messages = new List<CodeRuntimeMessage>();
            fixture.Editor.StatusChanged += _ => throw new InvalidOperationException("status observer failed");
            fixture.Editor.StatusChanged += statuses.Add;
            fixture.Editor.MessageReceived += _ => throw new InvalidOperationException("message observer failed");
            fixture.Editor.MessageReceived += messages.Add;
            Assert.AreEqual(CodeRuntimeStatus.Uninitialized, fixture.Editor.Status);
            fixture.Editor.Initialize();
            fixture.Editor.Initialize();
            CodeRunRequest request = Request(MutationSource);
            CodeRunResult result = Run(fixture, request);
            AssertResult(result, request, CodeRunStatus.Success, true, true);
            CollectionAssert.AreEqual(new[] { CodeRuntimeStatus.Ready, CodeRuntimeStatus.Running, CodeRuntimeStatus.Ready }, statuses);
            Assert.AreEqual(1, messages.Count);
            Assert.AreSame(result, messages[0].Result);
            Assert.AreEqual(request.RunId, messages[0].RunId);
            fixture.Editor.Dispose();
            Assert.AreEqual(CodeRuntimeStatus.Disposed, statuses[statuses.Count - 1]);
            int observed = statuses.Count;
            fixture.Editor.Dispose();
            Assert.AreEqual(observed, statuses.Count);
        }

        [Test]
        public void ReentrantCommandCannotMutateTheRealVmWhileAnotherRunIsActive()
        {
            using RuntimeFixture fixture = new RuntimeFixture();
            CodeRunRequest competing = Request("store_set('competing','yes')");
            CodeRunResult refused = null;
            fixture.Editor.StatusChanged += status =>
            {
                if (status == CodeRuntimeStatus.Running) refused = Run(fixture, competing);
            };
            CodeRunRequest request = Request(MutationSource);
            AssertResult(Run(fixture, request), request, CodeRunStatus.Success, true, true);
            Assert.IsNotNull(refused);
            AssertResult(refused, competing, CodeRunStatus.Failed, false, false);
            Assert.AreEqual("", fixture.State.Get(ModId, "competing"));
            Assert.AreEqual("yes", fixture.State.Get(ModId, "ran"));
            Assert.AreEqual(1, fixture.Sources.WriteCalls);
        }

        private static CodeRunRequest Request(string source, CodeSourceExpectation expected = null, long maxSteps = 500_000) =>
            new CodeRunRequest("runtime-document", "lua", 13, ModId, source, CodeRunOperation.SaveAndRun,
                1000, maxSteps, expected ?? new CodeSourceExpectation(false, ""));

        private static CodeRunResult Run(RuntimeFixture fixture, CodeRunRequest request,
            CancellationToken cancellationToken = default) =>
            fixture.Editor.Run(request, cancellationToken);

        private static void AssertResult(CodeRunResult result, CodeRunRequest request, CodeRunStatus status,
            bool executed, bool persisted)
        {
            Assert.AreEqual(status, result.Status, result.Message);
            Assert.AreEqual(executed, result.Executed, result.Message);
            Assert.AreEqual(persisted, result.Persisted, result.Message);
            Assert.IsTrue(result.Matches(request));
            Assert.AreEqual(request.RunId, result.RunId);
            Assert.AreEqual(request.DocumentId, result.DocumentId);
            Assert.AreEqual(request.LanguageId, result.LanguageId);
            Assert.AreEqual(request.ModId, result.ModId);
            Assert.AreEqual(request.Revision, result.Revision);
        }

        private static void AssertStoredSource(RuntimeFixture fixture, string expected, bool active = true)
        {
            Assert.IsTrue(fixture.Sources.TryLoad(ModId, out string source, out LuaModManifest manifest));
            Assert.AreEqual(expected, source);
            Assert.AreEqual(active, manifest.Active);
        }

        private static SideEffects Snapshot(RuntimeFixture fixture) => new SideEffects(
            fixture.State.WriteCalls, fixture.Sources.WriteCalls, fixture.Sources.DeleteCalls,
            fixture.Stack.Runtime.ListMods(Host).Count, fixture.Rbx.Registry.AuthoredCount);

        private static void AssertUnchanged(RuntimeFixture fixture, SideEffects before)
        {
            SideEffects after = Snapshot(fixture);
            Assert.AreEqual(before.StateWrites, after.StateWrites, "Mod state must not change.");
            Assert.AreEqual(before.SourceWrites, after.SourceWrites, "Source must not be saved.");
            Assert.AreEqual(before.SourceDeletes, after.SourceDeletes, "Source must not be deleted.");
            Assert.AreEqual(before.LoadedMods, after.LoadedMods, "The VM must not load or unload a mod.");
            Assert.AreEqual(before.WorldObjects, after.WorldObjects, "The instance world must not change.");
        }

        private readonly struct SideEffects
        {
            public SideEffects(int stateWrites, int sourceWrites, int sourceDeletes, int loadedMods, int worldObjects)
            {
                StateWrites = stateWrites;
                SourceWrites = sourceWrites;
                SourceDeletes = sourceDeletes;
                LoadedMods = loadedMods;
                WorldObjects = worldObjects;
            }
            public int StateWrites { get; }
            public int SourceWrites { get; }
            public int SourceDeletes { get; }
            public int LoadedMods { get; }
            public int WorldObjects { get; }
        }

        private sealed class NonPumpingSynchronizationContext : SynchronizationContext
        {
            public int PostCalls { get; private set; }
            public int SendCalls { get; private set; }
            public override void Post(SendOrPostCallback callback, object state) => PostCalls++;
            public override void Send(SendOrPostCallback callback, object state) => SendCalls++;
        }

        private sealed class RuntimeFixture : IDisposable
        {
            public readonly MemoryModStore State = new MemoryModStore();
            public readonly MemorySourceStore Sources = new MemorySourceStore();
            public readonly LuaCsRbxApiBindings Rbx;
            public readonly LuaCsModStack Stack;
            public readonly LuaCsModRuntimeHubService Service;
            public readonly LuaCodePlaygroundRuntime Editor;

            public RuntimeFixture()
            {
                Rbx = new LuaCsRbxApiBindings(registry: new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion));
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    ModStore = State,
                    ModSourceStore = Sources,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore(),
                    AutoPersistMods = false,
                    RbxApi = Rbx,
                    Capabilities = LuaCapabilities.All,
                    Log = NullLog.Instance,
                    RegisterWorldEditBuildBindings = false
                });
                Service = new LuaCsModRuntimeHubService(Stack.Runtime, Host, Sources, rbxApiAvailable: true);
                Editor = new LuaCodePlaygroundRuntime(Service);
            }

            public void Dispose()
            {
                Editor.Dispose();
                Service.Dispose();
                Stack.Runtime.ShutdownWithoutPersistence();
                Rbx.Dispose();
            }
        }

        private sealed class MemoryModStore : ILuaModStore
        {
            private readonly Dictionary<(string Mod, string Key), string> _values =
                new Dictionary<(string Mod, string Key), string>();
            public int WriteCalls { get; private set; }
            public string Get(string modId, string key) =>
                _values.TryGetValue((modId, key), out string value) ? value : "";
            public void Set(string modId, string key, string value)
            {
                WriteCalls++;
                _values[(modId, key)] = value;
            }
            public void Clear(string modId)
            {
                WriteCalls++;
                foreach ((string Mod, string Key) key in new List<(string Mod, string Key)>(_values.Keys))
                    if (key.Mod == modId) _values.Remove(key);
            }
        }

        private sealed class MemorySourceStore : ILuaModSourceStore, ILuaModSourceStrictWriter, ILuaModSourceStrictCapabilities
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _sources =
                new Dictionary<string, (string Source, LuaModManifest Manifest)>(StringComparer.Ordinal);
            public int WriteCalls { get; private set; }
            public int DeleteCalls { get; private set; }
            public bool FailStrictSave { get; set; }
            public bool SupportsStrictSave { get; set; } = true;
            public bool SupportsStrictDelete => false;

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                WriteCalls++;
                if (FailStrictSave) throw new IOException("The test source store refused its strict save.");
                _sources[id] = (source, manifest);
            }
            public void Save(string id, string source, LuaModManifest manifest)
            {
                WriteCalls++;
                _sources[id] = (source, manifest);
            }
            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_sources.TryGetValue(id, out (string Source, LuaModManifest Manifest) stored))
                {
                    source = stored.Source;
                    manifest = stored.Manifest;
                    return true;
                }
                source = "";
                manifest = null;
                return false;
            }
            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> result = new List<LuaModManifest>();
                foreach ((string Source, LuaModManifest Manifest) stored in _sources.Values) result.Add(stored.Manifest);
                return result;
            }
            public void SetActive(string id, bool active)
            {
                WriteCalls++;
                if (_sources.TryGetValue(id, out (string Source, LuaModManifest Manifest) stored)) stored.Manifest.Active = active;
            }
            public void Delete(string id)
            {
                DeleteCalls++;
                _sources.Remove(id);
            }
        }
    }
}
#endif

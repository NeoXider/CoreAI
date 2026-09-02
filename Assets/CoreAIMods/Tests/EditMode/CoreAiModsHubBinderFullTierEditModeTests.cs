#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Demos;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Security regression guarding the Hub Mods tab Full-tier gate: the binder's <c>allowFullTier</c>
    /// flag must default to <c>false</c>, and a mod imported through the Hub service must never receive
    /// <see cref="LuaCapabilities.Full"/> unless the host explicitly opted in (allowFull), even when the
    /// bundle's own header requests Full. Full is a deliberate host decision, never derived from an
    /// untrusted mod's header on the import/share/rehydrate path.
    /// </summary>
    public sealed class CoreAiModsHubBinderFullTierEditModeTests
    {
        private sealed class RecordingWorldRuntimeService : IRbxWorldRuntimeService
        {
            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return AutoSaves.ToArray();
            }

            public UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
                ActorContext caller,
                string autoFileName,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AutoLoadCallers.Add(caller);
                RequestedAutoFiles.Add(autoFileName);
                return QueuePending(autoFileName);
            }

            private readonly List<RbxPendingWorldLoadRequest> _pending = new();
            private int _nextRequest;

            public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

            public DateTime UtcNow { get; set; } =
                new DateTime(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            public int ConfirmCalls { get; private set; }

            public int AppliedCount { get; private set; }

            public List<RbxAutoSaveInfo> AutoSaves { get; } = new();

            public List<ActorContext> AutoLoadCallers { get; } = new();

            public List<string> RequestedAutoFiles { get; } = new();

            public List<string> SavedSlots { get; } = new();

            public List<string> RequestedSlots { get; } = new();

            public int Revision { get; private set; } = 17;

            public string WorldMarker { get; private set; } = "original";

            public List<string> Ledger { get; } = new() { "existing-entry" };

            public Dictionary<string, string> ManualSlots { get; } =
                new(StringComparer.Ordinal) { ["keep"] = "original-slot" };

            public RbxWorldPackagePayload CaptureCurrent()
            {
                return null;
            }

            public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads()
            {
                _pending.RemoveAll(request => request.ExpiresAtUtc <= UtcNow);
                return _pending.ToArray();
            }

            public UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SavedSlots.Add(slot);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    true,
                    slot + ".world",
                    ""));
            }

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestedSlots.Add(slot);
                return QueuePending(slot);
            }

            private UniTask<RbxWorldLoadRequest> QueuePending(string slot)
            {
                _nextRequest++;
                string requestId = "request-" + _nextRequest;
                DateTime expiresAtUtc = UtcNow.AddMinutes(1);
                RbxPendingWorldLoadRequest pending = new(
                    requestId,
                    slot,
                    "world-" + _nextRequest,
                    UtcNow,
                    expiresAtUtc);
                _pending.Add(pending);
                ManualLoadConfirmationRequested?.Invoke(pending);
                return UniTask.FromResult(new RbxWorldLoadRequest(
                    requestId,
                    slot,
                    pending.WorldId,
                    pending.RequestedAtUtc,
                    expiresAtUtc));
            }

            public UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
                string requestId,
                bool playerConfirmed,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConfirmCalls++;
                int index = _pending.FindIndex(request =>
                    string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
                if (index < 0)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(
                        false,
                        "unknown, expired, or consumed",
                        0));
                }

                _pending.RemoveAt(index);
                if (!playerConfirmed)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(
                        false,
                        "rejected",
                        0));
                }

                AppliedCount++;
                Revision++;
                WorldMarker = "loaded";
                Ledger.Add("load-applied");
                return UniTask.FromResult(new RbxWorldLoadResult(true, "", 1));
            }

            public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        [Test]
        public void CoreAiModsHubBinder_AllowFullTier_DefaultsToFalse()
        {
            GameObject go = new(nameof(CoreAiModsHubBinderFullTierEditModeTests));
            try
            {
                CoreAiModsHubBinder binder = go.AddComponent<CoreAiModsHubBinder>();
                FieldInfo field = typeof(CoreAiModsHubBinder).GetField(
                    "allowFullTier", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "Expected a serialized 'allowFullTier' field on the binder.");
                Assert.IsFalse((bool)field.GetValue(binder),
                    "allowFullTier must default to false so untrusted mods cannot self-escalate to Full.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void WorldLoadPage_LateSubscription_RendersPendingAndRejectsWithoutMutation()
        {
            RecordingWorldRuntimeService service = new();
            RbxWorldLoadRequest request = service.RequestManualLoadAsync(
                default,
                "manual-a").GetAwaiter().GetResult();
            int revisionBefore = service.Revision;
            string markerBefore = service.WorldMarker;
            string[] ledgerBefore = service.Ledger.ToArray();
            KeyValuePair<string, string>[] slotsBefore = service.ManualSlots.ToArray();

            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                Button reject = root.Q<Button>("coreai-world-load-reject-" + request.RequestId);
                Assert.IsNotNull(reject, "A late subscriber must render the service's pending list.");

                InvokeButton(reject);

                Assert.AreEqual(1, service.ConfirmCalls);
                Assert.AreEqual(0, service.AppliedCount);
                Assert.AreEqual(revisionBefore, service.Revision);
                Assert.AreEqual(markerBefore, service.WorldMarker);
                CollectionAssert.AreEqual(ledgerBefore, service.Ledger);
                CollectionAssert.AreEqual(slotsBefore, service.ManualSlots);
                Assert.AreEqual(0, service.GetPendingManualLoads().Count);
                Assert.IsNull(root.Q<VisualElement>("coreai-world-load-row-" + request.RequestId));
                Assert.AreEqual(
                    "No pending requests.",
                    root.Q<Label>("coreai-hub-world-loads-status").text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_EventConfirmsExactlyOnce_AndReuseFailsClosed()
        {
            RecordingWorldRuntimeService service = new();
            int attentionRequests = 0;
            HubWorldLoadConfirmationPage page = new(service, () => attentionRequests++);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                RbxWorldLoadRequest request = service.RequestManualLoadAsync(
                    default,
                    "manual-b").GetAwaiter().GetResult();
                Button confirm = root.Q<Button>("coreai-world-load-confirm-" + request.RequestId);
                Assert.IsNotNull(confirm);
                Assert.AreEqual(1, attentionRequests);
                Assert.AreEqual(0, service.AppliedCount,
                    "Requesting a load must not mutate before a player clicks Confirm.");

                InvokeButton(confirm);
                Assert.AreEqual(1, service.AppliedCount);
                Assert.AreEqual(18, service.Revision);
                Assert.AreEqual("loaded", service.WorldMarker);
                Assert.AreEqual(0, service.GetPendingManualLoads().Count);

                InvokeButton(confirm);
                Assert.AreEqual(2, service.ConfirmCalls,
                    "A reused UI callback may reach the service, which must reject the consumed id.");
                Assert.AreEqual(1, service.AppliedCount,
                    "Reusing a consumed request id must never apply the world twice.");
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_ExpiredRequest_IsRemovedOnRefresh()
        {
            RecordingWorldRuntimeService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                RbxWorldLoadRequest request = service.RequestManualLoadAsync(
                    default,
                    "manual-expiring").GetAwaiter().GetResult();
                Assert.IsNotNull(root.Q<VisualElement>("coreai-world-load-row-" + request.RequestId));

                service.UtcNow = request.ExpiresAtUtc.AddSeconds(1);
                page.OnActivated();

                Assert.IsNull(root.Q<VisualElement>("coreai-world-load-row-" + request.RequestId));
                Assert.AreEqual(0, service.GetPendingManualLoads().Count);
                Assert.AreEqual(0, service.AppliedCount);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_Autosaves_RenderMetadataAndRefreshToEmptyState()
        {
            RecordingWorldRuntimeService service = new();
            DateTime timestampUtc = new(2035, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            service.AutoSaves.Add(new RbxAutoSaveInfo(
                "auto-001.world.json",
                "manual-save",
                timestampUtc,
                1536));

            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                VisualElement row = root.Q<VisualElement>("coreai-autosave-row-auto-001.world.json");
                Assert.IsNotNull(row, "Autosaves returned by the runtime service must be rendered.");
                string[] labels = row.Query<Label>().ToList().Select(label => label.text).ToArray();
                CollectionAssert.Contains(labels, "Name: auto-001.world.json");
                CollectionAssert.Contains(labels, "Trigger: manual-save");
                CollectionAssert.Contains(
                    labels,
                    "Saved: " + timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                CollectionAssert.Contains(labels, "Size: 1.5 KB");

                service.AutoSaves.Clear();
                page.OnActivated();

                Assert.IsNull(root.Q<VisualElement>("coreai-autosave-row-auto-001.world.json"));
                Assert.IsNotNull(root.Q<Label>("coreai-autosaves-empty"));
                Assert.AreEqual(
                    "No autosaves are available.",
                    root.Q<Label>("coreai-autosaves-empty").text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_AutoLoadRequestsExistingConfirmation_AndRejectsWithoutMutation()
        {
            RecordingWorldRuntimeService service = new();
            ActorContext actor = new LocalActorIdentityProvider("hub-autosave-ui-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            service.AutoSaves.Add(new RbxAutoSaveInfo(
                "auto-reject.world.json",
                "interval",
                service.UtcNow,
                2048));
            int revisionBefore = service.Revision;
            string markerBefore = service.WorldMarker;
            string[] ledgerBefore = service.Ledger.ToArray();

            HubWorldLoadConfirmationPage page = new(service, actorContext: actor);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                Button load = root.Q<Button>("coreai-autosave-load-auto-reject.world.json");
                Assert.IsNotNull(load);

                InvokeButton(load);

                CollectionAssert.AreEqual(
                    new[] { "auto-reject.world.json" },
                    service.RequestedAutoFiles);
                Assert.AreEqual(actor.ActorId, service.AutoLoadCallers.Single().ActorId);
                Assert.AreEqual(1, service.GetPendingManualLoads().Count);
                RbxPendingWorldLoadRequest pending = service.GetPendingManualLoads()[0];
                Button reject = root.Q<Button>("coreai-world-load-reject-" + pending.RequestId);
                Assert.IsNotNull(reject,
                    "Autosave loads must appear in the existing pending confirmation UI.");
                Assert.AreEqual(0, service.AppliedCount,
                    "Requesting an autosave load must not apply it directly.");

                InvokeButton(reject);

                Assert.AreEqual(0, service.AppliedCount);
                Assert.AreEqual(revisionBefore, service.Revision);
                Assert.AreEqual(markerBefore, service.WorldMarker);
                CollectionAssert.AreEqual(ledgerBefore, service.Ledger);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_AutosaveFormatting_UsesLocalTimeAndKilobytes()
        {
            DateTime timestampUtc = new(2035, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            MethodInfo formatTimestamp = typeof(HubWorldLoadConfirmationPage).GetMethod(
                "FormatAutoSaveTimestamp",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo formatKilobytes = typeof(HubWorldLoadConfirmationPage).GetMethod(
                "FormatKilobytes",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(formatTimestamp,
                "The autosave presenter must expose its local timestamp formatter to the callback tests.");
            Assert.IsNotNull(formatKilobytes,
                "The autosave presenter must expose its KB formatter to the callback tests.");

            Assert.AreEqual(
                timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                formatTimestamp.Invoke(null, new object[] { timestampUtc }));
            Assert.AreEqual("1.5 KB", formatKilobytes.Invoke(null, new object[] { 1536L }));
        }

        [Test]
        public void WorldLoadPage_AutoLoadController_UsesCurrentActorAndQueuesPendingWithoutMutation()
        {
            RecordingWorldRuntimeService service = new();
            ActorContext actor = new LocalActorIdentityProvider("hub-autosave-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            HubWorldLoadConfirmationPage page = new(service);
            MethodInfo requestAutoLoad = typeof(HubWorldLoadConfirmationPage).GetMethod(
                "RequestAutoLoad",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(ActorContext), typeof(string) },
                null);
            Assert.IsNotNull(requestAutoLoad,
                "Autosave Load must use a controller callback that can be tested without attached UI.");

            try
            {
                requestAutoLoad.Invoke(page, new object[] { actor, "auto-controller.world.json" });

                CollectionAssert.AreEqual(
                    new[] { "auto-controller.world.json" },
                    service.RequestedAutoFiles);
                Assert.AreEqual(actor.ActorId, service.AutoLoadCallers.Single().ActorId);
                Assert.AreEqual(1, service.GetPendingManualLoads().Count,
                    "The autosave request must enter the shared pending confirmation pool.");
                Assert.AreEqual(0, service.AppliedCount,
                    "The autosave callback must never apply a world directly.");
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        private static void InvokeButton(Button button)
        {
            MethodInfo invoke = typeof(Clickable).GetMethod(
                "Invoke",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(EventBase) },
                null);
            Assert.IsNotNull(invoke, "Unity UI Toolkit Clickable.Invoke(EventBase) must be available.");
            invoke.Invoke(button.clickable, new object[] { null });
        }

#if COREAI_LUA
        private sealed class RecordingLuaExecutor : LuaTool.ILuaExecutor
        {
            public List<string> Code { get; } = new();

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Code.Add(code);
                return Task.FromResult(new LuaTool.LuaResult
                {
                    Success = true,
                    Output = "ok"
                });
            }
        }

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            // See LuaCsModRuntimePersistenceEditModeTests: detach the Unity context so the runtime's
            // sync-over-async execution guard does not deadlock the blocked main thread.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void LuaPlatformWorldDriver_UsesProductionSeams_AndExposesNoConfirmationBypass()
        {
            RecordingLuaExecutor executor = new();
            RecordingWorldRuntimeService service = new();
            GameObject gameObject = new("LuaPlatformExampleController_Test");
            try
            {
                LuaPlatformExampleController controller =
                    gameObject.AddComponent<LuaPlatformExampleController>();
                SetPrivateField(controller, "_worldLuaExecutor", executor);
                SetPrivateField(controller, "_worldRuntimeService", service);

                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_MARKER_CREATE requested name=CoreAI_WebGL_Marker_browser_check");
                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_MARKER_CREATE success name=CoreAI_WebGL_Marker_browser_check");
                controller.CreateWorldMarker("browser_check");

                Assert.AreEqual(1, executor.Code.Count);
                StringAssert.Contains("Instance.new('Folder')", executor.Code[0]);
                StringAssert.Contains("CoreAI_WebGL_Marker_browser_check", executor.Code[0]);

                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_SAVE requested slot=browser_slot");
                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_SAVE success slot=browser_slot");
                controller.SaveWorld("browser_slot");
                CollectionAssert.AreEqual(new[] { "browser_slot" }, service.SavedSlots);

                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_LOAD_REQUEST requested slot=browser_slot");
                LogAssert.Expect(
                    LogType.Log,
                    "[LuaPlatformExample] WORLD_LOAD_REQUEST success slot=browser_slot request=request-1 "
                    + "world=world-1 expires=2035-01-02T03:05:05.0000000Z");
                controller.RequestWorldLoad("browser_slot");
                CollectionAssert.AreEqual(new[] { "browser_slot" }, service.RequestedSlots);
                Assert.AreEqual(0, service.AppliedCount,
                    "RequestWorldLoad must not apply a package without the player Hub decision.");

                LogAssert.Expect(
                    LogType.Warning,
                    "[LuaPlatformExample] WORLD_MARKER_CREATE failure reason=invalid-name");
                controller.CreateWorldMarker("bad'name");
                LogAssert.Expect(
                    LogType.Warning,
                    "[LuaPlatformExample] WORLD_SAVE failure reason=invalid-slot");
                controller.SaveWorld("../bad");
                Assert.AreEqual(1, executor.Code.Count);
                Assert.AreEqual(1, service.SavedSlots.Count);

                MethodInfo[] publicMethods = typeof(LuaPlatformExampleController).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.IsFalse(publicMethods.Any(method =>
                    method.Name.StartsWith("ConfirmWorld", StringComparison.Ordinal)),
                    "The browser driver must not expose a world-load confirmation bypass.");
                Assert.IsNotNull(typeof(LuaPlatformExampleController).GetMethod("DumpWorldMarker"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Expected private driver field: " + fieldName);
            field.SetValue(target, value);
        }

        /// <summary>In-memory package store so the test can drive import without touching the file system.</summary>
        private sealed class FakeSourceStore : ILuaModSourceStore
        {
            private sealed class Entry
            {
                public string Source = "";
                public LuaModManifest Manifest;
            }

            private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = new Entry { Source = source, Manifest = manifest };
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out Entry entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> result = new();
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Manifest != null)
                    {
                        result.Add(entry.Manifest);
                    }
                }

                return result;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out Entry entry) && entry.Manifest != null)
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }
        }

        private static string ExportFullTierBundle()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore exportStore = new();
            LuaCsModRuntime exporter = new(sourceStore: exportStore);
            exporter.LoadMod(
                actorContext,
                "shared",
                "local x = 1",
                LuaCapabilities.Read | LuaCapabilities.Full);
            string bundle = exporter.ExportMod(actorContext, "shared");
            Assert.IsNotNull(bundle, "ExportMod must return a bundle whose header requests Full.");
            return bundle;
        }

        private static ActorContext CreateHostActor()
        {
            return CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        [Test]
        public void HubService_ImportMod_StripsFull_WhenHostDidNotOptIn()
        {
            string bundle = ExportFullTierBundle();

            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            // Host grant includes Full, but allowFull is false — the default Mods tab wiring.
            IHubModService service = new LuaCsModRuntimeHubService(
                runtime, actorContext, store, LuaCapabilities.All | LuaCapabilities.Full, false);
            bool modsChanged = false;
            service.ModsChanged += () => modsChanged = true;

            Assert.IsTrue(service.ImportMod(bundle), "Import of a valid bundle must succeed.");
            Assert.IsTrue(modsChanged, "The authorized runtime listener must preserve Hub live refresh.");
            Assert.IsTrue(runtime.IsLoaded(actorContext, "shared"));
            Assert.AreEqual(
                LuaCapabilities.None,
                runtime.ListMods(actorContext)[0].Capabilities & LuaCapabilities.Full,
                "An imported mod must not self-escalate to Full when the host has not opted in.");
        }

        [Test]
        public void HubService_ImportMod_KeepsFull_WhenHostExplicitlyOptedIn()
        {
            string bundle = ExportFullTierBundle();

            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            // Explicit host opt-in (allowFullTier=true) — trusted/first-party/singleplayer content only.
            IHubModService service = new LuaCsModRuntimeHubService(
                runtime, actorContext, store, LuaCapabilities.All | LuaCapabilities.Full, true);

            Assert.IsTrue(service.ImportMod(bundle));
            Assert.AreEqual(
                LuaCapabilities.Full,
                runtime.ListMods(actorContext)[0].Capabilities & LuaCapabilities.Full,
                "Full must survive import only when the host explicitly opted in and grants Full.");
        }
#endif
    }
}
#endif

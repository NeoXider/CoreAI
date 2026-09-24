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

        /// <summary>A world service that also owns the startup selection, as the production controller does.</summary>
        private sealed class StartupWorldRuntimeService : IRbxWorldRuntimeService, IRbxWorldStartupSelection
        {
            private readonly List<RbxPendingWorldLoadRequest> _pending = new();
            private int _nextRequest;

            public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

            public DateTime UtcNow { get; } = new(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            public RbxWorldStartupSelection Selection { get; set; } = RbxWorldStartupSelection.NoneSelected;

            public RbxWorldLoadResult ConfirmResult { get; set; } = new(true, "", 1, true, "");

            public int AppliedCount { get; private set; }

            public int ClearCalls { get; private set; }

            public int ReadCalls { get; private set; }

            public RbxWorldLoadRequest Queue(string slot)
            {
                _nextRequest++;
                RbxPendingWorldLoadRequest pending = new(
                    "startup-request-" + _nextRequest,
                    slot,
                    "world-" + _nextRequest,
                    UtcNow,
                    UtcNow.AddMinutes(1));
                _pending.Add(pending);
                ManualLoadConfirmationRequested?.Invoke(pending);
                return new RbxWorldLoadRequest(
                    pending.RequestId,
                    slot,
                    pending.WorldId,
                    pending.RequestedAtUtc,
                    pending.ExpiresAtUtc);
            }

            public RbxWorldPackagePayload CaptureCurrent()
            {
                return null;
            }

            public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads()
            {
                return _pending.ToArray();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }

            public UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(Queue(slot));
            }

            public UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
                ActorContext caller,
                string autoFileName,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(Queue(autoFileName));
            }

            public UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
                string requestId,
                bool playerConfirmed,
                CancellationToken cancellationToken = default)
            {
                int index = _pending.FindIndex(request =>
                    string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
                if (index < 0)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(false, "unknown", 0));
                }

                _pending.RemoveAt(index);
                if (!playerConfirmed)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(false, "rejected", 0));
                }

                AppliedCount++;
                return UniTask.FromResult(ConfirmResult);
            }

            public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The Hub never restores; only the startup composition does.");
            }

            public UniTask<RbxWorldPackageWriteResult> ClearStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                ClearCalls++;
                Selection = new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Default,
                    Selection.Sequence + 1,
                    null,
                    "",
                    null,
                    "",
                    "",
                    "");
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, "startup.default", ""));
            }

            public UniTask<RbxWorldStartupSelection> ReadStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                ReadCalls++;
                return UniTask.FromResult(Selection);
            }
        }

        [Test]
        public void WorldLoadPage_StartupSection_ShowsSelectedWorldAndUtcTime_AndResetChoosesDefault()
        {
            StartupWorldRuntimeService service = new()
            {
                Selection = new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package,
                    3,
                    null,
                    "castle-world",
                    new DateTime(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    "manual",
                    "castle",
                    "")
            };
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                Label startup = root.Q<Label>("coreai-world-startup-selection");
                Button reset = root.Q<Button>("coreai-world-startup-reset");
                Assert.IsNotNull(startup, "a startup-aware service gets the startup section");
                Assert.IsNotNull(reset);
                Assert.AreEqual("Opens on start: castle-world (selected 2035-01-02 03:04:05 UTC)", startup.text);
                Assert.IsTrue(reset.enabledSelf);

                InvokeButton(reset);

                Assert.AreEqual(1, service.ClearCalls);
                Assert.AreEqual("Opens on start: default world", startup.text);
                Assert.IsFalse(reset.enabledSelf, "there is nothing left to reset");
                Assert.AreEqual(0, service.AppliedCount, "choosing the default world for the next start loads nothing");
                StringAssert.Contains(
                    "next start opens the default world",
                    root.Q<Label>("coreai-world-load-outcome").text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_StartupSection_WithoutSelection_ShowsDefaultWorld()
        {
            StartupWorldRuntimeService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();

                Assert.AreEqual(
                    "Opens on start: default world",
                    root.Q<Label>("coreai-world-startup-selection").text);
                Assert.IsFalse(root.Q<Button>("coreai-world-startup-reset").enabledSelf);
                Assert.AreEqual(1, service.ReadCalls);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_ConfirmTooltipAndOutcome_SayWhetherTheWorldReopensOnNextStart()
        {
            StartupWorldRuntimeService service = new()
            {
                ConfirmResult = new RbxWorldLoadResult(true, "", 2, false, "browser storage is not armed")
            };
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                RbxWorldLoadRequest unpersisted = service.Queue("castle");
                Button confirm = root.Q<Button>("coreai-world-load-confirm-" + unpersisted.RequestId);
                Assert.IsNotNull(confirm);
                StringAssert.Contains("reopen on the next start", confirm.tooltip);

                InvokeButton(confirm);

                Label outcome = root.Q<Label>("coreai-world-load-outcome");
                Assert.AreEqual(1, service.AppliedCount);
                StringAssert.Contains("will NOT reopen after a restart", outcome.text);
                StringAssert.Contains("browser storage is not armed", outcome.text);
                Assert.AreEqual(2, service.ReadCalls, "a confirmed load refreshes the startup section");

                service.ConfirmResult = new RbxWorldLoadResult(true, "", 2, true, "");
                RbxWorldLoadRequest persisted = service.Queue("castle");
                InvokeButton(root.Q<Button>("coreai-world-load-confirm-" + persisted.RequestId));

                Assert.AreEqual(2, service.AppliedCount);
                Assert.AreEqual("World loaded. It will also reopen on the next start.", outcome.text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_ServiceWithoutStartupSelection_HasNoStartupSection_AndSaysSo()
        {
            RecordingWorldRuntimeService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                RbxWorldLoadRequest request = service.RequestManualLoadAsync(
                    default,
                    "manual-no-startup").GetAwaiter().GetResult();
                Button confirm = root.Q<Button>("coreai-world-load-confirm-" + request.RequestId);

                Assert.IsNull(root.Q<Label>("coreai-world-startup-selection"));
                Assert.IsNull(root.Q<Button>("coreai-world-startup-reset"));
                Assert.AreEqual("Replace the live world with this saved world.", confirm.tooltip);

                InvokeButton(confirm);

                StringAssert.Contains(
                    "will NOT reopen after a restart: this world service keeps no startup selection",
                    root.Q<Label>("coreai-world-load-outcome").text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldLoadPage_RegisteredWithAnExplicitStartupSelection_ShowsIt()
        {
            StartupWorldRuntimeService startup = new()
            {
                Selection = new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package,
                    1,
                    null,
                    "explicit-world",
                    null,
                    "autosave",
                    "auto.world",
                    "")
            };
            CoreAI.Hub.HubPageRegistry registry = new();
            HubWorldLoadConfirmationPage page = HubModsPages.RegisterWorldLoadConfirmation(
                registry,
                new RecordingWorldRuntimeService(),
                startupSelection: startup);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();

                Assert.AreEqual(
                    "Opens on start: explicit-world",
                    root.Q<Label>("coreai-world-startup-selection").text);
                Assert.IsTrue(root.Q<Button>("coreai-world-startup-reset").enabledSelf);
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

        // ==================== A1-02: Hub writes keep the load order ====================

        private static long StoredLoadOrder(ILuaModSourceStore store, string id)
        {
            Assert.IsTrue(store.TryLoad(id, out _, out LuaModManifest manifest), "'" + id + "' must be stored.");
            return manifest.LoadOrder;
        }

        /// <summary>
        /// A1-02: a Hub save rewrites the manifest after the runtime load or reload. It used to build a new
        /// manifest without the load order, so every mod saved from the Hub lost its place.
        /// </summary>
        [Test]
        public void HubService_SaveOrReload_EditKeepsTheModsLoadOrder()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);

            service.SaveOrReload("first", "local x = 1");
            service.SaveOrReload("second", "local x = 2");
            service.SaveOrReload("first", "local x = 10");

            Assert.AreEqual(1L, StoredLoadOrder(store, "first"), "A Hub edit must keep the mod's place.");
            Assert.AreEqual(2L, StoredLoadOrder(store, "second"));
        }

        /// <summary>
        /// A1-02: a mod created in the Hub after another one, whose chunk reads that one's export at init,
        /// restarts after it although the ids sort the other way.
        /// </summary>
        [Test]
        public void HubService_SaveOrReload_NewModAfterAnExistingOne_RestartsAfterIt()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);
            service.SaveOrReload("zz-base", "mods_export('marker', 'base-ready')");
            service.SaveOrReload(
                "aa-user",
                "if mods_get('zz-base', 'marker') ~= 'base-ready' then error('zz-base has not started') end");

            LuaCsModRuntime restarted = new(sourceStore: store);

            Assert.AreEqual(2, restarted.RehydrateFromStore(LuaCapabilities.All),
                "The mod created in the Hub must restart after the mod it reads at init.");
            Assert.Greater(StoredLoadOrder(store, "aa-user"), StoredLoadOrder(store, "zz-base"));
        }

        /// <summary>
        /// A Hub save into a store the runtime does not write (the runtime persisted nothing there) is a
        /// first-time write: it gets the next load order after every stored mod.
        /// </summary>
        [Test]
        public void HubService_SaveOrReload_IntoAStoreTheRuntimeDoesNotWrite_StampsTheNextLoadOrder()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            store.Save("existing", "local x = 1", new LuaModManifest
            {
                Id = "existing",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = false,
                LoadOrder = 4
            });
            LuaCsModRuntime runtime = new();
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);

            service.SaveOrReload("new-mod", "local x = 2");

            Assert.AreEqual(5L, StoredLoadOrder(store, "new-mod"));
            Assert.AreEqual(4L, StoredLoadOrder(store, "existing"));
        }

        /// <summary>
        /// A1-02: applying a bundled update to a loaded mod reloads it and rewrites its manifest twice (the
        /// Hub save, then the seed markers); neither may move the mod in the restart order. Uses the
        /// shipped <c>sample_welcome</c> resource, the only source ApplyBundledUpdate reads.
        /// </summary>
        [Test]
        public void HubService_ApplyBundledUpdate_KeepsTheModsLoadOrder()
        {
            const string welcomeId = "sample_welcome";
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceNullLogger(),
                ModSourceStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = new LuaCsRbxApiBindings(),
                RegisterWorldEditBuildBindings = false
            });
            LuaCsModRuntime runtime = stack.Runtime;
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);
            store.Save(welcomeId, "local seeded = 1", new LuaModManifest
            {
                Id = welcomeId,
                Origin = "resources",
                SeededVersion = "0.0.1",
                Capabilities = LuaCapabilities.All.ToString(),
                Active = true
            });
            runtime.LoadMod(actorContext, "earlier", "local x = 1");
            runtime.LoadMod(actorContext, welcomeId, "local seeded = 1");
            long placed = StoredLoadOrder(store, welcomeId);
            Assert.Greater(placed, StoredLoadOrder(store, "earlier"), "precondition: the runtime placed the mod");

            Assert.IsTrue(service.ApplyBundledUpdate(welcomeId), "The shipped sample_welcome must apply.");

            Assert.AreEqual(placed, StoredLoadOrder(store, welcomeId), "A bundled update must keep the mod's place.");
            Assert.IsTrue(store.TryLoad(welcomeId, out _, out LuaModManifest updated));
            Assert.AreNotEqual("0.0.1", updated.SeededVersion, "precondition: the bundled update was applied");
        }

        private const string HubCastleSource = "--[[@coreai\ncapabilities: All\n]]\n" + @"
            local root = Instance.new('Folder')
            root.Name = 'CastleShowcase'
            root.Parent = workspace
            for index = 1, 25 do
                local part = Instance.new('Part')
                part.Name = 'Castle' .. index
                part.Parent = root
            end";

        private static LuaCsModStack HubRbxStack(LuaCsRbxApiBindings bindings, ILuaModSourceStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceNullLogger(),
                ModSourceStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
        }

        private static int CountWorkspaceChildren(LuaCsRbxApiBindings bindings, string name)
        {
            int count = 0;
            foreach (CoreAI.Mods.Rbx.Instances.RbxInstance child in bindings.Registry.WorldRoot.GetChildren())
            {
                if (child.Name == name)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// HUB-CRASH R6: every Hub Save &amp; run of sample_castle3d added its 126 instances again. A save
        /// cleans the previous run's startup objects by default and says how many it cleaned.
        /// </summary>
        [Test]
        public void HubService_SaveAndRun_CleansThePreviousRunsStartupObjectsByDefault_AndSaysHowMany()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = HubRbxStack(bindings, store);
            IHubModService service = new LuaCsModRuntimeHubService(stack.Runtime, actorContext, store);

            HubModSaveResult first = service.SaveOrReload("castle", HubCastleSource, ModReloadMode.CleanStartupObjects);
            service.SaveOrReload("castle", HubCastleSource + "\n-- edit 1");
            HubModSaveResult third = service.SaveOrReload(
                "castle", HubCastleSource + "\n-- edit 2", ModReloadMode.CleanStartupObjects);

            Assert.IsFalse(first.Reloaded, "the first save loads the mod");
            Assert.AreEqual("Saved & ran 'castle'.", first.Describe());
            Assert.IsTrue(third.Reloaded);
            Assert.IsNotNull(third.Reload, "the reload reports what it did through the runtime");
            Assert.AreEqual(26, third.Reload.CleanedObjects);
            Assert.AreEqual("Saved & ran 'castle'; cleaned 26 objects of the previous run.", third.Describe());
            Assert.AreEqual(1, CountWorkspaceChildren(bindings, "CastleShowcase"),
                "three saves, the mode-less one included, leave one castle");
        }

        /// <summary>Negative twin: with "Keep objects on Save &amp; run" every save builds next to the previous runs.</summary>
        [Test]
        public void HubService_SaveAndRun_InKeepMode_KeepsThePreviousRunsObjects_AndSaysSo()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = HubRbxStack(bindings, store);
            IHubModService service = new LuaCsModRuntimeHubService(stack.Runtime, actorContext, store);
            service.SaveOrReload("castle", HubCastleSource);

            HubModSaveResult kept = service.SaveOrReload(
                "castle", HubCastleSource + "\n-- keep", ModReloadMode.KeepObjects);

            Assert.AreEqual(ModReloadMode.KeepObjects, kept.Mode);
            Assert.AreEqual("Saved & ran 'castle'; kept 26 objects of the previous run.", kept.Describe());
            Assert.AreEqual(2, CountWorkspaceChildren(bindings, "CastleShowcase"));
        }

        /// <summary>
        /// With the Roblox API wired, the "Add" template runs per-frame work on Heartbeat and periodic work
        /// in a task loop (each resume under the scheduler's short budget), never a legacy hooks_every
        /// timer, whose calls run under the long handler budget; and it runs as written.
        /// </summary>
        [Test]
        public void HubService_NewModTemplate_WithTheRobloxApi_UsesRunServiceAndTask_AndRuns()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = HubRbxStack(bindings, store);
            IHubModService service = new LuaCsModRuntimeHubService(stack.Runtime, actorContext, store);

            string template = service.NewModTemplate;

            Assert.AreEqual(HubModTemplates.RbxApi, template);
            StringAssert.Contains("RunService.Heartbeat:Connect(", template);
            StringAssert.Contains("task.spawn(", template);
            StringAssert.Contains("task.wait(", template);
            StringAssert.DoesNotContain("hooks_every", template);

            service.SaveOrReload("new_mod", template);
            for (int frame = 0; frame < 3; frame++)
            {
                bindings.Scheduler.Advance(1d / 60d);
                stack.Runtime.Tick(1d / 60d);
            }

            Assert.IsEmpty(service.RecentErrorEntries("new_mod"), "the template runs without an error");
            Assert.IsTrue(service.RecentReports("new_mod").Any(report => report.Message == "new_mod tick"),
                "the template's task loop printed");
        }

        /// <summary>Negative twin: a composition without the Roblox API keeps the hooks_every template, which runs there.</summary>
        [Test]
        public void HubService_NewModTemplate_WithoutTheRobloxApi_KeepsHooksEvery_AndRuns()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);

            string template = service.NewModTemplate;

            Assert.AreEqual(HubModTemplates.Legacy, template);
            Assert.AreEqual(HubModEditorPage.NewModTemplate, template);
            StringAssert.Contains("hooks_every(", template);
            StringAssert.DoesNotContain("RunService", template);

            service.SaveOrReload("new_mod", template);
            runtime.Tick(1.1d);

            Assert.IsTrue(service.RecentReports("new_mod").Any(report => report.Message == "new_mod tick"));
        }

        /// <summary>
        /// The Hub usually holds a facade over the active world session, which cannot say whether the
        /// Roblox API is wired; the host passes the answer.
        /// </summary>
        [Test]
        public void HubService_NewModTemplate_FollowsTheHostsAnswerForAFacade()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);

            IHubModService withApi = new LuaCsModRuntimeHubService(
                runtime, actorContext, store, rbxApiAvailable: true);
            IHubModService withoutApi = new LuaCsModRuntimeHubService(
                runtime, actorContext, store, rbxApiAvailable: false);

            Assert.AreEqual(HubModTemplates.RbxApi, withApi.NewModTemplate);
            Assert.AreEqual(HubModTemplates.Legacy, withoutApi.NewModTemplate);
        }

        /// <summary>
        /// A mod the runtime suspended after repeated budget trips shows as suspended in the list, and
        /// starting it by hand clears the suspension even where the runtime does not persist loads.
        /// </summary>
        [Test]
        public void HubService_ASuspendedMod_IsListedAsSuspended_AndStartingItByHandClearsIt()
        {
            ActorContext actorContext = CreateHostActor();
            FakeSourceStore store = new();
            store.Save("spinner", "local x = 1", new LuaModManifest
            {
                Id = "spinner",
                Capabilities = LuaCapabilities.All.ToString(),
                Active = false,
                SuspendedAfterBudgetTrips = true
            });
            LuaCsModRuntime runtime = new(sourceStore: store, autoPersistMods: false);
            IHubModService service = new LuaCsModRuntimeHubService(runtime, actorContext, store);

            HubModRecord listed = service.ListMods().Single(record => record.Id == "spinner");
            Assert.IsTrue(listed.SuspendedAfterBudgetTrips);
            Assert.IsFalse(listed.StoredActive);

            service.Enable("spinner");

            Assert.IsTrue(runtime.IsLoaded(actorContext, "spinner"));
            Assert.IsTrue(store.TryLoad("spinner", out _, out LuaModManifest started));
            Assert.IsTrue(started.Active);
            Assert.IsFalse(started.SuspendedAfterBudgetTrips);
            Assert.IsFalse(service.ListMods().Single(record => record.Id == "spinner").SuspendedAfterBudgetTrips);
        }
#endif
    }
}
#endif

using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Mods.WorldPackages;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// One-call registration of the CoreAI Mods tab into a <see cref="HubPageRegistry"/>: a single
    /// grouped page with [Mods, Logs] sub-tabs (via <see cref="HubSubTabPage"/>). The page is
    /// registered as a lazy factory (its content is built only when the tab is first activated) at
    /// order 300 by default, so it slots after the built-in Chat (0) / Settings (100) / Statistics
    /// (200) tabs. Overloads accept the Lua-CSharp <see cref="LuaCsModRuntime"/> plus the shared
    /// <see cref="ILuaModSourceStore"/>, or a pre-built <see cref="IHubModService"/> for full control.
    /// </summary>
    public static class HubModsPages
    {
        /// <summary>Registry id of the Mods page.</summary>
        public const string ModsPageId = HubModsPage.DefaultPageId;

        /// <summary>Page id of the Mod Logs page, shown as the Logs sub-tab under the Mods tab.</summary>
        public const string LogsPageId = HubModLogsPage.DefaultPageId;

        /// <summary>Default Hub tab order for the Mods page (after Chat/Settings/Statistics).</summary>
        public const int DefaultOrder = 300;

        /// <summary>Default order of the Mod Logs child page inside the Mods tab.</summary>
        public const int DefaultLogsOrder = 350;

        /// <summary>Registry id of the player-facing pending world-load confirmation page.</summary>
        public const string WorldLoadsPageId = "coreai.hub.world-loads";

        /// <summary>Default Hub tab order for world-load confirmations.</summary>
        public const int DefaultWorldLoadsOrder = 250;

        /// <summary>Registers the Mods page backed by the Lua-CSharp <see cref="LuaCsModRuntime"/>.</summary>
        /// <param name="registry">Target registry. Required.</param>
        /// <param name="runtime">Live mod runtime (also driven by the manage_mods LLM tool). Required.</param>
        /// <param name="actorContext">Trusted host actor performing Hub mod operations.</param>
        /// <param name="sourceStore">Package store persisting mod source + manifest (may be null).</param>
        /// <param name="grant">Capability ceiling applied to every mod loaded from the UI.</param>
        /// <param name="allowFull">When true, <see cref="LuaCapabilities.Full"/> may be granted from the header.</param>
        /// <param name="order">Hub tab order (default 300).</param>
        public static void Register(
            HubPageRegistry registry,
            ILuaModRuntime runtime,
            ActorContext actorContext,
            ILuaModSourceStore sourceStore = null,
            LuaCapabilities grant = LuaCapabilities.All,
            bool allowFull = false,
            int order = DefaultOrder)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            Register(
                registry,
                new LuaCsModRuntimeHubService(runtime, actorContext, sourceStore, grant, allowFull),
                order);
        }

        /// <summary>Registers the Mods page backed by a pre-built <see cref="IHubModService"/>.</summary>
        public static void Register(HubPageRegistry registry, IHubModService service, int order = DefaultOrder)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            // WHY: one top-level Mods tab with [Mods, Logs] sub-tabs instead of two top tabs, keeping the
            // tab bar compact; HubSubTabPage proxies activation lifecycle to whichever sub-tab is visible.
            registry.Register(
                ModsPageId,
                () => new HubSubTabPage(
                    ModsPageId,
                    "Mods",
                    order,
                    new HubModsPage(service, order),
                    new HubModLogsPage(service, DefaultLogsOrder)),
                order);
        }

        /// <summary>Registers the reusable player confirmation surface for pending world loads.</summary>
        public static HubWorldLoadConfirmationPage RegisterWorldLoadConfirmation(
            HubPageRegistry registry,
            IRbxWorldRuntimeService service,
            Action attentionRequested = null,
            int order = DefaultWorldLoadsOrder)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            HubWorldLoadConfirmationPage page = new(service, attentionRequested, order);
            registry.Register(WorldLoadsPageId, () => page, order);
            return page;
        }
    }

    /// <summary>Runtime UI Toolkit surface that exposes only metadata and one-shot load decisions.</summary>
    public sealed class HubWorldLoadConfirmationPage : IHubPage
    {
        private readonly IRbxWorldRuntimeService _service;
        private readonly Action _attentionRequested;
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

        private VisualElement _root;
        private VisualElement _rows;
        private Label _status;
        private IVisualElementScheduledItem _refreshSchedule;
        private bool _subscribed;
        private bool _destroyed;

        /// <summary>Creates a confirmation page and immediately listens for future requests.</summary>
        public HubWorldLoadConfirmationPage(
            IRbxWorldRuntimeService service,
            Action attentionRequested = null,
            int order = HubModsPages.DefaultWorldLoadsOrder)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _attentionRequested = attentionRequested;
            Order = order;
            Subscribe();
        }

        /// <inheritdoc />
        public string PageId => HubModsPages.WorldLoadsPageId;

        /// <inheritdoc />
        public string DisplayName => "World Loads";

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public void OnActivated()
        {
            Refresh(false);
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
            if (_destroyed)
            {
                return;
            }

            _destroyed = true;
            _refreshSchedule?.Pause();
            Unsubscribe();
        }

        private object Build()
        {
            if (_root != null)
            {
                return _root;
            }

            _root = new VisualElement { name = "coreai-hub-world-loads" };
            _root.style.flexGrow = 1f;
            _root.style.flexDirection = FlexDirection.Column;
            _root.Add(HubModWidgets.MakeTitle("World load approval"));
            _root.Add(HubModWidgets.MakeNote(
                "A saved world can replace the live world only after you approve it here. " +
                "The request shows metadata only; rejecting it leaves the current world unchanged."));

            _status = HubModWidgets.MakeStatus();
            _status.name = "coreai-hub-world-loads-status";
            _root.Add(_status);

            ScrollView scroll = new(ScrollViewMode.Vertical)
            {
                name = "coreai-hub-world-loads-scroll"
            };
            scroll.style.flexGrow = 1f;
            _rows = scroll.contentContainer;
            _root.Add(scroll);

            _refreshSchedule = _root.schedule.Execute(() => Refresh(false)).Every(1000);
            Refresh(false);
            return _root;
        }

        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            _service.ManualLoadConfirmationRequested += OnConfirmationRequested;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            _service.ManualLoadConfirmationRequested -= OnConfirmationRequested;
            _subscribed = false;
        }

        private void OnConfirmationRequested(RbxPendingWorldLoadRequest request)
        {
            if (_destroyed || request == null)
            {
                return;
            }

            _attentionRequested?.Invoke();
            Refresh(true);
        }

        private void Refresh(bool focusFirstDecision)
        {
            if (_destroyed || _rows == null)
            {
                return;
            }

            IReadOnlyList<RbxPendingWorldLoadRequest> pending;
            try
            {
                pending = _service.GetPendingManualLoads();
            }
            catch (Exception ex)
            {
                _status.text = "Could not read pending world loads: " + ex.Message;
                _status.style.color = HubModWidgets.Danger;
                return;
            }

            List<RbxPendingWorldLoadRequest> ordered = new(pending ?? Array.Empty<RbxPendingWorldLoadRequest>());
            ordered.Sort(CompareRequests);
            _rows.Clear();

            Button firstDecision = null;
            bool hasPendingRows = false;
            for (int index = 0; index < ordered.Count; index++)
            {
                RbxPendingWorldLoadRequest request = ordered[index];
                if (request == null || _inFlight.Contains(request.RequestId))
                {
                    continue;
                }

                VisualElement row = BuildRow(request, out Button confirmButton);
                firstDecision ??= confirmButton;
                _rows.Add(row);
                hasPendingRows = true;
            }

            if (!hasPendingRows)
            {
                _rows.Add(HubModWidgets.MakeNote("No world load is waiting for player confirmation."));
            }

            _status.text = hasPendingRows
                ? "Review each request before continuing."
                : "No pending requests.";
            _status.style.color = HubModWidgets.Muted;

            if (focusFirstDecision && firstDecision != null)
            {
                firstDecision.Focus();
            }
        }

        private VisualElement BuildRow(
            RbxPendingWorldLoadRequest request,
            out Button confirmButton)
        {
            VisualElement panel = HubModWidgets.MakePanel();
            panel.name = "coreai-world-load-row-" + request.RequestId;

            Label slot = HubModWidgets.MakeFieldLabel("Slot: " + request.Slot);
            slot.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(slot);
            panel.Add(HubModWidgets.MakeMutedLabel("World: " + request.WorldId));
            panel.Add(HubModWidgets.MakeMutedLabel(
                "Requested: " + FormatUtc(request.RequestedAtUtc)
                + "    Expires: " + FormatUtc(request.ExpiresAtUtc)));

            VisualElement actions = new();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.marginTop = 6f;

            string requestId = request.RequestId;
            confirmButton = HubModWidgets.MakeButton(
                "Confirm load",
                () => Decide(requestId, true));
            confirmButton.name = "coreai-world-load-confirm-" + requestId;
            confirmButton.tooltip = "Replace the live world with this saved world.";
            actions.Add(confirmButton);

            Button rejectButton = HubModWidgets.MakeDangerButton(
                "Reject",
                () => Decide(requestId, false));
            rejectButton.name = "coreai-world-load-reject-" + requestId;
            rejectButton.tooltip = "Dismiss this request without changing the live world.";
            actions.Add(rejectButton);
            panel.Add(actions);
            return panel;
        }

        private void Decide(string requestId, bool playerConfirmed)
        {
            if (_destroyed || string.IsNullOrEmpty(requestId) || !_inFlight.Add(requestId))
            {
                return;
            }

            VisualElement row = _rows?.Q<VisualElement>("coreai-world-load-row-" + requestId);
            if (row != null)
            {
                row.SetEnabled(false);
                row.RemoveFromHierarchy();
            }

            _status.text = playerConfirmed ? "Loading the confirmed world…" : "Rejecting the world load…";
            _status.style.color = HubModWidgets.Muted;
            DecideAsync(requestId, playerConfirmed);
        }

        private async void DecideAsync(string requestId, bool playerConfirmed)
        {
            try
            {
                RbxWorldLoadResult result = await _service.ConfirmManualLoadAsync(
                    requestId,
                    playerConfirmed);
                if (_destroyed)
                {
                    return;
                }

                if (!playerConfirmed)
                {
                    _status.text = "World load rejected. The live world was not changed.";
                    _status.style.color = HubModWidgets.Muted;
                }
                else if (result.Success)
                {
                    _status.text = "World loaded successfully. Active mods started: "
                        + result.ActiveModsStarted + ".";
                    _status.style.color = HubModWidgets.Accent;
                }
                else
                {
                    _status.text = "World load failed: " + result.Error;
                    _status.style.color = HubModWidgets.Danger;
                }
            }
            catch (Exception ex)
            {
                if (!_destroyed)
                {
                    _status.text = "World load decision failed: " + ex.Message;
                    _status.style.color = HubModWidgets.Danger;
                }
            }
            finally
            {
                _inFlight.Remove(requestId);
                Refresh(false);
            }
        }

        private static int CompareRequests(
            RbxPendingWorldLoadRequest left,
            RbxPendingWorldLoadRequest right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            int requestedComparison = left.RequestedAtUtc.CompareTo(right.RequestedAtUtc);
            return requestedComparison != 0
                ? requestedComparison
                : string.Compare(left.RequestId, right.RequestId, StringComparison.Ordinal);
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }
    }
}

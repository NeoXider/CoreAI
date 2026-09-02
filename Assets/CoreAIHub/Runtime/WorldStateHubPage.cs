using System;
using CoreAI.Hub;
using CoreAI.Infrastructure.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    public sealed class WorldStateHubPage : HubPageBase
    {
        public const string DefaultPageId = "coreai.hub.worldstate";

        /// <summary>Prefix of the saved-state line asserted by the G11 §6.5 browser run.</summary>
        internal const string SavedStatePrefix = "Has saved state: ";

        /// <summary>Shown while the browser has been asked to flush but has not answered yet.</summary>
        public const string FlushPendingText =
            "Browser storage: flushing — not durable yet.";

        /// <summary>Shown once the browser's syncfs callback reported success.</summary>
        public const string FlushConfirmedText =
            "Browser storage: flush confirmed — this state survives a reload.";

        /// <summary>Shown when the browser's syncfs callback reported a failure.</summary>
        public const string FlushUnconfirmedText =
            "Browser storage: flush NOT confirmed — this change may not survive a reload.";

        private readonly IWorldStateManager _manager;

        private Label _statusLabel;
        private Label _durabilityLabel;
        private Button _resetButton;
        private Button _saveButton;
        private bool _subscribed;

        // WHY: G11 §6.5 / W3.5 — "Has saved state" must never flip on IDBFS request issuance, only
        // once the browser's FS.syncfs completion callback answers. While this is true the status
        // label keeps its last confirmed value.
        private bool _flushPending;

        public WorldStateHubPage(IWorldStateManager manager,
            string pageId = DefaultPageId,
            string displayName = "World",
            int order = 300)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "World" : displayName,
                order)
        {
            _manager = manager;
        }

        public override Func<object> CreatePageContent => BuildContent;

        private object BuildContent()
        {
            VisualElement panel = new() { name = "coreai-hub-worldstate-page" };
            panel.AddToClassList("coreai-hub-page");

            Label title = new("World State")
            {
                name = "coreai-hub-worldstate-title"
            };
            title.style.color = new Color(0.302f, 0.816f, 0.882f, 1f);
            title.style.fontSize = 18f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 8f;
            panel.Add(title);

            _statusLabel = new Label(
                ComposeStatusText(false, string.Empty, _manager != null && _manager.HasSavedState))
            {
                name = "coreai-hub-worldstate-status"
            };
            _statusLabel.style.color = new Color(0.863f, 0.91f, 0.941f, 1f);
            _statusLabel.style.fontSize = 14f;
            _statusLabel.style.marginBottom = 12f;
            panel.Add(_statusLabel);

            panel.Add(new Label("Reset all AI/mod-spawned world objects and delete the save file.")
            {
                style =
                {
                    color = new Color(0.7f, 0.7f, 0.7f, 1f),
                    fontSize = 12f,
                    marginBottom = 6f,
                    whiteSpace = WhiteSpace.Normal
                }
            });

            VisualElement actions = new()
            {
                name = "coreai-hub-actions",
                style = { flexDirection = FlexDirection.Row, marginBottom = 8f }
            };
            actions.AddToClassList("coreai-hub-actions");

            _resetButton = new Button(OnResetClicked)
            {
                text = "Reset World",
                name = "coreai-hub-worldstate-reset"
            };
            _resetButton.AddToClassList("coreai-hub-action-button");
            _resetButton.style.color = new Color(1f, 0.4f, 0.4f, 1f);
            actions.Add(_resetButton);

            _saveButton = new Button(OnSaveClicked)
            {
                text = "Save Now",
                name = "coreai-hub-worldstate-save"
            };
            _saveButton.AddToClassList("coreai-hub-action-button");
            actions.Add(_saveButton);

            panel.Add(actions);

            _durabilityLabel = new Label(_flushPending ? FlushPendingText : string.Empty)
            {
                name = "coreai-hub-worldstate-durability"
            };
            _durabilityLabel.style.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            _durabilityLabel.style.fontSize = 12f;
            _durabilityLabel.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(_durabilityLabel);

            if (_flushPending)
            {
                _saveButton.SetEnabled(false);
                _resetButton.SetEnabled(false);
            }

            // WHY: Guarded subscribe + the OnDestroyed unsubscribe below: without them every page
            // rebuild added another handler and the DI-singleton manager pinned dead VisualElement
            // trees through the event delegate.
            if (_manager != null && !_subscribed)
            {
                _manager.StateReset += OnStateReset;
                _subscribed = true;
            }

            return panel;
        }

        public override void OnDestroyed()
        {
            if (_subscribed && _manager != null)
            {
                _manager.StateReset -= OnStateReset;
                _subscribed = false;
            }
        }

        private void OnResetClicked()
        {
            if (_manager == null || _flushPending)
            {
                return;
            }

            // WHY: BeginFlush before Reset — Reset raises StateReset synchronously, and the handler
            // would otherwise publish "No" before the browser deleted anything durably.
            BeginFlush();
            _manager.Reset();
            _manager.ConfirmDurability(OnDurabilityConfirmed);
        }

        private void OnSaveClicked()
        {
            if (_manager == null || _flushPending)
            {
                return;
            }

            BeginFlush();
            _manager.Save();
            _manager.ConfirmDurability(OnDurabilityConfirmed);
        }

        private void BeginFlush()
        {
            _flushPending = true;
            _saveButton?.SetEnabled(false);
            _resetButton?.SetEnabled(false);
            if (_durabilityLabel != null)
            {
                _durabilityLabel.text = FlushPendingText;
            }
        }

        private void OnDurabilityConfirmed(bool durable)
        {
            _flushPending = false;
            _saveButton?.SetEnabled(true);
            _resetButton?.SetEnabled(true);
            if (_durabilityLabel != null)
            {
                _durabilityLabel.text = durable ? FlushConfirmedText : FlushUnconfirmedText;
            }

            RefreshStatus();
        }

        private void OnStateReset()
        {
            RefreshStatus();
        }

        /// <summary>
        /// Status-text policy. While a browser flush is in flight the label must keep its last
        /// confirmed text: G11 §6.5 and MVP2.5 gate W3.5 fail a page that reports a durable save at
        /// IDBFS request-issuance time, before the <c>FS.syncfs</c> completion callback answers.
        /// </summary>
        internal static string ComposeStatusText(
            bool flushPending,
            string lastConfirmedText,
            bool hasSavedState)
        {
            return flushPending
                ? lastConfirmedText
                : SavedStatePrefix + (hasSavedState ? "Yes" : "No");
        }

        private void RefreshStatus()
        {
            if (_statusLabel == null)
            {
                return;
            }

            _statusLabel.text = ComposeStatusText(
                _flushPending,
                _statusLabel.text,
                _manager != null && _manager.HasSavedState);
        }
    }
}

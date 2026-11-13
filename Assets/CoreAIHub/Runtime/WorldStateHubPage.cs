using System;
using CoreAI.Hub;
using CoreAI.Infrastructure.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// The legacy "AI objects" page over <see cref="IWorldStateManager"/>: the objects C# world commands spawned
    /// (<c>world_command</c>) and their save file, with Remove and Save. It never touches a Roblox-style world (its
    /// Terrain, Workspace or a kept world); the mods package's World page saves, loads and starts those.
    /// </summary>
    /// <remarks>
    /// WHY renamed and not hidden: the page cannot see whether a scene keeps a Roblox-style world, and hiding it by a
    /// guess would take the legacy objects' only controls from the scenes that still spawn them. Named for what it
    /// clears, it can no longer pass for the world's reset.
    /// </remarks>
    public sealed class WorldStateHubPage : HubPageBase
    {
        public const string DefaultPageId = "coreai.hub.worldstate";

        /// <summary>The tab name of the page; it says which objects it manages.</summary>
        public const string DefaultDisplayName = "AI objects";

        /// <summary>Prefix of the saved-state line asserted by the G11 §6.5 browser run.</summary>
        internal const string SavedStatePrefix = "Has saved state: ";

        /// <summary>What the page acts on, and what it never touches.</summary>
        internal const string ScopeText =
            "Objects that AI world commands (world_command) spawned, and their save file. Remove AI objects deletes "
            + "both; Save Now writes the file. Neither touches Terrain, the Workspace or a world kept between "
            + "starts: save, load or start a new world on the World page.";

        /// <summary>Shown while a local storage operation is running. The legacy member name is retained.</summary>
        public const string FlushPendingText =
            "Local storage: operation in progress.";

        /// <summary>Shown after local success when Unity automatic persistence is armed.</summary>
        public const string FlushConfirmedText =
            "Local storage operation completed; automatic persistence is armed. Browser completion is not confirmed.";

        /// <summary>Shown when local success or automatic persistence cannot be confirmed.</summary>
        public const string FlushUnconfirmedText =
            "Storage operation unconfirmed; automatic persistence may be unavailable.";

        private readonly IWorldStateManager _manager;

        private Label _statusLabel;
        private Label _durabilityLabel;
        private Button _resetButton;
        private Button _saveButton;
        private bool _subscribed;

        private bool _flushPending;
        private bool _destroyed;

        public WorldStateHubPage(IWorldStateManager manager,
            string pageId = DefaultPageId,
            string displayName = DefaultDisplayName,
            int order = 300)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName,
                order)
        {
            _manager = manager;
        }

        public override Func<object> CreatePageContent => BuildContent;

        private object BuildContent()
        {
            VisualElement panel = new() { name = "coreai-hub-worldstate-page" };
            panel.AddToClassList("coreai-hub-page");

            Label title = new("AI objects (legacy)")
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

            panel.Add(new Label(ScopeText)
            {
                name = "coreai-hub-worldstate-scope",
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
                text = "Remove AI objects",
                name = "coreai-hub-worldstate-reset",
                tooltip = "Remove the objects AI world commands spawned and delete their save file."
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
            _destroyed = true;
            if (_subscribed && _manager != null)
            {
                _manager.StateReset -= OnStateReset;
                _subscribed = false;
            }
        }

        private void OnResetClicked()
        {
            WriteState(true);
        }

        private void OnSaveClicked()
        {
            WriteState(false);
        }

        private void WriteState(bool reset)
        {
            if (_manager == null || _flushPending || _destroyed)
            {
                return;
            }

            BeginFlush();
            WorldStateWriteResult result;
            try
            {
                if (_manager is IWorldStateWriteResults results)
                {
                    result = reset ? results.ResetWithResult() : results.SaveWithResult();
                }
                else
                {
                    if (reset)
                    {
                        _manager.Reset();
                    }
                    else
                    {
                        _manager.Save();
                    }

                    result = new WorldStateWriteResult(WorldStateWriteStatus.Unsupported,
                        error: "This manager supplies no per-operation storage result.");
                }
            }
            catch (OperationCanceledException)
            {
                result = new WorldStateWriteResult(WorldStateWriteStatus.Cancelled);
            }
            catch (Exception ex)
            {
                result = new WorldStateWriteResult(WorldStateWriteStatus.Failed, error: ex.Message);
            }

            CompleteWrite(result);
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

        private void CompleteWrite(WorldStateWriteResult result)
        {
            _flushPending = false;
            if (_destroyed)
            {
                return;
            }

            _saveButton?.SetEnabled(true);
            _resetButton?.SetEnabled(true);
            if (_durabilityLabel != null)
            {
                _durabilityLabel.text = result.IsConfirmed ? FlushConfirmedText
                    : result.LocalOperationSucceeded
                        ? "Local storage operation completed; automatic persistence is NOT armed."
                        : result.Status == WorldStateWriteStatus.Cancelled
                            ? "Local storage operation cancelled; completion is unconfirmed."
                            : FlushUnconfirmedText + (string.IsNullOrEmpty(result.Error) ? "" : " " + result.Error);
            }

            RefreshStatus();
        }

        private void OnStateReset()
        {
            RefreshStatus();
        }

        /// <summary>
        /// Retains the previous local-state inventory while a storage operation is running.
        /// Inventory describes file presence, not browser transaction completion.
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

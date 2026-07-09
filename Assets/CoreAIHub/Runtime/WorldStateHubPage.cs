using System;
using CoreAI.Hub;
using CoreAI.Infrastructure.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    public sealed class WorldStateHubPage : IHubPage
    {
        public const string DefaultPageId = "coreai.hub.worldstate";

        private readonly IWorldStateManager _manager;

        private Label _statusLabel;
        private Button _resetButton;
        private Button _saveButton;

        public WorldStateHubPage(IWorldStateManager manager,
            string pageId = DefaultPageId,
            string displayName = "World",
            int order = 300)
        {
            _manager = manager;
            PageId = string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "World" : displayName;
            Order = order;
        }

        public string PageId { get; }
        public string DisplayName { get; }
        public int Order { get; }
        public Func<object> CreatePageContent => BuildContent;

        public void OnActivated() { }
        public void OnDeactivated() { }
        public void OnDestroyed() { }

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

            _statusLabel = new("Has saved state: " + (_manager.HasSavedState ? "Yes" : "No"))
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

            if (_manager != null)
            {
                _manager.StateReset += OnStateReset;
            }

            return panel;
        }

        private void OnResetClicked()
        {
            if (_manager == null)
            {
                return;
            }

            _manager.Reset();
            RefreshStatus();
        }

        private void OnSaveClicked()
        {
            if (_manager == null)
            {
                return;
            }

            _manager.Save();
            RefreshStatus();
        }

        private void OnStateReset()
        {
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = "Has saved state: " + (_manager != null && _manager.HasSavedState ? "Yes" : "No");
            }
        }
    }
}

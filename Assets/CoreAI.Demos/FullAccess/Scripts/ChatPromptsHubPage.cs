using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit Hub page that replaces the floating IMGUI prompt panel (F8). It reads the preset prompts
    /// from a GUI-less <see cref="ChatPromptButtonsController"/> driver and renders one button per prompt;
    /// clicking a button inserts that prompt into the chat input via the driver, so testers can review and
    /// edit it before sending. Renders a setup note when no driver is present.
    /// </summary>
    public sealed class ChatPromptsHubPage : HubPageBase
    {
        /// <summary>Default registry id for the Prompts page.</summary>
        public const string DefaultPageId = "coreai.demo.fullaccess.prompts";

        private readonly Func<ChatPromptButtonsController> _driverProvider;

        private ChatPromptButtonsController _driver;
        private Label _statusLabel;

        /// <param name="driverProvider">Resolves the scene's prompt driver (may return null).</param>
        public ChatPromptsHubPage(
            Func<ChatPromptButtonsController> driverProvider,
            string pageId = DefaultPageId,
            string displayName = "Prompts",
            int order = 15)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Prompts" : displayName,
                order)
        {
            _driverProvider = driverProvider;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("Prompt Templates", out VisualElement body);

            _driver = TryResolveDriver();
            if (_driver == null)
            {
                body.Add(DemoHubWidgets.MakeNote(
                    "No ChatPromptButtonsController driver was found in the scene. Add one with a list of " +
                    "preset prompts, then reopen this tab."));
                return scroll;
            }

            body.Add(DemoHubWidgets.MakeBody(
                "Click a prompt to insert it into the chat input; review and edit it before sending."));

            _statusLabel = DemoHubWidgets.MakeBody(_driver.Status);
            body.Add(_statusLabel);

            IReadOnlyList<ChatPromptButtonsController.PromptButton> prompts = _driver.Prompts;
            if (prompts == null || prompts.Count == 0)
            {
                body.Add(DemoHubWidgets.MakeNote("This driver has no prompts configured."));
                return scroll;
            }

            foreach (ChatPromptButtonsController.PromptButton prompt in prompts)
            {
                if (prompt == null || string.IsNullOrWhiteSpace(prompt.Prompt))
                {
                    continue;
                }

                string text = prompt.Prompt;
                Button button = DemoHubWidgets.MakeButton(prompt.Label, () =>
                {
                    _driver.InsertOrSubmit(text);
                    if (_statusLabel != null)
                    {
                        _statusLabel.text = _driver.Status;
                    }
                });
                button.style.marginTop = 3f;
                body.Add(button);
            }

            return scroll;
        }

        private ChatPromptButtonsController TryResolveDriver()
        {
            try
            {
                return _driverProvider?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

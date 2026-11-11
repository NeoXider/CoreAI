using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The host a game gets when it switches runtime UI off: every call fails with <c>UI_DISABLED</c>,
    /// nothing is created, and no text field is ever focused.
    /// </summary>
    public sealed class DisabledRuntimeUiHost : IRuntimeUiHost
    {
        private static readonly IReadOnlyList<RuntimeUiScreen> NoScreens = Array.Empty<RuntimeUiScreen>();

        /// <summary>Creates the switched-off host.</summary>
        public DisabledRuntimeUiHost()
        {
            Theme = new RuntimeUiTheme();
            Templates = new UxmlTemplateRegistry();
            Assets = new RuntimeUiAssetRegistry(false);
        }

        /// <inheritdoc />
        public event Action<bool> TextInputFocusChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public event Action<bool> PointerOverUiChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public event Action<RuntimeUiScreen> ScreenCreated
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public event Action<RuntimeUiScreen> ScreenDestroyed
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public bool IsEnabled => false;

        /// <inheritdoc />
        public bool IsTextInputFocused => false;

        /// <inheritdoc />
        public bool IsPointerOverUi => false;

        /// <inheritdoc />
        public RuntimeUiTheme Theme { get; }

        /// <inheritdoc />
        public IUxmlTemplateResolver Templates { get; }

        /// <inheritdoc />
        public IRuntimeUiAssetResolver Assets { get; }

        /// <inheritdoc />
        public IReadOnlyList<RuntimeUiScreen> Screens => NoScreens;

        /// <inheritdoc />
        public VisualElement LayerRoot => null;

        /// <inheritdoc />
        public RuntimeUiResult CreateScreen(string id, string uxml, string uss, RuntimeUiScreenOptions options = null)
        {
            return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiDisabled,
                "runtime UI is switched off in this game (RuntimeUiHostOptions.Enabled = false)");
        }

        /// <inheritdoc />
        public bool TryGetScreen(string id, out RuntimeUiScreen screen)
        {
            screen = null;
            return false;
        }

        /// <inheritdoc />
        public bool DestroyScreen(string id)
        {
            return false;
        }

        /// <inheritdoc />
        public int DestroyScreensOwnedBy(string owner)
        {
            return 0;
        }

        /// <inheritdoc />
        public RuntimeUiResult PushScreen(RuntimeUiScreen screen, bool hidePrevious = false)
        {
            return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiDisabled, "runtime UI is switched off");
        }

        /// <inheritdoc />
        public RuntimeUiScreen PopScreen()
        {
            return null;
        }

        /// <inheritdoc />
        public void AttachTo(VisualElement root)
        {
        }
    }
}

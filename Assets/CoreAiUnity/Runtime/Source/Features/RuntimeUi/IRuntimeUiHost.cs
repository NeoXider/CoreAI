using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Owns the runtime screens of a game: created from UXML and USS text while the game runs (or element
    /// by element through the typed API), restyled or rebuilt live, shown, hidden, stacked, ordered and
    /// destroyed. Registered through <c>RuntimeUiInstaller.RegisterRuntimeUi</c>; a host game replaces it
    /// by registering its own implementation, or switches it off with
    /// <see cref="RuntimeUiHostOptions.Enabled"/>.
    /// </summary>
    public interface IRuntimeUiHost : IRuntimeUiTextInputFocus
    {
        /// <summary>False for the switched-off host: every call fails with <c>UI_DISABLED</c>.</summary>
        bool IsEnabled { get; }

        /// <summary>The shared theme (design tokens and semantic classes).</summary>
        RuntimeUiTheme Theme { get; }

        /// <summary>Templates <c>Instance</c> and <c>item-template</c> resolve against.</summary>
        IUxmlTemplateResolver Templates { get; }

        /// <summary>Image lookup for <c>background-image</c> and <c>Image</c>.</summary>
        IRuntimeUiAssetResolver Assets { get; }

        /// <summary>Live screens in creation order.</summary>
        IReadOnlyList<RuntimeUiScreen> Screens { get; }

        /// <summary>The element every screen root is parented to, or null before the host attaches.</summary>
        VisualElement LayerRoot { get; }

        /// <summary>True while the pointer is over a pickable element of any screen (a click there is UI input).</summary>
        bool IsPointerOverUi { get; }

        /// <summary>Raised with the new value whenever <see cref="IsPointerOverUi"/> changes.</summary>
        event Action<bool> PointerOverUiChanged;

        /// <summary>Raised after a screen was created.</summary>
        event Action<RuntimeUiScreen> ScreenCreated;

        /// <summary>Raised after a screen was destroyed.</summary>
        event Action<RuntimeUiScreen> ScreenDestroyed;

        /// <summary>Creates a screen from UXML and USS text; either may be null or empty.</summary>
        RuntimeUiResult CreateScreen(string id, string uxml, string uss, RuntimeUiScreenOptions options = null);

        /// <summary>Finds a live screen.</summary>
        bool TryGetScreen(string id, out RuntimeUiScreen screen);

        /// <summary>Destroys a screen and everything attached to it.</summary>
        bool DestroyScreen(string id);

        /// <summary>Destroys every screen created with the given owner tag; returns how many.</summary>
        int DestroyScreensOwnedBy(string owner);

        /// <summary>Shows <paramref name="screen"/> on top of the stack; optionally hides the previous top.</summary>
        RuntimeUiResult PushScreen(RuntimeUiScreen screen, bool hidePrevious = false);

        /// <summary>Hides and removes the top of the stack, re-showing a screen the push hid; null when empty.</summary>
        RuntimeUiScreen PopScreen();

        /// <summary>
        /// Renders into <paramref name="root"/> instead of the auto-created panel (embedding into a host
        /// game's own UI document, a <c>PanelRenderer</c> root or a Hub page). Screens move with the layer.
        /// </summary>
        void AttachTo(VisualElement root);
    }
}

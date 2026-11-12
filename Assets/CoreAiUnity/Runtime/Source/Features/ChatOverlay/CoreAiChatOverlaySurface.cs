using System;
using CoreAI.PanelFonts;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// The overlay's own UI Toolkit document: a GameObject with a <see cref="UIDocument"/> on the assigned panel, or
    /// on a panel created at runtime (scale with screen size, 1920x1080 reference). Works in a built player, WebGL
    /// included; nothing here needs the editor. Dispose destroys what it created.
    /// </summary>
    public sealed class CoreAiChatOverlaySurface : IDisposable
    {
        /// <summary>Name of the GameObject that holds the overlay document.</summary>
        public const string GameObjectName = "CoreAI Chat Overlay";

        private GameObject _gameObject;
        private UIDocument _document;
        private PanelSettings _ownedPanelSettings;

        private CoreAiChatOverlaySurface()
        {
        }

        /// <summary>The document's root element; null once disposed or while the document has no panel.</summary>
        public VisualElement Root => _document != null ? _document.rootVisualElement : null;

        /// <summary>The document component; null once disposed.</summary>
        public UIDocument Document => _document;

        /// <summary>True when the panel settings were created by this surface rather than assigned.</summary>
        public bool OwnsPanelSettings => _ownedPanelSettings != null;

        /// <summary>Creates the document described by <paramref name="settings"/>.</summary>
        public static CoreAiChatOverlaySurface Create(CoreAiChatOverlaySettings settings)
        {
            CoreAiChatOverlaySettings resolved = settings ?? new CoreAiChatOverlaySettings();
            CoreAiChatOverlaySurface surface = new();
            PanelSettings panel = resolved.PanelSettings;
            if (panel == null)
            {
                panel = CreateRuntimePanelSettings(resolved);
                surface._ownedPanelSettings = panel;
            }

            GameObject gameObject = new(GameObjectName);
            gameObject.SetActive(false);
            CoreAiPanelFonts.ApplyTo(panel);
            UIDocument document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.sortingOrder = resolved.SortingOrder;
            if (resolved.DontDestroyOnLoad && Application.isPlaying)
            {
                Object.DontDestroyOnLoad(gameObject);
            }

            // WHY: activating after the panel is assigned builds the root once, on the right panel.
            gameObject.SetActive(true);
            surface._gameObject = gameObject;
            surface._document = document;
            return surface;
        }

        /// <summary>Destroys the document GameObject and a panel this surface created.</summary>
        public void Dispose()
        {
            GameObject gameObject = _gameObject;
            PanelSettings owned = _ownedPanelSettings;
            _gameObject = null;
            _document = null;
            _ownedPanelSettings = null;
            DestroyObject(gameObject);
            DestroyObject(owned);
        }

        internal static PanelSettings CreateRuntimePanelSettings(CoreAiChatOverlaySettings settings)
        {
            PanelSettings panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = "CoreAI Chat Overlay Panel";
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.sortingOrder = settings.SortingOrder;
            ThemeStyleSheet theme = settings.ThemeStyleSheet;
            if (theme == null)
            {
                theme = Resources.Load<ThemeStyleSheet>(CoreAiChatOverlaySettings.DefaultThemeResourcePath);
            }

            if (theme != null)
            {
                panel.themeStyleSheet = theme;
            }

            return panel;
        }

        private static void DestroyObject(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}

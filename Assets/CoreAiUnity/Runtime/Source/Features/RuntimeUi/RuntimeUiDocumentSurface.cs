using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The panel the runtime UI host creates for itself when nothing else was attached: one
    /// <see cref="UIDocument"/> on a dedicated GameObject, with runtime-created panel settings and the
    /// theme stylesheet from this package's Resources. It only hands its root to the host; it has no
    /// per-frame code.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class RuntimeUiDocumentSurface : MonoBehaviour
    {
        private UIDocument _document;
        private PanelSettings _ownedSettings;

        /// <summary>Raised when the document's root element is (re)created.</summary>
        public event Action<RuntimeUiDocumentSurface> RootChanged;

        /// <summary>Raised when the surface is destroyed (scene unload, host disposal).</summary>
        public event Action<RuntimeUiDocumentSurface> Destroyed;

        /// <summary>The document's root, or null before the panel exists.</summary>
        public VisualElement Root => _document != null ? _document.rootVisualElement : null;

        /// <summary>Creates the surface GameObject with its document and panel settings.</summary>
        public static RuntimeUiDocumentSurface Create(RuntimeUiHostOptions options)
        {
            GameObject host = new("CoreAI Runtime UI");
            host.SetActive(false);
            if (options.PersistAcrossScenes && Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            RuntimeUiDocumentSurface surface = host.AddComponent<RuntimeUiDocumentSurface>();
            surface._document = host.AddComponent<UIDocument>();
            PanelSettings settings = options.PanelSettings;
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.name = "CoreAI Runtime UI Panel";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 0.5f;
                settings.sortingOrder = options.PanelSortingOrder;
                settings.themeStyleSheet = options.ThemeStyleSheet != null
                    ? options.ThemeStyleSheet
                    : Resources.Load<ThemeStyleSheet>(RuntimeUiHostOptions.DefaultThemeResourcePath);
                surface._ownedSettings = settings;
            }

            surface._document.panelSettings = settings;
            host.SetActive(true);
            return surface;
        }

        /// <summary>Destroys the surface and the panel settings it created.</summary>
        public void DestroySurface()
        {
            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        private void OnEnable()
        {
            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            if (Root != null)
            {
                RootChanged?.Invoke(this);
            }
        }

        private void OnDestroy()
        {
            Destroyed?.Invoke(this);
            RootChanged = null;
            Destroyed = null;
            if (_ownedSettings != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_ownedSettings);
                }
                else
                {
                    DestroyImmediate(_ownedSettings);
                }

                _ownedSettings = null;
            }
        }
    }
}

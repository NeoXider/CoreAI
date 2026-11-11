using System;
using System.Reflection;
using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// A real runtime panel for EditMode tests: a UIDocument with fresh panel settings and a runtime UI
    /// host attached to its root. <see cref="Update"/> runs the panel's style and layout passes
    /// synchronously, so resolved styles can be read without waiting for a frame.
    /// </summary>
    internal sealed class RuntimeUiTestPanel : IDisposable
    {
        private readonly GameObject _gameObject;
        private readonly PanelSettings _settings;
        private Action _applyStyles;
        private Action _validateLayout;

        public RuntimeUiTestPanel(RuntimeUiHostOptions options = null, bool withTheme = false)
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            if (withTheme)
            {
                _settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>(RuntimeUiHostOptions.DefaultThemeResourcePath);
            }

            _gameObject = new GameObject("RuntimeUiTestPanel");
            _gameObject.SetActive(false);
            Document = _gameObject.AddComponent<UIDocument>();
            Document.panelSettings = _settings;
            _gameObject.SetActive(true);

            RuntimeUiHostOptions effective = options ?? new RuntimeUiHostOptions();
            effective.AutoCreateSurface = false;
            Host = new RuntimeUiHost(effective);
            Host.AttachTo(Document.rootVisualElement);
        }

        public UIDocument Document { get; }

        public RuntimeUiHost Host { get; }

        public VisualElement Root => Document.rootVisualElement;

        /// <summary>Creates a screen and fails the test when the host refuses it.</summary>
        public RuntimeUiScreen Create(string id, string uxml, string uss)
        {
            RuntimeUiResult result = Host.CreateScreen(id, uxml, uss);
            Assert.IsTrue(result.Success, result.ToString());
            return result.Screen;
        }

        /// <summary>Runs the panel's style and layout passes now.</summary>
        public void Update()
        {
            BindPanelPasses();
            _applyStyles();
            _validateLayout();
        }

        /// <summary>The panel's style and layout passes as allocation-free delegates.</summary>
        public Action PanelPasses
        {
            get
            {
                BindPanelPasses();
                return () =>
                {
                    _applyStyles();
                    _validateLayout();
                };
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            UnityEngine.Object.DestroyImmediate(_gameObject);
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        private void BindPanelPasses()
        {
            if (_applyStyles != null)
            {
                return;
            }

            IPanel panel = Root.panel;
            Assert.IsNotNull(panel, "The UIDocument has no panel in EditMode.");
            // WHY: ApplyStyles/ValidateLayout are public members of the internal panel type; binding them as
            // delegates lets a test run one frame's style and layout passes synchronously and without the
            // allocations MethodInfo.Invoke would add to an allocation measurement.
            MethodInfo applyStyles = panel.GetType().GetMethod("ApplyStyles",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo validateLayout = panel.GetType().GetMethod("ValidateLayout",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (applyStyles == null || validateLayout == null)
            {
                Assert.Ignore("This Unity version exposes no ApplyStyles/ValidateLayout on its panel type; the " +
                              "synchronous style pass these tests rely on is unavailable.");
            }

            _applyStyles = (Action)Delegate.CreateDelegate(typeof(Action), panel, applyStyles);
            _validateLayout = (Action)Delegate.CreateDelegate(typeof(Action), panel, validateLayout);
        }
    }
}

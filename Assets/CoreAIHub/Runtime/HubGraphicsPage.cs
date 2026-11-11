using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Look;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page for the player's graphics options: a quality preset plus one switch per option
    /// (post-processing, shadows, anti-aliasing, render scale, fog, sky) and "Reset to defaults". Every
    /// change applies at once and is stored by the scope's <see cref="IGraphicsOptionsService"/>, so it
    /// survives a restart. Without a service (the game owns rendering, or no CoreAI scope applies a look)
    /// the page says so instead of offering switches that would do nothing.
    /// </summary>
    public sealed class HubGraphicsPage : HubPageBase
    {
        /// <summary>Registry id of the built-in Graphics page.</summary>
        public const string DefaultPageId = "coreai.hub.graphics";

        /// <summary>Text shown when no graphics options service is available.</summary>
        public const string ManagedByGameNote =
            "Graphics are managed by the game. CoreAI's default look is off in this scene, or no CoreAI " +
            "scope is running.";

        internal static readonly string[] PresetChoices = { "Low", "Medium", "High" };
        internal static readonly string[] ShadowChoices = { "Off", "Hard", "Soft" };
        internal static readonly string[] AntiAliasingChoices = { "Off", "FXAA", "SMAA" };

        private const float RenderScaleStep = 0.05f;

        private readonly IGraphicsOptionsService _serviceOverride;
        private IGraphicsOptionsService _attached;
        private VisualElement _body;
        private bool _listeningForService;

        /// <summary>Creates the page.</summary>
        /// <param name="service">
        /// Options service to edit. Null uses the one the running scope publishes through
        /// <see cref="CoreAiGraphicsUi"/>, and follows it as scopes come and go.
        /// </param>
        /// <param name="pageId">Registry id.</param>
        /// <param name="displayName">Tab title.</param>
        /// <param name="order">Tab order.</param>
        public HubGraphicsPage(IGraphicsOptionsService service = null, string pageId = DefaultPageId,
            string displayName = "Graphics", int order = 150)
            : base(pageId, displayName, order)
        {
            _serviceOverride = service;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        internal IGraphicsOptionsService Service => _serviceOverride ?? CoreAiGraphicsUi.Service;

        internal DropdownField PresetField { get; private set; }

        internal Toggle PostProcessingToggle { get; private set; }

        internal DropdownField ShadowsField { get; private set; }

        internal DropdownField AntiAliasingField { get; private set; }

        internal Slider RenderScaleSlider { get; private set; }

        internal Label RenderScaleValue { get; private set; }

        internal Toggle FogToggle { get; private set; }

        internal Toggle SkyToggle { get; private set; }

        internal Button ResetButton { get; private set; }

        internal Label CustomNote { get; private set; }

        internal Label StatusLabel { get; private set; }

        internal Label ManagedNote { get; private set; }

        /// <inheritdoc />
        public override void OnActivated()
        {
            if (_body != null && !ReferenceEquals(Service, _attached))
            {
                Rebuild();
                return;
            }

            Refresh();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            if (_listeningForService)
            {
                CoreAiGraphicsUi.ServiceChanged -= HandleServiceChanged;
                _listeningForService = false;
            }

            Detach();
            _body = null;
        }

        /// <summary>Selects the preset at <paramref name="index"/> of <see cref="PresetChoices"/>.</summary>
        internal void SelectPreset(int index)
        {
            IGraphicsOptionsService service = Service;
            if (service == null || index < 0 || index >= PresetChoices.Length)
            {
                return;
            }

            service.SetOptions(GraphicsOptions.ForPreset((GraphicsQualityPreset)index));
        }

        /// <summary>Sets the post-processing switch.</summary>
        internal void SetPostProcessing(bool value)
        {
            Edit((options, preset) => options.WithPostProcessing(
                value == preset.PostProcessing ? (bool?)null : value));
        }

        /// <summary>Selects the shadow mode at <paramref name="index"/> of <see cref="ShadowChoices"/>.</summary>
        internal void SelectShadows(int index)
        {
            if (index < 0 || index >= ShadowChoices.Length)
            {
                return;
            }

            GraphicsShadowQuality value = (GraphicsShadowQuality)index;
            Edit((options, preset) => options.WithShadows(
                value == preset.Shadows ? (GraphicsShadowQuality?)null : value));
        }

        /// <summary>Selects the anti-aliasing mode at <paramref name="index"/> of the choices.</summary>
        internal void SelectAntiAliasing(int index)
        {
            if (index < 0 || index >= AntiAliasingChoices.Length)
            {
                return;
            }

            GraphicsAntiAliasing value = (GraphicsAntiAliasing)index;
            Edit((options, preset) => options.WithAntiAliasing(
                value == preset.AntiAliasing ? (GraphicsAntiAliasing?)null : value));
        }

        /// <summary>Sets the render scale, rounded to 5% steps and clamped to the supported range.</summary>
        internal void SetRenderScale(float value)
        {
            float rounded = GraphicsOptionsResolver.ClampRenderScale(
                Mathf.Round(value / RenderScaleStep) * RenderScaleStep);
            Edit((options, preset) => options.WithRenderScale(
                Mathf.Approximately(rounded, preset.RenderScale) ? (float?)null : rounded));
        }

        /// <summary>Sets the fog switch.</summary>
        internal void SetFog(bool value)
        {
            Edit((options, preset) => options.WithFog(value == preset.Fog ? (bool?)null : value));
        }

        /// <summary>Sets the sky switch.</summary>
        internal void SetSky(bool value)
        {
            Edit((options, preset) => options.WithSky(value == preset.Sky ? (bool?)null : value));
        }

        /// <summary>Drops the preset and every override.</summary>
        internal void ResetToDefaults()
        {
            Service?.ResetToDefaults();
        }

        /// <summary>Re-reads every control from the service without raising change callbacks.</summary>
        internal void Refresh()
        {
            IGraphicsOptionsService service = _attached;
            if (service == null || PresetField == null)
            {
                return;
            }

            ResolvedGraphicsOptions effective = service.Effective;
            PresetField.SetValueWithoutNotify(PresetChoices[(int)effective.Preset]);
            PostProcessingToggle.SetValueWithoutNotify(effective.PostProcessing);
            ShadowsField.SetValueWithoutNotify(ShadowChoices[(int)effective.Shadows]);
            AntiAliasingField.SetValueWithoutNotify(AntiAliasingChoices[(int)effective.AntiAliasing]);
            RenderScaleSlider.SetValueWithoutNotify(effective.RenderScale);
            RenderScaleValue.text = FormatPercent(effective.RenderScale);
            FogToggle.SetValueWithoutNotify(effective.Fog);
            SkyToggle.SetValueWithoutNotify(effective.Sky);
            CustomNote.text = service.Options.HasOverrides
                ? "Custom: some switches differ from the " + PresetChoices[(int)effective.Preset] + " preset."
                : service.Options.Preset.HasValue
                    ? ""
                    : "Using the platform default preset (" +
                      PresetChoices[(int)service.PlatformDefaultPreset] + ").";
            StatusLabel.text = service.LastSaveSucceeded
                ? ""
                : "The last change applies now but was not saved, so it will be lost on restart.";
        }

        private object BuildContent()
        {
            ScrollView scroll = HubPageWidgets.CreatePage(null, out VisualElement body);
            scroll.AddToClassList("coreai-hub-page");
            body.AddToClassList("coreai-hub-page-body");
            _body = body;
            if (!_listeningForService)
            {
                CoreAiGraphicsUi.ServiceChanged += HandleServiceChanged;
                _listeningForService = true;
            }

            Rebuild();
            return scroll;
        }

        private void HandleServiceChanged()
        {
            if (_body != null && _serviceOverride == null)
            {
                Rebuild();
            }
        }

        private void Rebuild()
        {
            Detach();
            ClearControls();
            if (_body == null)
            {
                return;
            }

            _body.Clear();
            _body.Add(HubPageWidgets.MakeTitle(DisplayName));
            IGraphicsOptionsService service = Service;
            if (service == null)
            {
                ManagedNote = HubPageWidgets.MakeNote(ManagedByGameNote);
                _body.Add(ManagedNote);
                return;
            }

            _body.Add(HubPageWidgets.MakeSection("Quality"));
            PresetField = new DropdownField("Preset", new List<string>(PresetChoices), 0)
            {
                tooltip = "Selecting a preset replaces every switch below with the preset's values."
            };
            StyleField(PresetField);
            PresetField.RegisterValueChangedCallback(evt =>
                SelectPreset(Array.IndexOf(PresetChoices, evt.newValue)));
            _body.Add(PresetField);

            CustomNote = HubPageWidgets.MakeNote("");
            _body.Add(CustomNote);

            _body.Add(HubPageWidgets.MakeSection("Image"));
            PostProcessingToggle = new Toggle("Post-processing")
            {
                tooltip = "Tonemapping, bloom on bright surfaces such as Neon, and colour grading."
            };
            StyleField(PostProcessingToggle);
            PostProcessingToggle.RegisterValueChangedCallback(evt => SetPostProcessing(evt.newValue));
            _body.Add(PostProcessingToggle);

            AntiAliasingField = new DropdownField("Anti-aliasing",
                new List<string>(AntiAliasingChoices), 0)
            {
                tooltip = "Smooths jagged edges. SMAA is sharper, FXAA is cheaper."
            };
            StyleField(AntiAliasingField);
            AntiAliasingField.RegisterValueChangedCallback(evt =>
                SelectAntiAliasing(Array.IndexOf(AntiAliasingChoices, evt.newValue)));
            _body.Add(AntiAliasingField);

            RenderScaleSlider = new Slider("Render scale", GraphicsOptionsResolver.MinimumRenderScale,
                GraphicsOptionsResolver.MaximumRenderScale)
            {
                tooltip = "Renders fewer pixels and scales the image up. Lower is faster and softer."
            };
            StyleField(RenderScaleSlider);
            RenderScaleSlider.RegisterValueChangedCallback(evt => SetRenderScale(evt.newValue));
            _body.Add(RenderScaleSlider);
            RenderScaleValue = HubPageWidgets.MakeNote("");
            RenderScaleValue.style.marginTop = 0f;
            _body.Add(RenderScaleValue);

            _body.Add(HubPageWidgets.MakeSection("World"));
            ShadowsField = new DropdownField("Shadows", new List<string>(ShadowChoices), 0)
            {
                tooltip = "How the sun casts shadows. Off is fastest."
            };
            StyleField(ShadowsField);
            ShadowsField.RegisterValueChangedCallback(evt =>
                SelectShadows(Array.IndexOf(ShadowChoices, evt.newValue)));
            _body.Add(ShadowsField);

            FogToggle = new Toggle("Fog") { tooltip = "Distance fog that fades far objects into the sky." };
            StyleField(FogToggle);
            FogToggle.RegisterValueChangedCallback(evt => SetFog(evt.newValue));
            _body.Add(FogToggle);

            SkyToggle = new Toggle("Sky") { tooltip = "Draw the sky. Off uses a flat ambient colour instead." };
            StyleField(SkyToggle);
            SkyToggle.RegisterValueChangedCallback(evt => SetSky(evt.newValue));
            _body.Add(SkyToggle);

            ResetButton = new Button(ResetToDefaults) { text = "Reset to defaults" };
            ResetButton.AddToClassList("coreai-hub-action-button");
            ResetButton.style.marginTop = 12f;
            _body.Add(ResetButton);

            StatusLabel = HubPageWidgets.MakeNote("");
            StatusLabel.AddToClassList("coreai-hub-status");
            _body.Add(StatusLabel);

            Attach(service);
            Refresh();
        }

        private void Edit(Func<GraphicsOptions, ResolvedGraphicsOptions, GraphicsOptions> change)
        {
            IGraphicsOptionsService service = Service;
            if (service == null)
            {
                return;
            }

            ResolvedGraphicsOptions presetValues =
                GraphicsOptionsResolver.PresetValues(service.Effective.Preset);
            service.SetOptions(change(service.Options, presetValues));
        }

        private void Attach(IGraphicsOptionsService service)
        {
            if (ReferenceEquals(_attached, service))
            {
                return;
            }

            Detach();
            _attached = service;
            if (_attached != null)
            {
                _attached.Changed += HandleOptionsChanged;
            }
        }

        private void Detach()
        {
            if (_attached != null)
            {
                _attached.Changed -= HandleOptionsChanged;
                _attached = null;
            }
        }

        private void HandleOptionsChanged(ResolvedGraphicsOptions _)
        {
            Refresh();
        }

        private void ClearControls()
        {
            PresetField = null;
            PostProcessingToggle = null;
            ShadowsField = null;
            AntiAliasingField = null;
            RenderScaleSlider = null;
            RenderScaleValue = null;
            FogToggle = null;
            SkyToggle = null;
            ResetButton = null;
            CustomNote = null;
            StatusLabel = null;
            ManagedNote = null;
        }

        private static string FormatPercent(float renderScale)
        {
            return Mathf.RoundToInt(renderScale * 100f).ToString(CultureInfo.InvariantCulture) +
                   "% of the screen resolution";
        }

        private static void StyleField(VisualElement field)
        {
            field.AddToClassList("coreai-hub-field");
        }
    }
}

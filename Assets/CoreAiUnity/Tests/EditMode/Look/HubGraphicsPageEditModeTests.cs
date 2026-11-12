using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Look;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Hub Graphics page. Its callbacks are called directly: a detached UI Toolkit element fires no
    /// <c>ChangeEvent</c>, so driving <c>element.value</c> would test nothing. Each callback must write
    /// through the options service, and the controls must follow the service's state.
    /// </summary>
    public sealed class HubGraphicsPageEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreAiGraphicsUi.Service = null;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiGraphicsUi.Service = null;
        }

        [Test]
        public void Page_ShowsTheEffectiveOptions()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            Assert.AreEqual("High", page.PresetField.value);
            Assert.IsTrue(page.PostProcessingToggle.value);
            Assert.AreEqual("SMAA", page.AntiAliasingField.value);
            Assert.AreEqual("Soft", page.ShadowsField.value);
            Assert.AreEqual(1f, page.RenderScaleSlider.value);
            Assert.IsTrue(page.FogToggle.value);
            Assert.IsTrue(page.SkyToggle.value);
            StringAssert.Contains("platform default", page.CustomNote.text);
            Assert.IsNull(page.ManagedNote);
        }

        [Test]
        public void SelectPreset_StoresThePresetAndRefreshesEveryControl()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            page.SelectPreset(0);

            Assert.AreEqual(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low), service.Options);
            Assert.AreEqual("Low", page.PresetField.value);
            Assert.IsFalse(page.PostProcessingToggle.value);
            Assert.AreEqual("Hard", page.ShadowsField.value);
            Assert.AreEqual(0.75f, page.RenderScaleSlider.value);
        }

        [Test]
        public void Switch_DifferentFromThePreset_IsStoredAsAnOverride()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            page.SetPostProcessing(false);

            Assert.AreEqual(false, service.Options.PostProcessing);
            Assert.IsFalse(service.Effective.PostProcessing);
            StringAssert.Contains("Custom", page.CustomNote.text);
        }

        [Test]
        public void Switch_BackToThePresetValue_DropsTheOverride()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);
            page.SetPostProcessing(false);

            page.SetPostProcessing(true);

            Assert.IsNull(service.Options.PostProcessing);
            Assert.IsFalse(service.Options.HasOverrides);
        }

        [Test]
        public void EveryOptionCallback_WritesThroughTheService()
        {
            InMemoryGraphicsOptionsStore store = new();
            GraphicsOptionsService service = new(store, GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            page.SelectShadows(0);
            page.SelectAntiAliasing(1);
            page.SetRenderScale(0.83f);
            page.SetFog(false);
            page.SetSky(false);

            Assert.AreEqual(GraphicsShadowQuality.Off, service.Effective.Shadows);
            Assert.AreEqual(GraphicsAntiAliasing.Fxaa, service.Effective.AntiAliasing);
            Assert.AreEqual(0.85f, service.Effective.RenderScale, 0.0001f, "render scale snaps to 5% steps");
            Assert.IsFalse(service.Effective.Fog);
            Assert.IsFalse(service.Effective.Sky);
            Assert.AreEqual(5, store.SaveCount);
            Assert.AreEqual("Off", page.ShadowsField.value);
            Assert.AreEqual("FXAA", page.AntiAliasingField.value);
            StringAssert.StartsWith("85%", page.RenderScaleValue.text);
        }

        [Test]
        public void ResetToDefaults_DropsPresetAndOverrides()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.Medium);
            HubGraphicsPage page = Build(service);
            page.SelectPreset(2);
            page.SetFog(false);

            page.ResetToDefaults();

            Assert.AreEqual(GraphicsOptions.Default, service.Options);
            Assert.AreEqual("Medium", page.PresetField.value);
            Assert.IsTrue(page.FogToggle.value);
        }

        [Test]
        public void OutOfRangeIndexes_AreIgnored()
        {
            InMemoryGraphicsOptionsStore store = new();
            HubGraphicsPage page = Build(new GraphicsOptionsService(store, GraphicsQualityPreset.High));

            page.SelectPreset(-1);
            page.SelectPreset(3);
            page.SelectShadows(9);
            page.SelectAntiAliasing(-4);

            Assert.AreEqual(0, store.SaveCount);
        }

        [Test]
        public void FailedSave_IsShownOnThePage()
        {
            GraphicsOptionsService service = new(new RefusingStore(), GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            page.SetSky(false);

            StringAssert.Contains("not saved", page.StatusLabel.text);
            Assert.IsFalse(page.SkyToggle.value, "the change still applies for this session");
        }

        [Test]
        public void NoService_SaysTheGameManagesGraphics_AndOffersNoSwitches()
        {
            HubGraphicsPage page = Build(null);

            Assert.IsNotNull(page.ManagedNote);
            Assert.AreEqual(HubGraphicsPage.ManagedByGameNote, page.ManagedNote.text);
            Assert.IsNull(page.PresetField);
            Assert.IsNull(page.PostProcessingToggle);
            page.SetFog(false);
        }

        [Test]
        public void PageWithoutOverride_FollowsTheServiceTheScopePublishes()
        {
            HubGraphicsPage page = Build(null);
            Assert.IsNull(page.PresetField);

            GraphicsOptionsService published = NewService(GraphicsQualityPreset.High);
            CoreAiGraphicsUi.Service = published;

            Assert.IsNotNull(page.PresetField, "the page rebuilds when a scope publishes its service");
            page.SetFog(false);
            Assert.IsFalse(published.Effective.Fog);

            CoreAiGraphicsUi.Service = null;
            Assert.IsNull(page.PresetField);
            Assert.IsNotNull(page.ManagedNote);
            page.OnDestroyed();
        }

        [Test]
        public void OnDestroyed_StopsFollowingTheService()
        {
            GraphicsOptionsService service = NewService(GraphicsQualityPreset.High);
            HubGraphicsPage page = Build(service);

            page.OnDestroyed();
            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));

            Assert.AreEqual("High", page.PresetField.value, "a destroyed page no longer refreshes");
        }

        [Test]
        public void RegisterAll_RegistersTheGraphicsPageBetweenSettingsAndStatistics()
        {
            HubPageRegistry registry = new();

            HubBuiltInPages.RegisterAll(registry);

            Assert.IsTrue(registry.TryGet(HubBuiltInPages.GraphicsPageId, out System.Func<IHubPage> factory));
            IHubPage page = factory();
            Assert.IsInstanceOf<HubGraphicsPage>(page);
            Assert.AreEqual("Graphics", page.DisplayName);
            int settingsOrder = -1;
            int graphicsOrder = -1;
            int statisticsOrder = -1;
            foreach ((string pageId, int order) in registry.List())
            {
                if (pageId == HubBuiltInPages.SettingsPageId)
                {
                    settingsOrder = order;
                }
                else if (pageId == HubBuiltInPages.GraphicsPageId)
                {
                    graphicsOrder = order;
                }
                else if (pageId == HubBuiltInPages.StatisticsPageId)
                {
                    statisticsOrder = order;
                }
            }

            Assert.Greater(graphicsOrder, settingsOrder);
            Assert.Less(graphicsOrder, statisticsOrder);
        }

        private static GraphicsOptionsService NewService(GraphicsQualityPreset platformDefault)
        {
            return new GraphicsOptionsService(new InMemoryGraphicsOptionsStore(), platformDefault);
        }

        [Test]
        public void Page_ShowsTheFrameRate_WhoeverOwnsRendering()
        {
            HubGraphicsPage page = Build(null);

            Assert.IsNotNull(page.ManagedNote, "no options service: the game owns rendering");
            Assert.AreEqual(HubFrameRateMeter.NoReadingText, page.FrameRateValue.text);

            page.SampleFrameRate(100, 10.0);
            page.SampleFrameRate(130, 10.5);

            Assert.AreEqual("60 (16.7 ms a frame)", page.FrameRateValue.text);
        }

        [Test]
        public void FrameRateMeter_WaitsForALongEnoughStretch_AndStartsAgainWhenTheCounterGoesBack()
        {
            HubFrameRateMeter meter = new();

            Assert.IsFalse(meter.Sample(10, 1.0), "the first call only sets the start");
            Assert.IsFalse(meter.Sample(12, 1.1), "too short a stretch to trust");
            Assert.IsTrue(meter.Sample(40, 2.0));
            Assert.AreEqual(30d, meter.FramesPerSecond, 0.001);
            Assert.IsFalse(meter.Sample(5, 2.5), "the frame counter went back");
            Assert.IsTrue(meter.Sample(55, 3.5));
            Assert.AreEqual(50d, meter.FramesPerSecond, 0.001);
        }

        private static HubGraphicsPage Build(IGraphicsOptionsService service)
        {
            HubGraphicsPage page = new(service);
            object content = page.CreatePageContent();
            Assert.IsInstanceOf<VisualElement>(content);
            return page;
        }

        private sealed class RefusingStore : IGraphicsOptionsStore
        {
            public GraphicsOptions Load()
            {
                return GraphicsOptions.Default;
            }

            public bool Save(GraphicsOptions options)
            {
                return false;
            }
        }
    }
}

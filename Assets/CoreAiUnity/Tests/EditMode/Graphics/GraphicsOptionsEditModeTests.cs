using System;
using System.Collections.Generic;
using CoreAI.Look;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free contract of the player's graphics options: preset table, override layering, the JSON
    /// file contract and the options service. Linked into the portable suite, so it must never touch
    /// UnityEngine.
    /// </summary>
    public sealed class GraphicsOptionsEditModeTests
    {
        [Test]
        public void NoChoice_ResolvesToThePlatformDefaultPreset()
        {
            ResolvedGraphicsOptions desktop =
                GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High);
            ResolvedGraphicsOptions web =
                GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.Medium);

            Assert.AreEqual(GraphicsOptionsResolver.PresetValues(GraphicsQualityPreset.High), desktop);
            Assert.AreEqual(GraphicsOptionsResolver.PresetValues(GraphicsQualityPreset.Medium), web);
            Assert.IsTrue(desktop.PostProcessing, "post-processing is on by default on desktop");
            Assert.IsTrue(web.PostProcessing, "post-processing is on by default on WebGL and mobile");
            Assert.AreEqual(GraphicsAntiAliasing.Smaa, desktop.AntiAliasing);
            Assert.AreEqual(GraphicsAntiAliasing.Fxaa, web.AntiAliasing);
        }

        [Test]
        public void ChosenPreset_BeatsThePlatformDefault()
        {
            ResolvedGraphicsOptions resolved = GraphicsOptionsResolver.Resolve(
                GraphicsOptions.ForPreset(GraphicsQualityPreset.Low), GraphicsQualityPreset.High);

            Assert.AreEqual(GraphicsQualityPreset.Low, resolved.Preset);
            Assert.IsFalse(resolved.PostProcessing);
            Assert.AreEqual(GraphicsShadowQuality.Hard, resolved.Shadows);
            Assert.AreEqual(0.75f, resolved.RenderScale);
        }

        [Test]
        public void Overrides_AreLayeredOnThePreset_AndOnlyThem()
        {
            GraphicsOptions options = new(GraphicsQualityPreset.High, postProcessing: false,
                shadows: GraphicsShadowQuality.Off, fog: false);

            ResolvedGraphicsOptions resolved = GraphicsOptionsResolver.Resolve(options, GraphicsQualityPreset.Medium);

            Assert.IsFalse(resolved.PostProcessing);
            Assert.AreEqual(GraphicsShadowQuality.Off, resolved.Shadows);
            Assert.IsFalse(resolved.Fog);
            Assert.AreEqual(GraphicsAntiAliasing.Smaa, resolved.AntiAliasing, "not overridden: from High");
            Assert.IsTrue(resolved.Sky, "not overridden: from High");
            Assert.AreEqual(1f, resolved.RenderScale, "not overridden: from High");
        }

        [Test]
        public void UndefinedPresetOrValues_FallBackInsteadOfThrowing()
        {
            GraphicsOptions options = new((GraphicsQualityPreset)99, shadows: (GraphicsShadowQuality)7,
                antiAliasing: (GraphicsAntiAliasing)(-1));

            ResolvedGraphicsOptions resolved = GraphicsOptionsResolver.Resolve(options, GraphicsQualityPreset.High);
            ResolvedGraphicsOptions badPlatform =
                GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, (GraphicsQualityPreset)42);

            Assert.AreEqual(GraphicsOptionsResolver.PresetValues(GraphicsQualityPreset.High), resolved);
            Assert.AreEqual(GraphicsOptionsResolver.FallbackPreset, badPlatform.Preset);
        }

        [Test]
        public void RenderScale_IsClampedIntoTheSupportedRange()
        {
            Assert.AreEqual(GraphicsOptionsResolver.MinimumRenderScale, Resolve(renderScale: 0.1f).RenderScale);
            Assert.AreEqual(GraphicsOptionsResolver.MaximumRenderScale, Resolve(renderScale: 3f).RenderScale);
            Assert.AreEqual(1f, Resolve(renderScale: float.NaN).RenderScale, "NaN keeps the preset value");
            Assert.AreEqual(0.8f, Resolve(renderScale: 0.8f).RenderScale, "an in-range value is kept as is");
        }

        [Test]
        public void Json_RoundTripsEveryOption()
        {
            GraphicsOptions options = new(GraphicsQualityPreset.Low, true, GraphicsShadowQuality.Soft,
                GraphicsAntiAliasing.Smaa, 0.85f, false, false);

            string json = GraphicsOptionsJson.Serialize(options);
            bool ok = GraphicsOptionsJson.TryDeserialize(json, out GraphicsOptions read, out string error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(options, read);
        }

        [Test]
        public void Json_DefaultOptions_WriteOnlyTheSchemaVersion()
        {
            string json = GraphicsOptionsJson.Serialize(GraphicsOptions.Default);

            StringAssert.Contains("\"schemaVersion\": 1", json);
            StringAssert.DoesNotContain("preset", json);
            Assert.IsTrue(GraphicsOptionsJson.TryDeserialize(json, out GraphicsOptions read, out _));
            Assert.AreEqual(GraphicsOptions.Default, read);
        }

        [Test]
        public void Json_UnknownNames_ReadAsNotOverridden()
        {
            const string json = "{ \"schemaVersion\": 1, \"preset\": \"Ultra\", \"shadows\": \"RayTraced\", " +
                                "\"fog\": false, \"futureOption\": 3 }";

            bool ok = GraphicsOptionsJson.TryDeserialize(json, out GraphicsOptions read, out string error);

            Assert.IsTrue(ok, error);
            Assert.IsNull(read.Preset, "an unknown preset becomes the platform default");
            Assert.IsNull(read.Shadows);
            Assert.AreEqual(false, read.Fog, "known values next to unknown ones are kept");
        }

        [TestCase("")]
        [TestCase("not json at all")]
        [TestCase("[1, 2, 3]")]
        [TestCase("{ \"preset\": \"High\" }")]
        [TestCase("{ \"schemaVersion\": 2, \"preset\": \"High\" }")]
        [TestCase("{ \"schemaVersion\": 1, \"postProcessing\": \"yes\" }")]
        [TestCase("{ \"schemaVersion\": 1, \"renderScale\": \"big\" }")]
        [TestCase("{ \"schemaVersion\": 1, \"preset\": 2 }")]
        public void Json_MalformedDocument_IsRejectedWithDefaults(string json)
        {
            bool ok = GraphicsOptionsJson.TryDeserialize(json, out GraphicsOptions read, out string error);

            Assert.IsFalse(ok);
            Assert.AreEqual(GraphicsOptions.Default, read);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error), "the caller needs a reason to log");
        }

        [Test]
        public void Service_LoadsTheStoredChoice()
        {
            InMemoryGraphicsOptionsStore store = new(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));

            GraphicsOptionsService service = new(store, GraphicsQualityPreset.High);

            Assert.AreEqual(GraphicsQualityPreset.Low, service.Effective.Preset);
            Assert.AreEqual(GraphicsQualityPreset.High, service.PlatformDefaultPreset);
        }

        [Test]
        public void Service_SetOptions_SavesThenRaisesChangedOnce()
        {
            InMemoryGraphicsOptionsStore store = new();
            GraphicsOptionsService service = new(store, GraphicsQualityPreset.High);
            List<ResolvedGraphicsOptions> raised = new();
            service.Changed += raised.Add;

            service.SetOptions(GraphicsOptions.Default.WithPostProcessing(false));

            Assert.AreEqual(1, store.SaveCount);
            Assert.AreEqual(GraphicsOptions.Default.WithPostProcessing(false), store.Load());
            Assert.AreEqual(1, raised.Count);
            Assert.IsFalse(raised[0].PostProcessing);
            Assert.AreSame(service.Effective, raised[0]);
        }

        [Test]
        public void Service_EqualChoice_SavesAndRaisesNothing()
        {
            InMemoryGraphicsOptionsStore store = new(GraphicsOptions.ForPreset(GraphicsQualityPreset.Medium));
            GraphicsOptionsService service = new(store, GraphicsQualityPreset.High);
            int raised = 0;
            service.Changed += _ => raised++;

            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Medium));

            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(0, raised);
        }

        [Test]
        public void Service_ResetToDefaults_DropsPresetAndOverrides()
        {
            InMemoryGraphicsOptionsStore store =
                new(new GraphicsOptions(GraphicsQualityPreset.Low, postProcessing: true));
            GraphicsOptionsService service = new(store, GraphicsQualityPreset.High);

            service.ResetToDefaults();

            Assert.AreEqual(GraphicsOptions.Default, service.Options);
            Assert.AreEqual(GraphicsQualityPreset.High, service.Effective.Preset);
        }

        [Test]
        public void Service_ReplacedStore_ReportsAFailedSave()
        {
            FailingStore store = new();
            GraphicsOptionsService service = new(store, GraphicsQualityPreset.Medium);
            ResolvedGraphicsOptions raised = null;
            service.Changed += effective => raised = effective;

            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));

            Assert.AreEqual(1, store.SaveCalls, "the service writes through the store it was given");
            Assert.IsFalse(service.LastSaveSucceeded);
            Assert.IsNotNull(raised, "the change still applies for this session");
            Assert.AreEqual(GraphicsQualityPreset.Low, service.Effective.Preset);
        }

        [Test]
        public void Service_RequiresAStoreAndRejectsNullOptions()
        {
            Assert.Throws<ArgumentNullException>(() => new GraphicsOptionsService(null, GraphicsQualityPreset.High));
            GraphicsOptionsService service = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            Assert.Throws<ArgumentNullException>(() => service.SetOptions(null));
        }

        [Test]
        public void Options_HasOverrides_IgnoresThePreset()
        {
            Assert.IsFalse(GraphicsOptions.ForPreset(GraphicsQualityPreset.High).HasOverrides);
            Assert.IsTrue(GraphicsOptions.Default.WithSky(false).HasOverrides);
            Assert.IsFalse(GraphicsOptions.Default.WithSky(false).WithSky(null).HasOverrides);
        }

        private static ResolvedGraphicsOptions Resolve(float? renderScale)
        {
            return GraphicsOptionsResolver.Resolve(new GraphicsOptions(renderScale: renderScale),
                GraphicsQualityPreset.High);
        }

        private sealed class FailingStore : IGraphicsOptionsStore
        {
            public int SaveCalls { get; private set; }

            public GraphicsOptions Load()
            {
                return GraphicsOptions.Default;
            }

            public bool Save(GraphicsOptions options)
            {
                SaveCalls++;
                return false;
            }
        }
    }
}

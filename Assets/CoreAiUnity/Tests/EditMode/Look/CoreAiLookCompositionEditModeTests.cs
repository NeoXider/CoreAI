using System;
using System.Collections.Generic;
using CoreAI.Look;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The look block's composition and lifecycle: every service is replaceable (settings overrides and
    /// host registrations made first), the default applier falls back without a render-pipeline assembly,
    /// and the entry point applies on start and on every change, publishes the options to the Hub, and
    /// reverts on dispose. <see cref="LookMode.Off"/> does nothing at all.
    /// </summary>
    public sealed class CoreAiLookCompositionEditModeTests
    {
        private Func<ICoreAiLookApplier> _savedPipelineDefault;

        [SetUp]
        public void SetUp()
        {
            _savedPipelineDefault = CoreAiLookApplierRegistry.PipelineDefault;
            CoreAiGraphicsUi.Service = null;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiLookApplierRegistry.RegisterPipelineDefault(_savedPipelineDefault);
            CoreAiGraphicsUi.Service = null;
        }

        [Test]
        public void Services_UseTheSettingsOverrides()
        {
            RecordingApplier applier = new();
            InMemoryGraphicsOptionsStore store = new();
            CoreAiLookSettings settings = new() { ApplierOverride = applier, StoreOverride = store };
            ContainerBuilder builder = new();

            builder.RegisterCoreAiLookServices(settings);
            using IObjectResolver container = builder.Build();
            IGraphicsOptionsService service = container.Resolve<IGraphicsOptionsService>();
            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));

            Assert.AreSame(applier, container.Resolve<ICoreAiLookApplier>());
            Assert.AreSame(settings, container.Resolve<CoreAiLookSettings>());
            Assert.AreSame(store, container.Resolve<IGraphicsOptionsStore>());
            Assert.AreEqual(1, store.SaveCount, "the service writes through the replaced store");
        }

        [Test]
        public void Services_HostRegistrationsMadeFirst_Win()
        {
            RecordingApplier hostApplier = new();
            InMemoryGraphicsOptionsStore hostStore = new();
            FixedOptionsService hostService = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<ICoreAiLookApplier>(hostApplier);
            builder.RegisterInstance<IGraphicsOptionsStore>(hostStore);
            builder.RegisterInstance<IGraphicsOptionsService>(hostService);

            builder.RegisterCoreAiLookServices(new CoreAiLookSettings { ApplierOverride = new RecordingApplier() });
            using IObjectResolver container = builder.Build();

            Assert.AreSame(hostApplier, container.Resolve<ICoreAiLookApplier>());
            Assert.AreSame(hostStore, container.Resolve<IGraphicsOptionsStore>());
            Assert.AreSame(hostService, container.Resolve<IGraphicsOptionsService>());
        }

        [Test]
        public void DefaultApplier_WithoutAPipelineAssembly_IsTheRenderSettingsApplier()
        {
            CoreAiLookApplierRegistry.RegisterPipelineDefault(null);
            ContainerBuilder builder = new();

            builder.RegisterCoreAiLookServices(
                new CoreAiLookSettings { StoreOverride = new InMemoryGraphicsOptionsStore() });
            using IObjectResolver container = builder.Build();

            Assert.AreEqual(typeof(RenderSettingsLookApplier), container.Resolve<ICoreAiLookApplier>().GetType());
        }

        [Test]
        public void DefaultApplier_ComesFromTheRegisteredPipelineFactory()
        {
            RecordingApplier pipelineApplier = new();
            CoreAiLookApplierRegistry.RegisterPipelineDefault(() => pipelineApplier);

            Assert.AreSame(pipelineApplier, CoreAiLookApplierRegistry.CreateDefault());

            CoreAiLookApplierRegistry.RegisterPipelineDefault(() => null);
            Assert.IsInstanceOf<RenderSettingsLookApplier>(CoreAiLookApplierRegistry.CreateDefault(),
                "a pipeline factory that declines (pipeline not active) falls back");
        }

        [Test]
        public void RegisterCoreAiLook_AddsTheEntryPointOnce()
        {
            ContainerBuilder builder = new();
            CoreAiLookSettings settings = new();

            builder.RegisterCoreAiLook(settings);
            builder.RegisterCoreAiLook(new CoreAiLookSettings());

            Assert.IsTrue(builder.Exists(typeof(CoreAiLookEntryPoint)));
            Assert.IsTrue(builder.Exists(typeof(IGraphicsOptionsService), true));
            Assert.IsTrue(builder.Exists(typeof(ICoreAiLookApplier), true));
        }

        [Test]
        public void EntryPoint_AppliesOnStartAndOnEveryChange_AndPublishesTheService()
        {
            RecordingApplier applier = new();
            GraphicsOptionsService service = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            CoreAiLookSettings settings = new() { Mode = LookMode.FillMissing };
            CoreAiLookEntryPoint entryPoint = new(service, applier, settings);

            entryPoint.Start();
            service.SetOptions(GraphicsOptions.Default.WithPostProcessing(false));

            Assert.AreEqual(2, applier.Applied.Count);
            Assert.AreEqual(LookMode.FillMissing, applier.Applied[0].Mode);
            Assert.IsTrue(applier.Applied[0].Options.PostProcessing);
            Assert.IsFalse(applier.Applied[1].Options.PostProcessing);
            Assert.IsNotNull(applier.Applied[0].Profile, "the code-default profile is used without an asset");
            Assert.AreSame(service, CoreAiGraphicsUi.Service);

            entryPoint.Dispose();
        }

        [Test]
        public void EntryPoint_Dispose_RevertsAndWithdrawsTheService()
        {
            RecordingApplier applier = new();
            GraphicsOptionsService service = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            CoreAiLookEntryPoint entryPoint = new(service, applier, new CoreAiLookSettings());
            entryPoint.Start();

            entryPoint.Dispose();
            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));

            Assert.AreEqual(1, applier.RevertCount);
            Assert.AreEqual(1, applier.Applied.Count, "no apply after dispose");
            Assert.IsNull(CoreAiGraphicsUi.Service);
            Assert.IsFalse(entryPoint.IsActive);
        }

        [Test]
        public void EntryPoint_ModeOff_ChangesNothing()
        {
            RecordingApplier applier = new();
            GraphicsOptionsService service = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            CoreAiLookEntryPoint entryPoint = new(service, applier, new CoreAiLookSettings { Mode = LookMode.Off });

            entryPoint.Start();
            service.SetOptions(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));
            entryPoint.Dispose();

            Assert.IsEmpty(applier.Applied);
            Assert.AreEqual(0, applier.RevertCount);
            Assert.IsNull(CoreAiGraphicsUi.Service, "the Hub page shows 'managed by the game'");
        }

        [Test]
        public void EntryPoint_DoesNotWithdrawAServicePublishedByAnotherScope()
        {
            GraphicsOptionsService mine = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            GraphicsOptionsService other = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            CoreAiLookEntryPoint entryPoint = new(mine, new RecordingApplier(), new CoreAiLookSettings());
            entryPoint.Start();
            CoreAiGraphicsUi.Service = other;

            entryPoint.Dispose();

            Assert.AreSame(other, CoreAiGraphicsUi.Service);
        }

        [Test]
        public void EntryPoint_RequiresEveryDependency()
        {
            GraphicsOptionsService service = new(new InMemoryGraphicsOptionsStore(), GraphicsQualityPreset.High);
            Assert.Throws<ArgumentNullException>(() =>
                new CoreAiLookEntryPoint(null, new RecordingApplier(), new CoreAiLookSettings()));
            Assert.Throws<ArgumentNullException>(() =>
                new CoreAiLookEntryPoint(service, null, new CoreAiLookSettings()));
            Assert.Throws<ArgumentNullException>(() =>
                new CoreAiLookEntryPoint(service, new RecordingApplier(), null));
        }

        [Test]
        public void PlatformDefault_IsMediumOnWebAndMobile_HighOnDesktop()
        {
            Assert.AreEqual(GraphicsQualityPreset.Medium,
                CoreAiGraphicsPlatform.DefaultPresetFor(UnityEngine.RuntimePlatform.WebGLPlayer));
            Assert.AreEqual(GraphicsQualityPreset.Medium,
                CoreAiGraphicsPlatform.DefaultPresetFor(UnityEngine.RuntimePlatform.Android));
            Assert.AreEqual(GraphicsQualityPreset.High,
                CoreAiGraphicsPlatform.DefaultPresetFor(UnityEngine.RuntimePlatform.WindowsPlayer));
        }

        private sealed class RecordingApplier : ICoreAiLookApplier
        {
            public List<(CoreAiLookProfile Profile, ResolvedGraphicsOptions Options, LookMode Mode)> Applied { get; } =
                new();

            public int RevertCount { get; private set; }

            public void Apply(CoreAiLookProfile profile, ResolvedGraphicsOptions options, LookMode mode)
            {
                Applied.Add((profile, options, mode));
            }

            public void Revert()
            {
                RevertCount++;
            }
        }

        private sealed class FixedOptionsService : IGraphicsOptionsService
        {
            public GraphicsOptions Options => GraphicsOptions.Default;

            public ResolvedGraphicsOptions Effective =>
                GraphicsOptionsResolver.PresetValues(GraphicsQualityPreset.Low);

            public GraphicsQualityPreset PlatformDefaultPreset => GraphicsQualityPreset.Low;

            public bool LastSaveSucceeded => true;

            public event Action<ResolvedGraphicsOptions> Changed
            {
                add { }
                remove { }
            }

            public void SetOptions(GraphicsOptions options)
            {
            }

            public void ResetToDefaults()
            {
            }
        }
    }
}

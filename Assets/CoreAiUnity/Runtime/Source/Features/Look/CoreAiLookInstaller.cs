using CoreAI.Infrastructure.Logging;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Look
{
    /// <summary>
    /// Registers the default look and the player's graphics options: <see cref="IGraphicsOptionsStore"/>,
    /// <see cref="IGraphicsOptionsService"/>, <see cref="ICoreAiLookApplier"/> and the entry point that
    /// applies them. A registration made before this call wins for each of the three services, and so do
    /// the overrides on <see cref="CoreAiLookSettings"/>.
    /// </summary>
    public static class CoreAiLookInstaller
    {
        /// <summary>Registers the look services and the entry point that applies them.</summary>
        /// <param name="builder">Target container builder.</param>
        /// <param name="settings">Scope settings; null applies the code-default look.</param>
        public static void RegisterCoreAiLook(this IContainerBuilder builder, CoreAiLookSettings settings = null)
        {
            if (builder.Exists(typeof(CoreAiLookEntryPoint)))
            {
                return;
            }

            builder.RegisterCoreAiLookServices(settings);
            builder.RegisterEntryPoint<CoreAiLookEntryPoint>();
        }

        /// <summary>
        /// Registers the settings, store, options service and applier without the entry point, for a host
        /// that drives <see cref="CoreAiLookEntryPoint"/> (or its own applier calls) itself.
        /// </summary>
        public static void RegisterCoreAiLookServices(this IContainerBuilder builder,
            CoreAiLookSettings settings = null)
        {
            CoreAiLookSettings look = settings ?? new CoreAiLookSettings();
            if (!builder.Exists(typeof(CoreAiLookSettings)))
            {
                builder.RegisterInstance(look);
            }

            if (!builder.Exists(typeof(IGraphicsOptionsStore), true))
            {
                builder.Register<IGraphicsOptionsStore>(
                    c => look.StoreOverride ?? new FileGraphicsOptionsStore(
                        logger: c.ResolveOrDefault<IGameLogger>()),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IGraphicsOptionsService), true))
            {
                builder.Register<IGraphicsOptionsService>(
                    c => new GraphicsOptionsService(
                        c.Resolve<IGraphicsOptionsStore>(),
                        CoreAiGraphicsPlatform.DefaultPresetFor(Application.platform)),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(ICoreAiLookApplier), true))
            {
                builder.Register<ICoreAiLookApplier>(
                    _ => look.ApplierOverride ?? CoreAiLookApplierRegistry.CreateDefault(),
                    Lifetime.Singleton);
            }
        }
    }
}

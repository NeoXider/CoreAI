using CoreAI.Infrastructure.Logging;
using VContainer;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Registers the runtime UI host. A lifetime scope adds one line, <c>builder.RegisterRuntimeUi();</c>,
    /// after <c>RegisterCore</c> (the host logs through <see cref="IGameLogger"/> when one is registered).
    /// A host game replaces the host by registering its own <see cref="IRuntimeUiHost"/> after this call,
    /// or switches runtime UI off with <c>new RuntimeUiHostOptions { Enabled = false }</c>.
    /// </summary>
    public static class RuntimeUiInstaller
    {
        /// <summary>Registers <see cref="IRuntimeUiHost"/> and <see cref="IRuntimeUiTextInputFocus"/> as one singleton.</summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="options">Host configuration; null uses the defaults (enabled, auto-created panel).</param>
        public static void RegisterRuntimeUi(this IContainerBuilder builder, RuntimeUiHostOptions options = null)
        {
            RuntimeUiHostOptions effective = options ?? new RuntimeUiHostOptions();
            if (!effective.Enabled)
            {
                builder.Register<DisabledRuntimeUiHost>(Lifetime.Singleton)
                    .As<IRuntimeUiHost, IRuntimeUiTextInputFocus>();
                return;
            }

            builder.Register(resolver =>
                {
                    resolver.TryResolve(out IGameLogger logger);
                    return new RuntimeUiHost(effective, logger);
                }, Lifetime.Singleton)
                .As<IRuntimeUiHost, IRuntimeUiTextInputFocus>()
                .AsSelf();
        }
    }
}

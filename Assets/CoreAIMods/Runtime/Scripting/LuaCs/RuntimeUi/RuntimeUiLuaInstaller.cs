using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.RuntimeUi;
using CoreAI.UiFocus;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers the Lua surface of runtime UI (<c>game:GetService("UxmlService")</c>). The runtime UI
    /// host comes from the container when the Unity layer registered it (<c>RegisterRuntimeUi</c>);
    /// without one (headless, dedicated server) every screen call answers <c>UI_UNAVAILABLE</c>. A host
    /// game configures the per-mod budgets with an <see cref="RbxUiBudgets"/> argument or registration,
    /// and keeps mods off the screen by masking <see cref="Ai.LuaCapabilities.Ui"/>.
    /// </summary>
    public static class RuntimeUiLuaInstaller
    {
        /// <summary>Registers <see cref="RuntimeUiLuaSurface"/> as a scope singleton, built eagerly so focus reporting starts at once.</summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="budgets">Per-mod limits; null resolves a registered <see cref="RbxUiBudgets"/> or uses the defaults.</param>
        public static void RegisterRuntimeUiLua(this IContainerBuilder builder, RbxUiBudgets budgets = null)
        {
            builder.Register(c =>
            {
                IRuntimeUiHost host = c.ResolveOrDefault<IRuntimeUiHost>();
                IRbxUiBackend backend = host != null ? new RuntimeUiHostBackend(host) : null;
                // WHY null when the CoreAI scope reports the host itself: one reporter per surface, and the scope's
                // reporter also serves games without the mods package.
                ICoreAiUiFocus focus = c.ResolveOrDefault<RuntimeUiHostFocusReporter>() != null
                    ? null
                    : c.ResolveOrDefault<ICoreAiUiFocus>();
                return new RuntimeUiLuaSurface(backend, budgets ?? c.ResolveOrDefault<RbxUiBudgets>(), focus);
            }, Lifetime.Singleton);
            builder.RegisterBuildCallback(container => container.Resolve<RuntimeUiLuaSurface>());
        }

        /// <summary>
        /// Gives one Rbx world its <c>UxmlService</c>; a no-op when <see cref="RegisterRuntimeUiLua"/> was
        /// not called. The mods composition calls it for every world it builds (the initial one and each
        /// world a load stages).
        /// </summary>
        public static void AttachRuntimeUi(IObjectResolver resolver, LuaCsRbxApiBindings rbxApi)
        {
            if (resolver == null || rbxApi == null)
            {
                return;
            }

            resolver.ResolveOrDefault<RuntimeUiLuaSurface>()?.AttachTo(rbxApi);
        }
    }
}

using CoreAI.ChatOverlay;
using CoreAI.Logging;
using CoreAI.RuntimeUi;
using CoreAI.UiFocus;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers the scope's one <see cref="ICoreAiUiFocus"/> and connects the surfaces the container builds to it:
    /// the in-world chat overlay reports its open input under <see cref="ChatOverlaySurfaceId"/>, the runtime UI host
    /// its focused field and hovered screens under <see cref="RuntimeUiSurfaceId"/>, and the overlay's open keys are
    /// gated on <c>!HoldsKeyboard</c>, so typing in any CoreAI text field, or a Hub open over the game, never moves a
    /// character and never opens the chat. The chat panel and the Hub report themselves.
    /// </summary>
    /// <remarks>
    /// A focus service or an <see cref="IChatOverlayOpenGate"/> registered before this call is kept. A scope without
    /// the overlay or the runtime UI host gets an inert reporter for it. The mods package's runtime-UI Lua surface
    /// reports the same host only when no <see cref="RuntimeUiHostFocusReporter"/> is registered, so each surface is
    /// reported once.
    /// </remarks>
    public static class CoreAiUiFocusInstaller
    {
        /// <summary>The surface id the chat overlay reports under.</summary>
        public const string ChatOverlaySurfaceId = "coreai.chat-overlay";

        /// <summary>The surface id the runtime UI host reports under (the one the Lua surface used to own).</summary>
        public const string RuntimeUiSurfaceId = "coreai.runtime-ui";

        /// <summary>Registers the focus service, the overlay open gate and the surface reporters.</summary>
        public static void RegisterCoreAiUiFocus(this IContainerBuilder builder)
        {
            if (!builder.Exists(typeof(ICoreAiUiFocus), true))
            {
                // WHY one per scope: every CoreAI surface (chat, Hub, overlays) reports into it, and a mods scope
                // bridges it into the world's input focus, so typing in any of them never moves a character.
                builder.Register<CoreAiUiFocus>(Lifetime.Singleton).As<ICoreAiUiFocus>();
            }

            if (!builder.Exists(typeof(IChatOverlayOpenGate), true))
            {
                builder.Register<IChatOverlayOpenGate>(c => new UiFocusChatOverlayOpenGate(c.Resolve<ICoreAiUiFocus>()),
                    Lifetime.Singleton);
            }

            builder.Register(c => new ChatOverlayUiFocusReporter(
                    c.ResolveOrDefault<IChatOverlayInputState>(),
                    c.Resolve<ICoreAiUiFocus>(),
                    ChatOverlaySurfaceId),
                Lifetime.Singleton);
            builder.Register(c => new RuntimeUiHostFocusReporter(
                    c.ResolveOrDefault<IRuntimeUiHost>(),
                    c.Resolve<ICoreAiUiFocus>(),
                    RuntimeUiSurfaceId),
                Lifetime.Singleton);

            // WHY resolved at build: a reporter subscribes in its constructor and nothing else asks for it; the
            // container disposes both with the scope, which releases what they reported.
            builder.RegisterBuildCallback(container =>
            {
                Connect<ChatOverlayUiFocusReporter>(container, "The chat overlay");
                Connect<RuntimeUiHostFocusReporter>(container, "The runtime UI");
            });
        }

        private static void Connect<T>(IObjectResolver container, string surface)
        {
            try
            {
                container.Resolve<T>();
            }
            catch (VContainerException ex)
            {
                (container.ResolveOrDefault<ILog>() ?? Log.Instance).Warn(
                    "[CoreAiUiFocus] " + surface + " could not be connected to the UI focus, so typing in it may " +
                    "reach the game: " + ex.Message, LogTag.Composition);
            }
        }
    }
}

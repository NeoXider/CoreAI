using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using MessagePipe;
using MessagePipe.VContainer;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Ensures the global MessagePipe broker is available before CoreAI services start.
    /// </summary>
    public static class GlobalMessagePipeMinimalBootstrap
    {
        /// <summary>
        /// Initializes a minimal global MessagePipe provider for LLM diagnostics when no main lifetime scope exists yet.
        /// </summary>
        public static void EnsureInitializedForLlmDiagnostics()
        {
            if (GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            ContainerBuilder builder = new();
            MessagePipeOptions opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<LlmBackendSelected>(opts);
            builder.RegisterMessageBroker<LlmRequestStarted>(opts);
            builder.RegisterMessageBroker<LlmRequestCompleted>(opts);
            builder.RegisterMessageBroker<LlmUsageReported>(opts);
            builder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            builder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            builder.RegisterMessageBroker<LlmToolCallFailed>(opts);
            builder.RegisterMessageBroker<LlmAuthExpired>(opts);
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
            builder.RegisterMessageBroker<LuaModEventEmitted>(opts);
#endif

            IObjectResolver resolver = builder.Build();
            GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
        }
    }
}
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
    /// <remarks>
    ///
    ///
    ///
    ///
    /// </remarks>
    public static class GlobalMessagePipeMinimalBootstrap
    {
        /// <summary>
        /// Executes ensure initialized for llm diagnostics.
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

            IObjectResolver resolver = builder.Build();
            GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
        }
    }
}

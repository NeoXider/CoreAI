using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Chat;
using CoreAI.ChatOverlay;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers orchestrator mode (a lead agent that plans and hands tasks to sub-agents) in a lifetime scope.
    /// Off unless <see cref="OrchestratorModeOptions.Enabled"/>: with the mode off this registers nothing, so no
    /// session, tool or option exists and a single-agent turn is exactly what it was.
    /// <para>
    /// Sub-agents run through the <see cref="AiOrchestrator"/> itself, below the admission queue: the lead's turn
    /// holds a queue slot while it waits for a delegation's result, so a sub-agent queued behind it could wait
    /// for a slot the lead never frees. Deadlines use the Unity frame-driven marshaler, which also exists in the
    /// WebGL player. Call it after <c>RegisterCore</c>, which registers the orchestrator and the log.
    /// </para>
    /// <para>
    /// It also registers the <see cref="OrchestratorChatDriver"/> the chat service hands chat turns to (the chat
    /// panel and the in-world overlay both pick it up) and <see cref="OrchestratorModeChatPresenter"/>, which
    /// forwards tool activity to the plans and, when the overlay is registered (before or after this call), shows
    /// the plan on its status line and feed and adds the <c>/orchestrator</c> command.
    /// </para>
    /// </summary>
    public static class OrchestratorModeInstaller
    {
        /// <summary>
        /// Registers <see cref="OrchestratorModeOptions"/>, <see cref="ISubAgentRegistry"/>,
        /// <see cref="ILeadInstructionsProvider"/>, the singleton <see cref="OrchestratorSessionRegistry"/>, the
        /// <see cref="OrchestratorChatDriver"/> and the presenter entry point when the mode is enabled. Returns
        /// whether anything was registered.
        /// </summary>
        /// <param name="builder">The scope's container builder.</param>
        /// <param name="options">Mode options; registered as the instance every session reads.</param>
        /// <param name="definitions">The sub-agents the lead may delegate to (validated when registered).</param>
        /// <param name="leadInstructions">Lead instructions; null uses <see cref="DefaultLeadInstructionsProvider"/>.</param>
        public static bool RegisterOrchestratorMode(
            this IContainerBuilder builder,
            OrchestratorModeOptions options,
            IEnumerable<SubAgentDefinition> definitions,
            ILeadInstructionsProvider leadInstructions = null)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (options == null || !options.Enabled)
            {
                return false;
            }

            SubAgentRegistry subAgents = new(definitions, options.DelegateToolName);
            ILeadInstructionsProvider lead = leadInstructions ?? new DefaultLeadInstructionsProvider();
            builder.RegisterInstance(options);
            builder.RegisterInstance<ISubAgentRegistry>(subAgents);
            builder.RegisterInstance<ILeadInstructionsProvider>(lead);
            builder.Register<OrchestratorSessionRegistry>(c => new OrchestratorSessionRegistry(
                    options,
                    subAgents,
                    c.Resolve<AiOrchestrator>(),
                    UnityMainThreadLlmAsyncMarshaler.Instance,
                    c.Resolve<ILog>(),
                    lead),
                Lifetime.Singleton);
            builder.Register<OrchestratorChatDriver>(c => new OrchestratorChatDriver(
                    c.Resolve<OrchestratorSessionRegistry>(),
                    c.Resolve<ILog>()),
                Lifetime.Singleton);
            builder.RegisterEntryPoint(c => new OrchestratorModeChatPresenter(
                    c.Resolve<OrchestratorChatDriver>(),
                    c.ResolveOrDefault<ChatOverlayController>(),
                    c.ResolveOrDefault<IChatCommandRegistry>()),
                Lifetime.Singleton);
            return true;
        }
    }
}

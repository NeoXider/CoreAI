using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Prompts;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>Registers prompt providers and prompt-version services in the DI container.</summary>
    public static class AgentPromptsInstaller
    {
        /// <summary>
        /// Registers and chains manifest, resource, and fallback prompt providers.
        /// </summary>
        public static void RegisterAgentPrompts(this IContainerBuilder builder, AgentPromptsManifest manifest)
        {
            AgentPromptsDefinition definition = manifest != null ? manifest.ToDefinition() : null;

            List<IAgentSystemPromptProvider> systemChain = new();
            if (definition != null)
            {
                systemChain.Add(new ManifestAgentSystemPromptProvider(definition));
            }

            systemChain.Add(new ResourcesAgentSystemPromptProvider("AgentPrompts/System"));
            systemChain.Add(new BuiltInDefaultAgentSystemPromptProvider());

            builder.RegisterInstance<IAgentSystemPromptProvider>(new ChainedAgentSystemPromptProvider(systemChain));

            List<IAgentUserPromptTemplateProvider> userChain = new();
            if (definition != null)
            {
                userChain.Add(new ManifestUserTemplateProvider(definition));
            }

            userChain.Add(new ResourcesUserTemplateProvider("AgentPrompts/User"));
            userChain.Add(new NoAgentUserPromptTemplateProvider());

            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(
                new ChainedAgentUserPromptTemplateProvider(userChain));

            // Skip processing when the checked condition is already satisfied.
            if (definition != null)
            {
                builder.RegisterBuildCallback(container =>
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)container.Resolve(typeof(AgentMemoryPolicy));
                    if (policy == null)
                    {
                        return;
                    }

                    foreach (AgentPromptEntryDefinition entry in definition.EnumerateEntries())
                    {
                        if (entry.OverrideUniversalPrefix && !string.IsNullOrWhiteSpace(entry.RoleId))
                        {
                            policy.SetOverrideUniversalPrefix(entry.RoleId, true);
                        }
                    }
                });
            }
        }
    }
}

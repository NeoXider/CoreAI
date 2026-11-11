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

            if (definition != null)
            {
                builder.RegisterBuildCallback(container =>
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)container.Resolve(typeof(AgentMemoryPolicy));
                    ApplyManifestRoles(policy, definition);
                });
            }
        }

        /// <summary>
        /// Applies a prompts manifest's role entries to <paramref name="policy"/>: the universal-prefix opt-outs, and
        /// every role the manifest names becomes a known role. A role whose prompt comes only from the manifest is
        /// configured, so it does not raise the policy's "role has no configuration" warning, while a misspelled role
        /// id still does (with the manifest's spelling as the suggestion). A role the policy already knows keeps its
        /// configuration. Internal for EditMode tests.
        /// </summary>
        internal static void ApplyManifestRoles(AgentMemoryPolicy policy, AgentPromptsDefinition definition)
        {
            if (policy == null || definition == null)
            {
                return;
            }

            foreach (AgentPromptEntryDefinition entry in definition.EnumerateEntries())
            {
                string roleId = entry?.RoleId?.Trim();
                if (string.IsNullOrEmpty(roleId))
                {
                    continue;
                }

                if (!policy.HasRole(roleId))
                {
                    // WHY ConfigureRole with no arguments: it records the role with exactly the defaults an
                    // unconfigured role gets anyway, so the manifest adds a known role without changing behaviour.
                    policy.ConfigureRole(roleId);
                }

                if (entry.OverrideUniversalPrefix)
                {
                    policy.SetOverrideUniversalPrefix(roleId, true);
                }
            }
        }
    }
}

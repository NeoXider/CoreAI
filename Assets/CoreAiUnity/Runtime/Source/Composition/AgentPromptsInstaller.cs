using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Prompts;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>Регистрация цепочек <see cref="IAgentSystemPromptProvider"/> и user-шаблонов из манифеста и Resources.</summary>
    public static class AgentPromptsInstaller
    {
        /// <summary>
        /// Регистрирует цепочку провайдеров промптов. Вызвать до <see cref="CorePortableInstaller.RegisterCorePortable"/>.
        /// </summary>
        public static void RegisterAgentPrompts(this IContainerBuilder builder, AgentPromptsManifest manifest)
        {
            List<IAgentSystemPromptProvider> systemChain = new();
            if (manifest != null)
            {
                systemChain.Add(new ManifestAgentSystemPromptProvider(manifest));
            }

            systemChain.Add(new ResourcesAgentSystemPromptProvider("AgentPrompts/System"));
            systemChain.Add(new BuiltInDefaultAgentSystemPromptProvider());

            builder.RegisterInstance<IAgentSystemPromptProvider>(new ChainedAgentSystemPromptProvider(systemChain));

            List<IAgentUserPromptTemplateProvider> userChain = new();
            if (manifest != null)
            {
                userChain.Add(new ManifestUserTemplateProvider(manifest));
            }

            userChain.Add(new ResourcesUserTemplateProvider("AgentPrompts/User"));
            userChain.Add(new NoAgentUserPromptTemplateProvider());

            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(
                new ChainedAgentUserPromptTemplateProvider(userChain));

            // Применяем overrideUniversalPrefix из манифеста при старте контейнера
            if (manifest != null)
            {
                builder.RegisterBuildCallback(container =>
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)container.Resolve(typeof(AgentMemoryPolicy));
                    if (policy == null) return;

                    foreach (AgentPromptsManifest.Entry entry in manifest.EnumerateEntries())
                    {
                        if (entry.overrideUniversalPrefix && !string.IsNullOrWhiteSpace(entry.roleId))
                        {
                            policy.SetOverrideUniversalPrefix(entry.roleId, true);
                        }
                    }
                });
            }
        }
    }
}
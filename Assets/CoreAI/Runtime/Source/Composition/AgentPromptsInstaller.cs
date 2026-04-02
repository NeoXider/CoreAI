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
            var systemChain = new List<IAgentSystemPromptProvider>();
            if (manifest != null)
                systemChain.Add(new ManifestAgentSystemPromptProvider(manifest));
            systemChain.Add(new ResourcesAgentSystemPromptProvider("AgentPrompts/System"));
            systemChain.Add(new BuiltInDefaultAgentSystemPromptProvider());

            builder.RegisterInstance<IAgentSystemPromptProvider>(new ChainedAgentSystemPromptProvider(systemChain));

            var userChain = new List<IAgentUserPromptTemplateProvider>();
            if (manifest != null)
                userChain.Add(new ManifestUserTemplateProvider(manifest));
            userChain.Add(new ResourcesUserTemplateProvider("AgentPrompts/User"));
            userChain.Add(new NoAgentUserPromptTemplateProvider());

            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(new ChainedAgentUserPromptTemplateProvider(userChain));
        }
    }
}

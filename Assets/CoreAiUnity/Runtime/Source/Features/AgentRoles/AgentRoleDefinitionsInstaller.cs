using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Unity;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Applies <see cref="AgentRoleDefinitionAsset"/>s to the scope's <see cref="AgentMemoryPolicy"/>, so a game
    /// replaces a built-in role or adds its own by assigning assets instead of writing code. The policy already
    /// holds the shipped <see cref="BuiltInRoleDefinitions"/>; an asset for the same role id replaces that one, and
    /// a later asset for one role id wins over an earlier one.
    /// </summary>
    public static class AgentRoleDefinitionsInstaller
    {
        /// <summary>Resources folder that <see cref="RegisterAgentRoleDefinitionsFromResources"/> reads by default.</summary>
        public const string DefaultResourcesPath = "CoreAI/AgentRoles";

        /// <summary>Applies <paramref name="definitions"/> to the policy when the container is built.</summary>
        public static void RegisterAgentRoleDefinitions(this IContainerBuilder builder,
            IReadOnlyList<AgentRoleDefinitionAsset> definitions)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            AgentRoleDefinitionAsset[] snapshot = new AgentRoleDefinitionAsset[definitions.Count];
            for (int i = 0; i < definitions.Count; i++)
            {
                snapshot[i] = definitions[i];
            }

            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    ApplyDefinitions(container.Resolve<AgentMemoryPolicy>(), snapshot);
                }
                catch (VContainerException)
                {
                    // WHY: Minimal containers (tests, headless tools) may omit the agent policy.
                }
            });
        }

        /// <summary>
        /// Applies every <see cref="AgentRoleDefinitionAsset"/> found under a Resources folder (sorted by asset
        /// name, so the result does not depend on load order) when the container is built.
        /// </summary>
        public static void RegisterAgentRoleDefinitionsFromResources(this IContainerBuilder builder,
            string resourcesPath = DefaultResourcesPath)
        {
            AgentRoleDefinitionAsset[] found = Resources.LoadAll<AgentRoleDefinitionAsset>(resourcesPath ?? "");
            if (found == null || found.Length == 0)
            {
                return;
            }

            System.Array.Sort(found, (left, right) => string.CompareOrdinal(left.name, right.name));
            RegisterAgentRoleDefinitions(builder, found);
        }

        /// <summary>
        /// Applies the assets to <paramref name="policy"/> in order and returns how many were applied. An asset
        /// without a role id is skipped with a warning.
        /// </summary>
        public static int ApplyDefinitions(AgentMemoryPolicy policy, IEnumerable<AgentRoleDefinitionAsset> definitions)
        {
            if (policy == null || definitions == null)
            {
                return 0;
            }

            int applied = 0;
            foreach (AgentRoleDefinitionAsset asset in definitions)
            {
                if (asset == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(asset.RoleId))
                {
                    (Log.Instance ?? NullLog.Instance).Warn(
                        $"[AgentRoleDefinitions] '{asset.name}' has no role id and was not applied.", LogTag.Core);
                    continue;
                }

                policy.ApplyRoleDefinition(asset.ToRoleDefinition());
                applied++;
            }

            return applied;
        }
    }
}

using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// One sub-agent of a default set: the role that runs it and the tool profiles its allowlist is read from, so
    /// the allowlist follows whatever the installers registered into those profiles instead of naming tools.
    /// </summary>
    public sealed class DefaultSubAgentSpec
    {
        /// <summary>Creates a spec; <paramref name="toolProfileIds"/> must name at least one profile.</summary>
        public DefaultSubAgentSpec(string id, string roleId, string description,
            IReadOnlyList<string> toolProfileIds, string instructions = "")
        {
            if (toolProfileIds == null || toolProfileIds.Count == 0)
            {
                throw new ArgumentException("A default sub-agent reads its tools from at least one profile.",
                    nameof(toolProfileIds));
            }

            Id = id ?? "";
            RoleId = roleId ?? "";
            Description = description ?? "";
            ToolProfileIds = toolProfileIds;
            Instructions = instructions ?? "";
        }

        /// <summary>Name the lead passes as <c>subagent</c>.</summary>
        public string Id { get; }

        /// <summary>Agent role that runs a delegated task.</summary>
        public string RoleId { get; }

        /// <summary>What the sub-agent is good at, written for the lead.</summary>
        public string Description { get; }

        /// <summary>The tool profiles whose tools the sub-agent may call.</summary>
        public IReadOnlyList<string> ToolProfileIds { get; }

        /// <summary>Optional per-sub-agent instructions.</summary>
        public string Instructions { get; }
    }

    /// <summary>
    /// The sub-agents orchestrator mode offers when the host supplies none: a <c>builder</c> on the Builder role with
    /// the world-builder profile's tools and a <c>scripter</c> on the Programmer role with the lua-builder profile's
    /// tools. Each is registered once the scope that fills its profiles has built (the CoreAI scope for the native
    /// world tool, the mods scope for the Lua tools), so a game without the mods package simply has no scripter.
    /// A host replaces the set through <c>CoreAILifetimeScope.DefaultSubAgents</c>, or supplies its own definitions
    /// through <c>CoreAILifetimeScope.SubAgents</c>, which turns the defaults off.
    /// </summary>
    public sealed class CoreAiDefaultSubAgents
    {
        /// <summary>Id of the default world-building sub-agent.</summary>
        public const string BuilderId = "builder";

        /// <summary>Id of the default Lua scripting sub-agent.</summary>
        public const string ScripterId = "scripter";

        // WHY named here and not read from a profile: the skill catalog tool is added by the policy to any role that
        // carries skills, so no profile lists it, and without it a sub-agent cannot open the skills its profile gives.
        private const string ReadSkillToolName = "read_skill";

        /// <summary>Creates a set from <paramref name="specs"/>; null or empty is an empty set.</summary>
        public CoreAiDefaultSubAgents(IEnumerable<DefaultSubAgentSpec> specs)
        {
            List<DefaultSubAgentSpec> list = new();
            if (specs != null)
            {
                foreach (DefaultSubAgentSpec spec in specs)
                {
                    if (spec != null)
                    {
                        list.Add(spec);
                    }
                }
            }

            Specs = list.AsReadOnly();
        }

        /// <summary>The specs of this set, in order.</summary>
        public IReadOnlyList<DefaultSubAgentSpec> Specs { get; }

        /// <summary>The shipped set: <see cref="BuilderId"/> and <see cref="ScripterId"/>.</summary>
        public static CoreAiDefaultSubAgents CreateBuiltIn()
        {
            return new CoreAiDefaultSubAgents(new[]
            {
                new DefaultSubAgentSpec(
                    BuilderId,
                    BuiltInAgentRoleIds.Builder,
                    "Places, moves and removes scene objects with the native world tools. Give it one concrete " +
                    "building task, for example 'build a small stone tower at the spawn'.",
                    new[] { BuiltInToolProfileIds.WorldBuilder }),
                new DefaultSubAgentSpec(
                    ScripterId,
                    BuiltInAgentRoleIds.Programmer,
                    "Builds and scripts the Roblox-style world in Lua: parts, models, mods with behaviour. Give it one " +
                    "self-contained task, for example 'make the door open when a player touches it'.",
                    new[] { BuiltInToolProfileIds.LuaBuilder })
            });
        }

        /// <summary>
        /// Registers every spec whose id is free and whose profiles hold at least one tool now; a spec whose
        /// profiles are still empty is left for a later call (the mods scope calls again once the Lua tools exist).
        /// Returns the ids it registered. Nothing happens without a registry or a policy.
        /// </summary>
        public IReadOnlyList<string> RegisterAvailable(ISubAgentRegistry registry, AgentMemoryPolicy policy)
        {
            List<string> added = new();
            if (!(registry is SubAgentRegistry writable) || policy == null)
            {
                return added;
            }

            foreach (DefaultSubAgentSpec spec in Specs)
            {
                if (registry.TryGet(spec.Id, out _))
                {
                    continue;
                }

                SubAgentDefinition definition = TryBuild(spec, policy);
                if (definition == null)
                {
                    continue;
                }

                writable.Register(definition);
                added.Add(definition.Id);
            }

            return added;
        }

        /// <summary>The definition of <paramref name="spec"/> from the profiles as they are now; null while they hold no tool.</summary>
        internal static SubAgentDefinition TryBuild(DefaultSubAgentSpec spec, AgentMemoryPolicy policy)
        {
            List<string> tools = new();
            bool hasSkills = false;
            foreach (string profileId in spec.ToolProfileIds)
            {
                ToolProfile profile = policy.GetToolProfile(profileId);
                if (profile == null)
                {
                    continue;
                }

                foreach (string name in profile.ToolNames)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !tools.Contains(name))
                    {
                        tools.Add(name);
                    }
                }

                hasSkills |= profile.Skills != null && profile.Skills.Count > 0;
            }

            if (tools.Count == 0)
            {
                return null;
            }

            if (hasSkills && !tools.Contains(ReadSkillToolName))
            {
                tools.Add(ReadSkillToolName);
            }

            return new SubAgentDefinition(spec.Id, spec.RoleId, spec.Description, tools,
                instructions: spec.Instructions);
        }
    }

    /// <summary>Registers a default sub-agent set into a CoreAI scope that runs orchestrator mode.</summary>
    public static class CoreAiDefaultSubAgentsInstaller
    {
        /// <summary>
        /// Registers <paramref name="defaults"/> as an instance (the mods scope reads it to add the sub-agents whose
        /// tools it supplies) and registers the ones available once this scope built. Call it after every installer
        /// that fills the profiles the set reads, since build callbacks run in registration order.
        /// </summary>
        public static void RegisterCoreAiDefaultSubAgents(this IContainerBuilder builder,
            CoreAiDefaultSubAgents defaults)
        {
            CoreAiDefaultSubAgents effective = defaults ?? CoreAiDefaultSubAgents.CreateBuiltIn();
            builder.RegisterInstance(effective);
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    effective.RegisterAvailable(
                        container.ResolveOrDefault<ISubAgentRegistry>(),
                        container.ResolveOrDefault<AgentMemoryPolicy>());
                }
                catch (VContainerException)
                {
                    // WHY: minimal containers (tests, headless tools) may omit the agent policy.
                }
            });
        }
    }
}

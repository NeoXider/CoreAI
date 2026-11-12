using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Golden snapshot of the per-role state a fresh <see cref="AgentMemoryPolicy"/> holds for every built-in role,
    /// and of the tool and skill wiring the shipped installers give those roles. The expected text was recorded
    /// from the policy as it was before role definitions and tool profiles existed (the hard-coded constructor
    /// and the direct <c>AddToolForRole(Programmer, ...)</c> installer calls), so any drift in the data-driven
    /// replacement shows up here as a one-line diff.
    /// </summary>
    public sealed class AgentMemoryPolicyGoldenSnapshotEditModeTests
    {
        internal const string GoldenFreshPolicy =
            "Creator|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=0|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "Builder|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=0|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "Analyzer|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "Programmer|known=True|memory=True|action=Append|duplicates=-|history=False|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=0|toolResults=Full|temperature=-|reasoning=-|sampling=-|llmCompaction=False|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "AINpc|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "CoreMechanicAI|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=Full|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "PlainChat|known=True|memory=False|action=Append|duplicates=-|history=True|persist=True|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "SmartChat|known=True|memory=True|action=Append|duplicates=-|history=True|persist=True|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "Merchant|known=True|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "HostRole|known=False|memory=True|action=Append|duplicates=-|history=True|persist=False|context=0|maxMessages=30|maxOutput=-|roundtrips=-|toolResults=CompactSummary|temperature=-|reasoning=-|sampling=-|llmCompaction=True|ratio=-|tools=memory|skills=|prefixOverride=False|streaming=-|additionalPrompt=-\n" +
            "known roles=AINpc,Analyzer,Builder,CoreMechanicAI,Creator,Merchant,PlainChat,Programmer,SmartChat\n" +
            "empty role config=memory=True|history=True|roundtrips=0|toolResults=CompactSummary|llmCompaction=True";

        internal const string GoldenComposedTools =
            "Creator|tools=memory,world_command|skills=\n" +
            "Builder|tools=memory,world_command|skills=\n" +
            "Analyzer|tools=memory|skills=\n" +
            "Programmer|tools=memory,camera,execute_lua,manage_mods,get_mod_logs,save_world,load_world,list_autosaves,load_autosave,read_skill|skills=Lua Modding,Rbx API,Full Lua\n" +
            "AINpc|tools=memory|skills=\n" +
            "CoreMechanicAI|tools=memory|skills=\n" +
            "PlainChat|tools=|skills=\n" +
            "SmartChat|tools=memory|skills=\n" +
            "Merchant|tools=memory|skills=";

        private const string HostRole = "HostRole";

        [Test]
        public void FreshPolicy_ReproducesTheRecordedBuiltInRoleConfiguration()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(GoldenFreshPolicy, DescribeFreshPolicy(policy));
        }

        [Test]
        public void ComposedPolicy_ReproducesTheRecordedInstallerToolWiring()
        {
            AgentMemoryPolicy policy = new();
            ShippedComposition.ComposeTheWayInstallersDidBeforeToolProfiles(policy);

            Assert.AreEqual(GoldenComposedTools, DescribeComposedTools(policy));
        }

        [Test]
        public void ComposedThroughToolProfiles_ReproducesTheSameWiring()
        {
            AgentMemoryPolicy policy = new();
            ShippedComposition.Compose(policy);

            Assert.AreEqual(GoldenComposedTools, DescribeComposedTools(policy),
                "registering into the shipped tool profiles must give every built-in role exactly the tools " +
                "and skills the direct AddToolForRole wiring gave it");
        }

        [Test]
        public void PolicyBuiltFromTheShippedDefinitions_MatchesTheFreshPolicy()
        {
            AgentMemoryPolicy policy = AgentMemoryPolicy.Create(BuiltInRoleDefinitions.CreateAll());

            Assert.AreEqual(GoldenFreshPolicy, DescribeFreshPolicy(policy));
        }

        [Test]
        public void GoldenText_IsSensitive_ToAChangedRole()
        {
            AgentMemoryPolicy policy = new();
            policy.SetMaxToolCallRoundtrips(BuiltInAgentRoleIds.Analyzer, 7);

            Assert.AreNotEqual(GoldenFreshPolicy, DescribeFreshPolicy(policy),
                "a snapshot that does not move when a role changes would pin nothing");
        }

        internal static string DescribeFreshPolicy(AgentMemoryPolicy policy)
        {
            StringBuilder text = new();
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                text.Append(DescribeRole(policy, roleId)).Append('\n');
            }

            text.Append(DescribeRole(policy, HostRole)).Append('\n');
            text.Append("known roles=").Append(string.Join(",", policy.GetKnownRoleIds())).Append('\n');
            AgentMemoryPolicy.RoleMemoryConfig empty = policy.GetRoleConfig("");
            text.Append("empty role config=memory=").Append(empty.UseMemoryTool)
                .Append("|history=").Append(empty.WithChatHistory)
                .Append("|roundtrips=").Append(Format(empty.MaxToolCallRoundtrips))
                .Append("|toolResults=").Append(empty.ToolResultMemory)
                .Append("|llmCompaction=").Append(empty.UseLlmContextCompaction);
            return text.ToString();
        }

        internal static string DescribeComposedTools(AgentMemoryPolicy policy)
        {
            List<string> lines = new();
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                lines.Add(roleId + "|tools=" + ToolNames(policy, roleId) + "|skills=" + SkillNames(policy, roleId));
            }

            return string.Join("\n", lines);
        }

        internal static string DescribeRole(AgentMemoryPolicy policy, string roleId)
        {
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(roleId);
            StringBuilder line = new();
            line.Append(roleId)
                .Append("|known=").Append(policy.HasRole(roleId))
                .Append("|memory=").Append(config.UseMemoryTool)
                .Append("|action=").Append(config.DefaultAction)
                .Append("|duplicates=").Append(Format(config.AllowDuplicateToolCalls))
                .Append("|history=").Append(config.WithChatHistory)
                .Append("|persist=").Append(config.PersistChatHistory)
                .Append("|context=").Append(config.ContextTokens.ToString(CultureInfo.InvariantCulture))
                .Append("|maxMessages=").Append(config.MaxChatHistoryMessages.ToString(CultureInfo.InvariantCulture))
                .Append("|maxOutput=").Append(Format(config.MaxOutputTokens))
                .Append("|roundtrips=").Append(Format(config.MaxToolCallRoundtrips))
                .Append("|toolResults=").Append(config.ToolResultMemory)
                .Append("|temperature=").Append(Format(config.Temperature))
                .Append("|reasoning=").Append(config.Reasoning.HasValue ? config.Reasoning.Value.Effort.ToString() : "-")
                .Append("|sampling=").Append(config.Sampling == null ? "-" : "set")
                .Append("|llmCompaction=").Append(config.UseLlmContextCompaction)
                .Append("|ratio=").Append(Format(config.CompactionTriggerRatio))
                .Append("|tools=").Append(ToolNames(policy, roleId))
                .Append("|skills=").Append(SkillNames(policy, roleId))
                .Append("|prefixOverride=").Append(policy.IsUniversalPrefixOverridden(roleId))
                .Append("|streaming=")
                .Append(policy.TryGetStreamingOverride(roleId, out bool streaming) ? streaming.ToString() : "-")
                .Append("|additionalPrompt=")
                .Append(policy.TryGetAdditionalSystemPrompt(roleId, out string prompt) ? prompt : "-");
            return line.ToString();
        }

        internal static string ToolNames(AgentMemoryPolicy policy, string roleId)
        {
            List<string> names = new();
            foreach (ILlmTool tool in policy.GetToolsForRole(roleId))
            {
                names.Add(tool?.Name ?? "<null>");
            }

            return string.Join(",", names);
        }

        internal static string SkillNames(AgentMemoryPolicy policy, string roleId)
        {
            List<string> names = new();
            foreach (SkillSet skill in policy.GetSkillsForRole(roleId))
            {
                names.Add(skill.Name);
            }

            return string.Join(",", names);
        }

        private static string Format(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "-";
        }

        private static string Format(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static string Format(float? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "-";
        }

        /// <summary>
        /// Reproduces the registrations the shipped installers make (<c>WorldCommandsInstaller</c> first, then
        /// the mods child scope's <c>CoreAiModsInstaller</c>) with stand-in tools of the same names.
        /// </summary>
        internal static class ShippedComposition
        {
            internal static readonly string[] LuaBuilderTools =
            {
                "execute_lua", "manage_mods", "get_mod_logs", "save_world", "load_world", "list_autosaves",
                "load_autosave"
            };

            internal static readonly string[] LuaBuilderSkills = { "Lua Modding", "Rbx API", "Full Lua" };

            internal static ILlmTool Stub(string name)
            {
                return new DelegateLlmTool(name, "Stand-in for " + name + ".", (Func<string, string>)(input => name));
            }

            internal static SkillSet StubSkill(string name)
            {
                return SkillSet.FromTextContent(name, name + " reference.", "Instructions for " + name + ".");
            }

            /// <summary>The registrations the installers make today: everything into the shipped tool profiles.</summary>
            internal static void Compose(AgentMemoryPolicy policy)
            {
                policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, "world_command",
                    roleId => Stub("world_command"));
                policy.AddToolToProfile(BuiltInToolProfileIds.Vision, "camera", roleId => Stub("camera"));
                foreach (string name in LuaBuilderTools)
                {
                    policy.AddToolToProfile(BuiltInToolProfileIds.LuaBuilder, name, roleId => Stub(name));
                }

                foreach (string skill in LuaBuilderSkills)
                {
                    policy.AddSkillToProfile(BuiltInToolProfileIds.LuaBuilder, StubSkill(skill));
                }
            }

            /// <summary>The registrations the installers made before tool profiles existed.</summary>
            internal static void ComposeTheWayInstallersDidBeforeToolProfiles(AgentMemoryPolicy policy)
            {
                foreach (string roleId in new[] { BuiltInAgentRoleIds.Creator, BuiltInAgentRoleIds.Builder })
                {
                    policy.AddToolForRole(roleId, Stub("world_command"));
                }

                policy.AddToolForRole(BuiltInAgentRoleIds.Programmer, Stub("camera"));
                foreach (string name in LuaBuilderTools)
                {
                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer, Stub(name));
                }

                foreach (string skill in LuaBuilderSkills)
                {
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, StubSkill(skill));
                }
            }
        }
    }
}

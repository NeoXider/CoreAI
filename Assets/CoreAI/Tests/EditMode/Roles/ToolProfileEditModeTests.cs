using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Tool profiles as data: installers register tools into named profiles, role definitions reference them, and
    /// changing a role's profile list in data changes what reaches its requests.
    /// </summary>
    public sealed class ToolProfileEditModeTests
    {
        private sealed class RoleBoundTool : ILlmTool
        {
            public RoleBoundTool(string name, string roleId)
            {
                Name = name;
                RoleId = roleId;
            }

            public string RoleId { get; }
            public string Name { get; }
            public string Description => "Role-bound test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        [Test]
        public void ProfileTool_ReachesEveryRoleWhoseDefinitionListsTheProfile()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, "world_command",
                    roleId => AgentRoleTestHarness.Stub("world_command")));

            CollectionAssert.AreEqual(new[] { "memory", "world_command" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)));
            CollectionAssert.AreEqual(new[] { "memory", "world_command" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Builder)));
            CollectionAssert.AreEqual(new[] { "memory" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Analyzer)),
                "a role whose definition does not list the profile gets nothing from it");
        }

        [Test]
        public void ProfileFactory_ReceivesTheRoleItServes_AndRunsOncePerRole()
        {
            AgentMemoryPolicy policy = new();
            int calls = 0;
            policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, "world_command", roleId =>
            {
                calls++;
                return new RoleBoundTool("world_command", roleId);
            });

            RoleBoundTool creator = (RoleBoundTool)policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)[1];
            RoleBoundTool builder = (RoleBoundTool)policy.GetToolsForRole(BuiltInAgentRoleIds.Builder)[1];
            RoleBoundTool creatorAgain = (RoleBoundTool)policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)[1];

            Assert.AreEqual(BuiltInAgentRoleIds.Creator, creator.RoleId);
            Assert.AreEqual(BuiltInAgentRoleIds.Builder, builder.RoleId);
            Assert.AreSame(creator, creatorAgain, "a role keeps its instance between reads");
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void SameName_RegisteredTwiceInAProfile_KeepsOneTool_TheLatest()
        {
            AgentMemoryPolicy policy = new();
            ILlmTool first = AgentRoleTestHarness.Stub("game_state");
            ILlmTool second = AgentRoleTestHarness.Stub("game_state");

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, first));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, first), "the very same instance changes nothing");
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Replaced,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, second));

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(BuiltInAgentRoleIds.AiNpc);
            Assert.AreEqual(1, tools.Count(tool => tool.Name == "game_state"));
            Assert.AreSame(second, tools.Single(tool => tool.Name == "game_state"));
            CollectionAssert.AreEqual(new[] { "game_state" },
                policy.GetToolProfile(BuiltInToolProfileIds.Npc).ToolNames);
        }

        [Test]
        public void InvalidRegistrations_ChangeNothing()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddToolToProfile(" ", AgentRoleTestHarness.Stub("x")));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, (ILlmTool)null));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, "", roleId => AgentRoleTestHarness.Stub("x")));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddToolToProfile(BuiltInToolProfileIds.Npc, "x", null));
            Assert.IsNull(policy.GetToolProfile("never-registered"));
        }

        [Test]
        public void DirectTool_WinsOverAProfileToolOfTheSameName()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, AgentRoleTestHarness.Stub("world_command"));
            ILlmTool hostTool = AgentRoleTestHarness.Stub("world_command");

            policy.AddToolForRole(BuiltInAgentRoleIds.Creator, hostTool);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(BuiltInAgentRoleIds.Creator);
            Assert.AreEqual(1, tools.Count(tool => tool.Name == "world_command"),
                "strict providers reject a request that declares one function twice");
            Assert.AreSame(hostTool, tools.Single(tool => tool.Name == "world_command"));
        }

        [Test]
        public void RemovedProfileTool_LeavesTheRole()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, AgentRoleTestHarness.Stub("world_command"));
            Assert.Contains("world_command",
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)));

            Assert.IsTrue(policy.RemoveToolFromProfile(BuiltInToolProfileIds.WorldBuilder, "world_command"));

            CollectionAssert.AreEqual(new[] { "memory" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)));
            Assert.IsFalse(policy.RemoveToolFromProfile(BuiltInToolProfileIds.WorldBuilder, "world_command"));
        }

        [Test]
        public void ReplacingTheProgrammersProfileWithNpc_InData_RemovesExecuteLua()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);
            RoleDefinition programmer = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Programmer);
            programmer.ToolProfiles = new[] { BuiltInToolProfileIds.Npc };

            policy.ApplyRoleDefinition(programmer);

            string[] tools = AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer));
            CollectionAssert.DoesNotContain(tools, "execute_lua");
            CollectionAssert.DoesNotContain(tools, "camera");
            CollectionAssert.DoesNotContain(tools, "read_skill",
                "the Lua skills came from lua-builder and leave with it, meta-tools included");
            Assert.IsEmpty(policy.GetSkillsForRole(BuiltInAgentRoleIds.Programmer));
            CollectionAssert.AreEqual(new[] { "memory" }, tools);
        }

        [Test]
        public async Task ProgrammerRequest_CarriesExecuteLua_WithTheShippedProfile_AndNotWithNpc()
        {
            AgentMemoryPolicy shipped = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(shipped);
            AgentRoleTestHarness.CapturingLlmClient shippedLlm = new();
            await AgentRoleTestHarness.BuildOrchestrator(shippedLlm, shipped).RunTaskAsync(
                new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Programmer, Hint = "spawn a red part" },
                CancellationToken.None);

            AgentMemoryPolicy npc = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(npc);
            RoleDefinition programmer = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Programmer);
            programmer.ToolProfiles = new[] { BuiltInToolProfileIds.Npc };
            npc.ApplyRoleDefinition(programmer);
            AgentRoleTestHarness.CapturingLlmClient npcLlm = new();
            await AgentRoleTestHarness.BuildOrchestrator(npcLlm, npc).RunTaskAsync(
                new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Programmer, Hint = "spawn a red part" },
                CancellationToken.None);

            Assert.Contains("execute_lua", AgentRoleTestHarness.ToolNames(shippedLlm.LastRequest),
                "negative twin: the shipped lua-builder profile keeps execute_lua in the request");
            CollectionAssert.DoesNotContain(AgentRoleTestHarness.ToolNames(npcLlm.LastRequest), "execute_lua");
        }

        [Test]
        public void ProfileSkills_ReachTheRole_WithTheMetaTools()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            CollectionAssert.AreEqual(new[] { "Lua Modding", "Rbx API", "Full Lua" },
                policy.GetSkillsForRole(BuiltInAgentRoleIds.Programmer).Select(skill => skill.Name));
            Assert.IsEmpty(policy.GetSkillsForRole(BuiltInAgentRoleIds.Creator));
        }

        [Test]
        public void SetToolProfilesForRole_AttachesAndDetaches()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            policy.SetToolProfilesForRole(BuiltInAgentRoleIds.SmartChat, new[] { BuiltInToolProfileIds.Vision, " " });

            CollectionAssert.AreEqual(new[] { BuiltInToolProfileIds.Vision },
                policy.GetToolProfilesForRole(BuiltInAgentRoleIds.SmartChat));
            CollectionAssert.AreEqual(new[] { "memory", "camera" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.SmartChat)));

            policy.SetToolProfilesForRole(BuiltInAgentRoleIds.SmartChat, Array.Empty<string>());

            CollectionAssert.AreEqual(new[] { "memory" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.SmartChat)));
        }

        [Test]
        public void SetToolsForRole_StillReplacesTheWholeToolList()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, new[] { AgentRoleTestHarness.Stub("dig") });

            CollectionAssert.AreEqual(new[] { "memory", "dig" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)),
                "SetToolsForRole replaced installer tools before profiles existed and still does");
        }

        [Test]
        public void AddToolForRole_KeepsTheProfileTools()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            policy.AddToolForRole(BuiltInAgentRoleIds.Creator, AgentRoleTestHarness.Stub("dig"));

            CollectionAssert.AreEqual(new[] { "memory", "world_command", "dig" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator)));
        }

        [Test]
        public void AgentBuilderWithoutAToolProfile_StillReplacesTheInstallerTools()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            new AgentBuilder(BuiltInAgentRoleIds.Programmer) { SuppressBuildWarnings = true }
                .WithTool(AgentRoleTestHarness.Stub("dig"))
                .BuildDetached()
                .ApplyToPolicy(policy);

            CollectionAssert.AreEqual(new[] { "dig" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer)));
            Assert.IsEmpty(policy.GetSkillsForRole(BuiltInAgentRoleIds.Programmer));
        }

        [Test]
        public void AgentBuilderWithAToolProfile_GetsItsToolsAndSkills()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);

            AgentConfig config = new AgentBuilder("Modder") { SuppressBuildWarnings = true }
                .WithSystemPrompt("You write mods.")
                .WithTool(AgentRoleTestHarness.Stub("dig"))
                .WithToolProfile(BuiltInToolProfileIds.LuaBuilder, BuiltInToolProfileIds.LuaBuilder)
                .BuildDetached();
            config.ApplyToPolicy(policy);

            string[] tools = AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Modder"));
            CollectionAssert.IsSubsetOf(
                AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.LuaBuilderTools, tools);
            CollectionAssert.IsSubsetOf(new[] { "dig", "read_skill", "call_skill_tool" }, tools);
            CollectionAssert.AreEqual(new[] { BuiltInToolProfileIds.LuaBuilder }, config.ToolProfiles);
            CollectionAssert.AreEqual(new[] { "Lua Modding", "Rbx API", "Full Lua" },
                policy.GetSkillsForRole("Modder").Select(skill => skill.Name));
            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt("Modder", out string prompt));
            StringAssert.Contains("Rbx API", prompt, "profile skills join the injected skill catalog");
        }

        [Test]
        public void FailingFactory_SkipsOnlyThatTool_AndReportsItOnce()
        {
            AgentMemoryPolicy policy = new();
            AgentRoleTestHarness.RecordingLog log = new();
            policy.DiagnosticsLog = log;
            policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, "broken",
                roleId => throw new InvalidOperationException("no executor"));
            policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, AgentRoleTestHarness.Stub("world_command"));

            string[] first = AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator));
            string[] second = AgentRoleTestHarness.ToolNames(policy.GetToolsForRole(BuiltInAgentRoleIds.Creator));

            CollectionAssert.AreEqual(new[] { "memory", "world_command" }, first);
            CollectionAssert.AreEqual(first, second);
            Assert.AreEqual(1, log.Errors.Count, string.Join("\n", log.Errors));
            StringAssert.Contains("no executor", log.Errors[0]);
        }
    }
}

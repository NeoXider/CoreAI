using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Role definitions as data: the nine shipped definitions, a host definition replacing a built-in role without
    /// code, a definition adding a role, the prompt reference and inline prompt, and the copy-on-apply contract.
    /// </summary>
    public sealed class RoleDefinitionEditModeTests
    {
        [Test]
        public void ShippedDefinitions_AreTheNineBuiltInRoles_AsFreshCopies()
        {
            IReadOnlyList<RoleDefinition> first = BuiltInRoleDefinitions.CreateAll();
            IReadOnlyList<RoleDefinition> second = BuiltInRoleDefinitions.CreateAll();

            CollectionAssert.AreEqual(BuiltInAgentRoleIds.AllBuiltInRoles, first.Select(d => d.Id).ToArray());
            Assert.AreNotSame(first[0], second[0], "every call hands out copies a host may edit freely");
            Assert.IsFalse(BuiltInRoleDefinitions.TryCreate("Blacksmith", out _));
            Assert.Throws<ArgumentException>(() => BuiltInRoleDefinitions.Create("Blacksmith"));
        }

        [Test]
        public void HostDefinition_ReplacesABuiltInRole_AndLeavesTheOthersAsShipped()
        {
            AgentMemoryPolicy policy = new();
            RoleDefinition programmer = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Programmer);
            programmer.WithChatHistory = true;
            programmer.MaxToolCallRoundtrips = 5;
            programmer.Temperature = 0.4f;
            programmer.AdditionalSystemPrompt = "Prefer small mods.";

            policy.ApplyRoleDefinition(programmer);

            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer);
            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(5, config.MaxToolCallRoundtrips);
            Assert.AreEqual(0.4f, config.Temperature);
            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt(BuiltInAgentRoleIds.Programmer, out string overlay));
            Assert.AreEqual("Prefer small mods.", overlay);

            string[] expected = AgentMemoryPolicyGoldenSnapshotEditModeTests.GoldenFreshPolicy.Split('\n');
            string[] actual = AgentMemoryPolicyGoldenSnapshotEditModeTests.DescribeFreshPolicy(policy).Split('\n');
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].StartsWith(BuiltInAgentRoleIds.Programmer + "|", StringComparison.Ordinal))
                {
                    Assert.AreNotEqual(expected[i], actual[i], "the override must reach the Programmer line");
                    continue;
                }

                Assert.AreEqual(expected[i], actual[i], "an override of one role must not move another");
            }
        }

        [Test]
        public void ApplyingTheShippedDefinitionAgain_RestoresTheBuiltInRole()
        {
            AgentMemoryPolicy policy = new();
            RoleDefinition changed = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.SmartChat);
            changed.UseMemoryTool = false;
            changed.OverrideUniversalPrefix = true;
            changed.EnableStreaming = false;
            policy.ApplyRoleDefinition(changed);

            policy.ApplyRoleDefinition(BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.SmartChat));

            Assert.AreEqual(AgentMemoryPolicyGoldenSnapshotEditModeTests.GoldenFreshPolicy,
                AgentMemoryPolicyGoldenSnapshotEditModeTests.DescribeFreshPolicy(policy),
                "a definition is the whole role: prefix opt-out and streaming preference go with it");
        }

        [Test]
        public void DefinitionForANewRole_RegistersIt()
        {
            AgentMemoryPolicy policy = new();
            Assert.IsFalse(policy.HasRole("Blacksmith"));

            policy.ApplyRoleDefinition(new RoleDefinition("  Blacksmith  ")
            {
                UseMemoryTool = false,
                WithChatHistory = false,
                RoutingProfileId = "small-local"
            });

            Assert.IsTrue(policy.HasRole("Blacksmith"), "the id is trimmed on apply");
            Assert.IsFalse(policy.IsMemoryEnabled("Blacksmith"));
            Assert.IsFalse(policy.GetRoleConfig("Blacksmith").WithChatHistory);
            Assert.IsTrue(policy.TryGetRoutingProfile("Blacksmith", out string routing));
            Assert.AreEqual("small-local", routing);
            Assert.IsFalse(policy.TryGetRoutingProfile(BuiltInAgentRoleIds.Merchant, out _),
                "built-in roles prefer no routing profile");
        }

        [Test]
        public void AppliedDefinition_IsACopy()
        {
            AgentMemoryPolicy policy = new();
            RoleDefinition definition = new("Guard") { MaxChatHistoryMessages = 12 };
            policy.ApplyRoleDefinition(definition);

            definition.MaxChatHistoryMessages = 99;
            definition.Id = "Other";

            Assert.AreEqual(12, policy.GetRoleConfig("Guard").MaxChatHistoryMessages);
            Assert.IsTrue(policy.TryGetRoleDefinition("Guard", out RoleDefinition stored));
            Assert.AreEqual("Guard", stored.Id);
            stored.MaxChatHistoryMessages = 1;
            Assert.AreEqual(12, policy.GetRoleConfig("Guard").MaxChatHistoryMessages,
                "the record handed back is a copy too");
        }

        [Test]
        public void DefinitionWithoutId_IsRefused()
        {
            AgentMemoryPolicy policy = new();

            Assert.Throws<ArgumentNullException>(() => policy.ApplyRoleDefinition(null));
            Assert.Throws<ArgumentException>(() => policy.ApplyRoleDefinition(new RoleDefinition("   ")));
        }

        [Test]
        public void NegativeRoundtripAndOutputCaps_MeanInherit()
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("Scout") { MaxToolCallRoundtrips = -1, MaxOutputTokens = -5 });

            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig("Scout");
            Assert.IsNull(config.MaxToolCallRoundtrips);
            Assert.IsNull(config.MaxOutputTokens);
        }

        [Test]
        public void PromptReference_BorrowsAnotherRolesBasePrompt()
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith") { PromptRoleId = BuiltInAgentRoleIds.Merchant });
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null, policy,
                new CoreAISettingsOptions { UniversalSystemPromptPrefix = "" });

            string prompt = composer.GetSystemPrompt("Blacksmith");

            StringAssert.StartsWith("You are a shopkeeper/merchant NPC.", prompt);
            Assert.AreEqual(BuiltInAgentRoleIds.Merchant, policy.ResolvePromptRoleId("Blacksmith"));
        }

        [Test]
        public void WithoutAPromptReference_ARoleKeepsItsOwnPrompt()
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith"));
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null, policy,
                new CoreAISettingsOptions { UniversalSystemPromptPrefix = "" });

            StringAssert.Contains("\"Blacksmith\"", composer.GetSystemPrompt("Blacksmith"));
            Assert.AreEqual("Blacksmith", policy.ResolvePromptRoleId("Blacksmith"));
        }

        [Test]
        public void InlineSystemPrompt_ReplacesTheChain_ButARequestOverrideStillWins()
        {
            AgentMemoryPolicy policy = new();
            RoleDefinition merchant = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Merchant);
            merchant.SystemPrompt = "You are a grumpy dwarf trader.";
            merchant.AdditionalSystemPrompt = "Prices are in gold.";
            policy.ApplyRoleDefinition(merchant);
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null, policy,
                new CoreAISettingsOptions { UniversalSystemPromptPrefix = "RULES" });

            Assert.AreEqual("RULES\nYou are a grumpy dwarf trader.\n\nPrices are in gold.",
                composer.GetSystemPrompt(BuiltInAgentRoleIds.Merchant));
            Assert.AreEqual("RULES\nPer request.\n\nPrices are in gold.",
                composer.GetSystemPrompt(BuiltInAgentRoleIds.Merchant, "Per request."));
        }

        [Test]
        public void OverrideUniversalPrefix_FromADefinition_DropsThePrefix()
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("JsonParser")
            {
                SystemPrompt = "Return JSON.",
                OverrideUniversalPrefix = true
            });
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null, policy,
                new CoreAISettingsOptions { UniversalSystemPromptPrefix = "RULES" });

            Assert.AreEqual("Return JSON.", composer.GetSystemPrompt("JsonParser"));
        }

        [Test]
        public void InlineSkills_ReachTheRolesCatalog_AndLeaveWithTheDefinition()
        {
            AgentMemoryPolicy policy = new();
            SkillSet forging = SkillSet.FromTextContent("Forging", "Forge items.", "Heat, hammer, quench.");
            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith") { Skills = new[] { forging } });

            CollectionAssert.AreEqual(new[] { "Forging" }, policy.GetSkillsForRole("Blacksmith").Select(s => s.Name));
            CollectionAssert.IsSubsetOf(new[] { "read_skill", "call_skill_tool" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Blacksmith")));

            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith"));

            Assert.IsEmpty(policy.GetSkillsForRole("Blacksmith"));
            CollectionAssert.AreEqual(new[] { "memory" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Blacksmith")),
                "a catalog emptied by the new definition takes its meta-tools with it");
        }

        [Test]
        public void HostSkill_SurvivesARedefinition()
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith")
            {
                Skills = new[] { SkillSet.FromTextContent("Forging", "Forge items.", "Hammer.") }
            });
            policy.AddSkillForRole("Blacksmith", SkillSet.FromTextContent("Haggling", "Haggle.", "Smile."));

            policy.ApplyRoleDefinition(new RoleDefinition("Blacksmith"));

            CollectionAssert.AreEqual(new[] { "Haggling" }, policy.GetSkillsForRole("Blacksmith").Select(s => s.Name));
            CollectionAssert.IsSubsetOf(new[] { "read_skill", "call_skill_tool" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Blacksmith")));
        }
    }
}

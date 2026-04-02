using System.Linq;
using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AgentRolesAndPromptsTests
    {
        [Test]
        public void BuiltInAgentRoleIds_AllBuiltInRoles_MatchesConstants()
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BuiltInAgentRoleIds.Creator,
                    BuiltInAgentRoleIds.Analyzer,
                    BuiltInAgentRoleIds.Programmer,
                    BuiltInAgentRoleIds.AiNpc,
                    BuiltInAgentRoleIds.CoreMechanic,
                    BuiltInAgentRoleIds.PlayerChat
                },
                BuiltInAgentRoleIds.AllBuiltInRoles.ToArray());
        }

        [Test]
        public void BuiltInDefaultSystemPrompts_AllRoles_NonEmptyAndDistinct()
        {
            var provider = new BuiltInDefaultAgentSystemPromptProvider();
            var texts = new System.Collections.Generic.List<string>();
            foreach (var role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                Assert.IsTrue(provider.TryGetSystemPrompt(role, out var sys), role);
                Assert.IsFalse(string.IsNullOrWhiteSpace(sys), role);
                StringAssert.Contains("You are", sys, role);
                texts.Add(sys.Trim());
            }

            Assert.AreEqual(texts.Count, texts.Distinct().Count(), "Промпты ролей должны различаться.");
        }

        [Test]
        public void AiPromptComposer_AllBuiltInRoles_GetSystemPrompt()
        {
            var provider = new BuiltInDefaultAgentSystemPromptProvider();
            var noUser = new NoAgentUserPromptTemplateProvider();
            var composer = new AiPromptComposer(provider, noUser);
            foreach (var role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                var s = composer.GetSystemPrompt(role);
                Assert.IsFalse(string.IsNullOrWhiteSpace(s), role);
            }
        }

        [Test]
        public void AiPromptComposer_AllBuiltInRoles_BuildUserPayload()
        {
            var provider = new BuiltInDefaultAgentSystemPromptProvider();
            var noUser = new NoAgentUserPromptTemplateProvider();
            var composer = new AiPromptComposer(provider, noUser);
            var snap = new GameSessionSnapshot();
            snap.Telemetry["wave"] = "3";
            snap.Telemetry["mode"] = "arena";
            snap.Telemetry["party"] = "2";
            foreach (var role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                var u = composer.BuildUserPayload(snap, new AiTaskRequest { RoleId = role, Hint = "test" });
                StringAssert.Contains("\"telemetry\":{", u);
                StringAssert.Contains("\"wave\":\"3\"", u);
                StringAssert.Contains("\"mode\":\"arena\"", u);
                StringAssert.Contains("\"party\":\"2\"", u);
                StringAssert.Contains("\"hint\":\"test\"", u);
            }
        }

        private static readonly string[] AllRoleIdCases = BuiltInAgentRoleIds.AllBuiltInRoles.ToArray();

        [TestCaseSource(nameof(AllRoleIdCases))]
        public void StubLlmClient_EachBuiltInRole_ReturnsOk(string roleId)
        {
            var client = new StubLlmClient();
            var r = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = roleId,
                SystemPrompt = "sys",
                UserPayload = "payload"
            }).GetAwaiter().GetResult();
            Assert.IsTrue(r.Ok, roleId);
            Assert.IsFalse(string.IsNullOrEmpty(r.Content), roleId);
            if (roleId == BuiltInAgentRoleIds.PlayerChat)
            {
                StringAssert.StartsWith("[stub]", r.Content);
            }
            else if (roleId == BuiltInAgentRoleIds.Programmer)
            {
                StringAssert.Contains("```lua", r.Content, roleId);
                StringAssert.Contains("report('stub:", r.Content, roleId);
            }
            else
            {
                StringAssert.Contains("ApplyWaveModifier", r.Content);
                StringAssert.Contains("\"agentRole\":\"" + roleId + "\"", r.Content);
            }
        }
    }
}

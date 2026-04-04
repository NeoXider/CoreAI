using System.Collections.Generic;
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
            BuiltInDefaultAgentSystemPromptProvider provider = new();
            List<string> texts = new();
            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                Assert.IsTrue(provider.TryGetSystemPrompt(role, out string sys), role);
                Assert.IsFalse(string.IsNullOrWhiteSpace(sys), role);
                StringAssert.Contains("You are", sys, role);
                texts.Add(sys.Trim());
            }

            Assert.AreEqual(texts.Count, texts.Distinct().Count(), "Промпты ролей должны различаться.");
        }

        [Test]
        public void AiPromptComposer_AllBuiltInRoles_GetSystemPrompt()
        {
            BuiltInDefaultAgentSystemPromptProvider provider = new();
            NoAgentUserPromptTemplateProvider noUser = new();
            AiPromptComposer composer = new(provider, noUser, new NullLuaScriptVersionStore());
            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                string s = composer.GetSystemPrompt(role);
                Assert.IsFalse(string.IsNullOrWhiteSpace(s), role);
            }
        }

        [Test]
        public void AiPromptComposer_AllBuiltInRoles_BuildUserPayload()
        {
            BuiltInDefaultAgentSystemPromptProvider provider = new();
            NoAgentUserPromptTemplateProvider noUser = new();
            AiPromptComposer composer = new(provider, noUser, new NullLuaScriptVersionStore());
            GameSessionSnapshot snap = new();
            snap.Telemetry["wave"] = "3";
            snap.Telemetry["mode"] = "arena";
            snap.Telemetry["party"] = "2";
            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                string u = composer.BuildUserPayload(snap, new AiTaskRequest { RoleId = role, Hint = "test" });
                StringAssert.Contains("\"telemetry\":{", u);
                StringAssert.Contains("\"wave\":\"3\"", u);
                StringAssert.Contains("\"mode\":\"arena\"", u);
                StringAssert.Contains("\"party\":\"2\"", u);
                StringAssert.Contains("\"hint\":\"test\"", u);
            }
        }

        [Test]
        public void AiPromptComposer_UserTemplate_ReplacesTelemetryPlaceholders()
        {
            BuiltInDefaultAgentSystemPromptProvider sys = new();
            FixedUserTemplateProvider user = new("w={wave} m={mode} hint={hint}");
            AiPromptComposer composer = new(sys, user, new NullLuaScriptVersionStore());
            GameSessionSnapshot snap = new();
            snap.Telemetry["wave"] = "7";
            snap.Telemetry["mode"] = "solo";
            string u = composer.BuildUserPayload(snap,
                new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "go" });
            Assert.AreEqual("w=7 m=solo hint=go", u);
        }

        private sealed class FixedUserTemplateProvider : IAgentUserPromptTemplateProvider
        {
            private readonly string _template;

            public FixedUserTemplateProvider(string template)
            {
                _template = template;
            }

            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = _template;
                return true;
            }
        }

        private static readonly string[] AllRoleIdCases = BuiltInAgentRoleIds.AllBuiltInRoles.ToArray();

        [TestCaseSource(nameof(AllRoleIdCases))]
        public void StubLlmClient_EachBuiltInRole_ReturnsOk(string roleId)
        {
            StubLlmClient client = new();
            LlmCompletionResult r = client.CompleteAsync(new LlmCompletionRequest
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
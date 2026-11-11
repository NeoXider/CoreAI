using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// A request without a role id, a memory store and a skill proxy used to throw from
    /// <c>RoleBinding.RequireRole</c> while the request was built, failing the whole turn. The owner's default:
    /// the turn runs, and <c>call_skill_tool</c> answers each call with a reported tool error.
    /// </summary>
    public sealed class CallSkillToolEmptyRoleEditModeTests
    {
        private static ILlmTool[] SkillProxies(out InMemoryAgentMemoryStore store)
        {
            store = new InMemoryAgentMemoryStore();
            SkillSet[] catalog =
            {
                new SkillSet("forge", "Forge items.", "Call forge_item.",
                    new DelegateLlmTool("forge_item", "Forges one item.", (Func<string, string>)(item => "forged " + item))),
                new SkillSet("notes", "Notes.", "Use memory.", new MemoryLlmTool())
            };
            return new[] { ReadSkillLlmTool.Create(catalog), CallSkillToolLlmTool.Create(catalog) };
        }

        private static async Task<JObject> InvokeAsync(ILlmTool tool, IDictionary<string, object> arguments)
        {
            object result = await ((IAIFunctionLlmTool)tool).CreateAIFunction()
                .InvokeAsync(new AIFunctionArguments(arguments), CancellationToken.None);
            return JObject.Parse(result.ToString());
        }

        private static IDictionary<string, object> Call(string tool, string json)
        {
            return new Dictionary<string, object> { ["tool_name"] = tool, ["arguments_json"] = json };
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void BindingWithoutARole_DoesNotThrow(string roleId)
        {
            ILlmTool[] proxies = SkillProxies(out InMemoryAgentMemoryStore store);

            Assert.DoesNotThrow(() => CallSkillToolLlmTool.BindToolsForRole(proxies, store, roleId));
            Assert.DoesNotThrow(() => CallSkillToolLlmTool.BindForRole(proxies[1], store, roleId));
        }

        [Test]
        public async Task CallWithoutARole_IsAReportedToolError_AndRunsNothing()
        {
            ILlmTool[] proxies = SkillProxies(out InMemoryAgentMemoryStore store);
            IReadOnlyList<ILlmTool> bound = CallSkillToolLlmTool.BindToolsForRole(proxies, store, "");

            Assert.IsFalse(((IResolvedLlmToolCallProvider)bound[1]).TryResolveInvocation(
                Call("forge_item", "{\"item\":\"sword\"}"), out ResolvedLlmToolInvocation invocation, out string error));
            Assert.IsNull(invocation);
            Assert.AreEqual(CallSkillToolLlmTool.MissingRoleError, error);

            JObject reported = await InvokeAsync(bound[1], Call("notes", "{\"action\":\"append\",\"content\":\"x\"}"));
            Assert.IsFalse(reported.Value<bool>("success"));
            StringAssert.Contains("role id", reported.Value<string>("error"));
            Assert.IsFalse(store.TryLoad("", out _), "a refused memory call writes nothing");
        }

        [Test]
        public async Task ReadSkillWithoutARole_StillReadsTheCatalog()
        {
            ILlmTool[] proxies = SkillProxies(out InMemoryAgentMemoryStore store);
            IReadOnlyList<ILlmTool> bound = CallSkillToolLlmTool.BindToolsForRole(proxies, store, " ");

            JObject skill = await InvokeAsync(bound[0], new Dictionary<string, object> { ["skill_name"] = "notes" });

            Assert.IsTrue(skill.Value<bool>("success"), skill.ToString());
        }

        [Test]
        public async Task CallWithARole_StillRuns()
        {
            ILlmTool[] proxies = SkillProxies(out InMemoryAgentMemoryStore store);
            IReadOnlyList<ILlmTool> bound = CallSkillToolLlmTool.BindToolsForRole(proxies, store, "Smith");

            Assert.IsTrue(((IResolvedLlmToolCallProvider)bound[1]).TryResolveInvocation(
                Call("forge_item", "{\"item\":\"sword\"}"), out ResolvedLlmToolInvocation invocation, out string error),
                error);
            Assert.AreEqual("forged sword", (await invocation.InvokeAsync(CancellationToken.None)).ToString());

            JObject memory = await InvokeAsync(bound[1], Call("memory", "{\"action\":\"append\",\"content\":\"fact\"}"));
            Assert.IsTrue(store.TryLoad("Smith", out AgentMemoryState state), memory.ToString());
            StringAssert.Contains("fact", state.Memory);
        }
    }
}

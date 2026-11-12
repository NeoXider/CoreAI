#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// P16: the argument shapes a small model gets wrong are accepted or refused in one sentence, on the direct path
    /// and through <c>call_skill_tool</c>; a cut result says so first; a read whose result was trimmed from the
    /// history runs again.
    /// </summary>
    [TestFixture]
    public sealed class ToolArgumentRepairEditModeTests
    {
        private static async Task<(ToolExecutionPolicy.ToolCallResult Result, ToolExecutionPolicy Policy)> CallAsync(
            DelegateLlmTool tool, Dictionary<string, object> arguments, CoreAISettingsOptions settings = null)
        {
            ToolExecutionPolicy policy = new(NullLog.Instance, settings ?? new CoreAISettingsOptions(),
                new ILlmTool[] { tool }, false, "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", tool.Name, arguments), options, CancellationToken.None);
            return (result, policy);
        }

        private static string Text(ToolExecutionPolicy.ToolCallResult result)
        {
            return result.Result.Result?.ToString() ?? "";
        }

        [TestCase("True", true)]
        [TestCase("true", true)]
        [TestCase("FALSE", false)]
        [TestCase("1", true)]
        [TestCase(1L, true)]
        [TestCase(0L, false)]
        public async Task Boolean_WrittenAsTextOrNumber_IsAccepted(object sent, bool expected)
        {
            bool? received = null;
            DelegateLlmTool tool = new("get_mod_logs", "Logs.",
                new Func<string, bool, string>((mod_id, since_reload) =>
                {
                    received = since_reload;
                    return "{\"success\":true}";
                }));

            (ToolExecutionPolicy.ToolCallResult result, ToolExecutionPolicy policy) = await CallAsync(tool,
                new Dictionary<string, object> { ["mod_id"] = "m", ["since_reload"] = sent });

            Assert.IsTrue(result.Succeeded, Text(result));
            Assert.AreEqual(expected, received);
            Assert.AreEqual("native", policy.ExecutedTraces.Single().Source);
        }

        [TestCase("5", 5)]
        [TestCase(" 7 ", 7)]
        [TestCase(5.0d, 5)]
        [TestCase("12.0", 12)]
        public async Task Integer_WrittenAsTextOrWholeReal_IsAccepted(object sent, int expected)
        {
            int received = -1;
            DelegateLlmTool tool = new("page", "Page.", new Func<int, string>(from_line =>
            {
                received = from_line;
                return "ok";
            }));

            (ToolExecutionPolicy.ToolCallResult result, _) =
                await CallAsync(tool, new Dictionary<string, object> { ["from_line"] = sent });

            Assert.IsTrue(result.Succeeded, Text(result));
            Assert.AreEqual(expected, received);
        }

        [Test]
        public async Task String_WrittenAsNumberOrBoolean_IsAccepted()
        {
            List<string> received = new();
            DelegateLlmTool tool = new("echo", "Echo.", new Func<string, string, string>((mod_id, flag) =>
            {
                received.Add(mod_id);
                received.Add(flag);
                return "ok";
            }));

            (ToolExecutionPolicy.ToolCallResult result, _) = await CallAsync(tool,
                new Dictionary<string, object> { ["mod_id"] = 123L, ["flag"] = true });

            Assert.IsTrue(result.Succeeded, Text(result));
            CollectionAssert.AreEqual(new[] { "123", "true" }, received);
        }

        [TestCase("modId")]
        [TestCase("ModID")]
        [TestCase("mod-id")]
        [TestCase("MOD_ID")]
        public async Task Key_DifferingOnlyInCaseOrSeparators_IsRenamedToTheDeclaredName(string key)
        {
            string received = null;
            DelegateLlmTool tool = new("source", "Source.", new Func<string, string>(mod_id =>
            {
                received = mod_id;
                return "ok";
            }));

            (ToolExecutionPolicy.ToolCallResult result, _) =
                await CallAsync(tool, new Dictionary<string, object> { [key] = "arena" });

            Assert.IsTrue(result.Succeeded, Text(result));
            Assert.AreEqual("arena", received);
            StringAssert.DoesNotContain("argument_note", Text(result));
        }

        [Test]
        public async Task Key_ResemblingAnAbsentDeclaredName_IsRefusedBeforeTheBody_WithThatName()
        {
            int runs = 0;
            DelegateLlmTool tool = new("manage_mods", "Mods.",
                new Func<string, string, int, int, string>((action, mod_id, from_line, line_count) =>
                {
                    runs++;
                    return "ok";
                }));

            (ToolExecutionPolicy.ToolCallResult result, ToolExecutionPolicy policy) = await CallAsync(tool,
                new Dictionary<string, object>
                {
                    ["action"] = "get_source", ["mod_id"] = "arena", ["start_line"] = 40L
                });

            string text = Text(result);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, runs, "running it would answer the first page, not line 40");
            StringAssert.Contains("no argument 'start_line'", text);
            StringAssert.Contains("'from_line'", text);
            Assert.AreEqual("schema-validation", policy.ExecutedTraces.Single().Source);
        }

        [Test]
        public async Task Key_ResemblingNothing_IsIgnored_AndTheResultSaysSo()
        {
            int runs = 0;
            DelegateLlmTool tool = new("source", "Source.", new Func<string, string>(mod_id =>
            {
                runs++;
                return "{\"success\":true,\"message\":\"done\"}";
            }));

            (ToolExecutionPolicy.ToolCallResult result, _) = await CallAsync(tool,
                new Dictionary<string, object> { ["mod_id"] = "arena", ["banana"] = "x" });

            Assert.IsTrue(result.Succeeded, Text(result));
            Assert.AreEqual(1, runs);
            JObject parsed = JObject.Parse(Text(result));
            StringAssert.Contains("'banana'", parsed.Value<string>("argument_note"));
            Assert.AreEqual("done", parsed.Value<string>("message"));
        }

        [Test]
        public async Task ToolWithoutArguments_IgnoresWhatItIsSent_WithoutANote()
        {
            DelegateLlmTool tool = new("list", "List.", (Func<string>)(() => "plain"));

            (ToolExecutionPolicy.ToolCallResult result, _) =
                await CallAsync(tool, new Dictionary<string, object> { ["key"] = "value" });

            Assert.AreEqual("plain", Text(result));
        }

        [Test]
        public async Task UncoercibleValue_IsRefusedInOneSentence_WithoutDotNetText()
        {
            int runs = 0;
            DelegateLlmTool tool = new("get_mod_logs", "Logs.", new Func<bool, string>(since_reload =>
            {
                runs++;
                return "ok";
            }));

            (ToolExecutionPolicy.ToolCallResult result, ToolExecutionPolicy policy) = await CallAsync(tool,
                new Dictionary<string, object> { ["since_reload"] = "maybe" });

            string text = Text(result);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, runs);
            StringAssert.Contains("'since_reload'", text);
            StringAssert.Contains("must be true or false, not \"maybe\"", text);
            StringAssert.DoesNotContain("System.", text);
            StringAssert.DoesNotContain("schema", text);
            Assert.AreEqual("arg-conversion", policy.ExecutedTraces.Single().Source);
        }

        [Test]
        public async Task MissingRequiredArgument_IsNamedWithItsDescription_WithoutTheSchema()
        {
            DelegateLlmTool tool = new("save_world", "Save.", new Func<string, string>(slot => "ok"));

            (ToolExecutionPolicy.ToolCallResult result, _) = await CallAsync(tool, new Dictionary<string, object>());

            string text = Text(result);
            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("missing required argument(s): slot", text);
            StringAssert.Contains("slot (string)", text);
            StringAssert.DoesNotContain("\"properties\"", text);
        }

        [Test]
        public async Task MissingRequiredArgument_NextToAnUnknownKey_NamesBoth()
        {
            DelegateLlmTool tool = new("execute_lua", "Lua.", new Func<string, string>(code => "ok"));

            (ToolExecutionPolicy.ToolCallResult result, _) =
                await CallAsync(tool, new Dictionary<string, object> { ["source"] = "print(1)" });

            string text = Text(result);
            StringAssert.Contains("missing required argument(s): code", text);
            StringAssert.Contains("Not its argument(s): source.", text);
        }

        [Test]
        public void ReflectionConversionText_IsRewordedForTheModel()
        {
            string text = ToolExecutionPolicy.DescribeReflectionConversion(new ArgumentException(
                "Object of type 'System.String' cannot be converted to type 'System.Boolean'."));

            StringAssert.Contains("text", text);
            StringAssert.Contains("true or false", text);
            StringAssert.DoesNotContain("System.", text);
            Assert.IsNull(ToolExecutionPolicy.DescribeReflectionConversion(new InvalidOperationException("world")));
        }

        [Test]
        public async Task SkillProxy_RepairsTheTargetsArguments_LikeTheDirectPath()
        {
            bool? received = null;
            DelegateLlmTool verdict = new("submit_task_verdict", "Verdict",
                new Func<string, bool, string>((task_id, accepted) =>
                {
                    received = accepted;
                    return "{\"success\":true}";
                }));
            ILlmTool proxy = CallSkillToolLlmTool.Create(new[] { new SkillSet("briefing", "", "", verdict) });
            ToolExecutionPolicy policy = new(NullLog.Instance, new CoreAISettingsOptions(), new[] { proxy }, false,
                "test");
            MEAI.ChatOptions options = new()
                { Tools = new List<MEAI.AITool> { ((IAIFunctionLlmTool)proxy).CreateAIFunction() } };

            ToolExecutionPolicy.ToolCallResult ran = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "call_skill_tool", new Dictionary<string, object>
                {
                    ["tool_name"] = "submit_task_verdict",
                    ["arguments_json"] = "{\"taskId\":\"t1\",\"accepted\":\"True\"}"
                }), options, CancellationToken.None);
            ToolExecutionPolicy.ToolCallResult refused = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c2", "call_skill_tool", new Dictionary<string, object>
                {
                    ["tool_name"] = "submit_task_verdict",
                    ["arguments_json"] = "{\"task_id\":\"t2\",\"accept\":true}"
                }), options, CancellationToken.None);

            Assert.IsTrue(ran.Succeeded, Text(ran));
            Assert.AreEqual(true, received);
            Assert.IsFalse(refused.Succeeded);
            StringAssert.Contains("'accepted'", Text(refused));
        }

        [Test]
        public async Task CutResult_StartsWithTheNote_AndTheToolsOwnAdvice()
        {
            AdvisingTool tool = new();
            ToolExecutionPolicy policy = new(NullLog.Instance, new CoreAISettingsOptions { MaxToolResultChars = 100 },
                new ILlmTool[] { tool }, false, "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", tool.Name, new Dictionary<string, object> { ["count"] = 500L }),
                options, CancellationToken.None);

            string text = Text(result);
            StringAssert.StartsWith("[Result cut: only the first 100 of 500 characters are below", text);
            StringAssert.Contains("Pass a smaller count.", text.Split('\n')[0]);
            StringAssert.Contains("500 chars total -> 100 shown", text);
        }

        [Test]
        public async Task CutResult_WithoutAdvice_CarriesTheGenericSentence()
        {
            DelegateLlmTool tool = new("big", "Big.", (Func<string>)(() => new string('x', 300)));

            (ToolExecutionPolicy.ToolCallResult result, _) = await CallAsync(tool, new Dictionary<string, object>(),
                new CoreAISettingsOptions { MaxToolResultChars = 100 });

            StringAssert.Contains(ToolExecutionPolicy.GenericCutAdvice, Text(result).Split('\n')[0]);
        }

        [Test]
        public async Task RepeatedRead_RunsAgain_OnceTheHistoryWasTrimmed()
        {
            int reads = 0;
            DelegateLlmTool read = new("get_source", "Read.", new Func<string, string>(mod_id =>
            {
                reads++;
                return "{\"success\":true}";
            }));
            ToolExecutionPolicy policy = new(NullLog.Instance, new CoreAISettingsOptions(), new ILlmTool[] { read },
                false, "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { read.CreateAIFunction() } };

            async Task<ToolExecutionPolicy.BatchToolCallResult> Turn(string id)
            {
                return await policy.ExecuteBatchAsync(new List<MEAI.FunctionCallContent>
                {
                    new(id, "get_source", new Dictionary<string, object> { ["mod_id"] = "arena" })
                }, options, CancellationToken.None);
            }

            await Turn("a");
            ToolExecutionPolicy.BatchToolCallResult echo = await Turn("b");
            policy.OnToolHistoryTrimmed(2);
            await Turn("c");

            Assert.IsTrue(echo.AllDuplicates, "an echo while the earlier result is still in the history is a no-op");
            Assert.AreEqual(2, reads, "after the trim the earlier result may be gone, so the read runs again");
        }

        [Test]
        public async Task WrappedDirectTool_SharesTheDirectCallsDuplicateSignature()
        {
            int runs = 0;
            DelegateLlmTool direct = new("manage_mods", "Mods.", new Func<string, string, string>((action, mod_id) =>
            {
                runs++;
                return "{\"success\":true}";
            })) { IsMutating = true };
            DelegateLlmTool skillTool = new("other", "Other.", (Func<string>)(() => "ok"));
            ILlmTool proxy = CallSkillToolLlmTool.Create(new[] { new SkillSet("s", "", "", skillTool) },
                () => new ILlmTool[] { direct });
            ToolExecutionPolicy policy = new(NullLog.Instance, new CoreAISettingsOptions(),
                new[] { direct, proxy }, false, "test");
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { direct.CreateAIFunction(), ((IAIFunctionLlmTool)proxy).CreateAIFunction() }
            };

            await policy.ExecuteBatchAsync(new List<MEAI.FunctionCallContent>
            {
                new("a", "manage_mods", new Dictionary<string, object> { ["action"] = "reload", ["mod_id"] = "arena" })
            }, options, CancellationToken.None);
            ToolExecutionPolicy.BatchToolCallResult wrapped = await policy.ExecuteBatchAsync(
                new List<MEAI.FunctionCallContent>
                {
                    new("b", "call_skill_tool", new Dictionary<string, object>
                    {
                        ["tool_name"] = "manage_mods",
                        ["arguments_json"] = "{\"action\":\"reload\",\"mod_id\":\"arena\"}"
                    })
                }, options, CancellationToken.None);

            Assert.AreEqual(1, runs, "the wrapper is not a way around the duplicate guard");
            Assert.IsTrue(wrapped.AllDuplicates);
        }

        [Test]
        public void CallSkillTool_IsOfferedOnlyWhileASkillHasTools_OrTheRoleAuthorsSkills()
        {
            AgentMemoryPolicy policy = new();
            policy.AddSkillForRole("Modder", SkillSet.FromTextContent("Lua Modding", "Mods.", "x"));
            policy.AddSkillForRole("Author", SkillSet.FromTextContent("Notes", "Notes.", "y"));
            policy.AddToolForRole("Author", new DelegateLlmTool("manage_skills", "Skills.", (Func<string>)(() => "ok")));

            string[] modder = policy.GetToolsForRole("Modder").Select(t => t.Name).ToArray();
            CollectionAssert.Contains(modder, "read_skill");
            CollectionAssert.DoesNotContain(modder, "call_skill_tool", "instruction-only skills: a second door only");
            CollectionAssert.Contains(policy.GetToolsForRole("Author").Select(t => t.Name).ToArray(),
                "call_skill_tool", "a skill authored in this turn must be callable in this turn");

            policy.AddSkillForRole("Modder", new SkillSet("Quiz", "Quiz.", "Ask.",
                new DelegateLlmTool("spawn_quiz", "Quiz.", (Func<string>)(() => "ok"))));

            CollectionAssert.Contains(policy.GetToolsForRole("Modder").Select(t => t.Name).ToArray(),
                "call_skill_tool", "a skill with tools brings it back");
            StringAssert.DoesNotContain("call_skill_tool",
                SkillSet.BuildCatalog(new[] { SkillSet.FromTextContent("Lua Modding", "Mods.", "x") }));
        }

        [Test]
        public async Task CallToAnUnofferedSkillProxy_NamesTheDirectTool()
        {
            DelegateLlmTool direct = new("manage_mods", "Mods.", new Func<string, string>(action => "ok"));
            ToolExecutionPolicy policy = new(NullLog.Instance, new CoreAISettingsOptions(), new ILlmTool[] { direct },
                false, "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { direct.CreateAIFunction() } };

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "call_skill_tool", new Dictionary<string, object>
                {
                    ["tool_name"] = "manage_mods", ["arguments_json"] = "{\"action\":\"list\"}"
                }), options, CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("Call manage_mods directly with the same arguments", Text(result));
        }

        private sealed class AdvisingTool : ILlmTool, IAIFunctionLlmTool, ILlmToolResultCutAdvice
        {
            public string Name => "numbers";
            public string Description => "Numbers.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(new Func<int, string>(count => new string('7', count)),
                    new MEAI.AIFunctionFactoryOptions { Name = Name, Description = Description });
            }

            public string AdviseOnCut(string functionName, IDictionary<string, object> arguments)
            {
                return "Pass a smaller count.";
            }
        }
    }
}
#endif

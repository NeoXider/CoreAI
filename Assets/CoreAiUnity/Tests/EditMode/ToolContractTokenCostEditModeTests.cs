using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Token cost of the tool contracts that live outside the portable core (the Lua, world-package, world-command
    /// and camera tools), measured with <see cref="PromptTokenCostMeter"/> for
    /// <c>Assets/CoreAI/Docs/PROMPT_TOKEN_COSTS.md</c>. The portable rows come from
    /// <c>PromptTokenCostTableEditModeTests</c>.
    /// </summary>
    public sealed class ToolContractTokenCostEditModeTests
    {
        private const string DocumentPath = "Assets/CoreAI/Docs/PROMPT_TOKEN_COSTS.md";

        /// <summary>Interface stand-in: the contract is read, nothing is ever invoked.</summary>
        public class InertProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return null;
            }
        }

        private static T Inert<T>() where T : class
        {
            return DispatchProxy.Create<T, InertProxy<T>>();
        }

        private static IReadOnlyList<ILlmTool> Tools()
        {
            CoreAISettingsOptions settings = new();
            IActorIdentityProvider identity = new LocalActorIdentityProvider("token-cost");
            const string role = BuiltInAgentRoleIds.Programmer;
            return new ILlmTool[]
            {
                new CoreAI.Vision.CameraLlmTool(Inert<CoreAI.Vision.IAgentCameraService>(), role),
                new LuaLlmTool(Inert<LuaTool.ILuaExecutor>(), settings, NullLog.Instance),
                new LuaModsLlmTool(Inert<ILuaModRuntime>(), settings, NullLog.Instance, LuaCapabilities.All, true,
                    identity, role, Inert<IConfirmedWorldMutationGate>(), null),
                new GetModLogsLlmTool(Inert<ILuaLogService>()),
                new SaveWorldLlmTool(Inert<IRbxWorldRuntimeService>(), identity, role),
                new LoadWorldLlmTool(Inert<IRbxWorldRuntimeService>(), identity, role),
                new ListAutoSavesLlmTool(Inert<IRbxWorldRuntimeService>(), identity, role),
                new LoadAutoSaveLlmTool(Inert<IRbxWorldRuntimeService>(), identity, role),
                new WorldLlmTool(Inert<ICoreAiWorldCommandExecutor>(), settings, Inert<IGameLogger>())
            };
        }

        private static string Rows()
        {
            PromptTokenCostMeter meter = new();
            StringBuilder rows = new();
            foreach (ILlmTool tool in Tools())
            {
                rows.Append(meter.FormatTextRow("`" + tool.Name + "`", PromptTokenCostMeter.ToProviderToolsJson(tool)))
                    .Append('\n');
            }

            return rows.ToString();
        }

        [Test]
        public void Document_HasAMeasuredRowForEveryShippedHostTool()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), DocumentPath);
            Assume.That(File.Exists(path), "the test runs from the project root: " + path);
            string document = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");

            foreach (ILlmTool tool in Tools())
            {
                Assert.IsTrue(Regex.IsMatch(document, @"^\| `" + Regex.Escape(tool.Name) + @"` \| \d+ \| \d+ \| [1-9]\d* \|",
                    RegexOptions.Multiline), "the cost table has no measured row for " + tool.Name);
            }
        }

        /// <summary>
        /// P16 DoD: every function the Programmer is offered in a play world carries a description within the tool
        /// contract limit, so the cacheable prefix never clips one and a small model reads each in full.
        /// world_command (Creator, Builder) is not in this surface.
        /// </summary>
        [Test]
        public void EveryProgrammerToolDescription_FitsTheToolContractLimit()
        {
            int limit = AiToolContractPromptFormatter.ToolDescriptionMaxChars;
            List<SkillSet> catalog = new()
            {
                SkillSet.FromTextContent("Lua Modding", "Mods.", "x"), SkillSet.FromTextContent("Rbx API", "Rbx.", "y")
            };
            List<Microsoft.Extensions.AI.AIFunction> functions = new()
            {
                new MemoryTool(new InMemoryAgentMemoryStore(), BuiltInAgentRoleIds.Programmer).CreateAIFunction(),
                ((IAIFunctionLlmTool)ReadSkillLlmTool.Create(catalog)).CreateAIFunction()
            };
            foreach (ILlmTool tool in Tools())
            {
                if (tool.Name == "world_command")
                {
                    continue;
                }

                Assert.LessOrEqual(tool.Description.Length, limit, tool.Name);
                if (tool is IAIFunctionsLlmTool wrapper)
                {
                    functions.AddRange(wrapper.CreateAIFunctions());
                }
                else if (tool is IAIFunctionLlmTool single)
                {
                    functions.Add(single.CreateAIFunction());
                }
            }

            foreach (Microsoft.Extensions.AI.AIFunction function in functions)
            {
                Assert.LessOrEqual(function.Description.Length, limit, function.Name);
            }
        }

        /// <summary>Prints the rows for the host-tool table of the document.</summary>
        [Test]
        [Explicit("Prints the regenerated host tool-contract rows for the document.")]
        public void PrintCurrentRows()
        {
            string rows = Rows();
            TestContext.Write(rows);
            string document = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), DocumentPath),
                Encoding.UTF8).Replace("\r\n", "\n");
            Assert.IsTrue(document.Contains(rows.Trim()),
                "the document is out of date; replace its host tool-contract rows with the output above");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// The token-cost table of <c>Assets/CoreAI/Docs/PROMPT_TOKEN_COSTS.md</c>: every shipped skill and core tool
    /// contract measured with CoreAI's own counter (<see cref="PromptTokenCostMeter"/>). The explicit test prints
    /// the current table for pasting into the document; the ordinary tests keep the generator deterministic and
    /// the document complete.
    /// </summary>
    public sealed class PromptTokenCostTableEditModeTests
    {
        internal const string DocumentPath = "Assets/CoreAI/Docs/PROMPT_TOKEN_COSTS.md";

        private static readonly (string Name, string Description, string ResourcesFile, string Literal)[] Skills =
        {
            (BuiltInLuaModdingSkillText.SkillName, BuiltInLuaModdingSkillText.SkillDescription,
                "Assets/CoreAiUnity/Resources/AgentSkills/LuaModding.txt", BuiltInLuaModdingSkillText.Instructions),
            (BuiltInRbxApiSkillText.SkillName, BuiltInRbxApiSkillText.SkillDescription,
                "Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt", BuiltInRbxApiSkillText.Instructions),
            (BuiltInFullLuaSkillText.SkillName, BuiltInFullLuaSkillText.SkillDescription,
                "Assets/CoreAiUnity/Resources/AgentSkills/FullLua.txt", BuiltInFullLuaSkillText.Instructions)
        };

        private static string Lf(string text)
        {
            return (text ?? "").Replace("\r\n", "\n");
        }

        private static string ReadRepositoryFile(string relativePath)
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            Assume.That(File.Exists(path), "the test runs from the repository root: " + path);
            return Lf(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>
        /// The core tool contracts a built-in role can be offered without the Unity or mods layers, as the provider
        /// receives them (memory as the function the client binds over a memory store; the skill meta-tools over
        /// the Programmer's three shipped skills).
        /// </summary>
        internal static IReadOnlyList<(string Name, string Json)> CoreToolContracts()
        {
            List<SkillSet> catalog = new();
            foreach ((string name, string description, _, string literal) in Skills)
            {
                catalog.Add(SkillSet.FromTextContent(name, description, literal));
            }

            List<(string Name, string Json)> contracts = new()
            {
                ("memory", PromptTokenCostMeter.ToProviderToolsJson(
                    new MemoryTool(new InMemoryAgentMemoryStore(), BuiltInAgentRoleIds.SmartChat).CreateAIFunction()))
            };
            foreach (ILlmTool tool in new ILlmTool[]
                     {
                         ReadSkillLlmTool.Create(catalog), CallSkillToolLlmTool.Create(catalog), new WaitLlmTool(),
                         new InventoryLlmTool(new EmptyInventory())
                     })
            {
                contracts.Add((tool.Name, PromptTokenCostMeter.ToProviderToolsJson(tool)));
            }

            return contracts;
        }

        /// <summary>The Markdown tables of the document, regenerated from the current sources.</summary>
        internal static string GenerateTables(PromptTokenCostMeter meter)
        {
            StringBuilder text = new();
            text.Append("Counter: ").Append(meter.IsExact ? "BPE (" + meter.Model + ")" : "BpeTokenCounter fallback estimator")
                .Append('\n').Append('\n');

            text.Append("| Skill text | Characters | UTF-8 bytes | Tokens |\n|---|---|---|---|\n");
            foreach ((string name, string description, string resourcesFile, string literal) in Skills)
            {
                string resources = ReadRepositoryFile(resourcesFile);
                SkillSet skill = SkillSet.FromTextContent(name, description, resources);
                text.Append(meter.FormatTextRow(name + " instructions (Resources)", resources)).Append('\n');
                text.Append(meter.FormatTextRow(name + " instructions (C# literal)", Lf(literal))).Append('\n');
                text.Append(meter.FormatTextRow(name + " read_skill result",
                    Lf(ReadSkillLlmTool.ReadSkillJson(new[] { skill }, name)))).Append('\n');
                text.Append(meter.FormatTextRow(name + " catalog entry",
                    Lf("- **" + name + "** - " + description))).Append('\n');
            }

            text.Append('\n').Append("| Tool contract | Characters | UTF-8 bytes | Tokens |\n|---|---|---|---|\n");
            foreach ((string name, string json) in CoreToolContracts())
            {
                text.Append(meter.FormatTextRow("`" + name + "`", json)).Append('\n');
            }

            text.Append('\n').Append("| Prompt | Characters | UTF-8 bytes | Tokens |\n|---|---|---|---|\n");
            text.Append(meter.FormatTextRow("Universal prefix (package default)",
                Lf(CoreAISettings.DefaultUniversalSystemPromptPrefix))).Append('\n');
            BuiltInDefaultAgentSystemPromptProvider prompts = new();
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                prompts.TryGetSystemPrompt(roleId, out string prompt);
                text.Append(meter.FormatTextRow(roleId + " role prompt", Lf(prompt))).Append('\n');
            }

            text.Append(meter.FormatTextRow("Skill catalog header",
                Lf(SkillSet.BuildCatalog(new[] { SkillSet.FromTextContent("x", "x", "x") }))
                    .Replace("\n- **x** - x", ""))).Append('\n');
            return text.ToString();
        }

        [Test]
        public void Generator_IsDeterministic_AndCountsEveryRow()
        {
            PromptTokenCostMeter meter = new();

            string first = GenerateTables(meter);
            string second = GenerateTables(new PromptTokenCostMeter());

            Assert.AreEqual(first, second);
            foreach (Match row in Regex.Matches(first, @"^\| (?<label>[^|]+) \| (?<chars>\d+) \| (?<bytes>\d+) \| (?<tokens>\d+) \|$",
                         RegexOptions.Multiline))
            {
                Assert.Greater(int.Parse(row.Groups["tokens"].Value), 0, row.Groups["label"].Value);
            }
        }

        [Test]
        public void ToolJson_IsWhatAProviderReceives()
        {
            string json = PromptTokenCostMeter.ToProviderToolsJson(new InventoryLlmTool(new EmptyInventory()));

            StringAssert.StartsWith("[{\"type\":\"function\",\"function\":{\"name\":\"get_inventory\"", json);
            Assert.Greater(new PromptTokenCostMeter().CountTool(new WaitLlmTool()), 0);
        }

        [Test]
        public void Document_HasAMeasuredRowForEveryShippedSkillAndCoreTool()
        {
            string document = ReadRepositoryFile(DocumentPath);
            List<string> labels = new();
            foreach ((string name, _, _, _) in Skills)
            {
                labels.Add(name + " instructions (Resources)");
                labels.Add(name + " read_skill result");
            }

            foreach ((string name, _) in CoreToolContracts())
            {
                labels.Add("`" + name + "`");
            }

            foreach (string label in labels)
            {
                Assert.IsTrue(Regex.IsMatch(document, @"^\| " + Regex.Escape(label) + @" \| \d+ \| \d+ \| [1-9]\d* \|",
                    RegexOptions.Multiline), "the cost table has no measured row for " + label);
            }
        }

        /// <summary>Prints the current tables; paste them into the document after a skill or tool changes.</summary>
        [Test]
        [Explicit("Prints the regenerated cost tables for the document.")]
        public void PrintCurrentTables()
        {
            string tables = GenerateTables(new PromptTokenCostMeter());
            TestContext.Write(tables);
            string document = ReadRepositoryFile(DocumentPath);
            Assert.IsTrue(document.Contains(tables.Trim()),
                "the document is out of date; replace its generated tables with the output above");
        }

        private sealed class EmptyInventory : InventoryTool.IInventoryProvider
        {
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new List<InventoryTool.InventoryItem>());
            }
        }
    }
}

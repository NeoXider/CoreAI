using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
#endif
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// The "Rbx API" skill is read in parts: a bare <c>read_skill("Rbx API")</c> returns the core rules and the
    /// section list, and <c>read_skill("Rbx API", section)</c> returns one section. Every read must fit the
    /// default tool-result cap (<see cref="CoreAISettingsOptions.MaxToolResultChars"/>, 8,000 characters):
    /// a longer result is cut before the model sees it, which is what happened to the whole 71,000-character
    /// skill before it was tiered.
    /// </summary>
    public sealed class RbxApiSkillTieredReadEditModeTests
    {
        private const string BeforeTextVariable = "COREAI_RBX_SKILL_BEFORE";

        private static int DefaultCap => new CoreAISettingsOptions().MaxToolResultChars;

        /// <summary>The skill exactly as <c>CoreAiModsInstaller</c> builds it from the shipped text.</summary>
        internal static SkillSet ShippedSkill()
        {
            return SkillSet.FromTextContent(BuiltInRbxApiSkillText.SkillName, BuiltInRbxApiSkillText.SkillDescription,
                BuiltInRbxApiSkillText.Instructions);
        }

        /// <summary>The same facts as one document: the shape the skill had before it was tiered.</summary>
        private static SkillSet WholeSkillAsOneDocument()
        {
            return new SkillSet(BuiltInRbxApiSkillText.SkillName, BuiltInRbxApiSkillText.SkillDescription,
                ShippedSkill().Instructions);
        }

        private static string Read(SkillSet skill, string section = null)
        {
            return ReadSkillLlmTool.ReadSkillJson(new[] { skill }, skill.Name, section);
        }

        private static IEnumerable<string> SectionNames()
        {
            return ShippedSkill().Sections.Skip(1).Select(section => section.Name).ToArray();
        }

        /// <summary>The three measured turns: the reads each one makes (null is the bare core read).</summary>
        internal static readonly (string Turn, string[] Reads)[] MeasuredTurns =
        {
            ("Move a part", new string[] { null }),
            ("Build a watchtower at the crosshair", new[] { null, "terrain", "building" }),
            ("Make a HUD with a hotbar", new[] { null, "uxml" })
        };

        [Test]
        public void ShippedSkill_IsReadInSections_CoreFirst()
        {
            SkillSet skill = ShippedSkill();

            Assert.AreEqual("core", skill.Sections[0].Name);
            Assert.GreaterOrEqual(skill.Sections.Count, 10);
            foreach (string needed in new[] { "terrain", "building", "uxml", "gui", "input", "camera", "characters" })
            {
                Assert.IsTrue(skill.TryGetSection(needed, out _), "the skill has a '" + needed + "' section");
            }
        }

        [Test]
        public void BareRead_ReturnsTheCoreAndTheSectionList_WithinTheDefaultCap()
        {
            string json = Read(ShippedSkill());
            JObject result = JObject.Parse(json);

            Assert.Less(json.Length, DefaultCap,
                "the core read must reach the model whole; move text out of 00-core.md into a section");
            Assert.AreEqual("core", result.Value<string>("section"));
            string core = result.Value<string>("instructions");
            StringAssert.Contains("1 stud = 0.28 m", core);
            StringAssert.Contains("read_skill('Rbx API', section='<name>')", core);
            StringAssert.DoesNotContain("WriteVoxels(region", core, "section bodies stay out of the core read");
            CollectionAssert.AreEqual(SectionNames().ToArray(), result["sections"].ToObject<string[]>());
        }

        [TestCaseSource(nameof(SectionNames))]
        public void SectionRead_FitsTheDefaultCap_AndHoldsOnlyThatSection(string section)
        {
            string json = Read(ShippedSkill(), section);
            JObject result = JObject.Parse(json);

            Assert.IsTrue(result.Value<bool>("success"), json);
            Assert.Less(json.Length, DefaultCap, "section '" + section + "' must reach the model whole");
            Assert.AreEqual(section, result.Value<string>("section"));
            string body = result.Value<string>("instructions");
            StringAssert.DoesNotContain("<!-- section:", body);
            StringAssert.DoesNotContain("## Rules\n- Coordinates are in STUDS", body, "the core is not repeated");
        }

        [Test]
        public void EverySection_IsListedInTheCore_WithItsSummary()
        {
            SkillSet skill = ShippedSkill();
            string core = skill.Sections[0].Content;

            foreach (SkillSection section in skill.Sections.Skip(1))
            {
                string summary = section.Content.Split('\n')[0].Substring(2);
                StringAssert.Contains("- " + section.Name + ": " + summary, core,
                    "a weak model picks a section by its line in the core");
            }
        }

        [Test]
        public void TheWholeSkillAsOneDocument_DoesNotFitTheCap()
        {
            int whole = Read(WholeSkillAsOneDocument()).Length;

            Assert.Greater(whole, DefaultCap * 4,
                "negative twin: as one document the skill is cut to its first 8,000 characters, which is why it is tiered");
        }

        [Test]
        public void UnknownSection_FailsAndListsTheRealOnes()
        {
            JObject result = JObject.Parse(Read(ShippedSkill(), "voxels"));

            Assert.IsFalse(result.Value<bool>("success"));
            CollectionAssert.Contains(result["sections"].ToObject<string[]>(), "terrain");
        }

        [Test]
        public void MeasuredTurns_CostLessThanTheWholeSkill()
        {
            PromptTokenCostMeter meter = new();
            string whole = Read(WholeSkillAsOneDocument());

            foreach ((string turn, string[] reads) in MeasuredTurns)
            {
                int characters = reads.Sum(section => Read(ShippedSkill(), section).Length);
                int tokens = reads.Sum(section => meter.CountText(Read(ShippedSkill(), section)));

                Assert.Less(characters * 4, whole.Length, turn + ": a turn pays for its sections, not for all of them");
                Assert.Less(tokens * 4, meter.CountText(whole), turn);
            }
        }

        /// <summary>
        /// Prints the before/after table of the three measured turns with CoreAI's own counter. Set
        /// COREAI_RBX_SKILL_BEFORE to a file with the pre-tiering text to measure that text as the "before".
        /// </summary>
        [Test]
        [Explicit("Prints the measured turns for the token-cost document.")]
        public void PrintMeasuredTurns()
        {
            PromptTokenCostMeter meter = new();
            string beforeText = BuiltInRbxApiSkillText.Instructions;
            string beforeLabel = "the same text as one document";
            string beforePath = Environment.GetEnvironmentVariable(BeforeTextVariable);
            if (!string.IsNullOrWhiteSpace(beforePath) && File.Exists(beforePath))
            {
                beforeText = Encoding.UTF8.GetString(File.ReadAllBytes(beforePath));
                beforeLabel = Path.GetFileName(beforePath);
            }

            SkillSet before = new(BuiltInRbxApiSkillText.SkillName, BuiltInRbxApiSkillText.SkillDescription,
                beforeText == BuiltInRbxApiSkillText.Instructions ? ShippedSkill().Instructions : beforeText);
            string beforeJson = Read(before);
            string beforeDelivered = beforeJson.Length > DefaultCap
                ? beforeJson.Substring(0, DefaultCap) + "\n...[truncated: " + beforeJson.Length + " chars total -> " +
                  DefaultCap + " shown]"
                : beforeJson;

            StringBuilder table = new();
            table.Append("Counter: ").Append(meter.IsExact ? "BPE (" + meter.Model + ")" : "BpeTokenCounter fallback estimator")
                .Append("; before = ").Append(beforeLabel).Append('\n').Append('\n');
            table.Append("| Turn | Reads | Before: characters | Before: tokens | Before as delivered (cap ")
                .Append(DefaultCap).Append("): tokens | After: characters | After: tokens |\n|---|---|---|---|---|---|---|\n");
            foreach ((string turn, string[] reads) in MeasuredTurns)
            {
                int characters = reads.Sum(section => Read(ShippedSkill(), section).Length);
                int tokens = reads.Sum(section => meter.CountText(Read(ShippedSkill(), section)));
                string readList = string.Join(" + ", reads.Select(section => section ?? "core"));
                table.Append("| ").Append(turn).Append(" | ").Append(readList).Append(" | ").Append(beforeJson.Length)
                    .Append(" | ").Append(meter.CountText(beforeJson)).Append(" | ").Append(meter.CountText(beforeDelivered))
                    .Append(" | ").Append(characters).Append(" | ").Append(tokens).Append(" |\n");
            }

            table.Append('\n').Append("| Read | Characters | Tokens |\n|---|---|---|\n");
            foreach (SkillSection section in ShippedSkill().Sections)
            {
                string json = Read(ShippedSkill(), section.Name == "core" ? null : section.Name);
                table.Append("| ").Append(section.Name == "core" ? "core (bare read)" : section.Name).Append(" | ")
                    .Append(json.Length).Append(" | ").Append(meter.CountText(json)).Append(" |\n");
            }

            TestContext.Write(table.ToString());
        }

#if COREAI_LLM
        [Test]
        public async Task ScriptedTurn_ReadsTheCoreThenOneSection_EachArrivingWhole()
        {
            ILlmTool readSkill = ReadSkillLlmTool.Create(new[] { ShippedSkill() });
            ScriptedModel model = new(iteration => iteration switch
            {
                1 => Call("call-core", new Dictionary<string, object> { ["skill_name"] = "Rbx API" }),
                2 => Call("call-terrain",
                    new Dictionary<string, object> { ["skill_name"] = "Rbx API", ["section"] = "terrain" }),
                _ => new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "Built."))
            });

            await RunTurnAsync(model, readSkill, "Dig a stone cave under the crosshair.");

            Assert.AreEqual(3, model.Requests.Count, "core read, section read, answer");
            string core = ResultOf(model.Requests[1], "call-core");
            StringAssert.Contains("\"section\":\"core\"", core);
            StringAssert.Contains("- terrain: ", core, "the core lists the section the model reads next");
            StringAssert.DoesNotContain("...[truncated:", core);
            StringAssert.DoesNotContain("WriteVoxels(region", core);
            string terrain = ResultOf(model.Requests[2], "call-terrain");
            StringAssert.Contains("\"section\":\"terrain\"", terrain);
            StringAssert.Contains("WriteVoxels(region", terrain);
            StringAssert.DoesNotContain("...[truncated:", terrain);
            StringAssert.DoesNotContain("UxmlService:CreateScreen", terrain, "no other section rides along");
        }

        [Test]
        public async Task ScriptedTurn_TheSameTextAsOneDocument_ArrivesCutShort()
        {
            ILlmTool readSkill = ReadSkillLlmTool.Create(new[] { WholeSkillAsOneDocument() });
            ScriptedModel model = new(iteration => iteration == 1
                ? Call("call-whole", new Dictionary<string, object> { ["skill_name"] = "Rbx API" })
                : new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "Built.")));

            await RunTurnAsync(model, readSkill, "Dig a stone cave under the crosshair.");

            string whole = ResultOf(model.Requests[1], "call-whole");
            StringAssert.Contains("...[truncated:", whole,
                "negative twin: one 70,000-character document is cut at the default cap");
            StringAssert.DoesNotContain("WriteVoxels(region", whole, "the terrain reference never reaches the model");
        }

        private static async Task RunTurnAsync(ScriptedModel model, ILlmTool readSkill, string userText)
        {
            SmartToolCallingChatClient client = new(model, NullLog.Instance, new CoreAISettingsOptions(), true,
                new List<ILlmTool> { readSkill }, BuiltInAgentRoleIds.Programmer, 3);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { ((IAIFunctionLlmTool)readSkill).CreateAIFunction() }
            };
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, userText) }, options,
                CancellationToken.None);
        }

        private static MEAI.ChatResponse Call(string callId, IDictionary<string, object> arguments)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, "read_skill", arguments) }));
        }

        private static string ResultOf(IEnumerable<MEAI.ChatMessage> request, string callId)
        {
            List<MEAI.FunctionResultContent> results = request.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionResultContent>().Where(result => result.CallId == callId).ToList();
            Assert.AreEqual(1, results.Count, "call " + callId + " is answered once");
            return results[0].Result?.ToString() ?? "";
        }

        /// <summary>A recorded model: answers request N with the scripted response for N, keeping each request.</summary>
        private sealed class ScriptedModel : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _script;

            public ScriptedModel(Func<int, MEAI.ChatResponse> script)
            {
                _script = script;
            }

            public List<List<MEAI.ChatMessage>> Requests { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Requests.Add(messages.ToList());
                return Task.FromResult(_script(Requests.Count));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
#endif
    }
}

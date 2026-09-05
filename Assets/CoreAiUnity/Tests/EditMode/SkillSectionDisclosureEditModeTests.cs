using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Progressive disclosure for a skill written across several documents: <c>read_skill</c> hands
    /// back the entry document plus an index, and one section only when asked.
    /// <para>
    /// WHY these are pinned: the whole point is that a reader stops paying for four documents to use
    /// one. A regression here is silent — the answer still looks correct, it is just the whole blob
    /// again — so the size relationship is asserted, not just the shape.
    /// </para>
    /// </summary>
    public sealed class SkillSectionDisclosureEditModeTests
    {
        private static DelegateLlmTool MakeTool(string name)
        {
            return new DelegateLlmTool(name, "Test tool: " + name, new Action(() => { }));
        }

        private static SkillSet MultiDocSkill()
        {
            return SkillSet.FromTextParts("Quiz", "Quizzes and tests",
                new[]
                {
                    new KeyValuePair<string, string>("overview.md", "ENTRY_BODY: start here."),
                    new KeyValuePair<string, string>("scoring.md", "SCORING_BODY: how points work."),
                    new KeyValuePair<string, string>("edge-cases.md", "EDGE_BODY: the awkward ones."),
                    new KeyValuePair<string, string>("blank.md", "   ")
                },
                MakeTool("ask_question"));
        }

        private static async Task<JObject> ReadSkillAsync(SkillSet skill, string section = null)
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new[] { skill });
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();
            Dictionary<string, object> args = new() { ["skill_name"] = skill.Name };
            if (section != null)
            {
                args["section"] = section;
            }

            object result = await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
            return JObject.Parse(result?.ToString() ?? "{}");
        }

        [Test]
        public void MultiDocSkill_KeepsItsPartsAddressable()
        {
            SkillSet skill = MultiDocSkill();

            Assert.AreEqual(3, skill.Sections.Count,
                "the whitespace-only part must not become an index entry that fetches nothing");
            Assert.AreEqual("overview.md", skill.Sections[0].Name);
            Assert.IsTrue(skill.TryGetSection("SCORING.MD", out SkillSection scoring),
                "section lookup is case-insensitive, like the skill name lookup");
            Assert.AreEqual("SCORING_BODY: how points work.", scoring.Content);
            Assert.IsFalse(skill.TryGetSection("nope.md", out _));
        }

        [Test]
        public void SingleDocSkill_HasExactlyOneSection()
        {
            SkillSet skill = new("Plain", "One document", "THE BODY", MakeTool("t"));

            Assert.AreEqual(1, skill.Sections.Count);
            Assert.AreEqual("THE BODY", skill.Sections[0].Content);
        }

        [Test]
        public async Task ReadSkill_OnAMultiDocSkill_ReturnsTheEntryDocumentAndAnIndex()
        {
            JObject response = await ReadSkillAsync(MultiDocSkill());

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.AreEqual("overview.md", response["section"].Value<string>());
            StringAssert.Contains("ENTRY_BODY", response["instructions"].Value<string>());
            Assert.That(response["instructions"].Value<string>(), Does.Not.Contain("SCORING_BODY"),
                "the other documents must NOT arrive with the entry one — that is the whole point");
            Assert.That(response["instructions"].Value<string>(), Does.Not.Contain("EDGE_BODY"));

            string[] sections = response["sections"].ToObject<string[]>();
            CollectionAssert.AreEqual(new[] { "scoring.md", "edge-cases.md" }, sections,
                "the index lists the remaining documents in order, entry excluded");
        }

        [Test]
        public async Task ReadSkill_WithASectionName_ReturnsThatDocumentAlone()
        {
            JObject response = await ReadSkillAsync(MultiDocSkill(), "edge-cases.md");

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.AreEqual("edge-cases.md", response["section"].Value<string>());
            StringAssert.Contains("EDGE_BODY", response["instructions"].Value<string>());
            Assert.That(response["instructions"].Value<string>(), Does.Not.Contain("ENTRY_BODY"));
            Assert.That(response["instructions"].Value<string>(), Does.Not.Contain("SCORING_BODY"));
        }

        [Test]
        public async Task ReadSkill_StagedAnswer_IsSmallerThanTheWholeSkill()
        {
            // WHY: the size relationship IS the feature. A regression that quietly returns the blob
            // again would pass every shape assertion above.
            SkillSet skill = MultiDocSkill();
            JObject staged = await ReadSkillAsync(skill);

            int stagedLength = staged["instructions"].Value<string>().Length;
            Assert.Less(stagedLength, skill.Instructions.Length,
                "the entry document must cost less than the assembled skill");
        }

        [Test]
        public async Task ReadSkill_WithAnUnknownSection_FailsAndListsTheRealOnes()
        {
            JObject response = await ReadSkillAsync(MultiDocSkill(), "does-not-exist.md");

            Assert.IsFalse(response["success"].Value<bool>());
            StringAssert.Contains("does-not-exist.md", response["error"].Value<string>());
            CollectionAssert.Contains(response["sections"].ToObject<string[]>(), "scoring.md",
                "a wrong section name must show the reader what it could have asked for");
        }

        [Test]
        public async Task ReadSkill_OnASingleDocSkill_IsUnchanged()
        {
            // The backward-compatibility guard: staging must not alter an existing one-document skill.
            SkillSet skill = new("Plain", "One document", "THE WHOLE BODY", MakeTool("t"));
            JObject response = await ReadSkillAsync(skill);

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.AreEqual("THE WHOLE BODY", response["instructions"].Value<string>());
            Assert.IsNull(response["sections"], "a one-document skill must advertise no index");
            Assert.IsNull(response["section"], "and must not claim to be a fragment");
        }

        [Test]
        public async Task ReadSkill_ToolsAreListedOnEveryStage()
        {
            // WHY: the tools must not be staged away. A reader that fetched one section still needs to
            // know what it can call, or the staging would cost it a round trip to find out.
            JObject entry = await ReadSkillAsync(MultiDocSkill());
            JObject section = await ReadSkillAsync(MultiDocSkill(), "scoring.md");

            Assert.AreEqual("ask_question", entry["tools"][0]["tool_name"].Value<string>());
            Assert.AreEqual("ask_question", section["tools"][0]["tool_name"].Value<string>());
        }
    }
}

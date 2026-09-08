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

        private static async Task<JObject> ReadSkillAsync(SkillSet skill, string section = null, bool all = false)
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new[] { skill });
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();
            Dictionary<string, object> args = new() { ["skill_name"] = skill.Name };
            args["all"] = all;
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

        [Test]
        public async Task ReadAll_ReturnsEveryDocumentExactlyOnceAndRejectsSectionConflict()
        {
            SkillSet skill = MultiDocSkill();
            JObject all = await ReadSkillAsync(skill, all: true);
            Assert.IsTrue(all["success"].Value<bool>());
            Assert.AreEqual(skill.Instructions, all["instructions"].Value<string>());
            CollectionAssert.AreEqual(new[] { "overview.md", "scoring.md", "edge-cases.md" },
                all["sections"].ToObject<string[]>());
            JObject conflict = await ReadSkillAsync(skill, "scoring.md", true);
            Assert.IsFalse(conflict["success"].Value<bool>());
            Assert.IsNull(conflict["instructions"]);
        }

        [Test]
        public async Task EmptyEntry_IsNotReplacedByReference()
        {
            SkillSet skill = SkillSet.FromTextParts("empty-entry", "", new[]
            {
                new KeyValuePair<string, string>("SKILL.md", ""),
                new KeyValuePair<string, string>("references/api.md", "reference")
            });
            JObject response = await ReadSkillAsync(skill);
            Assert.AreEqual("SKILL.md", response["section"].Value<string>());
            Assert.AreEqual("", response["instructions"].Value<string>());
            CollectionAssert.Contains(response["sections"].ToObject<string[]>(), "references/api.md");
        }

        [TestCase("../secret.md")]
        [TestCase("/absolute.md")]
        [TestCase("C:/secret.md")]
        [TestCase("references//api.md")]
        public void InvalidDocumentPaths_AreRejected(string path)
        {
            Assert.Throws<ArgumentException>(() => SkillSet.FromTextParts("paths", "", new[]
            {
                new KeyValuePair<string, string>(path, "body")
            }));
        }

        [Test]
        public void DuplicateNormalizedPaths_AreRejected()
        {
            Assert.Throws<ArgumentException>(() => SkillSet.FromTextParts("paths", "", new[]
            {
                new KeyValuePair<string, string>("references\\api.md", "one"),
                new KeyValuePair<string, string>("References/api.md", "two")
            }));
        }

        [Test]
        public void FileLoader_RetainsDirectoriesForEqualBasenames()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coreai-skill-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "a"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "b"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "SKILL.md"), "entry");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "a", "api.md"), "first");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "b", "api.md"), "second");
                SkillSet skill = SkillSet.FromFiles("files", "", root, new[] { "SKILL.md", "a/api.md", "b/api.md" });
                Assert.IsTrue(skill.TryGetSection("a/api.md", out SkillSection first));
                Assert.IsTrue(skill.TryGetSection("b/api.md", out SkillSection second));
                Assert.AreEqual("first", first.Content);
                Assert.AreEqual("second", second.Content);
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }

        [Test]
        public void SkillSnapshots_CannotBeMutatedThroughExposedCollections()
        {
            SkillSet skill = MultiDocSkill();
            Assert.Throws<NotSupportedException>(() => ((IList<SkillSection>)skill.Sections).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<ILlmTool>)skill.Tools).Clear());
            string[] names = skill.ToolNames;
            names[0] = "tampered";
            Assert.AreEqual("ask_question", skill.ToolNames[0]);
        }

        [Test]
        public void DefinitionJsonRoundtrip_PreservesDocumentsWithBothSupportedSerializers()
        {
            SkillSetDefinition definition = new()
            {
                Name = "portable",
                Sections = new[]
                {
                    new SkillSection("SKILL.md", "whole entry\nlast line"),
                    new SkillSection("references/api.md", "reference")
                }
            };
            SkillSetDefinition newtonsoft = Newtonsoft.Json.JsonConvert.DeserializeObject<SkillSetDefinition>(
                Newtonsoft.Json.JsonConvert.SerializeObject(definition));
            SkillSetDefinition systemText = System.Text.Json.JsonSerializer.Deserialize<SkillSetDefinition>(
                System.Text.Json.JsonSerializer.Serialize(definition));
            foreach (SkillSetDefinition restored in new[] { newtonsoft, systemText })
            {
                SkillSet skill = restored.BuildSkillSet();
                Assert.AreEqual(definition.Sections[0].Content, skill.Sections[0].Content);
                Assert.IsTrue(skill.TryGetSection("references/api.md", out SkillSection reference));
                Assert.AreEqual(definition.Sections[1].Content, reference.Content);
            }
        }
    }
}

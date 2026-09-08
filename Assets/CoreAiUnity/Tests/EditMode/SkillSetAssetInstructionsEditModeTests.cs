using System;
using CoreAI.Ai;
using CoreAI.Unity;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers a skill written across several documents: the Inspector accepts more than one
    /// instruction file, and a skill that still uses exactly one keeps its text byte-for-byte.
    /// </summary>
    public sealed class SkillSetAssetInstructionsEditModeTests
    {
        private SkillSetAsset _asset;
        private TextAsset _overview;
        private TextAsset _details;
        private TextAsset _empty;

        [SetUp]
        public void SetUp()
        {
            _asset = ScriptableObject.CreateInstance<SkillSetAsset>();
            _overview = new TextAsset("Alpha body.") { name = "overview" };
            _details = new TextAsset("Beta body.") { name = "details" };
            _empty = new TextAsset("   ") { name = "blank" };
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_asset);
            UnityEngine.Object.DestroyImmediate(_overview);
            UnityEngine.Object.DestroyImmediate(_details);
            UnityEngine.Object.DestroyImmediate(_empty);
        }

        private void Apply(TextAsset primary, params TextAsset[] additional)
        {
            _asset.SetInstructionAssets(primary, additional);
        }

        [Test]
        public void SingleInstructionFile_KeepsItsTextExactly()
        {
            // WHY: adding a heading to a one-file skill would silently rewrite the instructions of
            // every asset that already exists in a project.
            Apply(_overview);

            Assert.AreEqual("Alpha body.", _asset.Instructions);
        }

        [Test]
        public void SeveralInstructionFiles_JoinInOrderUnderTheirOwnHeadings()
        {
            Apply(_overview, _details);

            Assert.AreEqual("## overview\nAlpha body.\n\n## details\nBeta body.", _asset.Instructions);
        }

        [Test]
        public void JoinedInstructions_MatchTheEngineFreeJoiner()
        {
            // The Unity asset and SkillSet.FromTextParts must produce the same document, or the same
            // skill would read differently depending on which door it came through.
            Apply(_overview, _details);

            string expected = SkillSet.JoinInstructionParts(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("overview", "Alpha body."),
                new System.Collections.Generic.KeyValuePair<string, string>("details", "Beta body.")
            });

            Assert.AreEqual(expected, _asset.Instructions);
        }

        [Test]
        public void EmptyAndUnassignedSlots_AreSkipped()
        {
            Apply(_overview, null, _empty, _details);

            Assert.AreEqual("## overview\nAlpha body.\n\n## details\nBeta body.", _asset.Instructions);
        }

        [Test]
        public void NoInstructionFiles_FallsBackToTheInlineField()
        {
            _asset.ApplyDefinition(new SkillSetDefinition { Instructions = "Typed straight into the Inspector." });
            Apply(null);

            Assert.AreEqual("Typed straight into the Inspector.", _asset.Instructions);
        }

        [Test]
        public void ApplyDefinition_ClearsEveryFileReference()
        {
            Apply(_overview, _details);
            _asset.ApplyDefinition(new SkillSetDefinition
            {
                Name = "Rewritten",
                Description = "d",
                Instructions = "Replacement body."
            });

            Assert.AreEqual("Replacement body.", _asset.Instructions,
                "a definition carries the whole body inline, so stale files must not be appended to it");
        }

        [Test]
        public void BuiltSkillSet_CarriesTheJoinedInstructions()
        {
            _asset.ApplyDefinition(new SkillSetDefinition { Name = "MultiDoc", Description = "Written across two files" });
            Apply(_overview, _details);

            SkillSet skill = _asset.BuildSkillSet();

            Assert.AreEqual("MultiDoc", skill.Name);
            Assert.That(skill.Instructions, Does.Contain("## overview"));
            Assert.That(skill.Instructions, Does.Contain("## details"));
        }

        [Test]
        public void DefinitionRoundtrip_PreservesCompleteEntryAndRelativeReferences()
        {
            _asset.SetInstructionAssets(_overview, new[] { _details }, "SKILL.md", new[] { "references/api.md" });
            SkillSetDefinition definition = _asset.ToSkillDefinition();
            _asset.ApplyDefinition(definition);
            SkillSet skill = _asset.BuildSkillSet();
            Assert.AreEqual(_overview.text, skill.Sections[0].Content);
            Assert.AreEqual("SKILL.md", skill.Sections[0].Name);
            Assert.IsTrue(skill.TryGetSection("references/api.md", out SkillSection reference));
            Assert.AreEqual(_details.text, reference.Content);
            Assert.AreEqual(skill.Instructions, definition.BuildSkillSet().Instructions);
        }

        [Test]
        public void MissingPrimary_DoesNotPromoteReferenceToEntry()
        {
            _asset.SetInstructionAssets(null, new[] { _details }, "SKILL.md", new[] { "references/api.md" });
            SkillSet skill = _asset.BuildSkillSet();
            Assert.AreEqual("SKILL.md", skill.Sections[0].Name);
            Assert.AreEqual("", skill.Sections[0].Content);
            Assert.AreEqual(_details.text, skill.Sections[1].Content);
        }

        [Test]
        public void SingleFileBuiltSkill_PreservesExactText()
        {
            Apply(_overview);
            Assert.AreEqual(_overview.text, _asset.BuildSkillSet().Instructions);
        }
    }
}

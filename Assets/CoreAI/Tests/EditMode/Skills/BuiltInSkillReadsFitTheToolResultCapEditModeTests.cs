using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CoreAI.Ai;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Every read of a built-in skill reaches the model whole. A <c>read_skill</c> result goes through the general
    /// tool-result cut (<see cref="CoreAISettingsOptions.MaxToolResultChars"/>, 8,000 characters by default), and a
    /// longer result is cut before the model sees it: that is how "Lua Modding" lost its later sections, the common
    /// errors among them, until it was split into sections. Checked for the bare read and every section of every
    /// built-in skill, from the C# constant and from the shipped Resources text, so the next oversized skill or
    /// section fails here instead of in a play test.
    /// </summary>
    public sealed class BuiltInSkillReadsFitTheToolResultCapEditModeTests
    {
        private const string ResourcesFolder = "Assets/CoreAiUnity/Resources/AgentSkills";

        private static int DefaultCap => new CoreAISettingsOptions().MaxToolResultChars;

        /// <summary>
        /// Every <c>BuiltIn*SkillText</c> class of the core assembly, found by its shape (the three constants a
        /// built-in skill has), so a new built-in skill is checked without editing this test.
        /// </summary>
        internal static IReadOnlyList<(string Name, string Description, string Instructions)> BuiltInSkills()
        {
            List<(string, string, string)> skills = new();
            foreach (Type type in typeof(SkillSet).Assembly.GetTypes())
            {
                if (!type.IsClass || !type.IsAbstract || !type.IsSealed || !type.Name.StartsWith("BuiltIn") ||
                    !type.Name.EndsWith("SkillText"))
                {
                    continue;
                }

                string name = Constant(type, "SkillName");
                string description = Constant(type, "SkillDescription");
                string instructions = Constant(type, "Instructions");
                if (name != null && description != null && instructions != null)
                {
                    skills.Add((name, description, instructions));
                }
            }

            skills.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));
            return skills;
        }

        private static string Constant(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral && field.FieldType == typeof(string)
                ? (string)field.GetRawConstantValue()
                : null;
        }

        /// <summary>The reads a model can make of one skill, each answered by read_skill, that are over the cap.</summary>
        private static List<string> OversizedReads(string origin, string name, string description, string text)
        {
            SkillSet skill = SkillSet.FromTextContent(name, description, text);
            List<string> sections = new() { null };
            for (int index = 1; index < skill.Sections.Count; index++)
            {
                sections.Add(skill.Sections[index].Name);
            }

            List<string> oversized = new();
            foreach (string section in sections)
            {
                string json = ReadSkillLlmTool.ReadSkillJson(new[] { skill }, name, section);
                Assert.IsTrue(JObject.Parse(json).Value<bool>("success"), origin + " " + name + ": " + json);
                if (json.Length > DefaultCap)
                {
                    oversized.Add(origin + " '" + name + "' " + (section == null ? "bare read" : "section " + section) +
                                  ": " + json.Length + " chars > " + DefaultCap);
                }
            }

            return oversized;
        }

        [Test]
        public void TheBuiltInSkills_AreFound()
        {
            List<string> names = new();
            foreach ((string name, _, _) in BuiltInSkills())
            {
                names.Add(name);
            }

            CollectionAssert.IsSubsetOf(new[] { "Full Lua", "Lua Modding", "Rbx API" }, names);
        }

        [Test]
        public void EveryReadOfEveryBuiltInSkill_FitsTheDefaultCap()
        {
            List<string> oversized = new();
            foreach ((string name, string description, string instructions) in BuiltInSkills())
            {
                oversized.AddRange(OversizedReads("C# constant", name, description, instructions));
            }

            CollectionAssert.IsEmpty(oversized,
                "split the skill into sections (skill-sources/<Skill>, tools/generate_skills.py) or move text " +
                "out of the oversized section:\n" + string.Join("\n", oversized));
        }

        [Test]
        public void EveryReadOfEveryShippedResourcesSkillText_FitsTheDefaultCap()
        {
            string folder = Path.Combine(Directory.GetCurrentDirectory(), ResourcesFolder);
            Assume.That(Directory.Exists(folder), "the test runs from the project root: " + folder);
            IReadOnlyList<(string Name, string Description, string Instructions)> builtIn = BuiltInSkills();
            List<string> oversized = new();
            string[] files = Directory.GetFiles(folder, "*.txt");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.IsNotEmpty(files);
            foreach (string file in files)
            {
                string text = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");
                string name = Path.GetFileNameWithoutExtension(file);
                string description = name;
                foreach ((string skillName, string skillDescription, string instructions) in builtIn)
                {
                    if (string.Equals(instructions.Replace("\r\n", "\n"), text, StringComparison.Ordinal))
                    {
                        name = skillName;
                        description = skillDescription;
                    }
                }

                oversized.AddRange(OversizedReads("Resources " + Path.GetFileName(file), name, description, text));
            }

            CollectionAssert.IsEmpty(oversized, string.Join("\n", oversized));
        }

        [Test]
        public void LuaModding_IsReadInSections_AndItsCommonErrorsAreReachable()
        {
            SkillSet skill = SkillSet.FromTextContent(BuiltInLuaModdingSkillText.SkillName,
                BuiltInLuaModdingSkillText.SkillDescription, BuiltInLuaModdingSkillText.Instructions);

            Assert.AreEqual("core", skill.Sections[0].Name);
            JObject bare = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(new[] { skill }, skill.Name));
            CollectionAssert.Contains(bare["sections"].ToObject<string[]>(), "errors");
            StringAssert.Contains("read_skill('Lua Modding', section='<name>')", bare.Value<string>("instructions"));
            StringAssert.Contains("manage_mods", bare.Value<string>("instructions"), "the core keeps how to change a mod");
            JObject errors = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(new[] { skill }, skill.Name, "errors"));
            StringAssert.Contains("double-escaped newlines", errors.Value<string>("instructions"));
        }
    }
}

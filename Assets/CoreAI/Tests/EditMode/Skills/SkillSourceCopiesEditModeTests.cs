using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// One source of truth for the built-in skill texts: each skill is authored once as section files in
    /// <c>skill-sources/&lt;Skill&gt;/</c>, and its two shipped copies (the Unity Resources text and the built-in
    /// C# constant) are generated from it by <c>tools/generate_skills.py</c>. These tests fail when a copy and its
    /// source disagree, so a hand-edited copy or a section file the script was never run for is caught here.
    /// </summary>
    /// <remarks>
    /// <see cref="SkillSourceFolder.Compose"/> restates the generator's composition rule (documented in the
    /// script): every section opened by its marker line, the entry's <c>&lt;!-- sections --&gt;</c> line replaced
    /// by the section list. The rule is deliberately small enough to state twice; the negative twins below prove
    /// the check notices a difference.
    /// </remarks>
    public sealed class SkillSourceCopiesEditModeTests
    {
        private const string RbxApiFolder = "RbxApi";

        /// <summary>
        /// Every skill source folder, and always <c>RbxApi</c>: a checkout without sources (a package install)
        /// still gets a case, which then reports Ignored instead of the suite silently having none.
        /// </summary>
        private static IEnumerable<string> SkillFolders()
        {
            SortedSet<string> folders = new(StringComparer.Ordinal) { RbxApiFolder };
            string root = SkillSourceFolder.SourcesRoot;
            if (Directory.Exists(root))
            {
                foreach (string path in Directory.GetDirectories(root))
                {
                    folders.Add(Path.GetFileName(path));
                }
            }

            return folders;
        }

        private static SkillSourceFolder LoadOrIgnore(string skillFolder)
        {
            string folder = Path.Combine(SkillSourceFolder.SourcesRoot, skillFolder);
            if (!Directory.Exists(folder))
            {
                Assert.Ignore("No skill-sources folder in this checkout (a package install ships only the copies).");
            }

            return SkillSourceFolder.Load(folder);
        }

        [TestCaseSource(nameof(SkillFolders))]
        public void ShippedCopies_MatchTheirSource(string skillFolder)
        {
            SkillSourceFolder source = LoadOrIgnore(skillFolder);

            List<string> disagreements = source.Disagreements(
                SkillSourceFolder.ReadRepositoryText(source.ResourcesPath), source.ReadCompiledConstants());

            Assert.IsEmpty(disagreements, string.Join("\n", disagreements) +
                                          "\nRun: python tools/generate_skills.py");
        }

        [TestCaseSource(nameof(SkillFolders))]
        public void ShippedText_ParsesBackIntoTheSourceSections(string skillFolder)
        {
            SkillSourceFolder source = LoadOrIgnore(skillFolder);
            string shipped = SkillSourceFolder.ReadRepositoryText(source.ResourcesPath);
            IReadOnlyList<KeyValuePair<string, string>> parsed = SkillSet.ParseSectionMarkup(shipped);

            if (source.Sections.Count == 1)
            {
                Assert.IsNull(parsed, "a one-file skill ships as that file alone, without markers");
                return;
            }

            CollectionAssert.AreEqual(source.Sections.Select(s => s.Name).ToArray(),
                parsed.Select(p => p.Key).ToArray(), "sections in file-name order");
            Assert.AreEqual(source.EntryContent(), parsed[0].Value, "the entry with its section list");
            for (int i = 1; i < parsed.Count; i++)
            {
                Assert.AreEqual(source.Sections[i].Content, parsed[i].Value, "section " + parsed[i].Key);
            }
        }

        [TestCaseSource(nameof(SkillFolders))]
        public void SourceFolder_FollowsTheAuthoringRules(string skillFolder)
        {
            SkillSourceFolder source = LoadOrIgnore(skillFolder);

            foreach (SkillSourceFolder.Section section in source.Sections)
            {
                StringAssert.StartsWith("# ", section.Content, section.FileName + ": the first line is its summary");
                StringAssert.DoesNotContain("<!-- section:", section.Content, section.FileName);
            }

            int placeholders = source.Sections[0].Content.Split('\n').Count(line => line == SkillSourceFolder.Placeholder);
            Assert.AreEqual(source.Sections.Count > 1 ? 1 : 0, placeholders,
                "the entry holds exactly one '" + SkillSourceFolder.Placeholder + "' line when there are sections");
        }

        [Test]
        public void Checker_ReportsAHandEditedResourcesCopy()
        {
            SkillSourceFolder source = LoadOrIgnore("RbxApi");
            string edited = SkillSourceFolder.ReadRepositoryText(source.ResourcesPath)
                .Replace("1 stud = 0.28 m", "1 stud = 0.3 m");

            List<string> disagreements = source.Disagreements(edited, source.ReadCompiledConstants());

            Assert.AreEqual(1, disagreements.Count, "negative twin: a hand edit of one copy is reported");
            StringAssert.Contains(source.ResourcesPath, disagreements[0]);
        }

        [Test]
        public void Checker_ReportsASectionFileTheScriptWasNotRunFor()
        {
            SkillSourceFolder source = LoadOrIgnore("RbxApi");
            string scratch = Path.Combine(Path.GetTempPath(), "coreai-skill-source-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                foreach (string file in Directory.GetFiles(source.Folder))
                {
                    File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
                }

                File.WriteAllText(Path.Combine(scratch, "99-extra.md"), "# A new section\nNEW_FACT\n", new UTF8Encoding(false));
                SkillSourceFolder added = SkillSourceFolder.Load(scratch);

                List<string> disagreements = added.Disagreements(
                    SkillSourceFolder.ReadRepositoryText(source.ResourcesPath), source.ReadCompiledConstants());

                Assert.AreEqual(2, disagreements.Count,
                    "negative twin: a new section file makes both copies stale until the script runs");
                StringAssert.Contains("- extra: A new section", added.Compose(),
                    "the new section joins the entry's section list without editing the entry");
            }
            finally
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, true);
                }
            }
        }
    }

    /// <summary>One <c>skill-sources/&lt;Skill&gt;</c> folder: its manifest and its sections in file-name order.</summary>
    internal sealed class SkillSourceFolder
    {
        internal const string Placeholder = "<!-- sections -->";
        private static readonly Regex SectionFile = new("^[0-9]{2}-([a-z0-9][a-z0-9-]*)\\.md$");

        private SkillSourceFolder(string folder, JObject manifest, List<Section> sections)
        {
            Folder = folder;
            Manifest = manifest;
            Sections = sections;
        }

        /// <summary>
        /// The repository root: the first folder at or above the working directory (Unity: the project) or the
        /// test binaries (the portable suite, whose working directory is set only after test discovery) that
        /// holds <c>tools/generate_skills.py</c>. Falls back to the working directory.
        /// </summary>
        internal static string RepositoryRoot
        {
            get
            {
                foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
                    {
                        if (File.Exists(Path.Combine(directory.FullName, "tools", "generate_skills.py")))
                        {
                            return directory.FullName;
                        }
                    }
                }

                return Directory.GetCurrentDirectory();
            }
        }

        internal static string SourcesRoot => Path.Combine(RepositoryRoot, "skill-sources");

        internal string Folder { get; }
        internal JObject Manifest { get; }
        internal List<Section> Sections { get; }
        internal string SkillName => Manifest.Value<string>("name");
        internal string ResourcesPath => Manifest.Value<string>("resources");

        internal sealed class Section
        {
            internal Section(string fileName, string name, string content)
            {
                FileName = fileName;
                Name = name;
                Content = content;
            }

            internal string FileName { get; }
            internal string Name { get; }
            internal string Content { get; }
            internal string Title => Content.Split('\n')[0].Substring(2).Trim();
        }

        internal static SkillSourceFolder Load(string folder)
        {
            JObject manifest = JObject.Parse(ReadText(Path.Combine(folder, "skill.json")));
            List<Section> sections = new();
            foreach (string path in Directory.GetFiles(folder).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                string fileName = Path.GetFileName(path);
                if (fileName == "skill.json")
                {
                    continue;
                }

                Match match = SectionFile.Match(fileName);
                Assert.IsTrue(match.Success, fileName + ": section files are named NN-name.md");
                sections.Add(new Section(fileName, match.Groups[1].Value, ReadText(path).TrimEnd('\n')));
            }

            Assert.IsNotEmpty(sections, folder + " has no section files");
            return new SkillSourceFolder(folder, manifest, sections);
        }

        internal static string ReadRepositoryText(string relativePath)
        {
            string path = Path.Combine(RepositoryRoot, relativePath);
            Assert.IsTrue(File.Exists(path), "missing generated copy " + relativePath);
            return Encoding.UTF8.GetString(File.ReadAllBytes(path));
        }

        private static string ReadText(string path)
        {
            return Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n");
        }

        /// <summary>The entry document as shipped: its placeholder line replaced by the section list.</summary>
        internal string EntryContent()
        {
            if (Sections.Count == 1)
            {
                return Sections[0].Content;
            }

            StringBuilder list = new();
            list.Append("Sections (read_skill('").Append(SkillName).Append("', section='<name>')):");
            for (int i = 1; i < Sections.Count; i++)
            {
                list.Append("\n- ").Append(Sections[i].Name).Append(": ").Append(Sections[i].Title);
            }

            return Sections[0].Content.Replace(Placeholder, list.ToString());
        }

        /// <summary>The shipped skill text this source produces (see <c>tools/generate_skills.py</c>).</summary>
        internal string Compose()
        {
            if (Sections.Count == 1)
            {
                return Sections[0].Content + "\n";
            }

            List<string> blocks = new();
            for (int i = 0; i < Sections.Count; i++)
            {
                string content = i == 0 ? EntryContent() : Sections[i].Content;
                blocks.Add(SkillSet.FormatSectionMarker(Sections[i].Name) + "\n" + content + "\n");
            }

            return string.Join("\n", blocks);
        }

        /// <summary>The constants compiled into the built-in class this skill names (<c>csharpClass</c>).</summary>
        internal (string Name, string Description, string Instructions) ReadCompiledConstants()
        {
            Type type = typeof(SkillSet).Assembly.GetType("CoreAI.Ai." + Manifest.Value<string>("csharpClass"));
            Assert.IsNotNull(type, "no built-in class " + Manifest.Value<string>("csharpClass"));
            return ((string)Constant(type, "SkillName"), (string)Constant(type, "SkillDescription"),
                (string)Constant(type, "Instructions"));
        }

        private static object Constant(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, type.Name + "." + name);
            return field.GetRawConstantValue();
        }

        /// <summary>Every way the shipped copies differ from this source; empty when they agree.</summary>
        internal List<string> Disagreements(string resourcesText,
            (string Name, string Description, string Instructions) compiled)
        {
            string expected = Compose();
            List<string> problems = new();
            if (resourcesText != expected)
            {
                problems.Add(ResourcesPath + " differs from " + SourceName() + FirstDifference(expected, resourcesText));
            }

            if (compiled.Instructions != expected || compiled.Name != SkillName ||
                compiled.Description != Manifest.Value<string>("description"))
            {
                problems.Add(Manifest.Value<string>("csharp") + " differs from " + SourceName() +
                             FirstDifference(expected, compiled.Instructions));
            }

            return problems;
        }

        private string SourceName()
        {
            return "skill-sources/" + Path.GetFileName(Folder);
        }

        private static string FirstDifference(string expected, string actual)
        {
            actual ??= "";
            int length = Math.Min(expected.Length, actual.Length);
            int index = 0;
            while (index < length && expected[index] == actual[index])
            {
                index++;
            }

            if (index == length && expected.Length == actual.Length)
            {
                return "";
            }

            int line = expected.Take(index).Count(ch => ch == '\n') + 1;
            return " (first difference at line " + line + ")";
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Agents
{
    /// <summary>
    /// Architecture fitness of <c>CoreAI.RbxApi.Agents</c> (<c>Docs/ARCHITECTURE_RULES.md</c> §5): the asmdef stays
    /// engine-free with only the core and the two engine-free Rbx assemblies as references; no engine, VM, container
    /// or async-framework type, no timer, thread, blocking wait or static singleton in its sources (it runs on the
    /// WebGL main thread); explicit types; a <c>.meta</c> beside every file.
    /// </summary>
    [TestFixture]
    public sealed class RbxAgentsArchitectureFitnessEditModeTests
    {
        private const string Folder = "Assets/CoreAIMods/Runtime/RbxApi/Agents";

        private static readonly (string Rule, Regex Pattern)[] Forbidden =
        {
            ("engine reference", new Regex(@"\bUnityEngine\b|\bUnityEditor\b")),
            ("Lua VM", new Regex(@"\busing\s+Lua\b")),
            ("container", new Regex(@"\bVContainer\b|\bMessagePipe\b")),
            ("async framework", new Regex(@"\bUniTask\b|\bCysharp\b")),
            ("static singleton", new Regex(@"\bstatic\s+(?:readonly\s+)?\w+\s+Instance\b")),
            ("implicit type", new Regex(@"\bvar\s+[A-Za-z_(]")),
            ("timer delay", new Regex(@"\bTask\.Delay\s*\(|\.CancelAfter\s*\(")),
            ("thread", new Regex(@"\bTask\.Run\s*\(|\bThreadPool\b|\bnew\s+Thread\s*\(|\bThread\.Sleep\s*\(")),
            ("blocking wait", new Regex(@"\.Wait\s*\(|\)\s*\.Result\b|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult")),
            ("context escape", new Regex(@"\.ConfigureAwait\s*\(\s*false\s*\)")),
            ("async void", new Regex(@"\basync\s+void\b")),
            ("scene search", new Regex(@"\bFind(?:Objects?|Any)\w*ByType\b|\bFindObjectOfType\b"))
        };

        [Test]
        public void Agents_KeepTheirArchitectureRules()
        {
            List<string> violations = new();
            int scanned = 0;
            foreach (string file in Files("*.cs"))
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripCommentsAndStrings(lines[i]);
                    foreach ((string rule, Regex pattern) in Forbidden)
                    {
                        if (pattern.IsMatch(code))
                        {
                            violations.Add($"{Path.GetFileName(file)}:{i + 1} {rule}: {lines[i].Trim()}");
                        }
                    }
                }
            }

            Assert.Greater(scanned, 0);
            Assert.IsEmpty(violations, string.Join("\n", violations));
        }

        [Test]
        public void Asmdef_IsEngineFree_AndReferencesOnlyTheCoreAndTheEngineFreeRbxAssemblies()
        {
            string text = File.ReadAllText(Path.Combine(Root(), "CoreAI.RbxApi.Agents.asmdef"));

            StringAssert.Contains("\"noEngineReferences\": true", text);
            MatchCollection references = Regex.Matches(
                Regex.Match(text, "\"references\"\\s*:\\s*\\[(?<list>[^\\]]*)\\]").Groups["list"].Value, "\"([^\"]+)\"");
            List<string> names = new();
            foreach (Match reference in references)
            {
                names.Add(reference.Groups[1].Value);
            }

            CollectionAssert.AreEquivalent(
                new[] { "CoreAI.Core", "CoreAI.RbxApi.Instances", "CoreAI.RbxApi.Datatypes" }, names);
        }

        [Test]
        public void EveryFile_HasAUnityMeta()
        {
            Assert.IsTrue(File.Exists(Root() + ".meta"), "The folder itself needs a .meta.");
            foreach (string file in Files("*"))
            {
                if (file.EndsWith(".meta"))
                {
                    continue;
                }

                Assert.IsTrue(File.Exists(file + ".meta"), "Missing .meta for " + Path.GetFileName(file));
            }
        }

        private static string Root()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            Assert.IsTrue(Directory.Exists(root), "Agents folder not found: " + root);
            return root;
        }

        private static IEnumerable<string> Files(string pattern)
        {
            return Directory.GetFiles(Root(), pattern, SearchOption.AllDirectories);
        }

        private static string StripCommentsAndStrings(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
            {
                return "";
            }

            string withoutStrings = Regex.Replace(line, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
            int comment = withoutStrings.IndexOf("//", System.StringComparison.Ordinal);
            return comment >= 0 ? withoutStrings.Substring(0, comment) : withoutStrings;
        }
    }
}

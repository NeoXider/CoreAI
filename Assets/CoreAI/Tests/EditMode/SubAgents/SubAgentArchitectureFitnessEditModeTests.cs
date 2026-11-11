using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// Architecture fitness of the sub-agent core (<c>Docs/ARCHITECTURE_RULES.md</c> §5): engine-free, no
    /// static service locator or settings singleton, explicit types, none of the async primitives that are dead
    /// in a WebGL player, never registering the delegate tool on a role, and a <c>.meta</c> beside every file.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentArchitectureFitnessEditModeTests
    {
        private const string Folder = "Assets/CoreAI/Runtime/Core/Features/Orchestration/SubAgents";

        private static readonly (string Rule, Regex Pattern)[] Forbidden =
        {
            ("engine reference", new Regex(@"\bUnityEngine\b")),
            ("static settings", new Regex(@"\bCoreAISettings\.")),
            ("static singleton", new Regex(@"\bstatic\s+(?:readonly\s+)?\w+\s+Instance\b")),
            ("implicit type", new Regex(@"\bvar\s+[A-Za-z_(]")),
            ("pool continuation", new Regex(@"\bRunContinuationsAsynchronously\b")),
            ("timer cancellation", new Regex(@"\.CancelAfter\s*\(")),
            ("timer delay", new Regex(@"\bTask\.Delay\s*\(")),
            ("thread pool", new Regex(@"\bTask\.Run\s*\(")),
            ("context escape", new Regex(@"\.ConfigureAwait\s*\(\s*false\s*\)")),
            ("blocking wait", new Regex(@"\.Wait\s*\(|\bThread\.Sleep\s*\(|\)\s*\.Result\b|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult")),
            ("role registration of the delegate tool", new Regex(@"\b(?:AddToolForRole|SetToolsForRole)\s*\("))
        };

        [Test]
        public void SubAgentCore_KeepsItsArchitectureRules()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            Assert.IsTrue(Directory.Exists(root), "Sub-agent core folder not found: " + root);
            List<string> violations = new();
            int scanned = 0;
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
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
        public void SubAgentCore_EveryFileHasAUnityMeta()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            Assert.IsTrue(File.Exists(root + ".meta"), "The folder itself needs a .meta.");
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                Assert.IsTrue(File.Exists(file + ".meta"), "Missing .meta for " + Path.GetFileName(file));
            }
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

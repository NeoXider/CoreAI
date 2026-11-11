using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Architecture fitness of the decision core (<c>Docs/ARCHITECTURE_RULES.md</c> §5): engine-free, no static
    /// service locator or settings singleton, explicit types, no timer-backed or thread-pool primitives (dead in a
    /// WebGL player), no blocking waits, no socket of its own outside the one GET seam, and a <c>.meta</c> beside
    /// every file.
    /// </summary>
    [TestFixture]
    public sealed class DecisionArchitectureFitnessEditModeTests
    {
        private const string Folder = "Assets/CoreAI/Runtime/Core/Features/Decisions";

        private const string GetSeamFile = "DecisionCapability.cs";

        private static readonly (string Rule, Regex Pattern)[] Forbidden =
        {
            ("engine reference", new Regex(@"\bUnityEngine\b")),
            ("static settings", new Regex(@"\bCoreAISettings\.")),
            ("static singleton", new Regex(@"\bstatic\s+(?:readonly\s+)?\w+\s+Instance\b")),
            ("implicit type", new Regex(@"\bvar\s+[A-Za-z_(]")),
            ("pool continuation", new Regex(@"\bRunContinuationsAsynchronously\b")),
            ("timer cancellation", new Regex(@"\.CancelAfter\s*\(")),
            ("timer delay", new Regex(@"\bTask\.Delay\s*\(")),
            ("thread pool", new Regex(@"\bTask\.Run\s*\(|\bThreadPool\b|\bnew\s+Thread\s*\(")),
            ("context escape", new Regex(@"\.ConfigureAwait\s*\(\s*false\s*\)")),
            ("blocking wait", new Regex(@"\.Wait\s*\(|\bThread\.Sleep\s*\(|\)\s*\.Result\b|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult")),
            ("async void", new Regex(@"\basync\s+void\b"))
        };

        private static readonly Regex OwnSocket = new(@"\bnew\s+HttpClient\b|\bHttpClient\s*\(|\bSocket\b|\bWebRequest\b");

        [Test]
        public void DecisionCore_KeepsItsArchitectureRules()
        {
            List<string> violations = new();
            int scanned = 0;
            foreach (string file in Files())
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                bool getSeam = Path.GetFileName(file) == GetSeamFile;
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

                    if (!getSeam && OwnSocket.IsMatch(code))
                    {
                        violations.Add($"{Path.GetFileName(file)}:{i + 1} own socket outside the GET seam: {lines[i].Trim()}");
                    }
                }
            }

            Assert.Greater(scanned, 0);
            Assert.IsEmpty(violations, string.Join("\n", violations));
        }

        [Test]
        public void DecisionCore_EveryFileHasAUnityMeta()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            Assert.IsTrue(File.Exists(root + ".meta"), "The folder itself needs a .meta.");
            foreach (string file in Files())
            {
                Assert.IsTrue(File.Exists(file + ".meta"), "Missing .meta for " + Path.GetFileName(file));
            }
        }

        [Test]
        public void HttpBackends_AreCompiledOnlyWithTheLlmModule()
        {
            string[] gated =
            {
                "DecisionHttpExchange.cs", "SystemOneHttpDecisionClient.cs", "OpenRouterDecisionClient.cs",
                "LogprobDecisionClient.cs"
            };
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            foreach (string name in gated)
            {
                string text = File.ReadAllText(Path.Combine(root, name));
                Assert.IsTrue(text.StartsWith("#if COREAI_LLM"), name + " must be gated like the transport it uses.");
            }
        }

        private static IEnumerable<string> Files()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), Folder);
            Assert.IsTrue(Directory.Exists(root), "Decision core folder not found: " + root);
            return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
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

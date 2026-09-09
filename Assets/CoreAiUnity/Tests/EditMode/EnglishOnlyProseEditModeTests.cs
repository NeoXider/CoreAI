using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// CoreAI ships to people who do not read Russian, so every word a consumer can meet - doc
    /// comments, inline WHYs, exception text, log text, assertion messages - is written in English.
    /// The rule has been in <c>AGENTS.md</c> from the start and had no guard, so it drifted: by
    /// 2026-09-10 there were 99 source files carrying Russian prose, including the load-bearing WHYs
    /// on the memory store and the streaming client. A rule that nothing checks is a preference, not
    /// a rule; this is the check.
    /// <para>
    /// WHAT IS NOT A VIOLATION: non-Latin text that is QUOTED rather than written. Two shapes of it
    /// exist here. One is a string literal a test feeds in or compares against - token estimation,
    /// think-block filtering and response sanitising all have to survive Cyrillic input, and
    /// rewriting their data in English would delete the coverage. The other is a verbatim sample of
    /// observed model output pasted into a comment as evidence for the WHY around it; translating
    /// that sample would turn a piece of evidence into a paraphrase of one.
    /// Both are excused by the same mechanical rule - every Cyrillic character on the line sits
    /// inside quotes - and only for files listed in <see cref="QuotedNonLatin"/> with a reason. An
    /// unquoted Russian sentence is prose and fails wherever it appears.
    /// </para>
    /// </summary>
    public sealed class EnglishOnlyProseEditModeTests
    {
        /// <summary>Package roots that ship to a consumer, relative to the project's Assets folder.</summary>
        private static readonly string[] PackageRoots =
        {
            "CoreAI",
            "CoreAiUnity",
            "CoreAIMods",
            "CoreAIMcp",
            "CoreAIMirror",
            "CoreAIHub",
            "CoreAIBenchmark",
            "CoreAI.Demos",
        };

        /// <summary>
        /// Files allowed to carry non-Latin text that is QUOTED - a test's input data, or a verbatim
        /// sample of model output cited as evidence. The value is the reason, and it is required: an
        /// entry without one is how an allowlist turns into a place to hide. The excuse is per line
        /// and mechanical: every Cyrillic character on the line must sit inside quotes, so an ordinary
        /// Russian comment in one of these files still fails.
        /// </summary>
        private static readonly Dictionary<string, string> QuotedNonLatin = new(StringComparer.Ordinal)
        {
            ["CoreAiUnity/Tests/EditMode/CalibratingTokenEstimatorEditModeTests.cs"] =
                "token estimation is calibrated against Cyrillic text, which tokenises very differently " +
                "from English; English samples would not exercise the case at all",
            ["CoreAiUnity/Tests/EditMode/ThinkBlockFilterEditModeTests.cs"] =
                "the reasoning models this filter was written for emit their think blocks in the " +
                "conversation's language, and ours emitted Russian",
            ["CoreAiUnity/Tests/EditMode/ThinkBlockStreamFilterEditModeTests.cs"] =
                "same filter, streamed: the block is cut across chunk boundaries, and a multi-byte " +
                "alphabet is what makes a boundary bug visible",
            ["CoreAiUnity/Tests/EditMode/ToolCallExtractionParityEditModeTests.cs"] =
                "tool arguments arrive in the learner's language; parity between the streamed and " +
                "non-streamed extractors has to hold for those bytes too",
            ["CoreAiUnity/Tests/EditMode/LlmResponseSanitizerTests.cs"] =
                "the sanitiser trims and normalises model output, and trimming is where a byte-length " +
                "assumption about a character-length problem shows up",
            ["CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs"] =
                "two WHYs quote the exact text a learner read when a defect fired: the two messages " +
                "fused at a lost boundary, and the teacher answer that lost its last character without " +
                "Flush. The sample IS the evidence - translated, it would only be a description of one",
            ["CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs"] =
                "the same fused-message sample, quoted at the site that raises the boundary flag",
        };

        [Test]
        public void ShippedSources_CarryNoRussianProse()
        {
            List<string> violations = new();
            foreach (string file in ShippedSourceFiles())
            {
                string relative = ToRelative(file);
                bool quotedIsAllowed = QuotedNonLatin.ContainsKey(relative);
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (!ContainsCyrillic(line))
                    {
                        continue;
                    }

                    if (quotedIsAllowed && ContainsCyrillicOnlyInsideQuotes(line))
                    {
                        continue;
                    }

                    violations.Add($"{relative}:{index + 1}: {line.Trim()}");
                }
            }

            Assert.IsEmpty(
                violations,
                "CoreAI ships to people who do not read Russian: doc comments, WHYs, exception text and " +
                "assertion messages are English. Translate the lines below. If a line is non-Latin TEST " +
                "DATA rather than prose, add its file to QuotedNonLatins with the reason - and only string " +
                "literals are excused there, never comments.\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void QuotedNonLatinAllowlist_NamesOnlyFilesThatStillExist()
        {
            // WHY: an allowlist entry for a deleted or renamed file is a hole nobody can see. It stops
            // excusing anything, so it looks harmless, right up until a new file lands at that path.
            List<string> stale = QuotedNonLatin.Keys
                .Where(relative => !File.Exists(Path.Combine(Application.dataPath, relative)))
                .ToList();

            Assert.IsEmpty(stale, "Allowlist entries point at files that no longer exist: " + string.Join(", ", stale));
        }

        [Test]
        public void QuotedNonLatinAllowlist_ActuallyExcusesSomething()
        {
            // WHY: once a fixture's data is translated or the fixture is rewritten, its entry becomes a
            // standing permission nobody needs. Requiring the entry to still cover a real line is what
            // makes the allowlist shrink by itself instead of only ever growing.
            List<string> unused = new();
            foreach (string relative in QuotedNonLatin.Keys)
            {
                string absolute = Path.Combine(Application.dataPath, relative);
                if (!File.Exists(absolute))
                {
                    continue;
                }

                bool excusesALine = File.ReadLines(absolute, Encoding.UTF8)
                    .Any(line => ContainsCyrillic(line) && ContainsCyrillicOnlyInsideQuotes(line));
                if (!excusesALine)
                {
                    unused.Add(relative);
                }
            }

            Assert.IsEmpty(
                unused,
                "These files no longer contain non-Latin test data, so their allowlist entries excuse " +
                "nothing and should be deleted: " + string.Join(", ", unused));
        }

        private static IEnumerable<string> ShippedSourceFiles()
        {
            foreach (string package in PackageRoots)
            {
                string root = Path.Combine(Application.dataPath, package);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    // Generated build output (obj/, bin/) is not source and is not shipped.
                    string normalised = file.Replace('\\', '/');
                    if (normalised.Contains("/obj/", StringComparison.Ordinal) ||
                        normalised.Contains("/bin/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }

        private static string ToRelative(string absolute)
        {
            string assets = Application.dataPath.Replace('\\', '/').TrimEnd('/') + "/";
            string normalised = absolute.Replace('\\', '/');
            return normalised.StartsWith(assets, StringComparison.Ordinal)
                ? normalised.Substring(assets.Length)
                : normalised;
        }

        private static bool ContainsCyrillic(string text)
        {
            foreach (char symbol in text)
            {
                if (symbol >= 'Ѐ' && symbol <= 'ӿ')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether every Cyrillic character on this line sits inside double quotes - a string literal
        /// in code, or a quoted sample inside a comment. The scanner does not distinguish the two on
        /// purpose: what makes non-Latin text acceptable is that it is quoted, not where it sits.
        /// <para>
        /// This is a scanner, not a C# parser, and it is deliberately conservative: it tracks escapes
        /// so a <c>\"</c> inside a literal does not look like the end of one, and it treats a line
        /// whose quotes do not balance as prose. Being wrong in that direction costs a translation
        /// that was not strictly required; being wrong the other way would let a comment through.
        /// </para>
        /// </summary>
        private static bool ContainsCyrillicOnlyInsideQuotes(string line)
        {
            bool insideLiteral = false;
            bool escaped = false;
            bool sawCyrillicOutside = false;

            foreach (char symbol in line)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (insideLiteral && symbol == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (symbol == '"')
                {
                    insideLiteral = !insideLiteral;
                    continue;
                }

                if (!insideLiteral && symbol >= 'Ѐ' && symbol <= 'ӿ')
                {
                    sawCyrillicOutside = true;
                }
            }

            // Unbalanced quotes mean the scanner lost track (a verbatim string spanning lines, say),
            // so it refuses to vouch for the line.
            return !insideLiteral && !sawCyrillicOutside;
        }
    }
}

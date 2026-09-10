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
    /// on the memory store and the streaming client - exactly the ones a consumer needs to read. A
    /// rule that nothing checks is a preference, not a rule; this is the check.
    /// <para>
    /// The rule has TWO TIERS, because "no Cyrillic anywhere" would be both too strict and too loose.
    /// </para>
    /// <para>
    /// <b>Tier 1 - unquoted Cyrillic is prose, and is never allowed.</b> A doc comment, a WHY, a
    /// section banner. This holds in tests too: an assertion message is read by whoever the test
    /// fails on, and that is the same audience as the docs.
    /// </para>
    /// <para>
    /// <b>Tier 2 - quoted Cyrillic depends on where it is.</b> Under <c>Tests/</c> it is DATA and
    /// needs no permission: token estimation, think-block filtering and response sanitising all have
    /// to survive Cyrillic input, the teacher's own replies are Russian, and rewriting that in
    /// English would delete the coverage rather than translate it. Outside <c>Tests/</c> a quoted
    /// Cyrillic string is one of two things - text a consumer can actually read (a log line, an
    /// exception message), or a verbatim sample of observed output quoted as the evidence a WHY rests
    /// on. The first must be translated; only the second is allowed, by name, in
    /// <see cref="QuotedNonLatin"/>.
    /// </para>
    /// </summary>
    public sealed class EnglishOnlyProseEditModeTests
    {
        // The Cyrillic block, written as escapes rather than as the characters themselves: spelled
        // literally, this guard would report itself on every run.
        private const char CyrillicFirst = '\u0400';
        private const char CyrillicLast = '\u04FF';

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
        /// Non-test files allowed to carry a quoted non-Latin string: a verbatim sample of observed
        /// output, cited inside an English WHY as the evidence that WHY rests on. The value is the
        /// reason and it is required - an entry without one is how an allowlist becomes a place to
        /// hide. Test data does not belong here; under <c>Tests/</c> quoted non-Latin needs no entry.
        /// </summary>
        private static readonly Dictionary<string, string> QuotedNonLatin = new(StringComparer.Ordinal)
        {
            ["CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs"] =
                "two WHYs quote the exact text a learner read when a defect fired: the two messages " +
                "fused at a lost boundary, and the teacher answer that lost its last character without " +
                "Flush. The sample IS the evidence - translated, it would only be a description of one",
            ["CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs"] =
                "the same fused-message sample, quoted at the site that raises the boundary flag",
            ["CoreAI.Demos/QwenDemo/SpellcraftDemo.cs"] =
                "the Russian words ARE the demo. It shows a 0.8B model holding an explicit RU/EN alias " +
                "table - молния maps to storm, огонь to fire - so the prompt, the tool description and " +
                "the preset spells have to carry both languages. Translate them and the demo stops " +
                "demonstrating anything",
            ["CoreAI.Demos/QwenDemo/GenieDemo.cs"] =
                "same demo pair: the preset wishes are the Russian input a small model has to understand",
        };

        [Test]
        public void ShippedSources_CarryNoRussianProse()
        {
            List<string> unquoted = new();
            List<string> unlistedRuntimeStrings = new();

            foreach (string file in ShippedSourceFiles())
            {
                string relative = ToRelative(file);
                bool isTest = relative.Contains("/Tests/", StringComparison.Ordinal);
                bool evidenceAllowed = QuotedNonLatin.ContainsKey(relative);
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);

                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (!ContainsCyrillic(line))
                    {
                        continue;
                    }

                    string where = $"{relative}:{index + 1}: {line.Trim()}";
                    if (!ContainsCyrillicOnlyInsideQuotes(line))
                    {
                        unquoted.Add(where);
                    }
                    else if (!isTest && !evidenceAllowed)
                    {
                        unlistedRuntimeStrings.Add(where);
                    }
                }
            }

            Assert.IsEmpty(
                unquoted,
                "Unquoted Cyrillic is prose - a doc comment, a WHY, a section banner, an assertion " +
                "message - and CoreAI ships to people who do not read it. Translate these lines. This " +
                "tier holds in tests too: whoever a test fails on reads its message.\n" +
                string.Join("\n", unquoted));

            Assert.IsEmpty(
                unlistedRuntimeStrings,
                "A non-Latin string outside Tests/ is text a consumer can read - a log line, an " +
                "exception message - so translate it. The one exception is a verbatim sample of " +
                "observed output quoted as the evidence a WHY rests on; if that is what this is, add " +
                "the file to QuotedNonLatin with the reason.\n" +
                string.Join("\n", unlistedRuntimeStrings));
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
            // WHY: once the quoted sample is gone - the defect was fixed, the WHY rewritten - the entry
            // becomes a standing permission nobody needs. Requiring it to still cover a real line is
            // what makes the list shrink by itself instead of only ever growing.
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
                "These files no longer quote a non-Latin sample, so their allowlist entries excuse " +
                "nothing and should be deleted: " + string.Join(", ", unused));
        }

        [Test]
        public void QuotedNonLatinAllowlist_CoversOnlyNonTestFiles()
        {
            // WHY: an entry for a file under Tests/ is not wrong, it is MEANINGLESS - tier 2 already
            // lets test data through. Left in place it would read like a rule, and the next person
            // would add one for every fixture with a Russian string, which is 25 files and climbing.
            List<string> tests = QuotedNonLatin.Keys
                .Where(relative => relative.Contains("/Tests/", StringComparison.Ordinal))
                .ToList();

            Assert.IsEmpty(
                tests,
                "Quoted non-Latin under Tests/ is data and needs no entry; delete these: " +
                string.Join(", ", tests));
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
                if (symbol >= CyrillicFirst && symbol <= CyrillicLast)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether every Cyrillic character on this line sits inside double quotes - a string literal
        /// in code, or a quoted sample inside a comment. The scanner does not distinguish the two on
        /// purpose: what decides tier 1 is whether the text is quoted, not where it sits.
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

            for (int index = 0; index < line.Length; index++)
            {
                char symbol = line[index];

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

                // A CHAR literal is quoted too, and it is how a test says "one multi-byte character":
                // new string(one, 5000) with a multi-byte char proves a byte limit is not a character one.
                // Matched as the whole bounded shape rather than by toggling on every apostrophe,
                // because English prose is full of apostrophes ("don't", "the model's") and toggling
                // on those would silently swallow the rest of a Russian comment.
                if (!insideLiteral && TryMeasureCharLiteral(line, index, out int literalLength))
                {
                    index += literalLength - 1;
                    continue;
                }

                if (!insideLiteral && symbol >= CyrillicFirst && symbol <= CyrillicLast)
                {
                    sawCyrillicOutside = true;
                }
            }

            // Unbalanced quotes mean the scanner lost track (a verbatim string spanning lines, say),
            // so it refuses to vouch for the line.
            return !insideLiteral && !sawCyrillicOutside;
        }

        /// <summary>
        /// Whether a C# char literal starts at <paramref name="start"/>, and how long it is.
        /// Recognises <c>'x'</c>, <c>'\n'</c> and <c>'\u0400'</c>; anything else - including a stray
        /// apostrophe in prose - is not a literal and is left to the caller as an ordinary character.
        /// </summary>
        private static bool TryMeasureCharLiteral(string line, int start, out int length)
        {
            length = 0;
            if (line[start] != '\'' || start + 2 >= line.Length)
            {
                return false;
            }

            int cursor = start + 1;
            if (line[cursor] == '\\')
            {
                cursor++;
                // An escape runs to the closing quote: \n is one character, \u0400 is five.
                while (cursor < line.Length && line[cursor] != '\'')
                {
                    cursor++;
                }
            }
            else
            {
                cursor++;
            }

            if (cursor >= line.Length || line[cursor] != '\'')
            {
                return false;
            }

            length = cursor - start + 1;
            return true;
        }
    }
}

using System.Collections.Generic;
using CoreAI.Ai.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Covers <see cref="LuaLogFormatter.ToPromptText"/>: full rendering and truncation.</summary>
    public sealed class LuaLogFormatterEditModeTests
    {
        private static LuaLogEntry Entry(long sequence, LuaLogLevel level, string modId, string message,
            string scriptName = null, int? line = null)
        {
            return new LuaLogEntry
            {
                Sequence = sequence,
                Level = level,
                ModId = modId,
                Message = message,
                ScriptName = scriptName,
                Line = line
            };
        }

        [Test]
        public void ToPromptText_EmptyInput_ReturnsEmptyString()
        {
            Assert.AreEqual("", LuaLogFormatter.ToPromptText(new List<LuaLogEntry>(), 1000));
        }

        [Test]
        public void ToPromptText_NullInput_ReturnsEmptyString()
        {
            Assert.AreEqual("", LuaLogFormatter.ToPromptText(null, 1000));
        }

        [Test]
        public void ToPromptText_NonPositiveMaxChars_ReturnsEmptyString()
        {
            List<LuaLogEntry> entries = new() { Entry(1, LuaLogLevel.Print, "m", "hi") };
            Assert.AreEqual("", LuaLogFormatter.ToPromptText(entries, 0));
        }

        [Test]
        public void ToPromptText_IncludesSequenceLevelModIdLineAndMessage()
        {
            List<LuaLogEntry> entries = new()
            {
                Entry(7, LuaLogLevel.Error, "mymod", "something broke", "main.lua", 42)
            };

            string text = LuaLogFormatter.ToPromptText(entries, 1000);

            StringAssert.Contains("7", text);
            StringAssert.Contains("ERROR", text);
            StringAssert.Contains("mymod", text);
            StringAssert.Contains("main.lua:42", text);
            StringAssert.Contains("something broke", text);
        }

        [Test]
        public void ToPromptText_MultipleEntries_OneLinePerEntryOldestFirst()
        {
            List<LuaLogEntry> entries = new()
            {
                Entry(1, LuaLogLevel.Print, "m", "first"),
                Entry(2, LuaLogLevel.Print, "m", "second"),
                Entry(3, LuaLogLevel.Print, "m", "third")
            };

            string text = LuaLogFormatter.ToPromptText(entries, 1000);
            string[] lines = text.Split('\n');

            Assert.AreEqual(3, lines.Length);
            StringAssert.Contains("first", lines[0]);
            StringAssert.Contains("second", lines[1]);
            StringAssert.Contains("third", lines[2]);
        }

        [Test]
        public void ToPromptText_WhenEverythingFits_NoTruncationMarker()
        {
            List<LuaLogEntry> entries = new() { Entry(1, LuaLogLevel.Print, "m", "hi") };
            string text = LuaLogFormatter.ToPromptText(entries, 10_000);

            StringAssert.DoesNotContain(LuaLogFormatter.TruncationMarkerPrefix, text);
        }

        [Test]
        public void ToPromptText_NeverExceedsMaxChars()
        {
            List<LuaLogEntry> entries = new();
            for (int i = 0; i < 200; i++)
            {
                entries.Add(Entry(i, LuaLogLevel.Print, "m", $"message number {i} with some extra padding text"));
            }

            foreach (int budget in new[] { 1, 5, 20, 50, 200, 500 })
            {
                string text = LuaLogFormatter.ToPromptText(entries, budget);
                Assert.LessOrEqual(text.Length, budget,
                    $"Output must never exceed the requested budget ({budget} chars).");
            }
        }

        [Test]
        public void ToPromptText_WhenTruncated_MarksHowManyEntriesWereDropped()
        {
            List<LuaLogEntry> entries = new();
            for (int i = 0; i < 50; i++)
            {
                entries.Add(Entry(i, LuaLogLevel.Print, "m", $"message-{i}-padding-padding-padding"));
            }

            string fullText = LuaLogFormatter.ToPromptText(entries, 100_000);
            string truncatedText = LuaLogFormatter.ToPromptText(entries, 300);

            Assert.Less(truncatedText.Length, fullText.Length);
            StringAssert.Contains(LuaLogFormatter.TruncationMarkerPrefix, truncatedText);
        }

        [Test]
        public void ToPromptText_WhenTruncated_KeepsNewestEntries()
        {
            List<LuaLogEntry> entries = new();
            for (int i = 0; i < 50; i++)
            {
                entries.Add(Entry(i, LuaLogLevel.Print, "m", $"message-{i}-padding-padding-padding"));
            }

            string text = LuaLogFormatter.ToPromptText(entries, 300);

            StringAssert.Contains("message-49-", text);
            StringAssert.DoesNotContain("message-0-", text);
        }

        [Test]
        public void ToPromptText_IdenticalConsecutiveMessages_CoalescedIntoOneLineWithRepeatCount()
        {
            List<LuaLogEntry> entries = new();
            for (int i = 1; i <= 5; i++)
            {
                entries.Add(Entry(i, LuaLogLevel.Warn, "m", "same spammy message"));
            }

            string text = LuaLogFormatter.ToPromptText(entries, 1000);
            string[] lines = text.Split('\n');

            Assert.AreEqual(1, lines.Length);
            StringAssert.Contains("same spammy message", lines[0]);
            StringAssert.Contains("×5", lines[0]);
        }

        [Test]
        public void ToPromptText_DifferentConsecutiveMessages_AreNotCoalesced()
        {
            List<LuaLogEntry> entries = new()
            {
                Entry(1, LuaLogLevel.Print, "m", "alpha"),
                Entry(2, LuaLogLevel.Print, "m", "beta"),
                Entry(3, LuaLogLevel.Print, "m", "alpha")
            };

            string text = LuaLogFormatter.ToPromptText(entries, 1000);
            string[] lines = text.Split('\n');

            Assert.AreEqual(3, lines.Length);
            StringAssert.DoesNotContain("×", text);
        }

        [Test]
        public void ToPromptText_MissingScriptName_OmitsLocationSegment()
        {
            List<LuaLogEntry> entries = new() { Entry(1, LuaLogLevel.Warn, "m", "no script info") };
            string text = LuaLogFormatter.ToPromptText(entries, 1000);

            Assert.AreEqual("[1] WARN m - no script info", text);
        }
    }
}

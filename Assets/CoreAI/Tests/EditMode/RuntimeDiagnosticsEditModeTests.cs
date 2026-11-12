using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Diagnostics;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The engine-free readers behind the debug tools: the bounded engine log ring (cursor, eviction, filters,
    /// clamping, redaction), its compact formatter, and the frame-time window (fps over a window, slowest frame,
    /// percentile).
    /// </summary>
    public sealed class RuntimeDiagnosticsEditModeTests
    {
        [Test]
        public void LogBuffer_AssignsSequences_AndEvictsTheOldestBeyondCapacity()
        {
            RuntimeLogBuffer buffer = new(capacity: 3);
            for (int index = 1; index <= 5; index++) buffer.Append(RuntimeLogLevel.Info, "m" + index);

            IReadOnlyList<RuntimeLogEntry> all = buffer.Read(new RuntimeLogQuery { MaxCount = 0 });

            Assert.AreEqual(5, buffer.LatestSequence);
            Assert.AreEqual(3, buffer.OldestRetainedSequence);
            CollectionAssert.AreEqual(new[] { "m3", "m4", "m5" }, Messages(all));
            Assert.AreEqual(5, buffer.Counts.Info, "totals keep counting what the ring evicted");
        }

        [Test]
        public void LogBuffer_Cursor_ReturnsOnlyNewerEntries()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Info, "old");
            long cursor = buffer.LatestSequence;
            buffer.Append(RuntimeLogLevel.Warning, "new");

            IReadOnlyList<RuntimeLogEntry> newer = buffer.Read(new RuntimeLogQuery { SinceSequence = cursor });

            CollectionAssert.AreEqual(new[] { "new" }, Messages(newer));
        }

        [Test]
        public void LogBuffer_FiltersByMinimumLevelAndText_AndKeepsTheNewestMatches()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Info, "info door");
            buffer.Append(RuntimeLogLevel.Warning, "warn door");
            buffer.Append(RuntimeLogLevel.Error, "error window");
            buffer.Append(RuntimeLogLevel.Exception, "exception DOOR");

            CollectionAssert.AreEqual(new[] { "error window", "exception DOOR" },
                Messages(buffer.Read(new RuntimeLogQuery { MinLevel = RuntimeLogLevel.Error })));
            CollectionAssert.AreEqual(new[] { "warn door", "exception DOOR" },
                Messages(buffer.Read(new RuntimeLogQuery { MinLevel = RuntimeLogLevel.Warning, Contains = "door" })));
            CollectionAssert.AreEqual(new[] { "exception DOOR" },
                Messages(buffer.Read(new RuntimeLogQuery { MaxCount = 1 })));
        }

        [Test]
        public void LogBuffer_ClampsMessages_AndKeepsStackTracesOnlyFromTheConfiguredLevel()
        {
            RuntimeLogBuffer buffer = new(maxMessageChars: 40, maxStackTraceChars: 100,
                minStackTraceLevel: RuntimeLogLevel.Warning);
            buffer.Append(RuntimeLogLevel.Info, new string('a', 100), "info stack");
            buffer.Append(RuntimeLogLevel.Error, "boom", "Game.Update () (at Game.cs:10)");

            IReadOnlyList<RuntimeLogEntry> entries = buffer.Read(new RuntimeLogQuery());

            Assert.AreEqual(40, entries[0].Message.Length);
            StringAssert.EndsWith("...[truncated]", entries[0].Message);
            Assert.AreEqual("", entries[0].StackTrace, "an info entry drops its stack trace");
            Assert.AreEqual("Game.Update () (at Game.cs:10)", entries[1].StackTrace);
        }

        [Test]
        public void LogBuffer_RedactsARegisteredSecret_InMessagesAndStackTraces()
        {
            RuntimeLogBuffer buffer = new();
            const string token = "Qk3-secret-token-123";
            buffer.AddRedaction(token);
            buffer.AddRedaction("short");
            buffer.Append(RuntimeLogLevel.Error, "Auth token: " + token, "at " + token);
            buffer.Append(RuntimeLogLevel.Info, "a short word stays");

            IReadOnlyList<RuntimeLogEntry> entries = buffer.Read(new RuntimeLogQuery());

            Assert.AreEqual("Auth token: " + RuntimeLogBuffer.RedactedText, entries[0].Message);
            Assert.AreEqual("at " + RuntimeLogBuffer.RedactedText, entries[0].StackTrace);
            Assert.AreEqual("a short word stays", entries[1].Message, "secrets under 8 characters are not redacted");
        }

        [Test]
        public void LogBuffer_ConcurrentAppends_KeepEverySequenceExactlyOnce()
        {
            RuntimeLogBuffer buffer = new(capacity: 4000);
            Parallel.For(0, 4000, index => buffer.Append(RuntimeLogLevel.Info, "m" + index));

            IReadOnlyList<RuntimeLogEntry> all = buffer.Read(new RuntimeLogQuery { MaxCount = 0 });

            Assert.AreEqual(4000, all.Count);
            for (int index = 0; index < all.Count; index++) Assert.AreEqual(index + 1, all[index].Sequence);
        }

        [Test]
        public void Formatter_FoldsRepeats_AndKeepsTheNewestWithinBudget()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Info, "first");
            for (int index = 0; index < 3; index++) buffer.Append(RuntimeLogLevel.Error, "same");
            buffer.Append(RuntimeLogLevel.Warning, "last");
            IReadOnlyList<RuntimeLogEntry> entries = buffer.Read(new RuntimeLogQuery { MaxCount = 0 });

            string full = RuntimeLogFormatter.Format(entries, 1000);
            string tight = RuntimeLogFormatter.Format(entries, 40);

            Assert.AreEqual("[1] INFO first\n[2-4] ERROR same (x3)\n[5] WARN last", full);
            StringAssert.StartsWith(RuntimeLogFormatter.OmittedMarkerPrefix, tight);
            StringAssert.EndsWith("[5] WARN last", tight);
            Assert.LessOrEqual(tight.Length, 40);
        }

        [Test]
        public void Formatter_AddsStackFramesOnlyWhenAsked()
        {
            RuntimeLogBuffer buffer = new();
            buffer.Append(RuntimeLogLevel.Exception, "NullReferenceException", "A.B ()\nC.D ()\nE.F ()\n");
            IReadOnlyList<RuntimeLogEntry> entries = buffer.Read(new RuntimeLogQuery());

            Assert.AreEqual("[1] EXCEPTION NullReferenceException", RuntimeLogFormatter.Format(entries, 500));
            Assert.AreEqual("[1] EXCEPTION NullReferenceException\n    A.B ()\n    C.D ()",
                RuntimeLogFormatter.Format(entries, 500, 2));
        }

        [TestCase("warn", RuntimeLogLevel.Warning)]
        [TestCase("WARNING", RuntimeLogLevel.Warning)]
        [TestCase("print", RuntimeLogLevel.Info)]
        [TestCase("assert", RuntimeLogLevel.Error)]
        [TestCase("runtime_error", RuntimeLogLevel.Exception)]
        public void LevelNames_AcceptTheModLogSpellingsToo(string text, RuntimeLogLevel expected)
        {
            Assert.IsTrue(RuntimeLogLevels.TryParse(text, RuntimeLogLevel.Info, out RuntimeLogLevel level));
            Assert.AreEqual(expected, level);
        }

        [Test]
        public void LevelNames_RejectUnknown_AndBlankFallsBack()
        {
            Assert.IsFalse(RuntimeLogLevels.TryParse("loud", RuntimeLogLevel.Info, out _));
            Assert.IsTrue(RuntimeLogLevels.TryParse(" ", RuntimeLogLevel.Warning, out RuntimeLogLevel level));
            Assert.AreEqual(RuntimeLogLevel.Warning, level);
        }

        [Test]
        public void FrameWindow_ReportsAverageSlowestAndPercentile_OverTheRequestedWindow()
        {
            FrameTimeWindow window = new(capacity: 100);
            double now = 0d;
            for (int index = 0; index < 19; index++) window.Record(now += 0.010, 10d);
            window.Record(now += 0.100, 100d);

            FrameRateSnapshot all = window.Snapshot(now, 0d);

            Assert.AreEqual(20, all.Frames);
            Assert.AreEqual(0.29, all.WindowSeconds, 1e-9);
            Assert.AreEqual(20 / 0.29, all.AverageFps, 1e-6);
            Assert.AreEqual(10d, all.MinimumFps, 1e-9, "the slowest frame was 100 ms");
            Assert.AreEqual(100d, all.MaxFrameMilliseconds, 1e-9);
            Assert.AreEqual(10d, all.P95FrameMilliseconds, 1e-9, "19 of 20 frames are 10 ms; nearest rank 19");
            Assert.AreEqual(1, all.SlowFrames);

            FrameRateSnapshot recent = window.Snapshot(now, 0.05);
            Assert.AreEqual(1, recent.Frames, "only the frame recorded inside the last 50 ms");
        }

        [Test]
        public void FrameWindow_IsEmptyBeforeTheFirstFrame_AndRingWraps()
        {
            FrameTimeWindow window = new(capacity: 4);
            Assert.AreEqual(0, window.Snapshot(1d, 5d).Frames);
            for (int index = 1; index <= 10; index++) window.Record(index, index);

            FrameRateSnapshot snapshot = window.Snapshot(10d, 0d);

            Assert.AreEqual(4, snapshot.Frames);
            Assert.AreEqual(10d, snapshot.MaxFrameMilliseconds, 1e-9);
            Assert.AreEqual(1000d / 10d, snapshot.MinimumFps, 1e-9);
        }

        [Test]
        public void Json_FrameRateAndCounters_UseTheSharedFieldNames()
        {
            JObject empty = RuntimeDiagnosticsJson.FrameRate(default);
            Assert.AreEqual(0, (int)empty["frames"]);
            Assert.IsNotNull(empty["note"]);

            JObject counters = RuntimeDiagnosticsJson.Counters(new EngineCounterSnapshot(
                new[]
                {
                    new EngineCounterValue("draw_calls", 120.4, "count"),
                    new EngineCounterValue("gc_used_memory", 3d * 1024 * 1024, "bytes"),
                    new EngineCounterValue("main_thread_ms", 4.567, "ms")
                },
                new[] { "gpu_ms" }));

            Assert.AreEqual(120, (long)counters["draw_calls"]);
            Assert.AreEqual(3.0, (double)counters["gc_used_memory_mb"], 1e-9);
            Assert.AreEqual(4.57, (double)counters["main_thread_ms"], 1e-9);
            CollectionAssert.AreEqual(new[] { "gpu_ms" }, counters["unavailable"].ToObject<string[]>());
        }

        private static string[] Messages(IReadOnlyList<RuntimeLogEntry> entries)
        {
            string[] messages = new string[entries.Count];
            for (int index = 0; index < entries.Count; index++) messages[index] = entries[index].Message;
            return messages;
        }
    }
}

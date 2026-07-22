using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers <see cref="LuaLogService"/>: per-mod/global ring-buffer eviction order and caps, the
    /// <see cref="LuaLogQuery"/> filter matrix, <see cref="LuaLogEntry.Sequence"/> monotonicity,
    /// <see cref="ILuaLogService.EntryAppended"/> firing, and a concurrent append/query smoke test.
    /// </summary>
    public sealed class LuaLogServiceEditModeTests
    {
        private static LuaLogEntry Entry(string modId, LuaLogLevel level, string message,
            string scriptName = null, int? line = null)
        {
            return new LuaLogEntry
            {
                ModId = modId,
                Level = level,
                Message = message,
                ScriptName = scriptName,
                Line = line
            };
        }

        [Test]
        public void Append_AssignsMonotonicallyIncreasingSequenceStartingAtOne()
        {
            LuaLogService service = new();

            LuaLogEntry a = Entry("m", LuaLogLevel.Print, "a");
            LuaLogEntry b = Entry("m", LuaLogLevel.Print, "b");
            LuaLogEntry c = Entry("m", LuaLogLevel.Print, "c");

            service.Append(a);
            service.Append(b);
            service.Append(c);

            Assert.AreEqual(1, a.Sequence);
            Assert.AreEqual(2, b.Sequence);
            Assert.AreEqual(3, c.Sequence);
        }

        [Test]
        public void Append_StampsUtcTime()
        {
            LuaLogService service = new();
            LuaLogEntry entry = Entry("m", LuaLogLevel.Print, "x");

            DateTime before = DateTime.UtcNow;
            service.Append(entry);
            DateTime after = DateTime.UtcNow;

            Assert.GreaterOrEqual(entry.UtcTime, before);
            Assert.LessOrEqual(entry.UtcTime, after);
        }

        [Test]
        public void Append_FiresEntryAppendedWithTheSameEntry()
        {
            LuaLogService service = new();
            LuaLogEntry captured = null;
            int fireCount = 0;

            service.EntryAppended += e =>
            {
                captured = e;
                fireCount++;
            };

            LuaLogEntry entry = Entry("m", LuaLogLevel.Warn, "hello");
            service.Append(entry);

            Assert.AreEqual(1, fireCount);
            Assert.AreSame(entry, captured);
        }

        [Test]
        public void PerModRingBuffer_EvictsOldestPastCap()
        {
            LuaLogService service = new(perModCapacity: 3, globalCapacity: 100);

            for (int i = 1; i <= 5; i++)
            {
                service.Append(Entry("m", LuaLogLevel.Print, $"msg{i}"));
            }

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { ModId = "m" });

            Assert.AreEqual(3, result.Count, "Per-mod buffer must cap at its configured capacity.");
            CollectionAssert.AreEqual(
                new[] { "msg3", "msg4", "msg5" },
                result.Select(e => e.Message).ToArray(),
                "Oldest entries (msg1, msg2) must be evicted first; newest-last order is preserved.");
        }

        [Test]
        public void GlobalRingBuffer_EvictsOldestPastCapAcrossMods()
        {
            LuaLogService service = new(perModCapacity: 100, globalCapacity: 3);

            service.Append(Entry("a", LuaLogLevel.Print, "1"));
            service.Append(Entry("b", LuaLogLevel.Print, "2"));
            service.Append(Entry("a", LuaLogLevel.Print, "3"));
            service.Append(Entry("b", LuaLogLevel.Print, "4"));
            service.Append(Entry("a", LuaLogLevel.Print, "5"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery());

            Assert.AreEqual(3, result.Count);
            CollectionAssert.AreEqual(new[] { "3", "4", "5" }, result.Select(e => e.Message).ToArray());
        }

        [Test]
        public void Query_FiltersByModId()
        {
            LuaLogService service = new();
            service.Append(Entry("a", LuaLogLevel.Print, "from-a"));
            service.Append(Entry("b", LuaLogLevel.Print, "from-b"));

            IReadOnlyList<LuaLogEntry> onlyA = service.Query(new LuaLogQuery { ModId = "a" });

            Assert.AreEqual(1, onlyA.Count);
            Assert.AreEqual("from-a", onlyA[0].Message);
        }

        [Test]
        public void Query_UnknownModId_ReturnsEmpty()
        {
            LuaLogService service = new();
            service.Append(Entry("a", LuaLogLevel.Print, "from-a"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { ModId = "does-not-exist" });

            Assert.IsEmpty(result);
        }

        [Test]
        public void Query_FiltersByMinLevel()
        {
            LuaLogService service = new();
            service.Append(Entry("m", LuaLogLevel.Print, "print-msg"));
            service.Append(Entry("m", LuaLogLevel.Warn, "warn-msg"));
            service.Append(Entry("m", LuaLogLevel.Error, "error-msg"));
            service.Append(Entry("m", LuaLogLevel.RuntimeError, "runtime-msg"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { MinLevel = LuaLogLevel.Error });

            Assert.AreEqual(2, result.Count);
            CollectionAssert.AreEqual(new[] { "error-msg", "runtime-msg" }, result.Select(e => e.Message).ToArray());
        }

        [Test]
        public void Query_FiltersBySinceSequence()
        {
            LuaLogService service = new();
            service.Append(Entry("m", LuaLogLevel.Print, "1"));
            LuaLogEntry second = Entry("m", LuaLogLevel.Print, "2");
            service.Append(second);
            service.Append(Entry("m", LuaLogLevel.Print, "3"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { SinceSequence = second.Sequence });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("3", result[0].Message);
        }

        [Test]
        public void Query_FiltersByTextContains_CaseInsensitive()
        {
            LuaLogService service = new();
            service.Append(Entry("m", LuaLogLevel.Print, "Player spawned"));
            service.Append(Entry("m", LuaLogLevel.Print, "Enemy died"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { TextContains = "player" });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Player spawned", result[0].Message);
        }

        [Test]
        public void Query_RespectsMaxCount_KeepingNewestNewestLast()
        {
            LuaLogService service = new();
            for (int i = 1; i <= 10; i++)
            {
                service.Append(Entry("m", LuaLogLevel.Print, $"msg{i}"));
            }

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery { MaxCount = 3 });

            CollectionAssert.AreEqual(
                new[] { "msg8", "msg9", "msg10" },
                result.Select(e => e.Message).ToArray());
        }

        [Test]
        public void Query_CombinesFiltersWithAndSemantics()
        {
            LuaLogService service = new();
            service.Append(Entry("a", LuaLogLevel.Print, "keep me"));
            service.Append(Entry("a", LuaLogLevel.Error, "wrong text"));
            service.Append(Entry("b", LuaLogLevel.Print, "keep me too"));

            IReadOnlyList<LuaLogEntry> result = service.Query(new LuaLogQuery
            {
                ModId = "a",
                MinLevel = LuaLogLevel.Print,
                TextContains = "keep"
            });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("keep me", result[0].Message);
        }

        [Test]
        public void Clear_WithModId_OnlyClearsThatModsBuffer()
        {
            LuaLogService service = new();
            service.Append(Entry("a", LuaLogLevel.Print, "from-a"));
            service.Append(Entry("b", LuaLogLevel.Print, "from-b"));

            service.Clear("a");

            Assert.IsEmpty(service.Query(new LuaLogQuery { ModId = "a" }));
            Assert.AreEqual(1, service.Query(new LuaLogQuery { ModId = "b" }).Count);
        }

        [Test]
        public void Clear_WithoutModId_ClearsEverythingIncludingGlobal()
        {
            LuaLogService service = new();
            service.Append(Entry("a", LuaLogLevel.Print, "from-a"));
            service.Append(Entry("b", LuaLogLevel.Print, "from-b"));

            service.Clear();

            Assert.IsEmpty(service.Query(new LuaLogQuery()));
            Assert.IsEmpty(service.Query(new LuaLogQuery { ModId = "a" }));
        }

        [Test]
        public void Append_NullEntry_Throws()
        {
            LuaLogService service = new();
            Assert.Throws<ArgumentNullException>(() => service.Append(null));
        }

        [Test]
        public void MirrorLogger_IsNotRequired_AppendWorksWithoutOne()
        {
            LuaLogService service = new(mirrorLogger: null);
            Assert.DoesNotThrow(() => service.Append(Entry("m", LuaLogLevel.RuntimeError, "boom")));
        }

        [Test]
        public void ThreadSafetySmoke_ParallelAppendsAndQueries_DoNotCorruptCounts()
        {
            LuaLogService service = new(perModCapacity: 1000, globalCapacity: 5000);
            const int producers = 8;
            const int perProducer = 200;
            long totalExpected = producers * perProducer;

            List<Task> tasks = new();
            for (int p = 0; p < producers; p++)
            {
                int producerId = p;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perProducer; i++)
                    {
                        service.Append(Entry($"mod{producerId}", LuaLogLevel.Print, $"p{producerId}-{i}"));
                    }
                }));
            }

            // WHY: NUnit assertions are not safe to raise off the test thread, so a concurrent reader
            // records violations (corrupted negative/oversized counts) here and they are asserted after
            // it joins, instead of asserting inside the loop.
            using CancellationTokenSource readerCts = new();
            bool corruptionObserved = false;
            Task reader = Task.Run(() =>
            {
                while (!readerCts.IsCancellationRequested)
                {
                    int count = service.Query(new LuaLogQuery()).Count;
                    if (count < 0 || count > LuaLogService.DefaultGlobalCapacity)
                    {
                        corruptionObserved = true;
                    }
                }
            });

            Task.WaitAll(tasks.ToArray());
            readerCts.Cancel();
            reader.Wait(TimeSpan.FromSeconds(5));

            Assert.IsFalse(corruptionObserved,
                "Query must never observe a negative or over-capacity count while appends are racing.");

            long observedSequenceMax = service.Query(new LuaLogQuery { MaxCount = 1 }).FirstOrDefault()?.Sequence ?? 0;
            Assert.AreEqual(totalExpected, observedSequenceMax,
                "Every append must claim a unique sequence number with no duplicates or gaps under contention.");

            int perModTotal = 0;
            for (int p = 0; p < producers; p++)
            {
                perModTotal += service.Query(new LuaLogQuery { ModId = $"mod{p}", MaxCount = int.MaxValue }).Count;
            }

            Assert.AreEqual(totalExpected, perModTotal,
                "No entry may be lost or duplicated across per-mod buffers under concurrent append.");
        }
    }
}

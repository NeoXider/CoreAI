using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// Cost of the engine-free half of the Mods page at 1,000 and 10,000 mods in nested folders: tree build,
    /// search and flatten, as time (median of 7 trials after a warm-up) and allocated bytes
    /// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>), printed for the record; plus zero-garbage budgets
    /// on the paths a view runs while scrolling and folding, each with an allocating twin.
    /// </summary>
    /// <remarks>
    /// WHY the allocation budgets self-ignore: Unity's Mono answers 0 from the per-thread counter, which would
    /// pass every budget silently. They are enforced where the counter works (the portable .NET leg).
    /// </remarks>
    [TestFixture]
    public sealed class ModFolderTreePerformanceEditModeTests
    {
        private const int Trials = 7;

        /// <summary>
        /// Mods spread over three folder levels: 10 top folders, <paramref name="perLevel"/> subfolders each,
        /// <paramref name="perLevel"/> sub-subfolders each, and the rest of the mods round-robin into the leaves.
        /// </summary>
        internal static List<ModFolderModEntry> NestedMods(int count, int perLevel)
        {
            List<ModFolderModEntry> mods = new(count);
            int leaves = 10 * perLevel * perLevel;
            for (int i = 0; i < count; i++)
            {
                int leaf = i % leaves;
                int top = leaf / (perLevel * perLevel);
                int middle = leaf / perLevel % perLevel;
                int bottom = leaf % perLevel;
                string folder = "Pack " + top.ToString(CultureInfo.InvariantCulture) + "/Area " +
                                middle.ToString(CultureInfo.InvariantCulture) + "/Set " +
                                bottom.ToString(CultureInfo.InvariantCulture);
                string id = "mod_" + i.ToString("D5", CultureInfo.InvariantCulture);
                mods.Add(new ModFolderModEntry(id, "Mod " + i.ToString(CultureInfo.InvariantCulture), folder,
                    i % 3 == 0 ? "arcade, blocks" : "tools", i % 4 != 0, i + 1));
            }

            return mods;
        }

        private static bool CounterWorks()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] probe = new object[32];
            GC.KeepAlive(probe);
            return GC.GetAllocatedBytesForCurrentThread() - before >= 32 * IntPtr.Size;
        }

        private static void RequireCounter()
        {
            if (!CounterWorks())
            {
                Assert.Ignore("GC.GetAllocatedBytesForCurrentThread does not count allocations on this runtime " +
                              "(Unity's Mono answers 0); the budget is enforced on the portable .NET leg.");
            }
        }

        private static (double Milliseconds, long Bytes) Median(Action action)
        {
            action();
            action();
            double[] times = new double[Trials];
            long[] bytes = new long[Trials];
            for (int trial = 0; trial < Trials; trial++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                action();
                long end = Stopwatch.GetTimestamp();
                bytes[trial] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                times[trial] = (end - start) * 1000.0 / Stopwatch.Frequency;
            }

            Array.Sort(times);
            Array.Sort(bytes);
            return (times[Trials / 2], bytes[Trials / 2]);
        }

        private static string Row(string what, int mods, (double Milliseconds, long Bytes) measured)
        {
            return "mod-folders " + what + " @" + mods.ToString(CultureInfo.InvariantCulture) + " mods: " +
                   measured.Milliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms, " +
                   measured.Bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        [TestCase(1000, 5, 100.0)]
        [TestCase(10000, 10, 1000.0)]
        public void Measure_BuildSearchAndFlatten_PrintsTimeAndBytes(int count, int perLevel, double buildBudgetMs)
        {
            List<ModFolderModEntry> mods = NestedMods(count, perLevel);
            ModFolderTree tree = null;

            (double Milliseconds, long Bytes) build = Median(() => tree = ModFolderTree.Build(mods));
            ModFolderTree built = tree;
            int folder = built.GetVisibleRow(0).Index;
            (double Milliseconds, long Bytes) search = Median(() =>
            {
                built.SetSearch("Area 3");
                built.SetSearch("arcade");
            });
            built.SetSearch("");
            (double Milliseconds, long Bytes) flatten = Median(() =>
            {
                built.SetExpanded(folder, false);
                built.SetExpanded(folder, true);
            });
            (double Milliseconds, long Bytes) collapseAll = Median(() =>
            {
                built.SetAllExpanded(false);
                built.SetAllExpanded(true);
            });

            TestContext.Progress.WriteLine(Row("build", count, build) + " (" +
                                           built.FolderCount.ToString(CultureInfo.InvariantCulture) + " folders, " +
                                           built.VisibleRowCount.ToString(CultureInfo.InvariantCulture) + " rows)");
            TestContext.Progress.WriteLine(Row("search x2", count, search));
            TestContext.Progress.WriteLine(Row("fold+unfold x2 flatten", count, flatten));
            TestContext.Progress.WriteLine(Row("collapse-all+expand-all", count, collapseAll));
            Assert.Less(build.Milliseconds, buildBudgetMs, Row("build", count, build));
        }

        [Test]
        public void Flatten_AfterBuild_AllocatesNothing()
        {
            RequireCounter();
            ModFolderTree tree = ModFolderTree.Build(NestedMods(1000, 5));
            int folder = tree.GetVisibleRow(0).Index;

            (double Milliseconds, long Bytes) measured = Median(() =>
            {
                tree.SetExpanded(folder, false);
                tree.SetExpanded(folder, true);
                tree.SetAllExpanded(false);
                tree.SetAllExpanded(true);
            });

            Assert.AreEqual(0, measured.Bytes, Row("fold and flatten", 1000, measured));
        }

        [Test]
        public void Build_Allocates_TheTwinThatProvesTheMeterSeesIt()
        {
            RequireCounter();
            List<ModFolderModEntry> mods = NestedMods(1000, 5);

            (double Milliseconds, long Bytes) measured = Median(() => ModFolderTree.Build(mods));

            Assert.Greater(measured.Bytes, 0, Row("build", 1000, measured));
        }

        [Test]
        public void Search_TrimmedQuery_AllocatesNothing()
        {
            RequireCounter();
            ModFolderTree tree = ModFolderTree.Build(NestedMods(1000, 5));

            (double Milliseconds, long Bytes) measured = Median(() =>
            {
                tree.SetSearch("Area 3");
                tree.SetSearch("arcade");
                tree.SetSearch("");
            });

            Assert.AreEqual(0, measured.Bytes, Row("search", 1000, measured));
        }

        [Test]
        public void ScrollingReads_AfterTheFirstPass_AllocateNothing()
        {
            RequireCounter();
            ModFolderTree tree = ModFolderTree.Build(NestedMods(1000, 5));
            int checksum = 0;

            (double Milliseconds, long Bytes) measured = Median(() =>
            {
                for (int i = 0; i < tree.VisibleRowCount; i++)
                {
                    ModFolderRow row = tree.GetVisibleRow(i);
                    checksum += row.IsFolder
                        ? tree.GetFolder(row.Index).Name.Length + tree.GetCountText(row.Index).Length +
                          (tree.IsExpanded(row.Index) ? 1 : 0)
                        : tree.GetMod(row.Index).Name.Length;
                }
            });

            Assert.AreEqual(0, measured.Bytes, Row("scroll reads", 1000, measured));
            Assert.Greater(checksum, 0);
        }

        [Test]
        public void CountText_FirstReadAfterASearch_Allocates_TheTwinOfTheCachedRead()
        {
            RequireCounter();
            ModFolderTree tree = ModFolderTree.Build(NestedMods(1000, 5));
            int folder = tree.GetVisibleRow(0).Index;
            string query = "Area";
            long allocated = 0;

            for (int trial = 0; trial < 3; trial++)
            {
                tree.SetSearch(trial % 2 == 0 ? query : "");
                long before = GC.GetAllocatedBytesForCurrentThread();
                tree.GetCountText(folder);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Greater(allocated, 0);
        }
    }
}

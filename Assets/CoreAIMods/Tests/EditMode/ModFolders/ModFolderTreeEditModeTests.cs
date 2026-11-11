using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>The folder tree: building from stored folders, counts, search, fold state and the flat visible rows.</summary>
    [TestFixture]
    public sealed class ModFolderTreeEditModeTests
    {
        private static ModFolderModEntry Mod(string id, string folder, bool enabled = true, string tags = "",
            string name = null)
        {
            return new ModFolderModEntry(id, name ?? id, folder, tags, enabled);
        }

        private static string Render(ModFolderTree tree)
        {
            StringBuilder text = new();
            for (int i = 0; i < tree.VisibleRowCount; i++)
            {
                ModFolderRow row = tree.GetVisibleRow(i);
                text.Append(new string(' ', row.Depth * 2));
                if (row.IsFolder)
                {
                    text.Append('[').Append(tree.GetFolder(row.Index).Name).Append("] ")
                        .Append(tree.GetCountText(row.Index));
                }
                else
                {
                    text.Append(tree.GetMod(row.Index).Id);
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        [Test]
        public void Build_FlatLegacyCategories_LoadAsOneLevel()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("clicker", "Samples"),
                Mod("controls", "System"),
                Mod("racer", "Samples", false),
                Mod("loose", "")
            });

            Assert.AreEqual(3, tree.FolderCount);
            Assert.AreEqual(
                "[Samples] 1/2\n" +
                "  clicker\n" +
                "  racer\n" +
                "[System] 1/1\n" +
                "  controls\n" +
                "loose\n",
                Render(tree));
            Assert.IsTrue(tree.TryFindFolder("Samples", out int samples));
            Assert.AreEqual(1, tree.GetFolder(samples).Depth);
            Assert.AreEqual(0, tree.GetFolder(samples).SubfolderCount);
        }

        [Test]
        public void Build_NestedPaths_CountsEveryBranch()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("a", "Games/Arcade"),
                Mod("b", "Games/Arcade/Retro", false),
                Mod("c", "Games/Puzzle"),
                Mod("d", "Games")
            });

            Assert.IsTrue(tree.TryFindFolder("Games", out int games));
            ModFolderNode node = tree.GetFolder(games);
            Assert.AreEqual(4, node.ModCount);
            Assert.AreEqual(3, node.EnabledModCount);
            Assert.AreEqual(1, node.DirectModCount);
            Assert.AreEqual(2, node.SubfolderCount);
            Assert.AreEqual(4, tree.Root.ModCount);
            Assert.AreEqual(
                "[Games] 3/4\n" +
                "  [Arcade] 1/2\n" +
                "    [Retro] 0/1\n" +
                "      b\n" +
                "    a\n" +
                "  [Puzzle] 1/1\n" +
                "    c\n" +
                "  d\n",
                Render(tree));
        }

        [Test]
        public void Build_DeclaredEmptyFolders_ShowWithoutMods()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "Games") },
                new[] { "Games/Empty", "Later" });

            Assert.AreEqual("[Games] 1/1\n  [Empty] empty\n  a\n[Later] empty\n", Render(tree));
            Assert.IsTrue(tree.TryFindFolder("Later", out int later));
            Assert.IsTrue(tree.GetFolder(later).IsDeclared);
            Assert.IsTrue(tree.GetFolder(later).IsEmpty);
            Assert.IsTrue(tree.TryFindFolder("Games", out int games));
            Assert.IsFalse(tree.GetFolder(games).IsDeclared);
        }

        [Test]
        public void Build_CaseVariants_AreOneFolderInTheFirstCasing()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("a", "Games/Arcade"),
                Mod("b", "games/ARCADE/new"),
                Mod("c", " GAMES / arcade ")
            });

            Assert.AreEqual(4, tree.FolderCount);
            Assert.IsTrue(tree.TryFindFolder("GAMES/ARCADE/NEW", out int folder));
            Assert.AreEqual("Games/Arcade/new", tree.GetFolder(folder).Path.Value);
        }

        [Test]
        public void Build_RepeatedOrBlankIds_KeepTheFirstEntry()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("a", "One"),
                Mod("a", "Two"),
                Mod("  ", "Three"),
                null
            });

            Assert.AreEqual(1, tree.ModCount);
            Assert.IsTrue(tree.TryFindMod("a", out int index));
            Assert.AreEqual("One", tree.GetMod(index).Folder);
            Assert.IsFalse(tree.TryFindFolder("Two", out _));
        }

        [Test]
        public void Build_SortsFoldersThenModsByNameThenId()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("z2", "", name: "Same"),
                Mod("z1", "", name: "Same"),
                Mod("m", "", name: "alpha"),
                Mod("x", "beta"),
                Mod("y", "Alpha")
            });

            Assert.AreEqual("[Alpha] 1/1\n  y\n[beta] 1/1\n  x\nm\nz1\nz2\n", Render(tree));
        }

        [Test]
        public void Build_VeryDeepLegacyPath_DoesNotRecurse()
        {
            StringBuilder deep = new();
            for (int i = 0; i < 2000; i++)
            {
                deep.Append(i == 0 ? "" : "/").Append('n');
            }

            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("deep", deep.ToString()) });

            Assert.AreEqual(2001, tree.FolderCount);
            Assert.AreEqual(2001, tree.VisibleRowCount);
            Assert.AreEqual(1999, tree.GetVisibleRow(1999).Depth);
            Assert.AreEqual(ModFolderRowKind.Mod, tree.GetVisibleRow(2000).Kind);
            Assert.AreEqual(2000, tree.GetVisibleRow(2000).Depth);
        }

        [Test]
        public void Fold_ClosedFolder_HidesItsBranchAndIsRemembered()
        {
            List<ModFolderModEntry> mods = new() { Mod("a", "Games/Arcade"), Mod("b", "Tools") };
            ModFolderFoldState fold = new();
            ModFolderTree tree = ModFolderTree.Build(mods, null, fold);
            Assert.IsTrue(tree.TryFindFolder("Games", out int games));

            tree.SetExpanded(games, false);

            Assert.AreEqual("[Games] 1/1\n[Tools] 1/1\n  b\n", Render(tree));
            Assert.IsFalse(fold.IsExpanded(ModFolderPath.FromStored("games")));
            ModFolderTree rebuilt = ModFolderTree.Build(mods, null, fold);
            Assert.AreEqual(Render(tree), Render(rebuilt));
        }

        [Test]
        public void Fold_CollapsedByDefault_ShowsTopLevelOnly()
        {
            ModFolderTree tree = ModFolderTree.Build(
                new List<ModFolderModEntry> { Mod("a", "Games/Arcade"), Mod("r", "") },
                null, new ModFolderFoldState(false));

            Assert.AreEqual("[Games] 1/1\nr\n", Render(tree));
        }

        [Test]
        public void Fold_Reveal_OpensEveryAncestor()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "A/B/C") }, null,
                new ModFolderFoldState(false));
            Assert.IsTrue(tree.TryFindFolder("A/B/C", out int c));
            Assert.AreEqual(-1, tree.FindVisibleRow(ModFolderRowKind.Folder, c));

            tree.Reveal(c);

            Assert.AreEqual(2, tree.FindVisibleRow(ModFolderRowKind.Folder, c));
        }

        [Test]
        public void Search_MatchesNameIdTagsAndPath_AndKeepsMatchingBranchesOpen()
        {
            List<ModFolderModEntry> mods = new()
            {
                Mod("racer", "Games/Arcade", name: "Lane Racer"),
                Mod("tetris", "Games/Puzzle", tags: "blocks, arcade-like"),
                Mod("clock", "Tools"),
                Mod("hud", "Tools/Arcade HUD")
            };
            ModFolderFoldState fold = new(false);
            ModFolderTree tree = ModFolderTree.Build(mods, null, fold);

            tree.SetSearch("arcade");

            Assert.AreEqual(
                "[Games] 2 found\n" +
                "  [Arcade] 1 found\n" +
                "    racer\n" +
                "  [Puzzle] 1 found\n" +
                "    tetris\n" +
                "[Tools] 1 found\n" +
                "  [Arcade HUD] 1 found\n" +
                "    hud\n",
                Render(tree));
            Assert.IsFalse(tree.IsModVisible(2));
        }

        [Test]
        public void Search_NoMatch_ShowsNothing_AndClearingRestoresTheFoldState()
        {
            ModFolderFoldState fold = new(false);
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "Games") }, null, fold);
            string before = Render(tree);

            tree.SetSearch("zzz");
            Assert.AreEqual(0, tree.VisibleRowCount);

            tree.SetSearch("  ");
            Assert.AreEqual(before, Render(tree));
            Assert.IsFalse(tree.IsSearching);
        }

        [Test]
        public void Search_EmptyFolderMatchingByName_IsShown()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "Games") },
                new[] { "Archive/Old" });

            tree.SetSearch("old");

            Assert.AreEqual("[Archive] 0 found\n  [Old] 0 found\n", Render(tree));
        }

        [Test]
        public void Search_FoldDuringSearch_LastsUntilTheSearchChanges_AndIsNotRemembered()
        {
            ModFolderFoldState fold = new();
            ModFolderTree tree = ModFolderTree.Build(
                new List<ModFolderModEntry> { Mod("a", "Games/Arcade"), Mod("b", "Games/Puzzle") }, null, fold);
            tree.SetSearch("games");
            Assert.IsTrue(tree.TryFindFolder("Games/Arcade", out int arcade));

            tree.SetExpanded(arcade, false);

            Assert.IsFalse(tree.IsExpanded(arcade));
            Assert.AreEqual(0, fold.Version);
            tree.SetSearch("game");
            Assert.IsTrue(tree.IsExpanded(arcade));
        }

        [Test]
        public void SetAllExpanded_ClosesEveryFolder()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                Mod("a", "A/B"), Mod("c", "C"), Mod("r", "")
            });

            tree.SetAllExpanded(false);

            Assert.AreEqual("[A] 1/1\n[C] 1/1\nr\n", Render(tree));
            tree.SetAllExpanded(true);
            Assert.AreEqual(6, tree.VisibleRowCount);
        }

        [Test]
        public void Version_GrowsOnlyWhenRowsChange()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "A") });
            int version = tree.Version;

            tree.SetSearch("");
            tree.SetExpanded(0, false);
            Assert.AreEqual(version, tree.Version);

            tree.SetSearch("a");
            Assert.Greater(tree.Version, version);
        }

        [Test]
        public void GetFolderPaths_ListsEveryFolderSorted()
        {
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry> { Mod("a", "b/c"), Mod("d", "A") },
                new[] { "Z" });

            CollectionAssert.AreEqual(new[] { "A", "b", "b/c", "Z" }, tree.GetFolderPaths());
        }

        [Test]
        public void Payload_TravelsThroughUnchanged()
        {
            object record = new object();
            ModFolderTree tree = ModFolderTree.Build(new List<ModFolderModEntry>
            {
                new ModFolderModEntry("a", "A", "Games", "", true, 3, record)
            });

            Assert.AreSame(record, tree.GetMod(0).Payload);
            Assert.AreEqual(3, tree.GetMod(0).LoadOrder);
        }
    }
}

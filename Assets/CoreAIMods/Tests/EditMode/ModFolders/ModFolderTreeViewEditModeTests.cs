using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CoreAI.Mods.Folders;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// The UI Toolkit tree view over the folder model: what the virtualized list is given, what a bound row
    /// shows, and which requests the rows raise. Elements are detached (no panel), so the tests drive the row
    /// handlers directly instead of relying on change events.
    /// </summary>
    [TestFixture]
    public sealed class ModFolderTreeViewEditModeTests
    {
        private static ModFolderTree SmallTree(ModFolderFoldState fold = null)
        {
            return ModFolderTree.Build(new List<ModFolderModEntry>
            {
                new("racer", "Lane Racer", "Games/Arcade", "", true),
                new("retro", "retro", "Games/Arcade/Retro", "", false),
                new("tetris", "Tetris", "Games/Puzzle", "blocks", true),
                new("clock", "Clock", "Tools", "", true)
            }, new[] { "Empty" }, fold);
        }

        private static (ModFolderTreeView View, ModFolderTreeView.RowElements Row, VisualElement Element) ViewWithRow(
            ModFolderTree tree)
        {
            ModFolderTreeView view = new();
            view.SetTree(tree);
            VisualElement element = view.MakeRow();
            return (view, (ModFolderTreeView.RowElements)element.userData, element);
        }

        private static int RowOf(ModFolderTree tree, string folderPath)
        {
            Assert.IsTrue(tree.TryFindFolder(folderPath, out int folder), folderPath);
            return tree.FindVisibleRow(ModFolderRowKind.Folder, folder);
        }

        private static int RowOfMod(ModFolderTree tree, string id)
        {
            Assert.IsTrue(tree.TryFindMod(id, out int mod), id);
            return tree.FindVisibleRow(ModFolderRowKind.Mod, mod);
        }

        private static bool Shown(VisualElement element)
        {
            return element.style.display.value != DisplayStyle.None;
        }

        [Test]
        public void SetTree_ThousandNestedMods_GivesTheListOnlyTheVisibleRows()
        {
            ModFolderTree tree = ModFolderTree.Build(ModFolderTreePerformanceEditModeTests.NestedMods(1000, 5), null,
                new ModFolderFoldState(false));
            ModFolderTreeView view = new();

            view.SetTree(tree);

            Assert.AreEqual(10, tree.VisibleRowCount);
            Assert.AreEqual(10, view.ListView.itemsSource.Count);
            Assert.AreEqual(CollectionVirtualizationMethod.FixedHeight, view.ListView.virtualizationMethod);
        }

        [Test]
        public void Opening_ThousandNestedMods_StaysUnder100Milliseconds()
        {
            List<ModFolderModEntry> mods = ModFolderTreePerformanceEditModeTests.NestedMods(1000, 5);
            Stopwatch watch = Stopwatch.StartNew();

            ModFolderTree tree = ModFolderTree.Build(mods);
            ModFolderTreeView view = new();
            view.SetTree(tree);
            for (int i = 0; i < 40; i++)
            {
                view.BindRow(view.MakeRow(), i);
            }

            watch.Stop();
            TestContext.Progress.WriteLine("mod-folders view open @1000 mods (build + view + 40 rows made and bound): " +
                                           watch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                                           " ms");
            Assert.Less(watch.Elapsed.TotalMilliseconds, 100.0);
        }

        [Test]
        public void BindRow_PartlyEnabledFolder_ShowsNameCountsArrowAndAMixedToggle()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);

            view.BindRow(element, RowOf(tree, "Games/Arcade"));

            Assert.AreEqual("Arcade", row.Name.text);
            Assert.AreEqual("1/2", row.Detail.text);
            Assert.AreEqual("▾", row.Fold.text);
            Assert.IsTrue(row.Toggle.showMixedValue);
            Assert.IsFalse(row.Toggle.value);
            Assert.AreEqual(16f, row.Indent.style.width.value.value);
            Assert.IsTrue(element.ClassListContains(ModFolderTreeView.FolderRowUssClassName));
            Assert.IsTrue(Shown(row.Move));
            Assert.IsFalse(Shown(row.Here));
            Assert.IsTrue(Shown(row.FolderActions[0]));
            Assert.IsFalse(Shown(row.ModActions[0]));
        }

        [Test]
        public void BindRow_EmptyFolder_HasNoArrowAndADisabledToggle()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);

            view.BindRow(element, RowOf(tree, "Empty"));

            Assert.AreEqual("empty", row.Detail.text);
            Assert.AreEqual(Visibility.Hidden, row.Fold.style.visibility.value);
            Assert.IsFalse(row.Toggle.enabledSelf);
        }

        [Test]
        public void BindRow_RecycledFromFolderToMod_ShowsTheModOnly()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);
            view.BindRow(element, RowOf(tree, "Games"));

            view.BindRow(element, RowOfMod(tree, "racer"));

            Assert.AreEqual("Lane Racer", row.Name.text);
            Assert.AreEqual("racer", row.Detail.text);
            Assert.IsTrue(row.Toggle.value);
            Assert.IsFalse(row.Toggle.showMixedValue);
            Assert.IsTrue(element.ClassListContains(ModFolderTreeView.ModRowUssClassName));
            Assert.IsFalse(element.ClassListContains(ModFolderTreeView.FolderRowUssClassName));
            Assert.IsTrue(Shown(row.ModActions[0]));
            Assert.IsFalse(Shown(row.FolderActions[0]));
            Assert.AreEqual(Visibility.Hidden, row.Fold.style.visibility.value);
        }

        [Test]
        public void FoldClick_ClosesTheFolder_UpdatesTheList_AndRaisesFoldChanged()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);
            int raised = 0;
            view.FoldChanged += () => raised++;
            int before = view.ListView.itemsSource.Count;
            view.BindRow(element, RowOf(tree, "Games"));

            view.OnFoldClicked(row);

            Assert.AreEqual(1, raised);
            Assert.Less(view.ListView.itemsSource.Count, before);
            Assert.IsFalse(tree.FoldState.IsExpanded(ModFolderPath.FromStored("Games")));
        }

        [Test]
        public void Toggles_RaiseBranchAndModRequests()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);
            List<string> requests = new();
            view.BranchEnableRequested += (node, on) => requests.Add("branch " + node.Path.Value + " " + on);
            view.ModEnableRequested += (mod, on) => requests.Add("mod " + mod.Id + " " + on);

            view.BindRow(element, RowOf(tree, "Games"));
            view.OnToggleChanged(row, false);
            view.BindRow(element, RowOfMod(tree, "clock"));
            view.OnToggleChanged(row, false);

            CollectionAssert.AreEqual(new[] { "branch Games False", "mod clock False" }, requests);
        }

        [Test]
        public void MoveFolder_HereShowsOnValidTargetsOnly_AndTheChoiceIsRaised()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);
            List<ModFolderMoveRequest> requests = new();
            view.MoveRequested += requests.Add;
            view.BindRow(element, RowOf(tree, "Games/Arcade"));

            view.OnMoveClicked(row);

            Assert.IsTrue(view.IsMoving);
            view.BindRow(element, RowOf(tree, "Games/Arcade"));
            Assert.IsFalse(Shown(row.Here), "a folder cannot move into itself");
            view.BindRow(element, RowOf(tree, "Games/Arcade/Retro"));
            Assert.IsFalse(Shown(row.Here), "a folder cannot move into its own subfolder");
            view.BindRow(element, RowOf(tree, "Games"));
            Assert.IsFalse(Shown(row.Here), "it is already in Games");
            view.BindRow(element, RowOf(tree, "Tools"));
            Assert.IsTrue(Shown(row.Here));
            Assert.IsFalse(Shown(row.Move));

            view.OnHereClicked(row);

            Assert.IsFalse(view.IsMoving);
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(ModFolderRowKind.Folder, requests[0].Kind);
            Assert.AreEqual("Games/Arcade", requests[0].Folder.Value);
            Assert.AreEqual("Tools", requests[0].Target.Value);
        }

        [Test]
        public void CompleteMoveTo_IntoTheMovingFolder_IsRefusedAndStaysInMoveMode()
        {
            ModFolderTree tree = SmallTree();
            ModFolderTreeView view = new();
            view.SetTree(tree);
            int raised = 0;
            view.MoveRequested += _ => raised++;
            view.BeginMoveFolder(ModFolderPath.FromStored("Games"));

            Assert.IsFalse(view.CompleteMoveTo(ModFolderPath.FromStored("Games/Puzzle")));

            Assert.IsTrue(view.IsMoving);
            Assert.AreEqual(0, raised);
            Assert.IsTrue(view.CompleteMoveTo(ModFolderPath.Root));
            Assert.AreEqual(1, raised);
        }

        [Test]
        public void MoveMod_ToTheTopLevel_IsRaisedWithTheModId()
        {
            ModFolderTreeView view = new();
            view.SetTree(SmallTree());
            ModFolderMoveRequest request = default;
            view.MoveRequested += value => request = value;

            view.BeginMoveMod("tetris");
            view.CompleteMoveTo(ModFolderPath.Root);

            Assert.AreEqual(ModFolderRowKind.Mod, request.Kind);
            Assert.AreEqual("tetris", request.ModId);
            Assert.IsTrue(request.Target.IsRoot);
        }

        [Test]
        public void SetTree_WithoutTheMovingItem_EndsMoveMode()
        {
            ModFolderTreeView view = new();
            view.SetTree(SmallTree());
            view.BeginMoveMod("tetris");

            view.SetTree(ModFolderTree.Build(new List<ModFolderModEntry> { new("other", "Other", "", "", true) }));

            Assert.IsFalse(view.IsMoving);
        }

        [Test]
        public void SetSearch_FiltersTheRows_AndANoMatchShowsTheNote()
        {
            ModFolderTreeView view = new();
            view.SetTree(SmallTree());

            view.SetSearch("blocks");
            Assert.AreEqual(3, view.ListView.itemsSource.Count);

            view.SetSearch("nothing-matches");
            Assert.AreEqual(0, view.ListView.itemsSource.Count);
            Assert.AreEqual(DisplayStyle.None, view.ListView.style.display.value);

            view.SetTree(SmallTree());
            Assert.AreEqual("nothing-matches", view.Tree.Search, "a new tree keeps the applied search");
        }

        [Test]
        public void HostActions_RaiseTheirLabels()
        {
            ModFolderTree tree = SmallTree();
            (ModFolderTreeView view, ModFolderTreeView.RowElements row, VisualElement element) = ViewWithRow(tree);
            List<string> actions = new();
            view.ModActionRequested += (mod, action) => actions.Add(action + " " + mod.Id);
            view.FolderActionRequested += (node, action) => actions.Add(action + " " + node.Path.Value);

            view.BindRow(element, RowOfMod(tree, "clock"));
            view.OnModAction(row, "Edit");
            view.BindRow(element, RowOf(tree, "Tools"));
            view.OnFolderAction(row, "Delete");

            CollectionAssert.AreEqual(new[] { "Edit clock", "Delete Tools" }, actions);
        }

        [Test]
        public void ModDetailAndActionFilter_ShapeTheModRow()
        {
            ModFolderTree tree = SmallTree();
            ModFolderTreeView view = new(new ModFolderTreeViewOptions
            {
                ModActions = new[] { "Edit", "Update" },
                ModActionFilter = (mod, action) => action != "Update" || mod.Id == "clock",
                ModDetail = mod => mod.Enabled ? "loaded" : "stored"
            });
            view.SetTree(tree);
            VisualElement element = view.MakeRow();
            ModFolderTreeView.RowElements row = (ModFolderTreeView.RowElements)element.userData;

            view.BindRow(element, RowOfMod(tree, "racer"));
            Assert.AreEqual("loaded", row.Detail.text);
            Assert.IsTrue(Shown(row.ModActions[0]));
            Assert.IsFalse(Shown(row.ModActions[1]));

            view.BindRow(element, RowOfMod(tree, "clock"));
            Assert.IsTrue(Shown(row.ModActions[1]));
            view.BindRow(element, RowOfMod(tree, "retro"));
            Assert.AreEqual("stored", row.Detail.text);
        }

        [Test]
        public void NamePrompt_OkHandsBackTheTypedText_AndCancelDoesNot()
        {
            ModFolderTreeView view = new();
            view.SetTree(SmallTree());
            List<string> accepted = new();

            view.BeginNamePrompt("Rename folder", "Tools", accepted.Add);
            Assert.IsTrue(view.IsPrompting);
            Assert.AreEqual("Tools", view.PromptField.value);
            view.PromptField.SetValueWithoutNotify("Utilities");
            view.AcceptPrompt();

            view.BeginNamePrompt("New folder", "", accepted.Add);
            view.CancelPrompt();
            view.AcceptPrompt();

            Assert.IsFalse(view.IsPrompting);
            CollectionAssert.AreEqual(new[] { "Utilities" }, accepted);
        }
    }
}

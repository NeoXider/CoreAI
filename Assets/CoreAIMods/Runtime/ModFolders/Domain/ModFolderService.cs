using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// The folder operations over an <see cref="IModFolderStore"/>: create, rename, move a folder, move a mod,
    /// delete an empty folder, and switch a whole branch on or off. Every operation reads a fresh snapshot of
    /// the store, checks every rule before it writes anything, and answers a <see cref="ModFolderResult"/>
    /// with a named error instead of throwing (only <see cref="OperationCanceledException"/> propagates).
    /// <para>
    /// Rules shared by all operations: target paths are read strictly (<see cref="ModFolderPath.Parse"/>),
    /// source paths leniently (so a legacy folder can be renamed or moved); folder names compare
    /// case-insensitively and a target reuses the casing of folders that already exist; an operation never
    /// makes the folder it moved out of disappear (an emptied folder is kept as a persisted folder), and
    /// only <see cref="DeleteFolder"/> removes a folder.
    /// </para>
    /// <para>
    /// A folder move writes the persisted folders as the union of the old and new state first, then moves
    /// the mods one by one, then writes the final folders; a failed mod write moves the already moved mods
    /// back and restores the folders, so an interrupted move leaves both folders visible and loses nothing.
    /// </para>
    /// <para>Not thread-safe: the caller serializes operations (the Hub's UI thread, the tool's lock).</para>
    /// </summary>
    public sealed class ModFolderService
    {
        private readonly IModFolderStore _store;

        /// <param name="store">The store the operations read and write.</param>
        public ModFolderService(IModFolderStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>Builds the tree of the store's current mods and persisted folders.</summary>
        public ModFolderTree BuildTree(ModFolderFoldState foldState = null)
        {
            return ModFolderTree.Build(_store.ListMods(), _store.LoadFolders(), foldState);
        }

        /// <summary>
        /// Reads a folder someone wants to write into (for example the folder of a new mod) strictly and
        /// returns it in the casing of the folders that already exist. <see cref="ModFolderResult.CreatedFolder"/>
        /// is true when no such folder exists yet. Writes nothing.
        /// </summary>
        public ModFolderResult ResolveFolder(string path)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(path);
            if (!parse.Succeeded)
            {
                return ModFolderResult.Failure(parse.Error, parse.Message);
            }

            if (!TryTakeSnapshot(out Snapshot snapshot, out ModFolderResult failure))
            {
                return failure;
            }

            ModFolderPath target = snapshot.Canonicalize(parse.Path);
            bool exists = snapshot.Tree.TryFindFolder(target.Value, out _);
            return ModFolderResult.Success("Folder '" + Display(target) + "'" + (exists ? " exists." : " does not exist yet."),
                target, ModFolderPath.Root, 0, false, !exists);
        }

        /// <summary>Creates an empty folder (and any missing ancestor). Fails when it already exists.</summary>
        public ModFolderResult CreateFolder(string path)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(path);
            if (!parse.Succeeded)
            {
                return ModFolderResult.Failure(parse.Error, parse.Message);
            }

            if (parse.Path.IsRoot)
            {
                return ModFolderResult.Failure(ModFolderErrorCode.RootNotAllowed,
                    "The top level always exists; name the folder to create.");
            }

            if (!TryTakeSnapshot(out Snapshot snapshot, out ModFolderResult failure))
            {
                return failure;
            }

            if (snapshot.Tree.TryFindFolder(parse.Path.Value, out int existing))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.FolderAlreadyExists,
                    "A folder '" + snapshot.Tree.GetFolder(existing).Path.Value + "' already exists.");
            }

            ModFolderPath target = snapshot.Canonicalize(parse.Path);
            List<string> declared = new(snapshot.Declared) { target.Value };
            if (!TrySaveFolders(declared, out string error))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Folder '" + target.Value + "' was not created: " + error);
            }

            return ModFolderResult.Success("Created folder '" + target.Value + "'.", target, ModFolderPath.Root, 0, true,
                true);
        }

        /// <summary>Renames a folder in place; <paramref name="newName"/> is one segment. A case-only rename is allowed.</summary>
        public ModFolderResult RenameFolder(string path, string newName)
        {
            if (!TryFindSourceFolder(path, out Snapshot snapshot, out ModFolderNode node, out ModFolderResult failure))
            {
                return failure;
            }

            ModFolderPathParse name = ModFolderPath.ParseName(newName);
            if (!name.Succeeded)
            {
                return ModFolderResult.Failure(name.Error, name.Message);
            }

            ModFolderPath parent = snapshot.Tree.GetFolder(node.ParentIndex).Path;
            return MoveBranch(snapshot, node, parent.Child(name.Path.Value), "Renamed");
        }

        /// <summary>
        /// Moves a folder with everything in it under <paramref name="newParentPath"/> (empty is the top level),
        /// keeping its name. The new parent is created when missing.
        /// </summary>
        public ModFolderResult MoveFolder(string path, string newParentPath)
        {
            if (!TryFindSourceFolder(path, out Snapshot snapshot, out ModFolderNode node, out ModFolderResult failure))
            {
                return failure;
            }

            ModFolderPathParse parse = ModFolderPath.Parse(newParentPath);
            if (!parse.Succeeded)
            {
                return ModFolderResult.Failure(parse.Error, parse.Message);
            }

            ModFolderPath parent = snapshot.Canonicalize(parse.Path);
            if (parent.IsSameOrDescendantOf(node.Path))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.MoveIntoItself,
                    "Folder '" + node.Path.Value + "' cannot move into " +
                    (parent == node.Path ? "itself." : "its own subfolder '" + parent.Value + "'."));
            }

            return MoveBranch(snapshot, node, parent.Child(node.Name), "Moved");
        }

        /// <summary>
        /// Moves one mod into <paramref name="folderPath"/> (empty is the top level). The folder is created
        /// when missing; the folder the mod leaves is kept even when it becomes empty.
        /// </summary>
        public ModFolderResult MoveMod(string modId, string folderPath)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(folderPath);
            if (!parse.Succeeded)
            {
                return ModFolderResult.Failure(parse.Error, parse.Message);
            }

            if (!TryTakeSnapshot(out Snapshot snapshot, out ModFolderResult failure))
            {
                return failure;
            }

            if (!snapshot.Tree.TryFindMod(modId, out int modIndex))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.ModNotFound,
                    "No mod with id '" + (modId ?? "").Trim() + "' exists.");
            }

            ModFolderModEntry mod = snapshot.Tree.GetMod(modIndex);
            ModFolderNode oldFolder = snapshot.Tree.GetFolder(snapshot.Tree.GetModFolderIndex(modIndex));
            ModFolderPath target = snapshot.Canonicalize(parse.Path);
            if (string.Equals(mod.Folder, target.Value, StringComparison.Ordinal))
            {
                return ModFolderResult.Success("Mod '" + mod.Id + "' is already in " + Describe(target) + ".", target,
                    oldFolder.Path, 0, false, false);
            }

            bool created = !snapshot.Tree.TryFindFolder(target.Value, out _);
            List<string> declaredAfter = new(snapshot.Declared);
            ModFolderPath oldPath = oldFolder.Path;
            if (!oldPath.IsRoot && oldFolder.ModCount == 1 && !target.IsSameOrDescendantOf(oldPath) &&
                !snapshot.HasDeclaredWithin(oldPath))
            {
                declaredAfter.Add(oldPath.Value);
            }

            bool foldersChanged = declaredAfter.Count != snapshot.Declared.Count;
            if (foldersChanged && !TrySaveFolders(declaredAfter, out string saveError))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Mod '" + mod.Id + "' was not moved: the folder list could not be saved: " + saveError);
            }

            if (!TrySetModFolder(mod.Id, target.Value, out string moveError))
            {
                string restore = foldersChanged && !TrySaveFolders(snapshot.Declared, out string restoreError)
                    ? " Restoring the folder list failed too: " + restoreError
                    : "";
                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Mod '" + mod.Id + "' was not moved to " + Describe(target) + ": " + moveError + restore);
            }

            return ModFolderResult.Success("Moved mod '" + mod.Id + "' from " + Describe(oldPath) + " to " +
                                           Describe(target) + ".", target, oldPath, 1, true, created);
        }

        /// <summary>
        /// Deletes a folder whose branch holds no mod, with its empty subfolders. Fails with
        /// <see cref="ModFolderErrorCode.FolderNotEmpty"/> while any mod is inside.
        /// </summary>
        public ModFolderResult DeleteFolder(string path)
        {
            if (!TryFindSourceFolder(path, out Snapshot snapshot, out ModFolderNode node, out ModFolderResult failure))
            {
                return failure;
            }

            if (node.ModCount > 0)
            {
                return ModFolderResult.Failure(ModFolderErrorCode.FolderNotEmpty,
                    "Folder '" + node.Path.Value + "' holds " + node.ModCount.ToString(CultureInfo.InvariantCulture) +
                    " mod(s); move or delete them first.");
            }

            List<string> declaredAfter = new();
            foreach (string declared in snapshot.Declared)
            {
                if (!ModFolderPath.FromNormalized(declared).IsSameOrDescendantOf(node.Path))
                {
                    declaredAfter.Add(declared);
                }
            }

            KeepParentIfEmptied(snapshot, node.Path.Parent, declaredAfter, 0);
            if (!TrySaveFolders(declaredAfter, out string error))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Folder '" + node.Path.Value + "' was not deleted: " + error);
            }

            return ModFolderResult.Success("Deleted folder '" + node.Path.Value + "'.", ModFolderPath.Root, node.Path, 0,
                true, false);
        }

        /// <summary>
        /// Switches every mod in the branch at <paramref name="path"/> on or off (the top level is every mod).
        /// Mods outside the branch are never touched; a sibling folder whose name merely starts the same
        /// (<c>GamesOld</c> next to <c>Games</c>) is outside. Mods start in their recorded load order and stop
        /// in reverse. A mod that fails is reported in <see cref="ModFolderResult.FailedModIds"/> and the
        /// rest still switch.
        /// </summary>
        public ModFolderResult SetBranchEnabled(string path, bool enabled)
        {
            if (!TryTakeSnapshot(out Snapshot snapshot, out ModFolderResult failure))
            {
                return failure;
            }

            ModFolderPath branch = ModFolderPath.FromStored(path);
            if (!snapshot.Tree.TryFindFolder(branch.Value, out int folderIndex))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.FolderNotFound,
                    "No folder '" + branch.Value + "' exists.");
            }

            ModFolderNode node = snapshot.Tree.GetFolder(folderIndex);
            List<ModFolderModEntry> targets = new();
            for (int i = 0; i < snapshot.Tree.ModCount; i++)
            {
                ModFolderModEntry mod = snapshot.Tree.GetMod(i);
                if (mod.Enabled != enabled &&
                    snapshot.Tree.GetFolder(snapshot.Tree.GetModFolderIndex(i)).Path.IsSameOrDescendantOf(node.Path))
                {
                    targets.Add(mod);
                }
            }

            targets.Sort(CompareStartOrder);
            if (!enabled)
            {
                targets.Reverse();
            }

            int changed = 0;
            List<string> failedIds = null;
            List<string> failures = null;
            foreach (ModFolderModEntry mod in targets)
            {
                try
                {
                    _store.SetModEnabled(mod.Id, enabled);
                    changed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    (failedIds ??= new List<string>()).Add(mod.Id);
                    (failures ??= new List<string>()).Add(mod.Id + ": " + ex.Message);
                }
            }

            string verb = enabled ? "on" : "off";
            if (failedIds != null)
            {
                return ModFolderResult.Partial(ModFolderErrorCode.StoreFailed,
                    "Switched " + changed.ToString(CultureInfo.InvariantCulture) + " mod(s) in " + Describe(node.Path) +
                    " " + verb + "; " + failedIds.Count.ToString(CultureInfo.InvariantCulture) + " failed: " +
                    string.Join("; ", failures), node.Path, changed, failedIds);
            }

            return ModFolderResult.Success("Switched " + changed.ToString(CultureInfo.InvariantCulture) + " mod(s) in " +
                                           Describe(node.Path) + " " + verb + ".", node.Path, ModFolderPath.Root, changed,
                changed > 0, false);
        }

        private ModFolderResult MoveBranch(Snapshot snapshot, ModFolderNode node, ModFolderPath target, string verb)
        {
            ModFolderPath source = node.Path;
            if (string.Equals(source.Value, target.Value, StringComparison.Ordinal))
            {
                return ModFolderResult.Success("Folder '" + source.Value + "' is already there.", source, source, 0, false,
                    false);
            }

            ModFolderPathParse targetRules = ModFolderPath.Parse(target.Value);
            if (!targetRules.Succeeded)
            {
                return ModFolderResult.Failure(targetRules.Error,
                    targetRules.Message + " Rename the folder to a valid name first.");
            }

            if (target != source && snapshot.Tree.TryFindFolder(target.Value, out int collision))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.FolderAlreadyExists,
                    "A folder '" + snapshot.Tree.GetFolder(collision).Path.Value + "' already exists; " +
                    (verb == "Renamed" ? "pick another name." : "rename one of them first."));
            }

            for (int i = 1; i < snapshot.Tree.FolderCount; i++)
            {
                ModFolderPath path = snapshot.Tree.GetFolder(i).Path;
                if (path.IsSameOrDescendantOf(source))
                {
                    ModFolderPathParse limits = ModFolderPath.CheckLimits(path.RebaseOnto(source, target));
                    if (!limits.Succeeded)
                    {
                        return ModFolderResult.Failure(limits.Error, limits.Message);
                    }
                }
            }

            List<PlannedMove> moves = new();
            for (int i = 0; i < snapshot.Tree.ModCount; i++)
            {
                ModFolderPath current = snapshot.Tree.GetFolder(snapshot.Tree.GetModFolderIndex(i)).Path;
                if (current.IsSameOrDescendantOf(source))
                {
                    ModFolderModEntry mod = snapshot.Tree.GetMod(i);
                    moves.Add(new PlannedMove(mod.Id, current.Value, current.RebaseOnto(source, target).Value));
                }
            }

            List<string> declaredAfter = new(snapshot.Declared.Count + 1);
            foreach (string declared in snapshot.Declared)
            {
                ModFolderPath path = ModFolderPath.FromNormalized(declared);
                declaredAfter.Add(path.IsSameOrDescendantOf(source) ? path.RebaseOnto(source, target).Value : declared);
            }

            if (moves.Count == 0 && !snapshot.HasDeclaredWithin(source))
            {
                declaredAfter.Add(target.Value);
            }

            ModFolderPath oldParent = source.Parent;
            if (!target.IsSameOrDescendantOf(oldParent))
            {
                KeepParentIfEmptied(snapshot, oldParent, declaredAfter, node.ModCount);
            }

            List<string> union = new(snapshot.Declared);
            union.AddRange(declaredAfter);
            bool foldersChanged = !SameFolders(snapshot.Declared, declaredAfter);
            if (foldersChanged && !TrySaveFolders(union, out string unionError))
            {
                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Folder '" + source.Value + "' was not moved: the folder list could not be saved: " + unionError);
            }

            for (int i = 0; i < moves.Count; i++)
            {
                if (TrySetModFolder(moves[i].ModId, moves[i].NewFolder, out string moveError))
                {
                    continue;
                }

                string rollback = RollBack(moves, i);
                if (foldersChanged && !TrySaveFolders(snapshot.Declared, out string restoreError))
                {
                    rollback += " Restoring the folder list failed too: " + restoreError;
                }

                return ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "Folder '" + source.Value + "' was not moved: mod '" + moves[i].ModId + "' could not be moved: " +
                    moveError + rollback);
            }

            string warning = "";
            if (foldersChanged && !TrySaveFolders(declaredAfter, out string finalError))
            {
                warning = "The folder list keeps the old folder entries until the next save: " + finalError;
            }

            return ModFolderResult.Success(verb + " folder '" + source.Value + "' to '" + target.Value + "' (" +
                                           moves.Count.ToString(CultureInfo.InvariantCulture) + " mod(s)).", target, source,
                moves.Count, true, false, warning);
        }

        private string RollBack(List<PlannedMove> moves, int failedIndex)
        {
            List<string> stuck = null;
            for (int i = failedIndex - 1; i >= 0; i--)
            {
                if (!TrySetModFolder(moves[i].ModId, moves[i].OldFolder, out _))
                {
                    (stuck ??= new List<string>()).Add(moves[i].ModId);
                }
            }

            return stuck == null
                ? (failedIndex == 0 ? "" : " The " + failedIndex.ToString(CultureInfo.InvariantCulture) +
                                           " mod(s) already moved were moved back.")
                : " These mods stay in the new folder because moving them back failed too: " + string.Join(", ", stuck) + ".";
        }

        /// <summary>
        /// Adds <paramref name="parent"/> to the persisted folders when the operation would leave it without
        /// any mod or persisted folder inside, so a move or delete never removes the folder next to it.
        /// </summary>
        private static void KeepParentIfEmptied(Snapshot snapshot, ModFolderPath parent, List<string> declaredAfter,
            int modsLeaving)
        {
            if (parent.IsRoot || !snapshot.Tree.TryFindFolder(parent.Value, out int parentIndex))
            {
                return;
            }

            if (snapshot.Tree.GetFolder(parentIndex).ModCount - modsLeaving > 0)
            {
                return;
            }

            foreach (string declared in declaredAfter)
            {
                if (ModFolderPath.FromNormalized(declared).IsSameOrDescendantOf(parent))
                {
                    return;
                }
            }

            declaredAfter.Add(snapshot.Tree.GetFolder(parentIndex).Path.Value);
        }

        private bool TryFindSourceFolder(string path, out Snapshot snapshot, out ModFolderNode node,
            out ModFolderResult failure)
        {
            node = null;
            ModFolderPath source = ModFolderPath.FromStored(path);
            if (source.IsRoot)
            {
                snapshot = null;
                failure = ModFolderResult.Failure(ModFolderErrorCode.RootNotAllowed,
                    "The top level cannot be renamed, moved or deleted; name a folder.");
                return false;
            }

            if (!TryTakeSnapshot(out snapshot, out failure))
            {
                return false;
            }

            if (!snapshot.Tree.TryFindFolder(source.Value, out int index))
            {
                failure = ModFolderResult.Failure(ModFolderErrorCode.FolderNotFound,
                    "No folder '" + source.Value + "' exists.");
                return false;
            }

            node = snapshot.Tree.GetFolder(index);
            return true;
        }

        private bool TryTakeSnapshot(out Snapshot snapshot, out ModFolderResult failure)
        {
            try
            {
                IReadOnlyList<ModFolderModEntry> mods = _store.ListMods() ?? Array.Empty<ModFolderModEntry>();
                IReadOnlyList<string> folders = _store.LoadFolders() ?? Array.Empty<string>();
                snapshot = new Snapshot(mods, folders);
                failure = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = null;
                failure = ModFolderResult.Failure(ModFolderErrorCode.StoreFailed,
                    "The mod store could not be read: " + ex.Message);
                return false;
            }
        }

        private bool TrySaveFolders(IEnumerable<string> folders, out string error)
        {
            try
            {
                _store.SaveFolders(Normalize(folders));
                error = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool TrySetModFolder(string modId, string folder, out string error)
        {
            try
            {
                _store.SetModFolder(modId, folder ?? "");
                error = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Normalized, deduplicated (case-insensitive, first casing kept), sorted, without the top level.</summary>
        internal static List<string> Normalize(IEnumerable<string> folders)
        {
            List<string> result = new();
            HashSet<string> seen = new(ModFolderPath.Comparer);
            foreach (string folder in folders)
            {
                ModFolderPath path = ModFolderPath.FromStored(folder);
                if (!path.IsRoot && seen.Add(path.Value))
                {
                    result.Add(path.Value);
                }
            }

            result.Sort(ModFolderTree.ComparePaths);
            return result;
        }

        private static bool SameFolders(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            List<string> a = Normalize(left);
            List<string> b = Normalize(right);
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The order mods start in: mods without a recorded load order first, by ordinal id, then recorded
        /// orders ascending with ties by id (the restore order of <c>LuaModManifest.LoadOrder</c>).
        /// </summary>
        private static int CompareStartOrder(ModFolderModEntry left, ModFolderModEntry right)
        {
            bool leftOrdered = left.LoadOrder > 0;
            bool rightOrdered = right.LoadOrder > 0;
            if (leftOrdered != rightOrdered)
            {
                return leftOrdered ? 1 : -1;
            }

            if (leftOrdered && left.LoadOrder != right.LoadOrder)
            {
                return left.LoadOrder < right.LoadOrder ? -1 : 1;
            }

            return string.CompareOrdinal(left.Id, right.Id);
        }

        private static string Describe(ModFolderPath path)
        {
            return path.IsRoot ? "the top level" : "'" + path.Value + "'";
        }

        private static string Display(ModFolderPath path)
        {
            return path.IsRoot ? "(top level)" : path.Value;
        }

        private readonly struct PlannedMove
        {
            public PlannedMove(string modId, string oldFolder, string newFolder)
            {
                ModId = modId;
                OldFolder = oldFolder;
                NewFolder = newFolder;
            }

            public string ModId { get; }

            public string OldFolder { get; }

            public string NewFolder { get; }
        }

        private sealed class Snapshot
        {
            public Snapshot(IReadOnlyList<ModFolderModEntry> mods, IReadOnlyList<string> folders)
            {
                Declared = Normalize(folders);
                Tree = ModFolderTree.Build(mods, Declared, new ModFolderFoldState());
            }

            public ModFolderTree Tree { get; }

            public List<string> Declared { get; }

            public bool HasDeclaredWithin(ModFolderPath branch)
            {
                foreach (string declared in Declared)
                {
                    if (ModFolderPath.FromNormalized(declared).IsSameOrDescendantOf(branch))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>The path with each leading segment that names an existing folder in that folder's casing.</summary>
            public ModFolderPath Canonicalize(ModFolderPath path)
            {
                if (path.IsRoot)
                {
                    return path;
                }

                string[] segments = path.GetSegments();
                ModFolderPath current = ModFolderPath.Root;
                bool existing = true;
                for (int i = 0; i < segments.Length; i++)
                {
                    ModFolderPath next = current.Child(segments[i]);
                    if (existing && Tree.TryFindFolder(next.Value, out int index))
                    {
                        current = Tree.GetFolder(index).Path;
                        continue;
                    }

                    existing = false;
                    current = next;
                }

                return current;
            }
        }
    }

    /// <summary>The outcome of a <see cref="ModFolderService"/> operation.</summary>
    public sealed class ModFolderResult
    {
        private static readonly IReadOnlyList<string> NoIds = Array.Empty<string>();

        private ModFolderResult(ModFolderErrorCode error, string message, ModFolderPath path, ModFolderPath previousPath,
            int modsChanged, bool changed, bool createdFolder, IReadOnlyList<string> failedModIds, string warning)
        {
            Error = error;
            Message = message ?? "";
            Path = path;
            PreviousPath = previousPath;
            ModsChanged = modsChanged;
            Changed = changed;
            CreatedFolder = createdFolder;
            FailedModIds = failedModIds ?? NoIds;
            Warning = warning ?? "";
        }

        /// <summary>The violated rule or failure; <see cref="ModFolderErrorCode.None"/> on success.</summary>
        public ModFolderErrorCode Error { get; }

        /// <summary>The stable code of <see cref="Error"/> (<c>OK</c> on success).</summary>
        public string Code => ModFolderErrors.Code(Error);

        /// <summary>A sentence for a status line or a tool result.</summary>
        public string Message { get; }

        /// <summary>The folder the operation ended in: a created, renamed or moved folder, a mod's new folder, or the switched branch.</summary>
        public ModFolderPath Path { get; }

        /// <summary>Where a renamed, moved or deleted folder (or a moved mod) was before.</summary>
        public ModFolderPath PreviousPath { get; }

        /// <summary>Mods whose folder or on state was written.</summary>
        public int ModsChanged { get; }

        /// <summary>True when anything was written.</summary>
        public bool Changed { get; }

        /// <summary>True when the target folder did not exist before.</summary>
        public bool CreatedFolder { get; }

        /// <summary>Mods a branch switch could not switch; empty otherwise.</summary>
        public IReadOnlyList<string> FailedModIds { get; }

        /// <summary>A non-fatal problem after a successful operation (empty when none).</summary>
        public string Warning { get; }

        /// <summary>True on success.</summary>
        public bool Succeeded => Error == ModFolderErrorCode.None;

        internal static ModFolderResult Success(string message, ModFolderPath path, ModFolderPath previousPath,
            int modsChanged, bool changed, bool createdFolder, string warning = "")
        {
            return new ModFolderResult(ModFolderErrorCode.None, message, path, previousPath, modsChanged, changed,
                createdFolder, null, warning);
        }

        internal static ModFolderResult Failure(ModFolderErrorCode error, string message)
        {
            return new ModFolderResult(error, message, ModFolderPath.Root, ModFolderPath.Root, 0, false, false, null, "");
        }

        internal static ModFolderResult Partial(ModFolderErrorCode error, string message, ModFolderPath path,
            int modsChanged, IReadOnlyList<string> failedModIds)
        {
            return new ModFolderResult(error, message, path, ModFolderPath.Root, modsChanged, modsChanged > 0, false,
                failedModIds, "");
        }
    }
}

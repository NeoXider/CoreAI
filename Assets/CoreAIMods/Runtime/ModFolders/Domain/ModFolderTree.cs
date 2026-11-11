using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Folders
{
    /// <summary>Whether a visible row is a folder or a mod.</summary>
    public enum ModFolderRowKind : byte
    {
        /// <summary>A folder row; <see cref="ModFolderRow.Index"/> is a folder index.</summary>
        Folder = 0,

        /// <summary>A mod row; <see cref="ModFolderRow.Index"/> is a mod index.</summary>
        Mod = 1
    }

    /// <summary>One visible row of the flattened tree: what it is, which one, and how deep it is indented.</summary>
    public readonly struct ModFolderRow
    {
        /// <summary>Creates a row.</summary>
        public ModFolderRow(ModFolderRowKind kind, int index, int depth)
        {
            Kind = kind;
            Index = index;
            Depth = depth;
        }

        /// <summary>Folder or mod.</summary>
        public ModFolderRowKind Kind { get; }

        /// <summary>A folder index (<see cref="ModFolderTree.GetFolder"/>) or a mod index (<see cref="ModFolderTree.GetMod"/>).</summary>
        public int Index { get; }

        /// <summary>The indent level: 0 for the contents of the top level.</summary>
        public int Depth { get; }

        /// <summary>True for a folder row.</summary>
        public bool IsFolder => Kind == ModFolderRowKind.Folder;
    }

    /// <summary>One folder of a <see cref="ModFolderTree"/> with its branch counts.</summary>
    public sealed class ModFolderNode
    {
        internal readonly List<int> FolderList = new();
        internal readonly List<int> ModList = new();

        internal ModFolderNode(int index, string name, ModFolderPath path, int parentIndex, int depth)
        {
            Index = index;
            Name = name;
            Path = path;
            ParentIndex = parentIndex;
            Depth = depth;
        }

        /// <summary>The folder index; 0 is the top level.</summary>
        public int Index { get; }

        /// <summary>The folder's own name (its last segment); empty for the top level.</summary>
        public string Name { get; }

        /// <summary>The folder's full path, in the casing it was first seen with.</summary>
        public ModFolderPath Path { get; }

        /// <summary>The index of the folder that holds this one; -1 for the top level.</summary>
        public int ParentIndex { get; }

        /// <summary>The number of segments of <see cref="Path"/>; 0 for the top level.</summary>
        public int Depth { get; }

        /// <summary>True for the top level.</summary>
        public bool IsRoot => Index == 0;

        /// <summary>True when the folder was persisted on its own (created or kept), not only implied by a mod.</summary>
        public bool IsDeclared { get; internal set; }

        /// <summary>Mods anywhere in this folder's branch.</summary>
        public int ModCount { get; internal set; }

        /// <summary>Mods that are on anywhere in this folder's branch.</summary>
        public int EnabledModCount { get; internal set; }

        /// <summary>Mods directly in this folder.</summary>
        public int DirectModCount => ModList.Count;

        /// <summary>Folders directly in this folder.</summary>
        public int SubfolderCount => FolderList.Count;

        /// <summary>True when the branch holds no mod (it may still hold empty subfolders).</summary>
        public bool IsEmpty => ModCount == 0;

        /// <summary>The folder indices directly inside, sorted by name.</summary>
        public IReadOnlyList<int> ChildFolderIndices => FolderList;

        /// <summary>The mod indices directly inside, sorted by name then id.</summary>
        public IReadOnlyList<int> ModIndices => ModList;
    }

    /// <summary>
    /// The folder tree of a mod set: built once from the mods' stored folders plus the persisted folders,
    /// with mod counts per branch, a search over name, id, folder path and tags that keeps matching branches
    /// open, the viewer's fold state, and a flat list of the visible rows for a virtualized list.
    /// <para>
    /// The flat list is the only thing a view reads per frame (<see cref="VisibleRowCount"/>,
    /// <see cref="GetVisibleRow"/>), and it is rebuilt only when the search or a fold changes. After
    /// <see cref="Build"/>, folding, unfolding and re-flattening allocate nothing; a search allocates only
    /// the count labels it changes, on first read.
    /// </para>
    /// <para>Not thread-safe: build and use it on one thread (the UI thread), and rebuild it after the mod set changes.</para>
    /// </summary>
    public sealed class ModFolderTree
    {
        private static readonly IComparer<string> PathOrder = Comparer<string>.Create(ComparePaths);

        private readonly List<ModFolderNode> _folders = new();
        private readonly Dictionary<string, int> _folderByPath = new(ModFolderPath.Comparer);
        private readonly Dictionary<string, int> _modByIdIndex = new(StringComparer.Ordinal);
        private readonly List<ModFolderModEntry> _mods = new();
        private readonly List<int> _modFolder = new();
        private readonly List<ModFolderPath> _missingScratch = new();
        private readonly ModFolderFoldState _foldState;

        private bool[] _expanded;
        private bool[] _folderVisible;
        private bool[] _folderMatch;
        private bool[] _searchCollapsed;
        private bool[] _modVisible;
        private int[] _matchCount;
        private string[] _countText;
        private ModFolderRow[] _rows;
        private int _rowCount;
        private int[] _stackFolder;
        private int[] _stackCursor;
        private string _search = "";
        private List<string> _folderPaths;

        private ModFolderTree(ModFolderFoldState foldState)
        {
            _foldState = foldState ?? new ModFolderFoldState();
            _folders.Add(new ModFolderNode(0, "", ModFolderPath.Root, -1, 0));
            _folderByPath.Add("", 0);
        }

        /// <summary>The fold state this tree reads and writes.</summary>
        public ModFolderFoldState FoldState => _foldState;

        /// <summary>Folders, the top level included.</summary>
        public int FolderCount => _folders.Count;

        /// <summary>Mods in the tree.</summary>
        public int ModCount => _mods.Count;

        /// <summary>The top level.</summary>
        public ModFolderNode Root => _folders[0];

        /// <summary>The current search text (trimmed); empty when not searching.</summary>
        public string Search => _search;

        /// <summary>True while a search is applied.</summary>
        public bool IsSearching => _search.Length > 0;

        /// <summary>Grows by one each time the visible rows change.</summary>
        public int Version { get; private set; }

        /// <summary>The number of visible rows.</summary>
        public int VisibleRowCount => _rowCount;

        /// <summary>
        /// Builds the tree. Mods with a blank id are skipped; a repeated id keeps its first entry. Folders
        /// come from <paramref name="declaredFolders"/> (persisted folders, empty ones included) and from
        /// every mod's stored folder, read with <see cref="ModFolderPath.FromStored"/>; every ancestor of
        /// either exists too.
        /// </summary>
        public static ModFolderTree Build(IReadOnlyList<ModFolderModEntry> mods,
            IEnumerable<string> declaredFolders = null, ModFolderFoldState foldState = null)
        {
            ModFolderTree tree = new(foldState);
            if (declaredFolders != null)
            {
                foreach (string declared in declaredFolders)
                {
                    ModFolderPath path = ModFolderPath.FromStored(declared);
                    if (!path.IsRoot)
                    {
                        tree._folders[tree.EnsureFolder(path)].IsDeclared = true;
                    }
                }
            }

            if (mods != null)
            {
                for (int i = 0; i < mods.Count; i++)
                {
                    tree.AddMod(mods[i]);
                }
            }

            tree.Complete();
            return tree;
        }

        /// <summary>The folder at <paramref name="index"/>; 0 is the top level.</summary>
        public ModFolderNode GetFolder(int index)
        {
            return _folders[index];
        }

        /// <summary>The mod at <paramref name="index"/>.</summary>
        public ModFolderModEntry GetMod(int index)
        {
            return _mods[index];
        }

        /// <summary>The index of the folder that directly holds the mod at <paramref name="modIndex"/>.</summary>
        public int GetModFolderIndex(int modIndex)
        {
            return _modFolder[modIndex];
        }

        /// <summary>The visible row at <paramref name="index"/> (0 to <see cref="VisibleRowCount"/> - 1).</summary>
        public ModFolderRow GetVisibleRow(int index)
        {
            if ((uint)index >= (uint)_rowCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _rows[index];
        }

        /// <summary>Finds a folder by path (read leniently, case-insensitive). The empty path is the top level.</summary>
        public bool TryFindFolder(string path, out int index)
        {
            return _folderByPath.TryGetValue(ModFolderPath.FromStored(path).Value, out index);
        }

        /// <summary>Finds a mod by id (ordinal, trimmed).</summary>
        public bool TryFindMod(string id, out int index)
        {
            return _modByIdIndex.TryGetValue((id ?? "").Trim(), out index);
        }

        /// <summary>
        /// The visible row showing the given folder or mod; -1 when it is hidden (folded away or filtered
        /// out). Linear in the visible rows; meant for scroll-to after an edit, not per frame.
        /// </summary>
        public int FindVisibleRow(ModFolderRowKind kind, int index)
        {
            for (int i = 0; i < _rowCount; i++)
            {
                if (_rows[i].Kind == kind && _rows[i].Index == index)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Every folder path except the top level, sorted: the targets a "Move to" list offers.</summary>
        public IReadOnlyList<string> GetFolderPaths()
        {
            if (_folderPaths == null)
            {
                List<string> paths = new(_folders.Count - 1);
                for (int i = 1; i < _folders.Count; i++)
                {
                    paths.Add(_folders[i].Path.Value);
                }

                paths.Sort(PathOrder);
                _folderPaths = paths;
            }

            return _folderPaths;
        }

        /// <summary>
        /// Whether the folder is open as shown now: while searching, every visible folder is open unless it
        /// was closed during this search; otherwise the fold state decides.
        /// </summary>
        public bool IsExpanded(int folderIndex)
        {
            if (folderIndex == 0)
            {
                return true;
            }

            return IsSearching ? !_searchCollapsed[folderIndex] : _expanded[folderIndex];
        }

        /// <summary>True when the folder is shown under the current search (always true without one).</summary>
        public bool IsFolderVisible(int folderIndex)
        {
            return _folderVisible[folderIndex];
        }

        /// <summary>True when the mod is shown under the current search (always true without one).</summary>
        public bool IsModVisible(int modIndex)
        {
            return _modVisible[modIndex];
        }

        /// <summary>Mods of the folder's branch that match the current search (the branch count without one).</summary>
        public int GetMatchCount(int folderIndex)
        {
            return _matchCount[folderIndex];
        }

        /// <summary>
        /// Opens or closes a folder and re-flattens. Without a search the change goes to the fold state (so it
        /// is remembered); during a search it lasts until the search changes.
        /// </summary>
        public void SetExpanded(int folderIndex, bool expanded)
        {
            if (folderIndex <= 0 || folderIndex >= _folders.Count || IsExpanded(folderIndex) == expanded)
            {
                return;
            }

            if (IsSearching)
            {
                _searchCollapsed[folderIndex] = !expanded;
            }
            else
            {
                _expanded[folderIndex] = expanded;
                _foldState.SetExpanded(_folders[folderIndex].Path, expanded);
            }

            RebuildRows();
        }

        /// <summary>Flips a folder open or closed.</summary>
        public void ToggleExpanded(int folderIndex)
        {
            SetExpanded(folderIndex, !IsExpanded(folderIndex));
        }

        /// <summary>Opens or closes every folder (remembered when not searching) and re-flattens once.</summary>
        public void SetAllExpanded(bool expanded)
        {
            for (int i = 1; i < _folders.Count; i++)
            {
                if (IsSearching)
                {
                    _searchCollapsed[i] = !expanded;
                }
                else
                {
                    _expanded[i] = expanded;
                    _foldState.SetExpanded(_folders[i].Path, expanded);
                }
            }

            RebuildRows();
        }

        /// <summary>Opens every folder above the given one, so it becomes visible (remembered when not searching).</summary>
        public void Reveal(int folderIndex)
        {
            bool changed = false;
            int current = folderIndex > 0 && folderIndex < _folders.Count ? _folders[folderIndex].ParentIndex : -1;
            while (current > 0)
            {
                if (!IsExpanded(current))
                {
                    if (IsSearching)
                    {
                        _searchCollapsed[current] = false;
                    }
                    else
                    {
                        _expanded[current] = true;
                        _foldState.SetExpanded(_folders[current].Path, true);
                    }

                    changed = true;
                }

                current = _folders[current].ParentIndex;
            }

            if (changed)
            {
                RebuildRows();
            }
        }

        /// <summary>
        /// Applies a search: a mod matches when its name, id, tags or folder path contains the text
        /// (ordinal, case-insensitive); a folder is shown when its path matches or anything in its branch
        /// matches, and every shown folder opens. Blank text clears the search and restores the fold state.
        /// </summary>
        public void SetSearch(string query)
        {
            string trimmed = (query ?? "").Trim();
            if (string.Equals(trimmed, _search, StringComparison.Ordinal))
            {
                return;
            }

            _search = trimmed;
            RecomputeVisibility();
            RebuildRows();
        }

        /// <summary>
        /// The count label of a folder row: <c>enabled/total</c> (<c>3/12</c>) without a search,
        /// <c>N found</c> during one, <c>empty</c> for a branch without mods. Cached until the search changes.
        /// </summary>
        public string GetCountText(int folderIndex)
        {
            string text = _countText[folderIndex];
            if (text != null)
            {
                return text;
            }

            ModFolderNode node = _folders[folderIndex];
            if (IsSearching)
            {
                text = _matchCount[folderIndex].ToString(CultureInfo.InvariantCulture) + " found";
            }
            else if (node.ModCount == 0)
            {
                text = "empty";
            }
            else
            {
                text = node.EnabledModCount.ToString(CultureInfo.InvariantCulture) + "/" +
                       node.ModCount.ToString(CultureInfo.InvariantCulture);
            }

            _countText[folderIndex] = text;
            return text;
        }

        /// <summary>Folder path order: case-insensitive ordinal, then ordinal, so the order is total and stable.</summary>
        public static int ComparePaths(string left, string right)
        {
            int order = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            return order != 0 ? order : string.CompareOrdinal(left, right);
        }

        private void AddMod(ModFolderModEntry entry)
        {
            if (entry == null || entry.Id.Length == 0 || _modByIdIndex.ContainsKey(entry.Id))
            {
                return;
            }

            int folder = EnsureFolder(ModFolderPath.FromStored(entry.Folder));
            int index = _mods.Count;
            _mods.Add(entry);
            _modFolder.Add(folder);
            _modByIdIndex.Add(entry.Id, index);
            _folders[folder].ModList.Add(index);

            bool enabled = entry.Enabled;
            int current = folder;
            while (current >= 0)
            {
                ModFolderNode node = _folders[current];
                node.ModCount++;
                if (enabled)
                {
                    node.EnabledModCount++;
                }

                current = node.ParentIndex;
            }
        }

        /// <summary>
        /// The folder index for a lenient path, creating it and its missing ancestors. A new folder takes its
        /// parent's casing for the shared part, so one folder never appears under two casings.
        /// </summary>
        private int EnsureFolder(ModFolderPath path)
        {
            if (_folderByPath.TryGetValue(path.Value, out int existing))
            {
                return existing;
            }

            List<ModFolderPath> missing = _missingScratch;
            missing.Clear();
            ModFolderPath current = path;
            int parent;
            do
            {
                missing.Add(current);
                current = current.Parent;
            }
            while (!_folderByPath.TryGetValue(current.Value, out parent));

            for (int i = missing.Count - 1; i >= 0; i--)
            {
                ModFolderNode parentNode = _folders[parent];
                string name = missing[i].Name;
                ModFolderPath canonical = parentNode.Path.Child(name);
                int index = _folders.Count;
                _folders.Add(new ModFolderNode(index, name, canonical, parent, parentNode.Depth + 1));
                parentNode.FolderList.Add(index);
                _folderByPath.Add(canonical.Value, index);
                parent = index;
            }

            return parent;
        }

        private void Complete()
        {
            FolderNameComparer folderOrder = new(_folders);
            ModNameComparer modOrder = new(_mods);
            int maxDepth = 0;
            for (int i = 0; i < _folders.Count; i++)
            {
                ModFolderNode node = _folders[i];
                if (node.FolderList.Count > 1)
                {
                    node.FolderList.Sort(folderOrder);
                }

                if (node.ModList.Count > 1)
                {
                    node.ModList.Sort(modOrder);
                }

                if (node.Depth > maxDepth)
                {
                    maxDepth = node.Depth;
                }
            }

            int folderCount = _folders.Count;
            _expanded = new bool[folderCount];
            _folderVisible = new bool[folderCount];
            _folderMatch = new bool[folderCount];
            _searchCollapsed = new bool[folderCount];
            _matchCount = new int[folderCount];
            _countText = new string[folderCount];
            _modVisible = new bool[_mods.Count];
            _rows = new ModFolderRow[Math.Max(1, folderCount - 1 + _mods.Count)];
            _stackFolder = new int[maxDepth + 2];
            _stackCursor = new int[maxDepth + 2];
            _expanded[0] = true;
            for (int i = 1; i < folderCount; i++)
            {
                _expanded[i] = _foldState.IsExpanded(_folders[i].Path);
            }

            RecomputeVisibility();
            RebuildRows();
        }

        private void RecomputeVisibility()
        {
            Array.Clear(_searchCollapsed, 0, _searchCollapsed.Length);
            Array.Clear(_countText, 0, _countText.Length);
            if (!IsSearching)
            {
                for (int i = 0; i < _folders.Count; i++)
                {
                    _folderVisible[i] = true;
                    _matchCount[i] = _folders[i].ModCount;
                }

                for (int i = 0; i < _modVisible.Length; i++)
                {
                    _modVisible[i] = true;
                }

                return;
            }

            string needle = _search;
            Array.Clear(_folderVisible, 0, _folderVisible.Length);
            Array.Clear(_matchCount, 0, _matchCount.Length);
            _folderVisible[0] = true;
            for (int i = 1; i < _folders.Count; i++)
            {
                _folderMatch[i] = Contains(_folders[i].Path.Value, needle);
                if (_folderMatch[i])
                {
                    MarkVisibleUpwards(i);
                }
            }

            _folderMatch[0] = false;
            for (int i = 0; i < _mods.Count; i++)
            {
                ModFolderModEntry mod = _mods[i];
                int folder = _modFolder[i];
                bool visible = _folderMatch[folder] || Contains(mod.Name, needle) || Contains(mod.Id, needle) ||
                               Contains(mod.Tags, needle);
                _modVisible[i] = visible;
                if (!visible)
                {
                    continue;
                }

                int current = folder;
                while (current >= 0)
                {
                    _matchCount[current]++;
                    _folderVisible[current] = true;
                    current = _folders[current].ParentIndex;
                }
            }
        }

        private void MarkVisibleUpwards(int folderIndex)
        {
            int current = folderIndex;
            while (current >= 0 && !_folderVisible[current])
            {
                _folderVisible[current] = true;
                current = _folders[current].ParentIndex;
            }
        }

        /// <summary>
        /// Flattens the visible tree depth-first, folders before mods at each level, into the preallocated
        /// row buffer. Iterative, so a deep legacy path cannot exhaust the stack.
        /// </summary>
        private void RebuildRows()
        {
            int count = 0;
            _stackFolder[0] = 0;
            _stackCursor[0] = 0;
            int top = 1;
            while (top > 0)
            {
                int frame = top - 1;
                ModFolderNode node = _folders[_stackFolder[frame]];
                List<int> children = node.FolderList;
                int cursor = _stackCursor[frame];
                if (cursor < children.Count)
                {
                    _stackCursor[frame] = cursor + 1;
                    int child = children[cursor];
                    if (!_folderVisible[child])
                    {
                        continue;
                    }

                    _rows[count++] = new ModFolderRow(ModFolderRowKind.Folder, child, frame);
                    ModFolderNode childNode = _folders[child];
                    if (IsExpanded(child) && (childNode.FolderList.Count > 0 || childNode.ModList.Count > 0))
                    {
                        _stackFolder[top] = child;
                        _stackCursor[top] = 0;
                        top++;
                    }

                    continue;
                }

                List<int> mods = node.ModList;
                for (int i = 0; i < mods.Count; i++)
                {
                    int mod = mods[i];
                    if (_modVisible[mod])
                    {
                        _rows[count++] = new ModFolderRow(ModFolderRowKind.Mod, mod, frame);
                    }
                }

                top--;
            }

            _rowCount = count;
            Version++;
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class FolderNameComparer : IComparer<int>
        {
            private readonly List<ModFolderNode> _folders;

            public FolderNameComparer(List<ModFolderNode> folders)
            {
                _folders = folders;
            }

            public int Compare(int x, int y)
            {
                return ComparePaths(_folders[x].Name, _folders[y].Name);
            }
        }

        private sealed class ModNameComparer : IComparer<int>
        {
            private readonly List<ModFolderModEntry> _mods;

            public ModNameComparer(List<ModFolderModEntry> mods)
            {
                _mods = mods;
            }

            public int Compare(int x, int y)
            {
                ModFolderModEntry left = _mods[x];
                ModFolderModEntry right = _mods[y];
                int order = ComparePaths(left.Name, right.Name);
                return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
            }
        }
    }
}

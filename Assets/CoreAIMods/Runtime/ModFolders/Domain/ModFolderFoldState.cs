using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// Which folders a viewer has open: a default (open or closed) plus the folders toggled away from it,
    /// keyed by path, case-insensitively. Per viewer, not per world: it never travels in a world package.
    /// Persist it with <see cref="ModFolderStateCodec.EncodeFoldState"/>; <see cref="Version"/> grows on every
    /// change, so a host saves only when it moved.
    /// </summary>
    public sealed class ModFolderFoldState
    {
        private readonly HashSet<string> _toggled = new(ModFolderPath.Comparer);

        /// <param name="expandedByDefault">Whether a folder the viewer never toggled starts open.</param>
        public ModFolderFoldState(bool expandedByDefault = true)
        {
            ExpandedByDefault = expandedByDefault;
        }

        /// <summary>Whether a folder the viewer never toggled is open.</summary>
        public bool ExpandedByDefault { get; }

        /// <summary>Grows by one on every change.</summary>
        public int Version { get; private set; }

        /// <summary>How many folders differ from the default.</summary>
        public int ToggledCount => _toggled.Count;

        /// <summary>Whether the folder at <paramref name="path"/> is open. The top level is always open.</summary>
        public bool IsExpanded(ModFolderPath path)
        {
            if (path.IsRoot)
            {
                return true;
            }

            return _toggled.Contains(path.Value) ? !ExpandedByDefault : ExpandedByDefault;
        }

        /// <summary>Opens or closes the folder at <paramref name="path"/>; the top level cannot be closed.</summary>
        public void SetExpanded(ModFolderPath path, bool expanded)
        {
            if (path.IsRoot)
            {
                return;
            }

            bool changed = expanded == ExpandedByDefault ? _toggled.Remove(path.Value) : _toggled.Add(path.Value);
            if (changed)
            {
                Version++;
            }
        }

        /// <summary>
        /// Moves the remembered state of every folder in the branch at <paramref name="from"/> to the same
        /// place under <paramref name="to"/>, after a folder rename or move.
        /// </summary>
        public void Rebase(ModFolderPath from, ModFolderPath to)
        {
            if (from.IsRoot || from == to && string.Equals(from.Value, to.Value, StringComparison.Ordinal))
            {
                return;
            }

            List<string> moved = null;
            foreach (string toggled in _toggled)
            {
                if (ModFolderPath.FromNormalized(toggled).IsSameOrDescendantOf(from))
                {
                    (moved ??= new List<string>()).Add(toggled);
                }
            }

            if (moved == null)
            {
                return;
            }

            foreach (string path in moved)
            {
                _toggled.Remove(path);
            }

            foreach (string path in moved)
            {
                _toggled.Add(ModFolderPath.FromNormalized(path).RebaseOnto(from, to).Value);
            }

            Version++;
        }

        /// <summary>Forgets the state of every folder in the branch at <paramref name="branch"/> (after a delete).</summary>
        public void Forget(ModFolderPath branch)
        {
            if (branch.IsRoot)
            {
                if (_toggled.Count > 0)
                {
                    _toggled.Clear();
                    Version++;
                }

                return;
            }

            int removed = _toggled.RemoveWhere(path => ModFolderPath.FromNormalized(path).IsSameOrDescendantOf(branch));
            if (removed > 0)
            {
                Version++;
            }
        }

        /// <summary>The folders that differ from the default, sorted (case-insensitive, then ordinal).</summary>
        public IReadOnlyList<string> GetToggledPaths()
        {
            List<string> paths = new(_toggled);
            paths.Sort(ModFolderTree.ComparePaths);
            return paths;
        }
    }
}

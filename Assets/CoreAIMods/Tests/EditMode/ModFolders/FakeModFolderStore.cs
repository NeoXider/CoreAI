using System;
using System.Collections.Generic;
using CoreAI.Mods.Folders;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// An in-memory <see cref="IModFolderStore"/> that keeps each mod as a Lua source with an <c>@coreai</c>
    /// header, as the CoreAI store does: the folder lives in the header and <see cref="SetModFolder"/> rewrites
    /// it with <see cref="ModFolderHeaderEditor"/>, so a reload of the store (a "restart") reads the folders
    /// back from the sources. Writes can be made to fail for chosen mods or for the folder list.
    /// </summary>
    internal sealed class FakeModFolderStore : IModFolderStore
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, StoredMod> _mods = new(StringComparer.Ordinal);
        private List<string> _folders = new();

        public HashSet<string> FailFolderWritesFor { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailEnableFor { get; } = new(StringComparer.Ordinal);

        public bool FailFolderListWrites { get; set; }

        public int FolderListWrites { get; private set; }

        public List<string> EnableCalls { get; } = new();

        public List<string> MoveCalls { get; } = new();

        public FakeModFolderStore Add(string id, string folder, bool enabled = true, string tags = "", long loadOrder = 0)
        {
            string header = "--[[@coreai\nid: " + id + "\nname: " + id + "\nversion: 1.0.0\n" +
                            (folder == null ? "" : "category: " + folder + "\n") +
                            (tags.Length == 0 ? "" : "tags: " + tags + "\n") + "]]\nprint('" + id + "')\n";
            return AddSource(id, header, enabled, loadOrder, tags);
        }

        public FakeModFolderStore AddSource(string id, string source, bool enabled = true, long loadOrder = 0,
            string tags = "")
        {
            _order.Add(id);
            _mods[id] = new StoredMod { Source = source, Enabled = enabled, LoadOrder = loadOrder, Tags = tags };
            return this;
        }

        public FakeModFolderStore WithFolders(params string[] folders)
        {
            _folders = new List<string>(folders);
            return this;
        }

        public string SourceOf(string id)
        {
            return _mods[id].Source;
        }

        public string FolderOf(string id)
        {
            return ModFolderHeaderEditor.ReadCategory(_mods[id].Source);
        }

        public bool IsEnabled(string id)
        {
            return _mods[id].Enabled;
        }

        public IReadOnlyList<string> Folders => _folders;

        public IReadOnlyList<ModFolderModEntry> ListMods()
        {
            List<ModFolderModEntry> entries = new(_order.Count);
            foreach (string id in _order)
            {
                StoredMod mod = _mods[id];
                entries.Add(new ModFolderModEntry(id, id, ModFolderHeaderEditor.ReadCategory(mod.Source), mod.Tags,
                    mod.Enabled, mod.LoadOrder));
            }

            return entries;
        }

        public IReadOnlyList<string> LoadFolders()
        {
            return new List<string>(_folders);
        }

        public void SaveFolders(IReadOnlyList<string> folders)
        {
            if (FailFolderListWrites)
            {
                throw new InvalidOperationException("folder list write refused");
            }

            FolderListWrites++;
            _folders = new List<string>(folders);
        }

        public void SetModFolder(string modId, string folderPath)
        {
            MoveCalls.Add(modId + "->" + folderPath);
            if (FailFolderWritesFor.Contains(modId))
            {
                throw new InvalidOperationException("write refused for " + modId);
            }

            StoredMod mod = _mods[modId];
            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(mod.Source, folderPath);
            if (!edit.Succeeded)
            {
                throw new InvalidOperationException(edit.Message);
            }

            mod.Source = edit.Source;
        }

        public void SetModEnabled(string modId, bool enabled)
        {
            EnableCalls.Add(modId + (enabled ? "+" : "-"));
            if (FailEnableFor.Contains(modId))
            {
                throw new InvalidOperationException("enable refused for " + modId);
            }

            _mods[modId].Enabled = enabled;
        }

        /// <summary>A second store over the same persisted state, as after a restart.</summary>
        public FakeModFolderStore Reopen()
        {
            FakeModFolderStore reopened = new();
            foreach (string id in _order)
            {
                StoredMod mod = _mods[id];
                reopened.AddSource(id, mod.Source, mod.Enabled, mod.LoadOrder, mod.Tags);
            }

            reopened._folders = new List<string>(_folders);
            return reopened;
        }

        private sealed class StoredMod
        {
            public string Source;
            public bool Enabled;
            public long LoadOrder;
            public string Tags = "";
        }
    }
}

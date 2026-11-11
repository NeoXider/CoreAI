using System;
using System.Collections.Generic;
using CoreAI.Mods.Folders;
using UnityEngine;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// The folder operations' view of a Hub mod service: the service's records as folder entries (the record
    /// travels as <see cref="ModFolderModEntry.Payload"/>), moves through <see cref="IHubModFolderWriter"/>,
    /// on and off through Enable and Disable, and the persisted folders in an <see cref="IModFolderListStore"/>.
    /// </summary>
    public sealed class HubModFolderStore : IModFolderStore
    {
        private readonly IHubModService _service;
        private readonly IModFolderListStore _folders;

        /// <param name="service">The Hub mod service the folders belong to.</param>
        /// <param name="folders">Where the persisted (empty or created) folders are kept.</param>
        public HubModFolderStore(IHubModService service, IModFolderListStore folders)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        }

        /// <summary>Maps service records to folder entries; a mod is on when it is loaded.</summary>
        public static List<ModFolderModEntry> ToEntries(IReadOnlyList<HubModRecord> records)
        {
            List<ModFolderModEntry> entries = new(records?.Count ?? 0);
            if (records == null)
            {
                return entries;
            }

            foreach (HubModRecord record in records)
            {
                if (record != null)
                {
                    entries.Add(new ModFolderModEntry(record.Id, record.Name, record.Category, record.Tags,
                        record.IsLoaded, record.LoadOrder, record));
                }
            }

            return entries;
        }

        /// <inheritdoc />
        public IReadOnlyList<ModFolderModEntry> ListMods()
        {
            return ToEntries(_service.ListMods());
        }

        /// <inheritdoc />
        public IReadOnlyList<string> LoadFolders()
        {
            return _folders.Load();
        }

        /// <inheritdoc />
        public void SaveFolders(IReadOnlyList<string> folders)
        {
            _folders.Save(folders);
        }

        /// <inheritdoc />
        public void SetModFolder(string modId, string folderPath)
        {
            if (!(_service is IHubModFolderWriter writer))
            {
                throw new NotSupportedException("This mod service cannot move mods between folders.");
            }

            writer.SetModFolder(modId, folderPath);
        }

        /// <inheritdoc />
        public void SetModEnabled(string modId, bool enabled)
        {
            if (enabled)
            {
                _service.Enable(modId);
            }
            else
            {
                _service.Disable(modId);
            }
        }
    }

    /// <summary>Where the Mods page keeps the persisted folders and the viewer's fold state, as codec text.</summary>
    public interface IHubModFolderPreferences
    {
        /// <summary>The persisted-folders document (<see cref="ModFolderStateCodec.EncodeFolders"/>); empty when none.</summary>
        string FoldersText { get; set; }

        /// <summary>The fold-state document (<see cref="ModFolderStateCodec.EncodeFoldState"/>); empty when none.</summary>
        string FoldStateText { get; set; }
    }

    /// <summary>
    /// <see cref="IHubModFolderPreferences"/> kept in Unity's PlayerPrefs, so folders and folds survive restarts
    /// on every platform, WebGL included. One set per device: a host with several mod stores or a world-bound
    /// folder list passes its own <see cref="IModFolderListStore"/> to the page instead.
    /// </summary>
    public sealed class PlayerPrefsHubModFolderPreferences : IHubModFolderPreferences
    {
        /// <summary>The PlayerPrefs key of <see cref="FoldersText"/>.</summary>
        public const string FoldersKey = "CoreAI.Hub.Mods.Folders";

        /// <summary>The PlayerPrefs key of <see cref="FoldStateText"/>.</summary>
        public const string FoldStateKey = "CoreAI.Hub.Mods.FoldState";

        /// <summary>The shared instance.</summary>
        public static readonly PlayerPrefsHubModFolderPreferences Instance = new();

        /// <inheritdoc />
        public string FoldersText
        {
            get => PlayerPrefs.GetString(FoldersKey, "");
            set
            {
                PlayerPrefs.SetString(FoldersKey, value ?? "");
                PlayerPrefs.Save();
            }
        }

        /// <inheritdoc />
        public string FoldStateText
        {
            get => PlayerPrefs.GetString(FoldStateKey, "");
            set
            {
                PlayerPrefs.SetString(FoldStateKey, value ?? "");
                PlayerPrefs.Save();
            }
        }
    }

    /// <summary>An in-memory <see cref="IHubModFolderPreferences"/> for tests and for hosts that keep nothing.</summary>
    public sealed class InMemoryHubModFolderPreferences : IHubModFolderPreferences
    {
        /// <inheritdoc />
        public string FoldersText { get; set; } = "";

        /// <inheritdoc />
        public string FoldStateText { get; set; } = "";
    }
}

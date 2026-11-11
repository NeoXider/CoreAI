using System;
using System.Collections.Generic;
using System.IO;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// Where a mod store keeps its persisted folders (created or kept folders, empty ones included): the half
    /// of <see cref="IModFolderStore"/> that no mod carries, and what a world package captures and restores.
    /// </summary>
    public interface IModFolderListStore
    {
        /// <summary>The persisted folders; empty when none were ever saved. Throws when they cannot be read.</summary>
        IReadOnlyList<string> Load();

        /// <summary>Replaces the persisted folders. Throws to refuse.</summary>
        void Save(IReadOnlyList<string> folders);
    }

    /// <summary>
    /// An <see cref="IModFolderListStore"/> over one text document in the <see cref="ModFolderStateCodec"/>
    /// format, read and written through the host's own callbacks (a file, a preference, a cloud slot). A
    /// document with an unknown header is refused on <see cref="Load"/> with
    /// <see cref="InvalidDataException"/>, so a folder operation fails instead of overwriting it.
    /// </summary>
    public sealed class TextModFolderListStore : IModFolderListStore
    {
        private readonly Func<string> _read;
        private readonly Action<string> _write;

        /// <param name="read">Returns the stored text; null or empty when nothing was stored yet.</param>
        /// <param name="write">Stores the text; throws to refuse.</param>
        public TextModFolderListStore(Func<string> read, Action<string> write)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        /// <inheritdoc />
        public IReadOnlyList<string> Load()
        {
            ModFolderDocument document = ModFolderStateCodec.DecodeFolders(_read());
            if (!document.Succeeded)
            {
                throw new InvalidDataException(document.Message);
            }

            return document.Folders;
        }

        /// <inheritdoc />
        public void Save(IReadOnlyList<string> folders)
        {
            _write(ModFolderStateCodec.EncodeFolders(folders));
        }
    }

    /// <summary>An in-memory <see cref="IModFolderListStore"/> for tests and for hosts that keep no folders between runs.</summary>
    public sealed class InMemoryModFolderListStore : IModFolderListStore
    {
        private List<string> _folders = new();

        /// <inheritdoc />
        public IReadOnlyList<string> Load()
        {
            return new List<string>(_folders);
        }

        /// <inheritdoc />
        public void Save(IReadOnlyList<string> folders)
        {
            _folders = ModFolderService.Normalize(folders ?? Array.Empty<string>());
        }
    }
}

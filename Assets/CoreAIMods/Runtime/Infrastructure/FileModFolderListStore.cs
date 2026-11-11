using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CoreAI.Infrastructure;
using CoreAI.Mods.Folders;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// The persisted mod folders (P20) of one file-backed mod store: a <see cref="FileName"/> document in the
    /// <see cref="ModFolderStateCodec"/> format at the store's root, beside the mod directories (the source store
    /// lists directories only, so the file is never read as a mod). One instance per mod store is shared by the Hub
    /// Mods page, <c>manage_mods</c> and the world session (<c>RbxWorldRuntimeSessionController.ModFolderListStore</c>),
    /// so a folder created anywhere shows everywhere and travels in the world package.
    /// </summary>
    /// <remarks>
    /// A save writes a temporary file and replaces the document with it, so a crash leaves the old list or the new
    /// one, never half of either; then it asks <see cref="CoreAiWebGlPersistence.Sync"/> and refuses with
    /// <see cref="IOException"/> when the browser persistence is not armed (AGENTS.md WebGL rule). A document with an
    /// unknown header is refused on load, so a folder operation fails instead of overwriting it.
    /// </remarks>
    public sealed class FileModFolderListStore : IModFolderListStore
    {
        /// <summary>The document's file name at the store root.</summary>
        public const string FileName = ".mod-folders";

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly TextModFolderListStore _document;
        private readonly Func<bool> _persistenceArmed;
        private readonly object _gate = new();

        /// <param name="rootDirectory">The mod store's root directory; the document is <see cref="FileName"/> in it.</param>
        /// <param name="persistenceArmed">
        /// Asked after every write; false refuses the save. Null asks <see cref="CoreAiWebGlPersistence.Sync"/>.
        /// </param>
        public FileModFolderListStore(string rootDirectory, Func<bool> persistenceArmed = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("A mod store root directory is required.", nameof(rootDirectory));
            }

            DocumentPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(rootDirectory.Trim()), FileName);
            _persistenceArmed = persistenceArmed ?? CoreAiWebGlPersistence.Sync;
            _document = new TextModFolderListStore(Read, Write);
        }

        /// <summary>The folder document of <paramref name="store"/>, at its storage root.</summary>
        public static FileModFolderListStore For(FileLuaModSourceStore store, Func<bool> persistenceArmed = null)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            return new FileModFolderListStore(store.StoragePath, persistenceArmed);
        }

        /// <summary>The full path of the document.</summary>
        public string DocumentPath { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> Load()
        {
            lock (_gate)
            {
                return _document.Load();
            }
        }

        /// <inheritdoc />
        public void Save(IReadOnlyList<string> folders)
        {
            lock (_gate)
            {
                _document.Save(folders ?? Array.Empty<string>());
            }
        }

        private string Read()
        {
            return File.Exists(DocumentPath) ? File.ReadAllText(DocumentPath, StrictUtf8) : "";
        }

        private void Write(string text)
        {
            string directory = System.IO.Path.GetDirectoryName(DocumentPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = DocumentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text ?? "", StrictUtf8);
                if (File.Exists(DocumentPath))
                {
                    File.Replace(temporary, DocumentPath, null);
                }
                else
                {
                    File.Move(temporary, DocumentPath);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            if (!_persistenceArmed())
            {
                throw new IOException(
                    "The mod folders were written, but the browser's persistence of them is not armed, so they may "
                    + "not survive a reload.");
            }
        }
    }
}

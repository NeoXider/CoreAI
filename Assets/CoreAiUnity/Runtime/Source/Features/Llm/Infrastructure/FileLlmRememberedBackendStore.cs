using System;
using System.IO;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// The player's remembered model server in one JSON file under CoreAI's persistent data
    /// (<c>persistentDataPath/CoreAI/remembered-backend.json</c>; in a WebGL player that is the page's IndexedDB
    /// through the engine's automatic persistence). The file never holds a key: <see cref="LlmRememberedBackend"/>
    /// has no field for one.
    /// </summary>
    public sealed class FileLlmRememberedBackendStore : ILlmRememberedBackendStore
    {
        /// <summary>File name under the CoreAI storage root.</summary>
        public const string FileName = "remembered-backend.json";

        private readonly string _path;

        /// <summary>Uses <paramref name="path"/>, or the file under <see cref="CoreAiStorageRoot"/> when empty.</summary>
        public FileLlmRememberedBackendStore(string path = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }

        /// <summary>The file this store reads and writes (resolved per call, so a storage root override applies).</summary>
        public string StoragePath => _path ?? Path.GetFullPath(CoreAiStorageRoot.Combine(FileName));

        /// <inheritdoc />
        public bool TryLoad(out LlmRememberedBackend backend)
        {
            backend = null;
            string path = StoragePath;
            try
            {
                return File.Exists(path) && LlmRememberedBackend.TryParse(File.ReadAllText(path), out backend);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public bool Save(LlmRememberedBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            string path = StoragePath;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, backend.ToJson());
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            return CoreAiWebGlPersistence.Sync();
        }

        /// <inheritdoc />
        public void Clear()
        {
            string path = StoragePath;
            if (File.Exists(path))
            {
                File.Delete(path);
                CoreAiWebGlPersistence.Sync();
            }
        }
    }
}

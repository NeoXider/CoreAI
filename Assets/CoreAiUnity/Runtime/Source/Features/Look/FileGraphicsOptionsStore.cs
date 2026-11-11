using System;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Look
{
    /// <summary>
    /// JSON file <see cref="IGraphicsOptionsStore"/> under <see cref="CoreAiStorageRoot"/>
    /// (<c>CoreAI/graphics-options.json</c>). Writes are atomic (temp file, then replace) and followed by
    /// the <see cref="CoreAiWebGlPersistence.Sync"/> check, so a WebGL page that will forget the write is
    /// reported instead of trusted. A corrupt file loads as defaults with exactly one warning per store.
    /// </summary>
    public sealed class FileGraphicsOptionsStore : IGraphicsOptionsStore
    {
        /// <summary>File name under the CoreAI data folder.</summary>
        public const string FileName = "graphics-options.json";

        private readonly string _path;
        private readonly IGameLogger _logger;
        private readonly Func<bool> _durabilityCheck;
        private bool _corruptReported;

        /// <summary>Creates the store.</summary>
        /// <param name="path">Explicit file path; null uses <see cref="FileName"/> under the storage root.</param>
        /// <param name="logger">Diagnostics; null uses the unscoped CoreAI logger.</param>
        /// <param name="durabilityCheck">
        /// Asked after each write whether it will survive a restart; null uses
        /// <see cref="CoreAiWebGlPersistence.Sync"/>.
        /// </param>
        public FileGraphicsOptionsStore(string path = null, IGameLogger logger = null,
            Func<bool> durabilityCheck = null)
        {
            _path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? CoreAiStorageRoot.Combine(FileName) : path);
            _logger = logger;
            _durabilityCheck = durabilityCheck ?? CoreAiWebGlPersistence.Sync;
        }

        /// <summary>The resolved file path.</summary>
        public string FilePath => _path;

        private IGameLogger Logger => _logger ?? GameLoggerUnscopedFallback.Instance;

        /// <inheritdoc />
        public GraphicsOptions Load()
        {
            string json;
            try
            {
                if (!File.Exists(_path))
                {
                    return GraphicsOptions.Default;
                }

                json = File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReportCorrupt("it could not be read (" + ex.Message + ")");
                return GraphicsOptions.Default;
            }

            if (GraphicsOptionsJson.TryDeserialize(json, out GraphicsOptions options, out string error))
            {
                return options;
            }

            ReportCorrupt(error);
            return GraphicsOptions.Default;
        }

        /// <inheritdoc />
        public bool Save(GraphicsOptions options)
        {
            string json = GraphicsOptionsJson.Serialize(options ?? GraphicsOptions.Default);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(temp, json);
                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, null);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAI.Look] Graphics options were not saved to '" + _path + "': " + ex.Message);
                return false;
            }
            finally
            {
                TryDelete(temp);
            }

            if (!_durabilityCheck())
            {
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAI.Look] Graphics options were written to '" + _path + "' but will not survive a " +
                    "reload: this WebGL page did not enable automatic persistence.");
                return false;
            }

            return true;
        }

        private void ReportCorrupt(string reason)
        {
            if (_corruptReported)
            {
                return;
            }

            _corruptReported = true;
            Logger.LogWarning(GameLogFeature.Core,
                "[CoreAI.Look] Graphics options file '" + _path + "' is ignored because " + reason +
                "; defaults are used until the next save replaces it.");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

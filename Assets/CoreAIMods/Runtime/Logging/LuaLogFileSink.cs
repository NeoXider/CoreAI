using System;
using System.IO;
using CoreAI.Infrastructure;
using UnityEngine;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Optional file sink for <see cref="ILuaLogService"/>: off by default. Construct and call
    /// <see cref="Attach"/> to start appending every logged entry as one line to a rolling text file
    /// under <c>persistentDataPath/CoreAI/Logs</c>; call <see cref="Detach"/> or <see cref="Dispose"/>
    /// to stop. The service itself is wired by <c>CoreAiModsInstaller.RegisterCoreAiMods</c>; this sink
    /// stays a deliberate host opt-in and is not attached by any composition root.
    /// </summary>
    public sealed class LuaLogFileSink : IDisposable
    {
        /// <summary>Folder segment under <see cref="CoreAiPersistentPaths.RootFolderName"/>.</summary>
        public const string LogsFolderName = "Logs";

        /// <summary>File name written under the logs folder.</summary>
        public const string FileName = "lua-mod-logs.log";

        /// <summary>Default size threshold that triggers a single-generation roll.</summary>
        public const long DefaultMaxFileBytes = 2 * 1024 * 1024;

        private readonly ILuaLogService _logService;
        private readonly string _filePath;
        private readonly long _maxFileBytes;
        private readonly object _writeGate = new();
        private bool _attached;

        /// <param name="logService">Service to subscribe to once <see cref="Attach"/> is called.</param>
        /// <param name="rootDirectory">
        /// Directory the log file is written under. Defaults to
        /// <c>Application.persistentDataPath/CoreAI/Logs</c> when null.
        /// </param>
        /// <param name="maxFileBytes">Size threshold that triggers a roll to a <c>.1</c> backup file.</param>
        public LuaLogFileSink(ILuaLogService logService, string rootDirectory = null,
            long maxFileBytes = DefaultMaxFileBytes)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            string dir = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRootDirectory() : rootDirectory.Trim();
            _filePath = Path.Combine(dir, FileName);
            _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;
        }

        private static string DefaultRootDirectory()
        {
            return Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName, LogsFolderName);
        }

        /// <summary>Full path of the active log file (before any roll).</summary>
        public string FilePath => _filePath;

        /// <summary>Starts writing every appended entry to disk. Idempotent.</summary>
        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            _logService.EntryAppended += OnEntryAppended;
        }

        /// <summary>Stops writing to disk. Idempotent.</summary>
        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;
            _logService.EntryAppended -= OnEntryAppended;
        }

        private void OnEntryAppended(LuaLogEntry entry)
        {
            WriteLine(entry);
        }

        private void WriteLine(LuaLogEntry entry)
        {
            string line = FormatLine(entry);
            lock (_writeGate)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    RollIfNeeded();
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                    CoreAiWebGlPersistence.Sync();
                }
                catch
                {
                    // WHY: A logging sink must never throw out of a mod's log call and break gameplay.
                }
            }
        }

        private void RollIfNeeded()
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            if (new FileInfo(_filePath).Length < _maxFileBytes)
            {
                return;
            }

            string rolledPath = _filePath + ".1";
            try
            {
                if (File.Exists(rolledPath))
                {
                    File.Delete(rolledPath);
                }

                File.Move(_filePath, rolledPath);
            }
            catch
            {
                // WHY: Best-effort roll; falling behind on rotation is preferable to losing new writes.
            }
        }

        private static string FormatLine(LuaLogEntry entry)
        {
            string location = entry.Line.HasValue ? $"{entry.ScriptName}:{entry.Line.Value}" : entry.ScriptName ?? "";
            return $"{entry.UtcTime:O}\t{entry.Sequence}\t{entry.Level}\t{entry.ModId}\t{location}\t{entry.Message}";
        }

        /// <summary>Equivalent to <see cref="Detach"/>.</summary>
        public void Dispose()
        {
            Detach();
        }
    }
}

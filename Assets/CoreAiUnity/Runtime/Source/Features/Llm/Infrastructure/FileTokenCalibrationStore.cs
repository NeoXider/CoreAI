using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// File-backed token calibration scales. Stores sharing a canonical path serialize their read/modify/write
    /// operations and reload committed values before mutation. Failed or unreadable records are preserved;
    /// reads fall back to an uncalibrated scale, and failed writes are logged without publishing new values.
    /// Successful writes queue WebGL persistence; the queue does not acknowledge browser durability.
    /// </summary>
    public sealed class FileTokenCalibrationStore : ITokenCalibrationStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };
        private static readonly ConcurrentDictionary<string, object> PathLocks = new(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private readonly string _path;
        private readonly ILog _log;
        private readonly object _lock;

        /// <summary>Creates a token calibration store under persistent data unless a path is supplied.</summary>
        public FileTokenCalibrationStore(string filePath = null, ILog log = null)
        {
            _path = Path.GetFullPath(!string.IsNullOrWhiteSpace(filePath)
                ? filePath.Trim()
                : Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    "TokenCalibration",
                    "scales.json"));
            _lock = PathLocks.GetOrAdd(_path, _ => new object());
            _log = log;
        }

        /// <summary>Loads a finite positive scale, or returns false and an uncalibrated scale.</summary>
        public bool TryLoadScale(string modelKey, out double scale)
        {
            string key = NormalizeKey(modelKey);
            lock (_lock)
            {
                Dictionary<string, double> data = LoadLocked();
                if (data != null && data.TryGetValue(key, out scale) && IsValidScale(scale))
                {
                    return true;
                }
            }

            scale = 1.0d;
            return false;
        }

        /// <summary>Atomically updates one model while preserving other committed calibration values.</summary>
        public void SaveScale(string modelKey, double scale)
        {
            if (!IsValidScale(scale))
            {
                return;
            }

            string key = NormalizeKey(modelKey);
            lock (_lock)
            {
                Dictionary<string, double> data = LoadLocked();
                if (data == null)
                {
                    return;
                }
                data[key] = scale;
                SaveLocked(data);
            }
        }

        private Dictionary<string, double> LoadLocked()
        {
            try
            {
                string json = File.ReadAllText(_path);
                return JsonConvert.DeserializeObject<Dictionary<string, double>>(json, JsonSettings)
                    ?? throw new InvalidDataException("Token calibration must contain a JSON object.");
            }
            catch (FileNotFoundException)
            {
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }
            catch (DirectoryNotFoundException)
            {
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[FileTokenCalibrationStore] Load failed; existing data is preserved: {ex.Message}", LogTag.Llm);
                return null;
            }
        }

        private void SaveLocked(Dictionary<string, double> data)
        {
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(temporary, JsonConvert.SerializeObject(data, JsonSettings));
                if (File.Exists(_path))
                {
                    File.Replace(temporary, _path, null);
                }
                else
                {
                    File.Move(temporary, _path);
                }
                CoreAiWebGlPersistence.Sync();
            }
            catch (Exception ex)
            {
                _log?.Warn($"[FileTokenCalibrationStore] Save failed: {ex.Message}", LogTag.Llm);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[FileTokenCalibrationStore] Temporary file cleanup failed: {ex.Message}", LogTag.Llm);
                }
            }
        }

        private static bool IsValidScale(double scale)
        {
            return !double.IsNaN(scale) && !double.IsInfinity(scale) && scale > 0d;
        }

        private static string NormalizeKey(string modelKey)
        {
            return string.IsNullOrWhiteSpace(modelKey) ? "default" : modelKey.Trim();
        }
    }
}

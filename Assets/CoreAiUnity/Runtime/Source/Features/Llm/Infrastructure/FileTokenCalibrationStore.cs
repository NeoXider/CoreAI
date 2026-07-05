using System;
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
    /// File-backed token calibration scale store under CoreAI persistent data.
    /// </summary>
    public sealed class FileTokenCalibrationStore : ITokenCalibrationStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private readonly string _path;
        private readonly ILog _log;
        private readonly object _lock = new();
        private Dictionary<string, double> _cache;

        /// <summary>Creates a token calibration store under persistent data unless a path is supplied.</summary>
        public FileTokenCalibrationStore(string filePath = null, ILog log = null)
        {
            _path = !string.IsNullOrWhiteSpace(filePath)
                ? filePath.Trim()
                : Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    "TokenCalibration",
                    "scales.json");
            _log = log;
        }

        /// <inheritdoc />
        public bool TryLoadScale(string modelKey, out double scale)
        {
            string key = NormalizeKey(modelKey);
            lock (_lock)
            {
                Dictionary<string, double> data = LoadLocked();
                if (data.TryGetValue(key, out scale) && scale > 0d)
                {
                    return true;
                }
            }

            scale = 1.0d;
            return false;
        }

        /// <inheritdoc />
        public void SaveScale(string modelKey, double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
            {
                return;
            }

            string key = NormalizeKey(modelKey);
            lock (_lock)
            {
                Dictionary<string, double> data = LoadLocked();
                data[key] = scale;
                SaveLocked(data);
            }
        }

        private Dictionary<string, double> LoadLocked()
        {
            if (_cache != null)
            {
                return _cache;
            }

            try
            {
                if (!File.Exists(_path))
                {
                    _cache = new Dictionary<string, double>(StringComparer.Ordinal);
                    return _cache;
                }

                string json = File.ReadAllText(_path);
                _cache = JsonConvert.DeserializeObject<Dictionary<string, double>>(json, JsonSettings)
                         ?? new Dictionary<string, double>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[FileTokenCalibrationStore] Load failed: {ex.Message}", LogTag.Llm);
                _cache = new Dictionary<string, double>(StringComparer.Ordinal);
            }

            return _cache;
        }

        private void SaveLocked(Dictionary<string, double> data)
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(data, JsonSettings));
                try
                {
                    if (File.Exists(_path))
                    {
                        File.Replace(tmp, _path, null);
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }
                }
                catch
                {
                    // Match the other file stores: clean up the temp file if the atomic swap fails so a
                    // stray scales.json.tmp is not left behind, then surface the failure to the outer catch.
                    if (File.Exists(tmp))
                    {
                        try
                        {
                            File.Delete(tmp);
                        }
                        catch
                        {
                            /* best-effort cleanup */
                        }
                    }

                    throw;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[FileTokenCalibrationStore] Save failed: {ex.Message}", LogTag.Llm);
            }
        }

        private static string NormalizeKey(string modelKey)
        {
            return string.IsNullOrWhiteSpace(modelKey) ? "default" : modelKey.Trim();
        }
    }
}
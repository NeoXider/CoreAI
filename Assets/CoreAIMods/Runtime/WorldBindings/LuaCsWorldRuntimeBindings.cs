using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;
using Lua;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.World.CoreAiWorldLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsWorldRuntimeBindings : ILuaTransactionScope
    {
        public const double MaxCoordinate = 100_000d;
        public const int MaxBatchSize = 100;
        public const double MinScale = 0.01d;
        public const double MaxScale = 100d;

        private const int MaxTransactionBuffer = 256;

        private readonly IAiGameCommandSink _sink;
        private readonly HashSet<string> _allowedScenes;
        private readonly List<ApplyAiGameCommand> _txBuffer = new();
        private bool _txActive;

        public LuaCsWorldRuntimeBindings(
            IAiGameCommandSink sink,
            IEnumerable<string> allowedScenes = null)
        {
            _sink = sink;
            if (allowedScenes != null)
            {
                HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
                foreach (string scene in allowedScenes)
                {
                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        set.Add(scene.Trim());
                    }
                }

                _allowedScenes = set.Count > 0 ? set : null;
            }
        }

        public void Register(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.WorldEdit) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("coreai_world_spawn", new Func<LuaTable, string>(props =>
            {
                if (props == null)
                {
                    throw new ArgumentException("props table is required.");
                }

                string prefab = GetRequiredString(props, "prefab");
                string name = GetRequiredString(props, "name");
                Vector3 position = ValidatePosition(
                    GetOptionalNumber(props, "x", 0d),
                    GetOptionalNumber(props, "y", 0d),
                    GetOptionalNumber(props, "z", 0d));
                Vector3 rotation = ValidateEulerAngles(
                    GetOptionalNumber(props, "rx", 0d),
                    GetOptionalNumber(props, "ry", 0d),
                    GetOptionalNumber(props, "rz", 0d));
                float uniformScale = GetOptionalScale(props, "scale", 0f);
                Vector3 nonUniformScale = new(
                    GetOptionalScale(props, "scaleX", 0f),
                    GetOptionalScale(props, "scaleY", 0f),
                    GetOptionalScale(props, "scaleZ", 0f));

                CoreAiWorldCommandEnvelope env = CoreAiWorldCommandEnvelope.Spawn(
                    prefab,
                    name,
                    position,
                    rotation,
                    uniformScale,
                    nonUniformScale);
                env.stringValue = GetOptionalString(props, "parent", "");
                Publish(env);
                return name;
            }));

            registry.Register("coreai_world_change", new Action<string, LuaTable>((targetName, props) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                if (props == null)
                {
                    throw new ArgumentException("props table is required.");
                }

                bool hasX = Has(props, "x");
                bool hasY = Has(props, "y");
                bool hasZ = Has(props, "z");
                bool hasPosition = hasX || hasY || hasZ;
                bool hasRx = Has(props, "rx");
                bool hasRy = Has(props, "ry");
                bool hasRz = Has(props, "rz");
                bool hasRotation = hasRx || hasRy || hasRz;

                bool hasScale = HasAny(props, "scale", "scaleX", "scaleY", "scaleZ");
                string parent = GetOptionalString(props, "parent", "");
                bool hasParent = !string.IsNullOrWhiteSpace(parent);
                if (!hasPosition && !hasRotation && !hasScale && !hasParent)
                {
                    return;
                }

                Vector3 position = hasPosition
                    ? ValidatePosition(
                        GetOptionalNumber(props, "x", 0d),
                        GetOptionalNumber(props, "y", 0d),
                        GetOptionalNumber(props, "z", 0d))
                    : Vector3.zero;
                Vector3 rotation = hasRotation
                    ? ValidateEulerAngles(
                        GetOptionalNumber(props, "rx", 0d),
                        GetOptionalNumber(props, "ry", 0d),
                        GetOptionalNumber(props, "rz", 0d))
                    : Vector3.zero;
                float uniformScale = GetOptionalScale(props, "scale", 0f);
                Vector3 nonUniformScale = new(
                    GetOptionalScale(props, "scaleX", 0f),
                    GetOptionalScale(props, "scaleY", 0f),
                    GetOptionalScale(props, "scaleZ", 0f));

                Publish(CoreAiWorldCommandEnvelope.Change(
                    name,
                    position,
                    hasPosition,
                    hasX,
                    hasY,
                    hasZ,
                    rotation,
                    hasRotation,
                    hasRx,
                    hasRy,
                    hasRz,
                    uniformScale,
                    nonUniformScale,
                    hasScale,
                    parent));
            }));

            registry.Register("coreai_world_destroy", new Action<string>(targetName =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Destroy(name));
            }));

            registry.Register("coreai_world_load_scene", new Action<string>(sceneName =>
            {
                string scene = (sceneName ?? "").Trim();
                if (string.IsNullOrEmpty(scene))
                {
                    return;
                }

                if (_allowedScenes != null && !_allowedScenes.Contains(scene))
                {
                    throw new ArgumentException($"scene '{scene}' is not in the allowed scene list.");
                }

                Publish(CoreAiWorldCommandEnvelope.LoadScene(scene));
            }));

            registry.Register("coreai_world_reload_scene",
                new Action(() => { Publish(CoreAiWorldCommandEnvelope.ReloadScene()); }));

            registry.Register("coreai_world_set_active", new Action<string, bool>((targetName, active) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.SetActive(name, active));
            }));

            registry.Register("coreai_world_set_color", new Action<string, string>((targetName, htmlColor) =>
            {
                string name = (targetName ?? "").Trim();
                string color = (htmlColor ?? "").Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(color))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.SetColor(name, color));
            }));

            registry.Register("coreai_world_spawn_batch", new Func<LuaTable, int>(entries =>
            {
                if (entries == null)
                {
                    throw new ArgumentException("entries table is required.");
                }

                List<CoreAiWorldCommandEnvelope> commands = new();
                foreach (KeyValuePair<LuaValue, LuaValue> pair in entries)
                {
                    if (pair.Value.Type != LuaValueType.Table)
                    {
                        throw new ArgumentException("spawn_batch entries must be tables.");
                    }

                    if (commands.Count >= MaxBatchSize)
                    {
                        throw new ArgumentException($"spawn_batch exceeds maximum of {MaxBatchSize} entries.");
                    }

                    LuaTable entry = pair.Value.Read<LuaTable>();
                    string prefab = GetRequiredString(entry, "prefab");
                    string name = GetRequiredString(entry, "name");
                    double x = GetRequiredNumber(entry, "x");
                    double y = GetRequiredNumber(entry, "y");
                    double z = GetRequiredNumber(entry, "z");
                    commands.Add(CoreAiWorldCommandEnvelope.Spawn(prefab, name, ValidatePosition(x, y, z)));
                }

                for (int i = 0; i < commands.Count; i++)
                {
                    Publish(commands[i]);
                }

                return commands.Count;
            }));

            registry.Register("coreai_world_grid",
                new Func<string, string, double, double, double, double, double, double, int>((prefabKey, namePrefix,
                    x0, z0, x1, z1, step, y) =>
                {
                    string prefab = (prefabKey ?? "").Trim();
                    string prefix = (namePrefix ?? "").Trim();
                    if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(prefix))
                    {
                        return 0;
                    }

                    if (double.IsNaN(step) || double.IsInfinity(step) || step < 0.5d)
                    {
                        throw new ArgumentException("grid step must be finite and at least 0.5.");
                    }

                    if (x1 < x0 || z1 < z0)
                    {
                        throw new ArgumentException(
                            "grid end coordinates must be greater than or equal to start coordinates.");
                    }

                    ValidatePosition(x0, y, z0);
                    ValidatePosition(x1, y, z1);

                    long xCount = (long)Math.Floor((x1 - x0) / step) + 1;
                    long zCount = (long)Math.Floor((z1 - z0) / step) + 1;
                    long total = xCount * zCount;
                    if (xCount <= 0 || zCount <= 0 || total > MaxBatchSize)
                    {
                        throw new ArgumentException($"grid exceeds maximum of {MaxBatchSize} cells.");
                    }

                    List<CoreAiWorldCommandEnvelope> commands = new();
                    for (int ix = 0; ix < xCount; ix++)
                    {
                        for (int iz = 0; iz < zCount; iz++)
                        {
                            double x = x0 + ix * step;
                            double z = z0 + iz * step;
                            string name = $"{prefix}_{ix}_{iz}";
                            commands.Add(CoreAiWorldCommandEnvelope.Spawn(
                                prefab,
                                name,
                                ValidatePosition(x, y, z)));
                        }
                    }

                    for (int i = 0; i < commands.Count; i++)
                    {
                        Publish(commands[i]);
                    }

                    return commands.Count;
                }));

            registry.Register("coreai_world_begin", new Action(() =>
            {
                _txActive = true;
                _txBuffer.Clear();
            }));

            registry.Register("coreai_world_commit", new Func<int>(() =>
            {
                if (!_txActive)
                {
                    throw new InvalidOperationException("no active transaction.");
                }

                int count = _txBuffer.Count;
                for (int i = 0; i < _txBuffer.Count; i++)
                {
                    _sink?.Publish(_txBuffer[i]);
                }

                _txBuffer.Clear();
                _txActive = false;
                return count;
            }));

            registry.Register("coreai_world_rollback", new Func<int>(() =>
            {
                if (!_txActive)
                {
                    throw new InvalidOperationException("no active transaction.");
                }

                int count = _txBuffer.Count;
                _txBuffer.Clear();
                _txActive = false;
                return count;
            }));

            registry.Register("coreai_world_play_animation", new Action<string, string>((targetName, animationName) =>
            {
                string name = (targetName ?? "").Trim();
                string anim = (animationName ?? "").Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(anim))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.PlayAnimation(name, anim));
            }));

            registry.Register("coreai_world_play_sound",
                new Action<string, string, double>((targetName, clipName, volume) =>
                {
                    string name = (targetName ?? "").Trim();
                    string clip = (clipName ?? "").Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        return;
                    }

                    float vol = double.IsNaN(volume) ? 1f : Mathf.Clamp01((float)volume);
                    Publish(CoreAiWorldCommandEnvelope.PlaySound(name, clip, vol));
                }));
        }

        public void AbortTransaction()
        {
            _txBuffer.Clear();
            _txActive = false;
        }

        public void ResetTransactions()
        {
            AbortTransaction();
        }

        private static bool TryValidateCoordinate(double v, out float f)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < -MaxCoordinate || v > MaxCoordinate)
            {
                f = 0f;
                return false;
            }

            f = (float)v;
            return true;
        }

        private static Vector3 ValidatePosition(double x, double y, double z)
        {
            if (!TryValidateCoordinate(x, out float fx) ||
                !TryValidateCoordinate(y, out float fy) ||
                !TryValidateCoordinate(z, out float fz))
            {
                throw new ArgumentException(
                    $"world position must be finite and within +/-{MaxCoordinate:0} per axis.");
            }

            return new Vector3(fx, fy, fz);
        }

        private static Vector3 ValidateEulerAngles(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) ||
                double.IsNaN(y) || double.IsInfinity(y) ||
                double.IsNaN(z) || double.IsInfinity(z))
            {
                throw new ArgumentException("world rotation must be finite on every axis.");
            }

            return new Vector3((float)x, (float)y, (float)z);
        }

        private static float ValidateUniformScale(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) ||
                scale < MinScale || scale > MaxScale)
            {
                throw new ArgumentException($"scale must be finite and within [{MinScale}; {MaxScale}].");
            }

            return (float)scale;
        }

        private static string GetRequiredString(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.String || string.IsNullOrWhiteSpace(value.Read<string>()))
            {
                throw new ArgumentException($"'{key}' must be a non-empty string.");
            }

            return value.Read<string>().Trim();
        }

        private static double GetRequiredNumber(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return value.Read<double>();
        }

        private static double GetOptionalNumber(LuaTable table, string key, double fallback)
        {
            LuaValue value = table[key];
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return value.Read<double>();
        }

        private static string GetOptionalString(LuaTable table, string key, string fallback)
        {
            LuaValue value = table[key];
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (value.Type == LuaValueType.Number)
            {
                double num = value.Read<double>();
                return double.IsNaN(num) ? fallback : num.ToString("G");
            }

            if (value.Type != LuaValueType.String)
            {
                throw new ArgumentException($"'{key}' must be a string.");
            }

            return value.Read<string>().Trim();
        }

        private static float GetOptionalScale(LuaTable table, string key, float fallback)
        {
            LuaValue value = table[key];
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return ValidateUniformScale(value.Read<double>());
        }

        private static bool HasAny(LuaTable table, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (Has(table, key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Has(LuaTable table, string key)
        {
            return table[key].Type != LuaValueType.Nil;
        }

        private void Publish(CoreAiWorldCommandEnvelope env)
        {
            if (env == null)
            {
                return;
            }

            string json = JsonUtility.ToJson(env, false);
            ApplyAiGameCommand command = new()
            {
                CommandTypeId = WorldCommand,
                JsonPayload = json,
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "world_command",
                SourceTag = "lua:world_command"
            };

            if (_txActive)
            {
                _txBuffer.Add(command);
                if (_txBuffer.Count > MaxTransactionBuffer)
                {
                    _txBuffer.Clear();
                    _txActive = false;
                    throw new InvalidOperationException("transaction buffer overflow; rolled back");
                }

                return;
            }

            _sink?.Publish(command);
        }
    }
}
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Registers world-command Lua APIs for CoreAI scripts.
    /// </summary>
    /// <remarks>
    /// Transactions are single-at-a-time per bindings instance and intended for main-thread use.
    /// </remarks>
    public sealed class CoreAiWorldLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        /// <summary>Coordinates beyond this magnitude are rejected (NaN/Infinity always are).</summary>
        public const double MaxCoordinate = 100_000d;

        /// <summary>Maximum world commands accepted by batch helpers.</summary>
        public const int MaxBatchSize = 100;

        /// <summary>Accepted <c>set_props.scale</c> range (mirrors the executor's clamp).</summary>
        public const double MinScale = 0.01d;

        /// <summary>Accepted <c>set_props.scale</c> range (mirrors the executor's clamp).</summary>
        public const double MaxScale = 100d;

        private const int MaxTransactionBuffer = 256;

        private readonly IAiGameCommandSink _sink;
        private readonly HashSet<string> _allowedScenes;
        private readonly List<ApplyAiGameCommand> _txBuffer = new();
        private bool _txActive;

        /// <param name="sink">Command sink that marshals world commands to the main thread.</param>
        /// <param name="allowedScenes">
        /// Optional whitelist for <c>coreai_world_load_scene</c>. When null or empty any scene from
        /// Build Settings stays loadable (legacy behavior); otherwise only listed names pass.
        /// </param>
        public CoreAiWorldLuaRuntimeBindings(
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

        /// <summary>
        /// Discards any unfinished transaction. Hosts can call this after a script run fails to
        /// guarantee no stale buffered commands survive into the next script.
        /// </summary>
        public void AbortTransaction()
        {
            _txBuffer.Clear();
            _txActive = false;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("coreai_world_spawn",
                new Func<string, string, double, double, double, string>((prefabKeyOrName, targetName, x, y, z) =>
                {
                    string key = (prefabKeyOrName ?? "").Trim();
                    string name = (targetName ?? "").Trim();
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name))
                    {
                        return "";
                    }

                    Publish(CoreAiWorldCommandEnvelope.Spawn(key, name, ValidatePosition(x, y, z)));
                    return name;
                }));

            registry.Register("coreai_world_move", new Action<string, double, double, double>((targetName, x, y, z) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Move(name, ValidatePosition(x, y, z)));
            }));

            registry.Register("coreai_world_rotate", new Action<string, double, double, double>((targetName, x, y, z) =>
            {
                string name = (targetName ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                Publish(CoreAiWorldCommandEnvelope.Rotate(name, ValidateEulerAngles(x, y, z)));
            }));

            registry.Register("coreai_world_set_transform",
                new Action<string, double, double, double, double, double, double, double>(
                    (targetName, x, y, z, rx, ry, rz, scale) =>
                    {
                        string name = (targetName ?? "").Trim();
                        if (string.IsNullOrEmpty(name))
                        {
                            return;
                        }

                        Publish(CoreAiWorldCommandEnvelope.SetTransform(
                            name,
                            ValidatePosition(x, y, z),
                            ValidateEulerAngles(rx, ry, rz),
                            ValidateUniformScale(scale)));
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

            registry.Register("coreai_world_parent", new Action<string, string>((childName, parentName) =>
            {
                string child = (childName ?? "").Trim();
                if (string.IsNullOrEmpty(child))
                {
                    throw new ArgumentException("child name is required.");
                }

                Publish(CoreAiWorldCommandEnvelope.Parent(child, (parentName ?? "").Trim()));
            }));

            registry.Register("coreai_world_set_props",
                new Action<string, MoonSharp.Interpreter.Table>((targetName, props) =>
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

                    foreach (MoonSharp.Interpreter.TablePair pair in props.Pairs)
                    {
                        if (pair.Key.Type != MoonSharp.Interpreter.DataType.String)
                        {
                            throw new ArgumentException("unsupported property key. Allowed keys: scale, color.");
                        }

                        string key = pair.Key.String;
                        switch (key)
                        {
                            case "scale":
                                if (pair.Value.Type != MoonSharp.Interpreter.DataType.Number)
                                {
                                    throw new ArgumentException("scale must be a number.");
                                }

                                Publish(CoreAiWorldCommandEnvelope.SetScale(
                                    name,
                                    ValidateUniformScale(pair.Value.Number)));
                                break;
                            case "color":
                                if (pair.Value.Type != MoonSharp.Interpreter.DataType.String)
                                {
                                    throw new ArgumentException("color must be a string.");
                                }

                                Publish(CoreAiWorldCommandEnvelope.SetColor(name, pair.Value.String));
                                break;
                            default:
                                throw new ArgumentException(
                                    $"unsupported property '{key}'. Allowed keys: scale, color.");
                        }
                    }
                }));

            registry.Register("coreai_world_spawn_batch",
                new Func<MoonSharp.Interpreter.Table, int>(entries =>
                {
                    if (entries == null)
                    {
                        throw new ArgumentException("entries table is required.");
                    }

                    List<CoreAiWorldCommandEnvelope> commands = new();
                    foreach (MoonSharp.Interpreter.TablePair pair in entries.Pairs)
                    {
                        if (pair.Value.Type != MoonSharp.Interpreter.DataType.Table)
                        {
                            throw new ArgumentException("spawn_batch entries must be tables.");
                        }

                        if (commands.Count >= MaxBatchSize)
                        {
                            throw new ArgumentException($"spawn_batch exceeds maximum of {MaxBatchSize} entries.");
                        }

                        MoonSharp.Interpreter.Table entry = pair.Value.Table;
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

                    int xCount = (int)Math.Floor((x1 - x0) / step) + 1;
                    int zCount = (int)Math.Floor((z1 - z0) / step) + 1;
                    int total = xCount * zCount;
                    if (total > MaxBatchSize)
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
                // A previous script may have died between begin() and commit/rollback (error,
                // instruction budget). The bindings instance is shared, so a stale transaction
                // must not lock out every later script: discard it and start fresh.
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

        private static string GetRequiredString(MoonSharp.Interpreter.Table table, string key)
        {
            MoonSharp.Interpreter.DynValue value = table.Get(key);
            if (value.Type != MoonSharp.Interpreter.DataType.String || string.IsNullOrWhiteSpace(value.String))
            {
                throw new ArgumentException($"'{key}' must be a non-empty string.");
            }

            return value.String.Trim();
        }

        private static double GetRequiredNumber(MoonSharp.Interpreter.Table table, string key)
        {
            MoonSharp.Interpreter.DynValue value = table.Get(key);
            if (value.Type != MoonSharp.Interpreter.DataType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return value.Number;
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

            if (_sink == null)
            {
                return;
            }

            _sink.Publish(command);
        }
    }
}
#endif

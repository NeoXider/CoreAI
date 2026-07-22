using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Scripting;
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

        /// <summary>One nesting level of world-transaction state (buffered commands + open flag).</summary>
        private sealed class TransactionFrame
        {
            public readonly List<ApplyAiGameCommand> Buffer = new();
            public bool Active;
        }

        // WHY: The world bindings are a single shared instance registered into EVERY mod's state, so a
        // nested mods_call runs a DIFFERENT LuaState against this SAME instance. A single _txBuffer/_txActive
        // would let the callee's coreai_world_begin/commit flush or clear the caller's still-open
        // transaction. A stack of frames — one pushed per guarded execution / load chunk — isolates each
        // run: begin/commit/rollback/Publish only ever touch the top frame. The base frame (index 0) is
        // always present so direct use with no enclosing run scope (e.g. the one-off executor) still works.
        private readonly List<TransactionFrame> _txStack = new() { new TransactionFrame() };

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

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.WorldEdit) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
        {
            registry.Register("coreai_world_spawn", new Func<IScriptTable, string>(props =>
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

            registry.Register("coreai_world_change", new Action<string, IScriptTable>((targetName, props) =>
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

            registry.Register("coreai_world_spawn_batch", new Func<IScriptTable, int>(entries =>
            {
                if (entries == null)
                {
                    throw new ArgumentException("entries table is required.");
                }

                List<CoreAiWorldCommandEnvelope> commands = new();
                foreach (KeyValuePair<object, object> pair in entries.Pairs)
                {
                    if (pair.Value is not IScriptTable entry)
                    {
                        throw new ArgumentException("spawn_batch entries must be tables.");
                    }

                    if (commands.Count >= MaxBatchSize)
                    {
                        throw new ArgumentException($"spawn_batch exceeds maximum of {MaxBatchSize} entries.");
                    }

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
                TransactionFrame frame = CurrentFrame;
                frame.Active = true;
                frame.Buffer.Clear();
            }));

            registry.Register("coreai_world_commit", new Func<int>(() =>
            {
                TransactionFrame frame = CurrentFrame;
                if (!frame.Active)
                {
                    throw new InvalidOperationException("no active transaction.");
                }

                int count = frame.Buffer.Count;
                for (int i = 0; i < frame.Buffer.Count; i++)
                {
                    _sink?.Publish(frame.Buffer[i]);
                }

                frame.Buffer.Clear();
                frame.Active = false;
                return count;
            }));

            registry.Register("coreai_world_rollback", new Func<int>(() =>
            {
                TransactionFrame frame = CurrentFrame;
                if (!frame.Active)
                {
                    throw new InvalidOperationException("no active transaction.");
                }

                int count = frame.Buffer.Count;
                frame.Buffer.Clear();
                frame.Active = false;
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

        /// <summary>The active (top-of-stack) transaction frame; never null (the base frame is permanent).</summary>
        private TransactionFrame CurrentFrame => _txStack[_txStack.Count - 1];

        public void AbortTransaction()
        {
            TransactionFrame frame = CurrentFrame;
            frame.Buffer.Clear();
            frame.Active = false;
        }

        public void ResetTransactions()
        {
            AbortTransaction();
        }

        /// <inheritdoc />
        public void PushTransactionScope()
        {
            _txStack.Add(new TransactionFrame());
        }

        /// <inheritdoc />
        public void PopTransactionScope()
        {
            // WHY: Never remove the base frame; an unbalanced pop just clears it so a leaked transaction
            // cannot bleed into later direct (non-nested) use.
            if (_txStack.Count <= 1)
            {
                AbortTransaction();
                return;
            }

            _txStack.RemoveAt(_txStack.Count - 1);
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

        private static string GetRequiredString(IScriptTable table, string key)
        {
            if (table[key] is not string s || string.IsNullOrWhiteSpace(s))
            {
                throw new ArgumentException($"'{key}' must be a non-empty string.");
            }

            return s.Trim();
        }

        private static double GetRequiredNumber(IScriptTable table, string key)
        {
            if (table[key] is not double d)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return d;
        }

        private static double GetOptionalNumber(IScriptTable table, string key, double fallback)
        {
            object value = table[key];
            if (value == null)
            {
                return fallback;
            }

            if (value is not double d)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return d;
        }

        private static string GetOptionalString(IScriptTable table, string key, string fallback)
        {
            object value = table[key];
            if (value == null)
            {
                return fallback;
            }

            if (value is double num)
            {
                return double.IsNaN(num) ? fallback : num.ToString("G");
            }

            if (value is not string s)
            {
                throw new ArgumentException($"'{key}' must be a string.");
            }

            return s.Trim();
        }

        private static float GetOptionalScale(IScriptTable table, string key, float fallback)
        {
            object value = table[key];
            if (value == null)
            {
                return fallback;
            }

            if (value is not double d)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return ValidateUniformScale(d);
        }

        private static bool HasAny(IScriptTable table, params string[] keys)
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

        private static bool Has(IScriptTable table, string key)
        {
            return table.Has(key);
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

            TransactionFrame frame = CurrentFrame;
            if (frame.Active)
            {
                frame.Buffer.Add(command);
                if (frame.Buffer.Count > MaxTransactionBuffer)
                {
                    frame.Buffer.Clear();
                    frame.Active = false;
                    throw new InvalidOperationException("transaction buffer overflow; rolled back");
                }

                return;
            }

            _sink?.Publish(command);
        }
    }
}

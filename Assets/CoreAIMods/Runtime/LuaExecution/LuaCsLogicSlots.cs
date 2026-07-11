using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using CoreAI.Sandbox.LuaCs;
using Lua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) counterpart of <see cref="CoreAI.Ai.LuaLogicSlots"/>: named
    /// overridable decision points (damage formula, loot table, price curve, ...). The game declares
    /// slots and calls <see cref="TryInvokeNumber"/> &amp; co. at the point of use, falling back to its
    /// C# default when no Lua override is installed. Sandboxed scripts redefine a slot with
    /// <c>logic_define(name, fn)</c> and remove it with <c>logic_reset(name)</c>.
    /// <para>
    /// Fail-open policy: when an override throws or exceeds its budget the override is removed and the
    /// call reports "not overridden", so a broken script degrades to vanilla behavior instead of
    /// breaking the game loop every frame. The error is logged and kept in <see cref="LastError"/>.
    /// </para>
    /// <para>
    /// VM-agnostic surface: unlike the MoonSharp version — whose public <c>TryInvoke</c> leaks a
    /// <c>DynValue</c> — the general <see cref="TryInvoke(string, out object, object[])"/> here returns
    /// a plain CLR value (double/bool/string/null/boxed) with no VM type in the signature, so callers do
    /// not depend on the concrete Lua VM. The typed helpers do the type checks internally.
    /// </para>
    /// </summary>
    public sealed class LuaCsLogicSlots
    {
        /// <summary>Wall-clock budget for a single slot invocation.</summary>
        public const int DefaultInvokeTimeoutMs = 200;

        /// <summary>Instruction budget for a single slot invocation.</summary>
        public const long DefaultInvokeMaxSteps = 200_000;

        private sealed class OverrideEntry
        {
            public LuaFunction Fn;
            public LuaState State;
        }

        private readonly object _gate = new();
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OverrideEntry> _overrides = new(StringComparer.Ordinal);
        private readonly LuaCsExecutionGuard _guard;
        private readonly ILog _log;

        /// <summary>Description of the most recent override failure, or empty.</summary>
        public string LastError { get; private set; } = "";

        public LuaCsLogicSlots(
            ILog log = null,
            int invokeTimeoutMs = DefaultInvokeTimeoutMs,
            long invokeMaxSteps = DefaultInvokeMaxSteps)
        {
            _log = log;
            _guard = new LuaCsExecutionGuard(invokeTimeoutMs, invokeMaxSteps);
        }

        /// <summary>
        /// Declares a slot as overridable. Scripts can only define slots the game declared, so the game
        /// stays in control of which decision points are moddable.
        /// </summary>
        public void DeclareSlot(string name)
        {
            string slot = Normalize(name);
            if (slot.Length == 0)
            {
                throw new ArgumentException("Slot name is required.", nameof(name));
            }

            lock (_gate)
            {
                _declared.Add(slot);
            }
        }

        /// <summary>Names of all declared slots.</summary>
        public IReadOnlyCollection<string> DeclaredSlots
        {
            get
            {
                lock (_gate)
                {
                    return new List<string>(_declared);
                }
            }
        }

        /// <summary>True when a Lua override is currently installed for the slot.</summary>
        public bool IsOverridden(string name)
        {
            lock (_gate)
            {
                return _overrides.ContainsKey(Normalize(name));
            }
        }

        /// <summary>Removes the Lua override for a slot (C# default applies again).</summary>
        public void Reset(string name)
        {
            lock (_gate)
            {
                _overrides.Remove(Normalize(name));
            }
        }

        /// <summary>Removes every installed override.</summary>
        public void ResetAll()
        {
            lock (_gate)
            {
                _overrides.Clear();
            }
        }

        /// <summary>
        /// Registers <c>logic_define(name, fn)</c>, <c>logic_reset(name)</c> and <c>logic_list()</c> on
        /// the sandbox registry.
        /// </summary>
        public void RegisterApis(LuaCsApiRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            // logic_define is registered as a raw callback so it can capture ctx.State: a LuaFunction
            // does not carry its owning LuaState, and TryInvoke needs that state to call the override
            // back under the guard.
            registry.RegisterCallback("logic_define", (ctx, ct) =>
            {
                string name = ctx.HasArgument(0) ? ctx.GetArgument(0).Read<string>() : null;
                LuaValue fnValue = ctx.HasArgument(1) ? ctx.GetArgument(1) : LuaValue.Nil;
                bool defined = Define(name, fnValue, ctx.State);
                return new ValueTask<int>(ctx.Return(new LuaValue(defined)));
            });

            registry.Register("logic_reset", new Action<string>(Reset));
            registry.Register("logic_list", new Func<List<object>>(ListSlots));
        }

        private bool Define(string name, LuaValue fnValue, LuaState state)
        {
            string slot = Normalize(name);
            if (fnValue.Type != LuaValueType.Function)
            {
                throw new ArgumentException("logic_define: second argument must be a function.");
            }

            LuaFunction fn = fnValue.Read<LuaFunction>();

            lock (_gate)
            {
                if (!_declared.Contains(slot))
                {
                    throw new ArgumentException(
                        $"logic_define: slot '{slot}' is not declared by the game. Use logic_list().");
                }

                _overrides[slot] = new OverrideEntry { Fn = fn, State = state };
            }

            return true;
        }

        private List<object> ListSlots()
        {
            List<object> result = new();
            lock (_gate)
            {
                foreach (string slot in _declared)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "name", slot },
                        { "overridden", _overrides.ContainsKey(slot) }
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Invokes the slot override when installed, returning the first result as a plain CLR value.
        /// Returns false (and leaves <paramref name="result"/> null) when the slot has no override or the
        /// override failed — callers fall back to the C# default. VM-agnostic: no Lua VM type appears in
        /// the signature.
        /// </summary>
        public bool TryInvoke(string name, out object result, params object[] args)
        {
            if (TryInvokeRaw(name, out LuaValue raw, args))
            {
                result = ToClr(raw);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>Numeric slot helper (formulas). False → use the C# default.</summary>
        public bool TryInvokeNumber(string name, out double value, params object[] args)
        {
            value = 0d;
            if (!TryInvokeRaw(name, out LuaValue result, args) || result.Type != LuaValueType.Number)
            {
                return false;
            }

            value = result.Read<double>();
            return double.IsFinite(value);
        }

        /// <summary>Boolean slot helper (predicates). False → use the C# default.</summary>
        public bool TryInvokeBool(string name, out bool value, params object[] args)
        {
            value = false;
            if (!TryInvokeRaw(name, out LuaValue result, args) || result.Type != LuaValueType.Boolean)
            {
                return false;
            }

            value = result.Read<bool>();
            return true;
        }

        /// <summary>String slot helper (tables/ids serialized by the script). False → use the C# default.</summary>
        public bool TryInvokeString(string name, out string value, params object[] args)
        {
            value = "";
            if (!TryInvokeRaw(name, out LuaValue result, args) || result.Type != LuaValueType.String)
            {
                return false;
            }

            value = result.Read<string>() ?? "";
            return true;
        }

        // Runs the override synchronously under the guard, returning the raw first Lua-CSharp result.
        // Formulas never yield, so the sync drive (inside the guard) is safe. Internal: the public
        // surface stays VM-agnostic.
        private bool TryInvokeRaw(string name, out LuaValue result, params object[] args)
        {
            result = LuaValue.Nil;
            string slot = Normalize(name);
            OverrideEntry entry;
            lock (_gate)
            {
                if (!_overrides.TryGetValue(slot, out entry))
                {
                    return false;
                }
            }

            try
            {
                LuaValue[] luaArgs = args == null ? Array.Empty<LuaValue>() : new LuaValue[args.Length];
                for (int i = 0; i < luaArgs.Length; i++)
                {
                    luaArgs[i] = HostToLua(args[i]);
                }

                LuaValue[] results =
                    _guard.Execute(entry.State, entry.Fn, CancellationToken.None, luaArgs);
                result = results.Length > 0 ? results[0] : LuaValue.Nil;
                return true;
            }
            catch (Exception ex)
            {
                // Fail open: a broken override must not break the game loop on every call.
                Reset(slot);
                LastError = $"slot '{slot}': {ex.Message}";
                _log?.Error($"[LuaCsLogicSlots] Override for '{slot}' failed and was reset: {ex}");
                result = LuaValue.Nil;
                return false;
            }
        }

        private static object ToClr(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return null;
                case LuaValueType.Boolean:
                    return value.Read<bool>();
                case LuaValueType.Number:
                    return value.Read<double>();
                case LuaValueType.String:
                    return value.Read<string>();
                default:
                    return value.Read<object>();
            }
        }

        private static LuaValue HostToLua(object arg)
        {
            switch (arg)
            {
                case null:
                    return LuaValue.Nil;
                case string s:
                    return new LuaValue(s);
                case bool b:
                    return new LuaValue(b);
                case double d:
                    return new LuaValue(d);
                case int i:
                    return new LuaValue((double)i);
                case long l:
                    return new LuaValue((double)l);
                case float f:
                    return new LuaValue((double)f);
                default:
                    return LuaValue.FromObject(arg);
            }
        }

        private static string Normalize(string name)
        {
            return (name ?? "").Trim();
        }
    }
}
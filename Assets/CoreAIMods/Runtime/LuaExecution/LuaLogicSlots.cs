using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;

namespace CoreAI.Ai
{
    /// <summary>
    /// Named logic slots: the game declares overridable decision points (damage formula, loot
    /// table, price curve, ...) and calls <see cref="TryInvokeNumber"/> & co. at the point of use,
    /// falling back to its C# default when no Lua override is installed. Sandboxed scripts
    /// redefine a slot with <c>logic_define(name, fn)</c> and remove it with <c>logic_reset(name)</c>,
    /// so the LLM can change mechanics at runtime without the game ever depending on Lua.
    /// <para>
    /// Fail-open policy: when an override throws or exceeds its budget the override is removed and
    /// the call reports "not overridden", so a broken script degrades to vanilla behavior instead
    /// of breaking the game loop every frame. The error is logged and kept in <see cref="LastError"/>.
    /// </para>
    /// </summary>
    public sealed class LuaLogicSlots
    {
        /// <summary>Wall-clock budget for a single slot invocation.</summary>
        public const int DefaultInvokeTimeoutMs = 200;

        /// <summary>Instruction budget for a single slot invocation.</summary>
        public const long DefaultInvokeMaxSteps = 200_000;

        private readonly object _gate = new();
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Closure> _overrides = new(StringComparer.Ordinal);
        private readonly LuaExecutionGuard _guard;
        private readonly ILog _log;

        /// <summary>Description of the most recent override failure, or empty.</summary>
        public string LastError { get; private set; } = "";

        public LuaLogicSlots(
            ILog log = null,
            int invokeTimeoutMs = DefaultInvokeTimeoutMs,
            long invokeMaxSteps = DefaultInvokeMaxSteps)
        {
            _log = log;
            _guard = new LuaExecutionGuard(invokeTimeoutMs, invokeMaxSteps);
        }

        /// <summary>
        /// Declares a slot as overridable. Scripts can only define slots the game declared,
        /// so the game stays in control of which decision points are moddable.
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
        /// Registers <c>logic_define(name, fn)</c>, <c>logic_reset(name)</c> and <c>logic_list()</c>
        /// on the sandbox registry.
        /// </summary>
        public void RegisterApis(LuaApiRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            registry.Register("logic_define", new Func<string, Closure, bool>(Define));
            registry.Register("logic_reset", new Action<string>(Reset));
            registry.Register("logic_list", new Func<List<object>>(ListSlots));
        }

        private bool Define(string name, Closure fn)
        {
            string slot = Normalize(name);
            if (fn == null)
            {
                throw new ArgumentException("logic_define: second argument must be a function.");
            }

            lock (_gate)
            {
                if (!_declared.Contains(slot))
                {
                    throw new ArgumentException(
                        $"logic_define: slot '{slot}' is not declared by the game. Use logic_list().");
                }

                _overrides[slot] = fn;
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
        /// Invokes the slot override when installed. Returns false (and leaves <paramref name="result"/>
        /// nil) when the slot has no override or the override failed — callers fall back to the C# default.
        /// </summary>
        public bool TryInvoke(string name, out DynValue result, params object[] args)
        {
            result = DynValue.Nil;
            string slot = Normalize(name);
            Closure fn;
            lock (_gate)
            {
                if (!_overrides.TryGetValue(slot, out fn))
                {
                    return false;
                }
            }

            Script owner = fn.OwnerScript;
            try
            {
                DynValue[] dynArgs = args == null ? Array.Empty<DynValue>() : new DynValue[args.Length];
                for (int i = 0; i < dynArgs.Length; i++)
                {
                    dynArgs[i] = DynValue.FromObject(owner, args[i]);
                }

                result = _guard.Execute(owner, DynValue.FromObject(owner, fn), dynArgs);
                return true;
            }
            catch (Exception ex)
            {
                // Fail open: a broken override must not break the game loop on every call.
                Reset(slot);
                LastError = $"slot '{slot}': {ex.Message}";
                _log?.Error($"[LuaLogicSlots] Override for '{slot}' failed and was reset: {ex}");
                result = DynValue.Nil;
                return false;
            }
        }

        /// <summary>Numeric slot helper (formulas). False → use the C# default.</summary>
        public bool TryInvokeNumber(string name, out double value, params object[] args)
        {
            value = 0d;
            if (!TryInvoke(name, out DynValue result, args) || result.Type != DataType.Number)
            {
                return false;
            }

            value = result.Number;
            return double.IsFinite(value);
        }

        /// <summary>Boolean slot helper (predicates). False → use the C# default.</summary>
        public bool TryInvokeBool(string name, out bool value, params object[] args)
        {
            value = false;
            if (!TryInvoke(name, out DynValue result, args) || result.Type != DataType.Boolean)
            {
                return false;
            }

            value = result.Boolean;
            return true;
        }

        /// <summary>String slot helper (tables/ids serialized by the script). False → use the C# default.</summary>
        public bool TryInvokeString(string name, out string value, params object[] args)
        {
            value = "";
            if (!TryInvoke(name, out DynValue result, args) || result.Type != DataType.String)
            {
                return false;
            }

            value = result.String ?? "";
            return true;
        }

        private static string Normalize(string name)
        {
            return (name ?? "").Trim();
        }
    }
}

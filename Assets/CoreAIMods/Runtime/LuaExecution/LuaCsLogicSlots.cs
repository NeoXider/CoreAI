using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Logging;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Ai.LuaLogicSlots"/>: named overridable decision
    /// points (damage formula, loot table, price curve, ...). The game declares slots and calls
    /// <see cref="TryInvokeNumber"/> &amp; co. at the point of use, falling back to its C# default when
    /// no Lua override is installed. Sandboxed scripts redefine a slot with
    /// <c>logic_define(name, fn)</c> and remove it with <c>logic_reset(name)</c>.
    /// <para>
    /// Fail-open policy: when an override throws or exceeds its budget the override is removed and the
    /// call reports "not overridden", so a broken script degrades to vanilla behavior instead of
    /// breaking the game loop every frame. The failure is attributed: the error is logged, kept in
    /// <see cref="LastError"/>, and raised via <see cref="OverrideFailed"/> with the defining mod's id
    /// so the host can route it into the mod-error diagnostics channel instead of a silent revert.
    /// </para>
    /// <para>
    /// Ownership: each override records the mod id passed to <see cref="RegisterApis"/> (null for
    /// ownerless surfaces such as the one-off executor). <see cref="ClearOwnedBy"/> removes a mod's
    /// overrides on unload/reload/quarantine so a dead or broken mod's formula is never invoked again.
    /// </para>
    /// <para>
    /// VM-agnostic surface: overrides are stored as opaque <see cref="IScriptState"/>/callable handles
    /// and invoked through the <see cref="IScriptExecutionGuard"/> seam, so no VM type appears anywhere
    /// in this class. <see cref="TryInvoke(string, out object, object[])"/> returns a plain CLR value
    /// (double/bool/string/null/boxed) and the typed helpers do the kind checks internally.
    /// </para>
    /// </summary>
    public sealed class LuaCsLogicSlots
    {
        /// <summary>Wall-clock budget for a single slot invocation.</summary>
        public const int DefaultInvokeTimeoutMs = 200;

        /// <summary>Instruction budget for a single slot invocation.</summary>
        public const long DefaultInvokeMaxSteps = 200_000;

        internal sealed class OverrideEntry
        {
            public object Fn;
            public IScriptState State;

            /// <summary>Id of the mod whose registry defined this override; null/empty for ownerless surfaces.</summary>
            public string OwnerModId;
        }

        /// <summary>
        /// The overrides installed when a mod chunk's build started (<see cref="CaptureOverrides"/>), so a
        /// build that fails can put back what its chunk changed (<see cref="RestoreAfterFailedBuild"/>).
        /// </summary>
        internal sealed class OverrideSnapshot
        {
            internal OverrideSnapshot(LuaCsLogicSlots owner, Dictionary<string, OverrideEntry> entries)
            {
                Owner = owner;
                Entries = entries;
            }

            /// <summary>The surface the snapshot was taken from, which alone may restore it.</summary>
            internal LuaCsLogicSlots Owner { get; }

            /// <summary>Slot name to the override installed at capture time.</summary>
            internal Dictionary<string, OverrideEntry> Entries { get; }
        }

        private readonly object _gate = new();
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OverrideEntry> _overrides = new(StringComparer.Ordinal);
        private readonly IScriptExecutionGuard _guard;
        private readonly IValueMarshaller _marshaller;
        private readonly ILog _log;
        private readonly Func<IScriptState, IScriptState> _stateResolver;
        private readonly Func<LuaCsLogicSlots> _activeProvider;
        private event Action<string, string, string> _overrideFailed;
        private Action _invocationStarting;
        private Action _invocationFinished;

        /// <summary>Description of the most recent override failure, or empty.</summary>
        public string LastError
        {
            get => ActiveTarget?.LastError ?? _lastError;
            private set => _lastError = value;
        }

        private string _lastError = "";

        /// <summary>
        /// Raised when an installed override throws or exceeds its budget and is reset to vanilla:
        /// (ownerModId, slot, error). <c>ownerModId</c> is empty for ownerless overrides (one-off
        /// scripts). Subscribers are isolated: one throwing subscriber never skips the rest.
        /// </summary>
        public event Action<string, string, string> OverrideFailed
        {
            add
            {
                _overrideFailed += value;
                LuaCsLogicSlots target = ActiveTarget;
                if (target != null)
                {
                    target.OverrideFailed += value;
                }
            }
            remove
            {
                _overrideFailed -= value;
                LuaCsLogicSlots target = ActiveTarget;
                if (target != null)
                {
                    target.OverrideFailed -= value;
                }
            }
        }

        public LuaCsLogicSlots(
            ILog log = null,
            int invokeTimeoutMs = DefaultInvokeTimeoutMs,
            long invokeMaxSteps = DefaultInvokeMaxSteps,
            Func<IScriptState, IScriptState> stateResolver = null)
        {
            _log = log;
            _stateResolver = stateResolver;

            // WHY: Slots are engine-passive — they never create states, only call back into states handed
            // to logic_define — so the Lua-CSharp guard/marshaller pair is bound here directly instead of
            // carrying a full IScriptEngine dependency through every host constructor.
            _guard = new LuaCsScriptExecutionGuard(new ExecutionBudget(invokeTimeoutMs, invokeMaxSteps));
            _marshaller = LuaCsValueMarshaller.Instance;
        }

        internal LuaCsLogicSlots(Func<LuaCsLogicSlots> activeProvider)
            : this()
        {
            _activeProvider = activeProvider
                ?? throw new ArgumentNullException(nameof(activeProvider));
        }

        /// <summary>
        /// Declares a slot as overridable. Scripts can only define slots the game declared, so the game
        /// stays in control of which decision points are moddable.
        /// </summary>
        public void DeclareSlot(string name)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                target.DeclareSlot(name);
                return;
            }

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
                LuaCsLogicSlots target = ActiveTarget;
                if (target != null)
                {
                    return target.DeclaredSlots;
                }

                lock (_gate)
                {
                    return new List<string>(_declared);
                }
            }
        }

        /// <summary>True when a Lua override is currently installed for the slot.</summary>
        public bool IsOverridden(string name)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.IsOverridden(name);
            }

            lock (_gate)
            {
                return _overrides.ContainsKey(Normalize(name));
            }
        }

        /// <summary>Removes the Lua override for a slot (C# default applies again).</summary>
        public void Reset(string name)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                target.Reset(name);
                return;
            }

            lock (_gate)
            {
                _overrides.Remove(Normalize(name));
            }
        }

        /// <summary>Removes every installed override.</summary>
        public void ResetAll()
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                target.ResetAll();
                return;
            }

            lock (_gate)
            {
                _overrides.Clear();
            }
        }

        /// <summary>
        /// Registers <c>logic_define(name, fn)</c>, <c>logic_reset(name)</c> and <c>logic_list()</c> on
        /// the sandbox registry. <paramref name="ownerModId"/> stamps every override defined through
        /// this registry with the owning mod, so <see cref="ClearOwnedBy"/> can remove them when the
        /// mod is unloaded, reloaded, or quarantined; null/empty marks ownerless surfaces (one-off
        /// scripts) whose overrides only ever leave via <c>logic_reset</c>/<see cref="ResetAll"/>.
        /// </summary>
        public void RegisterApis(IScriptFunctionRegistry registry, string ownerModId = null)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                target.RegisterApis(registry, ownerModId);
                return;
            }

            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            string owner = Normalize(ownerModId);

            // WHY: logic_define is registered as a var-args callback so it can capture the calling state:
            // a bare function handle does not carry its owning state, and TryInvoke needs that state to
            // call the override back under the guard.
            registry.RegisterVarArgs("logic_define", call =>
            {
                string name = call.GetString(0);
                object fn = call.GetKind(1) == ScriptValueKind.Function ? call.GetArgument(1) : null;
                bool defined = Define(name, fn, call.State, owner);
                return ScriptCallResult.Return(defined);
            });

            registry.Register("logic_reset", new Action<string>(Reset));
            registry.Register("logic_list", new Func<List<object>>(ListSlots));
        }

        /// <summary>
        /// Lets the mod runtime that owns this surface count a running formula as mod code:
        /// <paramref name="starting"/> runs before every override call this instance makes and
        /// <paramref name="finished"/> once that call has returned or failed, after the fail-open
        /// reset. Null for both detaches.
        /// </summary>
        /// <remarks>
        /// WHY: host code calls a slot directly, outside any hook or scheduler thread, so without this
        /// bracket the runtime saw no mod code running while a formula ran. A formula that kicked its
        /// own player then had its mod unloaded under its feet, and the rest of the formula resolved
        /// its actor as the host.
        /// </remarks>
        internal void SetInvocationScope(Action starting, Action finished)
        {
            lock (_gate)
            {
                _invocationStarting = starting;
                _invocationFinished = finished;
            }
        }

        private bool Define(string name, object fn, IScriptState state, string ownerModId)
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

                IScriptState ownerState = _stateResolver?.Invoke(state) ?? state;
                _overrides[slot] = new OverrideEntry
                    { Fn = fn, State = ownerState, OwnerModId = ownerModId };
            }

            return true;
        }

        /// <summary>
        /// Removes every override defined by the given mod, so an unloaded/reloaded/quarantined mod's
        /// formulas are never invoked again (callers fall back to the C# default). Pass
        /// <paramref name="except"/> to keep overrides bound to that live script state — the reload
        /// path uses this so the replacement chunk's fresh <c>logic_define</c> calls survive while the
        /// old instance's are cleared. Returns the number of overrides removed.
        /// </summary>
        public int ClearOwnedBy(string modId, IScriptState except = null)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.ClearOwnedBy(modId, except);
            }

            string owner = Normalize(modId);
            if (owner.Length == 0)
            {
                return 0;
            }

            List<string> removed = new();
            lock (_gate)
            {
                foreach (KeyValuePair<string, OverrideEntry> pair in _overrides)
                {
                    if (string.Equals(pair.Value.OwnerModId, owner, StringComparison.Ordinal) &&
                        !SameUnderlyingState(pair.Value.State, except))
                    {
                        removed.Add(pair.Key);
                    }
                }

                foreach (string slot in removed)
                {
                    _overrides.Remove(slot);
                }
            }

            return removed.Count;
        }

        /// <summary>
        /// Captures the installed overrides before a mod chunk is built, for
        /// <see cref="RestoreAfterFailedBuild"/>.
        /// </summary>
        internal OverrideSnapshot CaptureOverrides()
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.CaptureOverrides();
            }

            lock (_gate)
            {
                return new OverrideSnapshot(
                    this, new Dictionary<string, OverrideEntry>(_overrides, StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// Undoes what the chunk of a failed build of <paramref name="modId"/> did to the overrides since
        /// <paramref name="snapshot"/>: a formula the candidate state (<paramref name="candidateState"/>)
        /// defined is removed, and a formula it replaced or reset is put back while
        /// <paramref name="isOwnerLive"/> still reports its owning mod loaded (ownerless formulas always
        /// are). Changes made by any other code during the build are left alone. Returns the number of
        /// slots changed.
        /// </summary>
        /// <remarks>
        /// WHY a failed build needs this at all: <c>logic_define</c> installs its formula at once,
        /// while the chunk is still running, so a chunk that failed after it left a mod that never
        /// loaded answering the game's formula calls, and a failed reload replaced the formulas of the
        /// mod it promised to leave untouched (A2-04).
        /// WHY a formula that vanished since the capture is put back only when it belonged to the
        /// candidate's own mod id: <c>logic_reset</c> records no caller, and a reload chunk resetting its
        /// own mod's formulas is the change a failed reload must undo. Another mod's formula that vanished
        /// during the build was reset by that mod's code or its unload, and stays removed.
        /// </remarks>
        internal int RestoreAfterFailedBuild(OverrideSnapshot snapshot, string modId,
            IScriptState candidateState, Func<string, bool> isOwnerLive)
        {
            if (snapshot == null)
            {
                return 0;
            }

            if (!ReferenceEquals(snapshot.Owner, this))
            {
                return snapshot.Owner.RestoreAfterFailedBuild(snapshot, modId, candidateState, isOwnerLive);
            }

            string owner = Normalize(modId);
            if (owner.Length == 0 || candidateState == null)
            {
                return 0;
            }

            // WHY decided before taking the gate: the callback reads the runtime's own lock, and the
            // runtime calls into this surface without holding it; asking it under this gate would nest
            // the two locks in the opposite order.
            Dictionary<string, bool> ownerLive = new(StringComparer.Ordinal);
            foreach (OverrideEntry before in snapshot.Entries.Values)
            {
                string beforeOwner = before.OwnerModId ?? "";
                if (!ownerLive.ContainsKey(beforeOwner))
                {
                    ownerLive[beforeOwner] = beforeOwner.Length == 0 || isOwnerLive == null
                                             || isOwnerLive(beforeOwner);
                }
            }

            int changed = 0;
            lock (_gate)
            {
                HashSet<string> slots = new(snapshot.Entries.Keys, StringComparer.Ordinal);
                slots.UnionWith(_overrides.Keys);
                foreach (string slot in slots)
                {
                    _overrides.TryGetValue(slot, out OverrideEntry current);
                    snapshot.Entries.TryGetValue(slot, out OverrideEntry before);
                    bool definedByCandidate = current != null
                                              && !ReferenceEquals(current, before)
                                              && string.Equals(current.OwnerModId, owner, StringComparison.Ordinal)
                                              && SameUnderlyingState(current.State, candidateState);
                    bool ownFormulaRemoved = current == null
                                             && before != null
                                             && string.Equals(before.OwnerModId, owner, StringComparison.Ordinal);
                    if (!definedByCandidate && !ownFormulaRemoved)
                    {
                        continue;
                    }

                    if (before != null && ownerLive[before.OwnerModId ?? ""])
                    {
                        _overrides[slot] = before;
                        changed++;
                    }
                    else if (current != null)
                    {
                        _overrides.Remove(slot);
                        changed++;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// True when both handles wrap the same live VM state. Wrapper identity is not enough: the
        /// call-context and the runtime hand out distinct <see cref="IScriptState"/> wrappers around
        /// the same underlying state, so the comparison unwraps Lua-CSharp states.
        /// </summary>
        private static bool SameUnderlyingState(IScriptState a, IScriptState b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a is LuaCsScriptState left && b is LuaCsScriptState right &&
                   ReferenceEquals(left.State, right.State);
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
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.TryInvoke(name, out result, args);
            }

            if (TryInvokeRaw(name, out object raw, args))
            {
                result = _marshaller.ToHostValue(raw);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>Numeric slot helper (formulas). False → use the C# default.</summary>
        public bool TryInvokeNumber(string name, out double value, params object[] args)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.TryInvokeNumber(name, out value, args);
            }

            value = 0d;
            if (!TryInvokeRaw(name, out object result, args) ||
                _marshaller.GetKind(result) != ScriptValueKind.Number)
            {
                return false;
            }

            value = (double)_marshaller.ToHostValue(result);
            return double.IsFinite(value);
        }

        /// <summary>Boolean slot helper (predicates). False → use the C# default.</summary>
        public bool TryInvokeBool(string name, out bool value, params object[] args)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.TryInvokeBool(name, out value, args);
            }

            value = false;
            if (!TryInvokeRaw(name, out object result, args) ||
                _marshaller.GetKind(result) != ScriptValueKind.Boolean)
            {
                return false;
            }

            value = (bool)_marshaller.ToHostValue(result);
            return true;
        }

        /// <summary>String slot helper (tables/ids serialized by the script). False → use the C# default.</summary>
        public bool TryInvokeString(string name, out string value, params object[] args)
        {
            LuaCsLogicSlots target = ActiveTarget;
            if (target != null)
            {
                return target.TryInvokeString(name, out value, args);
            }

            value = "";
            if (!TryInvokeRaw(name, out object result, args) ||
                _marshaller.GetKind(result) != ScriptValueKind.String)
            {
                return false;
            }

            value = (string)_marshaller.ToHostValue(result) ?? "";
            return true;
        }

        // WHY: Formulas never yield, so driving the override synchronously under the guard is safe.
        // Internal: the public surface stays VM-agnostic.
        private bool TryInvokeRaw(string name, out object result, params object[] args)
        {
            result = null;
            string slot = Normalize(name);
            OverrideEntry entry;
            Action starting;
            Action finished;
            lock (_gate)
            {
                if (!_overrides.TryGetValue(slot, out entry))
                {
                    return false;
                }

                starting = _invocationStarting;
                finished = _invocationFinished;
            }

            RaiseInvocationScope(starting, slot);
            try
            {
                try
                {
                    object[] results = _guard.Invoke(entry.State, entry.Fn, CancellationToken.None,
                        args ?? Array.Empty<object>());
                    result = results.Length > 0 ? results[0] : null;
                    return true;
                }
                catch (Exception ex)
                {
                    // WHY: Fail open: a broken override must not break the game loop on every call. The
                    // reset is attributed, not silent: OverrideFailed carries the defining mod's id so the
                    // runtime can record it in the same diagnostics channel as handler errors.
                    Reset(slot);
                    string owner = entry.OwnerModId ?? "";
                    LastError = $"slot '{slot}': {ex}";
                    _log?.Error(
                        $"[LuaCsLogicSlots] Override for '{slot}'" +
                        $"{(owner.Length > 0 ? $" (mod '{owner}')" : "")} failed and was reset: {ex}");
                    RaiseOverrideFailed(owner, slot, ex.Message ?? ex.GetType().Name);
                    result = null;
                    return false;
                }
            }
            finally
            {
                RaiseInvocationScope(finished, slot);
            }
        }

        // WHY contained: the bracket runs around a fail-open call, so a throwing callback must not turn
        // a formula into a game-loop failure.
        private void RaiseInvocationScope(Action callback, string slot)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsLogicSlots] Invocation scope callback for '{slot}' threw: {ex}");
            }
        }

        // WHY: Per-subscriber isolated raise (mirrors the runtime's event raisers): a throwing
        // telemetry subscriber must not turn the fail-open path into a game-loop failure.
        private void RaiseOverrideFailed(string ownerModId, string slot, string error)
        {
            Action<string, string, string> handler = _overrideFailed;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, string> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(ownerModId, slot, error);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsLogicSlots] [subscriber] OverrideFailed handler for '{slot}' threw: {ex}");
                }
            }
        }

        private static string Normalize(string name)
        {
            return (name ?? "").Trim();
        }

        internal void OnActiveTargetChanging(
            LuaCsLogicSlots previous,
            LuaCsLogicSlots next)
        {
            if (_activeProvider == null || ReferenceEquals(previous, next))
            {
                return;
            }

            Action<string, string, string> listeners = _overrideFailed;
            if (listeners == null)
            {
                return;
            }

            foreach (Action<string, string, string> listener in listeners.GetInvocationList())
            {
                if (previous != null)
                {
                    previous.OverrideFailed -= listener;
                }

                if (next != null)
                {
                    next.OverrideFailed += listener;
                }
            }
        }

        private LuaCsLogicSlots ActiveTarget
        {
            get
            {
                if (_activeProvider == null)
                {
                    return null;
                }

                LuaCsLogicSlots target = _activeProvider();
                if (target == null || ReferenceEquals(target, this))
                {
                    throw new InvalidOperationException(
                        "The active Lua world session has no logic-slot surface.");
                }

                return target;
            }
        }
    }
}

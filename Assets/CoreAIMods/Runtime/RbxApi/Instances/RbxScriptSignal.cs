using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// One RBXScriptConnection: the handle Connect returns, carrying Roblox's Connected flag and
    /// Disconnect(). Disconnecting is idempotent and safe while the owning signal is firing.
    /// </summary>
    public sealed class RbxScriptConnection
    {
        private readonly RbxScriptSignal _signal;

        internal RbxScriptConnection(RbxScriptSignal signal, Action<object[]> handler, bool once)
        {
            _signal = signal;
            Handler = handler;
            Once = once;
        }

        internal Action<object[]> Handler { get; }

        internal bool Once { get; }

        /// <summary>Roblox RBXScriptConnection.Connected.</summary>
        public bool Connected { get; internal set; } = true;

        /// <summary>Roblox RBXScriptConnection:Disconnect().</summary>
        public void Disconnect()
        {
            if (!Connected)
            {
                return;
            }

            Connected = false;
            _signal.Remove(this);
        }
    }

    /// <summary>
    /// Roblox signal (ChildAdded, Destroying, UserInputService.InputBegan, ...). Two modes:
    /// the default MVP1 shape keeps every entry point a loud stub (dispatch, connections, and
    /// yielding belong to the MVP2 scheduler/signal system — roadmap §5.1.6), while a signal
    /// constructed with <c>supportsDispatch: true</c> stores connections and fires them
    /// synchronously via <see cref="Fire"/>. The dispatch-enabled path exists so the MVP1 input
    /// slice (UserInputService.InputBegan/InputEnded/InputChanged) can deliver events this frame.
    /// WHY: exposing the final surface shape now lets the registry/tree code compile against it
    /// without building the deferred-dispatch machinery out of order.
    /// </summary>
    public sealed class RbxScriptSignal
    {
        private readonly string _signalName;
        private readonly List<RbxScriptConnection> _connections;

        // WHY: dispatch happens every frame for the input signals, so the fire snapshot is reused
        // rather than allocated per event; a re-entrancy flag falls back to a fresh buffer for the
        // (rare) nested fire so the shared one is never clobbered.
        private readonly List<RbxScriptConnection> _fireBuffer;
        private bool _firing;

        public RbxScriptSignal(string signalName, bool supportsDispatch = false)
        {
            _signalName = signalName;
            SupportsDispatch = supportsDispatch;
            _connections = supportsDispatch ? new List<RbxScriptConnection>() : null;
            _fireBuffer = supportsDispatch ? new List<RbxScriptConnection>() : null;
        }

        /// <summary>Signal name used in stub errors, e.g. "Instance.ChildAdded".</summary>
        public string SignalName => _signalName;

        /// <summary>True for the MVP1 input signals that dispatch now; false for the signals whose
        /// dispatch lands with the MVP2 scheduler.</summary>
        public bool SupportsDispatch { get; }

        // TODO: MVP2 — general RbxScriptSignal dispatch replaces the SupportsDispatch split; every
        // signal then connects/fires through the scheduler with deferred-dispatch semantics.
        public RbxScriptConnection Connect(object handler)
        {
            return ConnectCore(handler, once: false, member: "Connect");
        }

        public RbxScriptConnection Once(object handler)
        {
            return ConnectCore(handler, once: true, member: "Once");
        }

        // TODO: MVP2 — RbxScriptSignal:Wait() needs the scheduler's coroutine yield even for
        // dispatch-enabled signals.
        public object Wait()
        {
            throw Stub("Wait");
        }

        /// <summary>
        /// Fires every live connection synchronously in connection order. Handlers are opaque
        /// <c>Action&lt;object[]&gt;</c> wrappers built by the scripting adapter (which owns
        /// guarding/marshalling/error attribution), so a throwing wrapper is the adapter's bug —
        /// this layer stays engine-free and does not swallow.
        /// </summary>
        public void Fire(params object[] args)
        {
            if (!SupportsDispatch || _connections.Count == 0)
            {
                return;
            }

            Dispatch(args);
        }

        // WHY: iterate a snapshot so a handler may Connect/Disconnect on the same signal while
        // firing; the snapshot reuses a shared buffer in the common (non-nested) case, and a nested
        // fire takes a private copy. Handlers are opaque Action<object[]> wrappers built by the
        // scripting adapter (which owns guarding/marshalling/error attribution), so a throwing
        // wrapper is the adapter's bug — this layer stays engine-free and does not swallow.
        private void Dispatch(object[] args)
        {
            bool usingShared = !_firing;
            List<RbxScriptConnection> buffer;
            if (usingShared)
            {
                _firing = true;
                buffer = _fireBuffer;
                buffer.Clear();
                buffer.AddRange(_connections);
            }
            else
            {
                buffer = new List<RbxScriptConnection>(_connections);
            }

            try
            {
                for (int i = 0; i < buffer.Count; i++)
                {
                    RbxScriptConnection connection = buffer[i];
                    if (!connection.Connected)
                    {
                        continue;
                    }

                    // WHY: Roblox Once disconnects BEFORE the handler runs, so a re-fire from
                    // inside the handler can never invoke it twice.
                    if (connection.Once)
                    {
                        connection.Disconnect();
                    }

                    connection.Handler(args);
                }
            }
            finally
            {
                if (usingShared)
                {
                    buffer.Clear();
                    _firing = false;
                }
            }
        }

        internal void Remove(RbxScriptConnection connection)
        {
            _connections?.Remove(connection);
        }

        private RbxScriptConnection ConnectCore(object handler, bool once, string member)
        {
            if (!SupportsDispatch)
            {
                throw Stub(member);
            }

            if (!(handler is Action<object[]> action))
            {
                throw RbxError.BadArgument(
                    _signalName + ":" + member + " received a non-invokable handler",
                    "the scripting adapter must wrap the script function into an Action<object[]> " +
                    "before connecting");
            }

            var connection = new RbxScriptConnection(this, action, once);
            _connections.Add(connection);
            return connection;
        }

        private RbxError Stub(string member)
        {
            return RbxError.NotImplemented(_signalName + ":" + member, "MVP2",
                "signals land in MVP2 (scheduler); poll with FindFirstChild/GetAttribute until then");
        }
    }
}

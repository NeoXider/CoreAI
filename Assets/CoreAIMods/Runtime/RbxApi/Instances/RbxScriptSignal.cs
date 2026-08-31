using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>One deferred RBXScriptConnection owned by a ModScheduler.</summary>
    public sealed class RbxScriptConnection
    {
        private enum DisconnectKind
        {
            None,
            Explicit,
            Destroy,
            Once
        }

        private readonly RbxScriptSignal _signal;
        private DisconnectKind _disconnectKind;
        private bool _onceQueued;
        private int _pendingCount;

        internal RbxScriptConnection(RbxScriptSignal signal, ModScheduler scheduler,
            Action<object[]> handler, bool once)
        {
            _signal = signal;
            Scheduler = scheduler;
            Handler = handler;
            Once = once;
        }

        internal ModScheduler Scheduler { get; }

        internal Action<object[]> Handler { get; }

        internal bool Once { get; }

        internal string SignalName => _signal.SignalName;

        /// <summary>Roblox RBXScriptConnection.Connected.</summary>
        public bool Connected => _disconnectKind == DisconnectKind.None;

        /// <summary>Disconnects explicitly and drops every pending invocation per R5.7.</summary>
        public void Disconnect()
        {
            if (!Connected)
            {
                return;
            }

            DisconnectCore(DisconnectKind.Explicit);
        }

        internal bool TryQueueInvocation()
        {
            if (!Connected || Once && _onceQueued)
            {
                return false;
            }

            _pendingCount++;
            if (Once)
            {
                _onceQueued = true;
            }

            return true;
        }

        internal void InvokePending(object[] arguments)
        {
            try
            {
                if (_disconnectKind == DisconnectKind.Explicit)
                {
                    return;
                }

                if (Once && Connected)
                {
                    DisconnectCore(DisconnectKind.Once);
                }

                Handler(arguments);
            }
            finally
            {
                _pendingCount = Math.Max(0, _pendingCount - 1);
            }
        }

        internal void DisconnectFromDestroy()
        {
            if (Connected)
            {
                DisconnectCore(DisconnectKind.Destroy);
            }
        }

        internal void DropQueuedInvocation()
        {
            _pendingCount = Math.Max(0, _pendingCount - 1);
            if (Once && Connected && _pendingCount == 0)
            {
                _onceQueued = false;
            }
        }

        private void DisconnectCore(DisconnectKind kind)
        {
            _disconnectKind = kind;
            _signal.Remove(this);
        }
    }

    /// <summary>
    /// Deferred-only RBXScriptSignal. Fire snapshots live connections and queues their invocations
    /// on the owning ModScheduler; callbacks never run inside the mutation that fired the signal.
    /// </summary>
    public sealed class RbxScriptSignal
    {
        private static RbxInstance _readableTombstone;

        private readonly string _signalName;
        private readonly List<RbxScriptConnection> _connections = new();
        private ModScheduler _scheduler;

        public RbxScriptSignal(string signalName)
        {
            _signalName = string.IsNullOrWhiteSpace(signalName)
                ? throw new ArgumentException("Signal name is required.", nameof(signalName))
                : signalName;
        }

        /// <summary>Signal name used in diagnostics, e.g. "Instance.ChildAdded".</summary>
        public string SignalName => _signalName;

        /// <summary>True when at least one live handler or waiter is connected.</summary>
        public bool HasConnections => _connections.Count > 0;

        public RbxScriptConnection Connect(object handler)
        {
            return ConnectCore(handler, false, "Connect");
        }

        public RbxScriptConnection Once(object handler)
        {
            return ConnectCore(handler, true, "Once");
        }

        /// <summary>
        /// The Lua binding supplies the current scheduler thread and performs the actual yield.
        /// Direct C# calls cannot infer a yielding caller and therefore fail loudly.
        /// </summary>
        public object Wait()
        {
            throw RbxError.BadArgument(
                _signalName + ":Wait requires a scheduler-owned Lua thread",
                "call signal:Wait() from a running mod thread");
        }

        /// <summary>Queues one deferred invocation for every connection live at fire time.</summary>
        public void Fire(params object[] args)
        {
            FireCore(null, args);
        }

        internal void FireForDestruction(RbxInstance readableTombstone, params object[] args)
        {
            FireCore(readableTombstone, args);
        }

        internal void BindScheduler(ModScheduler scheduler)
        {
            if (scheduler == null)
            {
                throw RbxError.BadArgument(
                    _signalName + " requires a ModScheduler",
                    "bind the signal through the active Lua mod context");
            }

            if (_scheduler != null && !ReferenceEquals(_scheduler, scheduler)
                && _connections.Count > 0)
            {
                throw RbxError.BadArgument(
                    _signalName + " is already bound to another ModScheduler",
                    "use one scheduler per shared Rbx world");
            }

            _scheduler = scheduler;
        }

        internal RbxScriptConnection Wait(Action<object[]> resume)
        {
            return ConnectCore(resume, true, "Wait");
        }

        internal void DisconnectAll()
        {
            if (_connections.Count == 0)
            {
                return;
            }

            RbxScriptConnection[] snapshot = _connections.ToArray();
            for (int index = 0; index < snapshot.Length; index++)
            {
                snapshot[index].DisconnectFromDestroy();
            }
        }

        internal static bool CanReadTombstone(RbxInstance instance)
        {
            return ReferenceEquals(_readableTombstone, instance);
        }

        internal static RbxInstance EnterTombstoneScope(RbxInstance instance)
        {
            RbxInstance previous = _readableTombstone;
            _readableTombstone = instance;
            return previous;
        }

        internal static void ExitTombstoneScope(RbxInstance previous)
        {
            _readableTombstone = previous;
        }

        internal void Remove(RbxScriptConnection connection)
        {
            _connections.Remove(connection);
        }

        private void FireCore(RbxInstance readableTombstone, object[] args)
        {
            if (_connections.Count == 0)
            {
                return;
            }

            object[] arguments = args ?? Array.Empty<object>();
            RbxScriptConnection[] snapshot = _connections.ToArray();
            for (int index = 0; index < snapshot.Length; index++)
            {
                RbxScriptConnection connection = snapshot[index];
                if (connection.TryQueueInvocation())
                {
                    connection.Scheduler.EnqueueSignalInvocation(
                        connection, arguments, readableTombstone);
                }
            }
        }

        private RbxScriptConnection ConnectCore(object handler, bool once, string member)
        {
            if (!(handler is Action<object[]> action))
            {
                throw RbxError.BadArgument(
                    _signalName + ":" + member + " received a non-invokable handler",
                    "the scripting adapter must wrap the script function before connecting");
            }

            if (_scheduler == null)
            {
                throw RbxError.BadArgument(
                    _signalName + ":" + member + " has no scheduler",
                    "read and connect the signal through an active Lua mod context");
            }

            RbxScriptConnection connection = new(this, _scheduler, action, once);
            _connections.Add(connection);
            return connection;
        }
    }
}

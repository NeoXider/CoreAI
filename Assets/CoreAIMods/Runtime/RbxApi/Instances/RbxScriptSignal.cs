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
            Action<object[]> handler, bool once, bool signalWaiter = false)
        {
            _signal = signal;
            Scheduler = scheduler;
            Handler = handler;
            Once = once;
            IsSignalWaiter = signalWaiter;
        }

        internal ModScheduler Scheduler { get; }

        internal Action<object[]> Handler { get; }

        internal bool Once { get; }

        /// <summary>
        /// True for the connection a <c>:Wait()</c> opened: its invocation resumes the waiting thread
        /// instead of starting a handler thread.
        /// </summary>
        internal bool IsSignalWaiter { get; }

        /// <summary>
        /// Set by the scripting adapter for a connection whose every invocation starts one scheduler
        /// thread (a script handler). The scheduler holds such an invocation, when a thread charged to a
        /// remote sender fired it, until that sender's induced budget has room (C1-02).
        /// </summary>
        internal bool StartsSchedulerThread { get; set; }

        /// <summary>True after <see cref="Disconnect"/>: the pending invocations run nothing.</summary>
        internal bool IsExplicitlyDisconnected => _disconnectKind == DisconnectKind.Explicit;

        internal string SignalName => _signal.SignalName;

        /// <summary>
        /// The mod that opened this connection, stamped by <see cref="ModConnectionRegistry.Track"/>;
        /// null for host-owned connections and the ownerless one-off surface. The scheduler attributes a
        /// handler failure, a signal cascade and a fan-out budget overflow to this owner (M2-02).
        /// </summary>
        public string OwnerModId { get; internal set; }

        /// <summary>Index of this connection in its signal's connection list; -1 once removed.</summary>
        internal int SignalSlot { get; set; } = -1;

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

        /// <summary>
        /// Runs one queued invocation. A <c>Once</c> connection is consumed when its invocation starts:
        /// by <see cref="ConsumeOnce"/> when the scheduler starts or resumes the thread it runs, otherwise
        /// once the handler returned. When the scheduler refused to start that thread, the connection is
        /// not consumed and is armed again for a later fire (C1-02).
        /// </summary>
        internal void InvokePending(object[] arguments)
        {
            bool invoked = false;
            try
            {
                if (_disconnectKind == DisconnectKind.Explicit)
                {
                    return;
                }

                invoked = true;
                Handler(arguments);
            }
            finally
            {
                _pendingCount = Math.Max(0, _pendingCount - 1);
                if (invoked && Once && Connected)
                {
                    if (Scheduler.CurrentSignalInvocationRefused)
                    {
                        // WHY armed again instead of consumed: the refused handler never ran; consumed,
                        // the Once listener was lost for good, not even run for the host's own later change.
                        if (_pendingCount == 0)
                        {
                            _onceQueued = false;
                        }
                    }
                    else
                    {
                        DisconnectCore(DisconnectKind.Once);
                    }
                }
            }
        }

        /// <summary>
        /// Consumes a <c>Once</c> connection whose invocation starts now; called by the scheduler right
        /// before it runs the invocation's thread, so the handler already sees the connection closed.
        /// </summary>
        internal void ConsumeOnce()
        {
            if (Once && Connected)
            {
                DisconnectCore(DisconnectKind.Once);
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
        private const int MinTombstonesBeforeCompaction = 16;

        private static RbxInstance _readableTombstone;

        private readonly string _signalName;

        /// <summary>
        /// Connections in connect order. A disconnected connection leaves a null tombstone in its slot
        /// so removal is O(1) instead of a List.Remove scan (M2-26); tombstones are compacted in
        /// amortized O(1), and compaction keeps the connect order that dispatch follows.
        /// </summary>
        private readonly List<RbxScriptConnection> _connections = new();
        private int _liveConnectionCount;
        private int _tombstoneCount;
        private bool _disconnectingAll;
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
        public bool HasConnections => _liveConnectionCount > 0;

        /// <summary>Connection slots held, tombstones included (M2-26 regression counter).</summary>
        internal int ConnectionSlotCount => _connections.Count;

        /// <summary>Connections moved by tombstone compaction (M2-26 regression counter).</summary>
        internal long CompactionMoveCount { get; private set; }

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
                && _liveConnectionCount > 0)
            {
                throw RbxError.BadArgument(
                    _signalName + " is already bound to another ModScheduler",
                    "use one scheduler per shared Rbx world");
            }

            _scheduler = scheduler;
        }

        internal RbxScriptConnection Wait(Action<object[]> resume)
        {
            return ConnectCore(resume, true, "Wait", true);
        }

        internal void DisconnectAll()
        {
            if (_liveConnectionCount == 0)
            {
                return;
            }

            // WHY no snapshot: disconnecting only turns slots into tombstones and never calls out, so
            // the list can be walked in place; compaction is held off until the walk is over.
            _disconnectingAll = true;
            try
            {
                for (int index = 0; index < _connections.Count; index++)
                {
                    _connections[index]?.DisconnectFromDestroy();
                }
            }
            finally
            {
                _disconnectingAll = false;
            }

            CompactIfWorthwhile();
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
            int slot = connection.SignalSlot;
            if (slot < 0 || slot >= _connections.Count
                || !ReferenceEquals(_connections[slot], connection))
            {
                return;
            }

            _connections[slot] = null;
            connection.SignalSlot = -1;
            _liveConnectionCount--;
            _tombstoneCount++;
            if (!_disconnectingAll)
            {
                CompactIfWorthwhile();
            }
        }

        private void CompactIfWorthwhile()
        {
            if (_liveConnectionCount == 0)
            {
                _connections.Clear();
                _tombstoneCount = 0;
                return;
            }

            if (_tombstoneCount < MinTombstonesBeforeCompaction
                || _tombstoneCount * 2 < _connections.Count)
            {
                return;
            }

            int writeIndex = 0;
            for (int readIndex = 0; readIndex < _connections.Count; readIndex++)
            {
                RbxScriptConnection connection = _connections[readIndex];
                if (connection == null)
                {
                    continue;
                }

                if (writeIndex != readIndex)
                {
                    _connections[writeIndex] = connection;
                    connection.SignalSlot = writeIndex;
                    CompactionMoveCount++;
                }

                writeIndex++;
            }

            _connections.RemoveRange(writeIndex, _connections.Count - writeIndex);
            _tombstoneCount = 0;
        }

        private void FireCore(RbxInstance readableTombstone, object[] args)
        {
            if (_liveConnectionCount == 0)
            {
                return;
            }

            object[] arguments = args ?? Array.Empty<object>();
            // WHY no snapshot copy: queueing an invocation never calls out (handlers run later in the
            // scheduler's drain), so nothing can connect or disconnect while this loop runs. The count
            // is still captured up front so a connection made later can never see this fire (R5.5).
            int slotCount = _connections.Count;
            for (int index = 0; index < slotCount; index++)
            {
                RbxScriptConnection connection = _connections[index];
                if (connection != null && connection.TryQueueInvocation())
                {
                    connection.Scheduler.EnqueueSignalInvocation(
                        connection, arguments, readableTombstone);
                }
            }
        }

        private RbxScriptConnection ConnectCore(object handler, bool once, string member,
            bool signalWaiter = false)
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

            RbxScriptConnection connection = new(this, _scheduler, action, once, signalWaiter);
            connection.SignalSlot = _connections.Count;
            _connections.Add(connection);
            _liveConnectionCount++;
            return connection;
        }
    }
}

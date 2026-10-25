using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Features.Audit
{
    internal sealed class AuditDiagnosticBuffer
    {
        private readonly object _gate = new();
        private readonly Queue<(LogType Level, string Message)> _queue = new();
        private readonly int _capacity;
        private readonly int _deliveryBudget;
        private readonly Action<LogType, string> _sink;
        private long _droppedCount;
        private long _deliveryFailureCount;
        private int _draining;
        private bool _closed;

        internal AuditDiagnosticBuffer(AuditLogWriterOptions options)
        {
            _capacity = options.DiagnosticQueueCapacity;
            _deliveryBudget = options.MaxDiagnosticsPerPump;
            _sink = options.DiagnosticSink ?? WriteUnityLog;
        }

        internal int Count
        {
            get { lock (_gate) return _queue.Count; }
        }

        internal long DroppedCount => Interlocked.Read(ref _droppedCount);
        internal long DeliveryFailureCount => Interlocked.Read(ref _deliveryFailureCount);

        internal void Enqueue(LogType level, string message)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    Interlocked.Increment(ref _droppedCount);
                    return;
                }

                if (_queue.Count == _capacity)
                {
                    _queue.Dequeue();
                    Interlocked.Increment(ref _droppedCount);
                }

                _queue.Enqueue((level, message));
            }
        }

        internal void Drain()
        {
            if (!PlayerLoopHelper.IsMainThread || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            {
                return;
            }

            try
            {
                int count;
                lock (_gate)
                {
                    count = Math.Min(_queue.Count, _deliveryBudget);
                }

                for (int i = 0; i < count; i++)
                {
                    (LogType Level, string Message) diagnostic;
                    lock (_gate)
                    {
                        if (_queue.Count == 0)
                        {
                            break;
                        }

                        diagnostic = _queue.Dequeue();
                    }

                    try
                    {
                        _sink(diagnostic.Level, diagnostic.Message);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _deliveryFailureCount);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _draining, 0);
            }
        }

        internal void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Interlocked.Add(ref _droppedCount, _queue.Count);
                _queue.Clear();
            }
        }

        private static void WriteUnityLog(LogType level, string message)
        {
            switch (level)
            {
                case LogType.Warning:
                    Debug.LogWarning(message);
                    break;
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    Debug.LogError(message);
                    break;
                default:
                    Debug.Log(message);
                    break;
            }
        }
    }

    internal sealed class AuditDiagnosticPump : IPlayerLoopItem, IDisposable
    {
        private AuditDiagnosticBuffer _buffer;

        internal AuditDiagnosticPump(AuditDiagnosticBuffer buffer)
        {
            _buffer = buffer;
        }

        internal bool IsAttached => Volatile.Read(ref _buffer) != null;

        public bool MoveNext()
        {
            AuditDiagnosticBuffer buffer = Volatile.Read(ref _buffer);
            if (buffer == null)
            {
                return false;
            }

            buffer.Drain();
            return Volatile.Read(ref _buffer) != null;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _buffer, null);
        }
    }
}

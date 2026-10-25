using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CoreAI.Features.Audit
{
    /// <summary>Shares a chain head while any writer or retained operation still owns its canonical path.</summary>
    internal static class AuditPathStateRegistry
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, State> States = new(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private static int _capacity = 1024;

        internal sealed class State
        {
            internal readonly object Gate = new();
            internal long Sequence;
            internal string PreviousHash = "";
            internal bool Initialized;
            internal bool RotationPending;
            internal long MaxFileSizeBytes;
            internal int MaxRetainedRotatedFiles;
            internal long LastRotationIndex;
            internal int Users;
            internal bool Quarantined;
            internal long RecoveryLength;
            internal long RecoverySequence;
            internal string RecoveryHash = "";
            internal string AppendFailure = "";
            internal string RollbackFailure = "";
        }

        internal static int Count
        {
            get { lock (Gate) return States.Count; }
        }

        internal static int Capacity
        {
            get { lock (Gate) return _capacity; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Shared audit path capacity must be positive.");
                }

                lock (Gate)
                {
                    if (value < States.Count)
                    {
                        throw new InvalidOperationException(
                            "Shared audit path capacity cannot be smaller than the current retained path count.");
                    }

                    _capacity = value;
                }
            }
        }

        internal static Lease TryAcquireExisting(string path)
        {
            string key = Path.GetFullPath(path);
            lock (Gate)
            {
                if (!States.TryGetValue(key, out State state))
                {
                    return null;
                }

                state.Users++;
                return new Lease(key, state);
            }
        }

        internal static Lease Acquire(string path, long maxFileSizeBytes, int maxRetainedRotatedFiles)
        {
            string key = Path.GetFullPath(path);
            lock (Gate)
            {
                if (!States.TryGetValue(key, out State state))
                {
                    if (States.Count >= _capacity)
                    {
                        throw new InvalidOperationException(
                            "Shared audit path capacity is exhausted by active operations or quarantined paths.");
                    }

                    state = new State
                    {
                        MaxFileSizeBytes = maxFileSizeBytes,
                        MaxRetainedRotatedFiles = maxRetainedRotatedFiles
                    };
                    States.Add(key, state);
                }
                else if (state.MaxFileSizeBytes != maxFileSizeBytes ||
                         state.MaxRetainedRotatedFiles != maxRetainedRotatedFiles)
                {
                    throw new InvalidOperationException(
                        "Audit writers sharing a canonical path must use identical file-size and retention limits.");
                }

                state.Users++;
                return new Lease(key, state);
            }
        }

        internal sealed class Lease : IDisposable
        {
            private readonly string _key;
            private bool _released;

            internal Lease(string key, State state)
            {
                _key = key;
                SharedState = state;
            }

            internal State SharedState { get; }

            internal Lease Retain()
            {
                lock (Gate)
                {
                    if (_released)
                    {
                        return null;
                    }

                    // WHY: Waiters retain before taking the path lock, so retiring an owner cannot split it.
                    SharedState.Users++;
                    return new Lease(_key, SharedState);
                }
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    if (_released)
                    {
                        return;
                    }

                    _released = true;
                    SharedState.Users--;
                    if (SharedState.Users == 0 && !Volatile.Read(ref SharedState.Quarantined) &&
                        States.TryGetValue(_key, out State state) &&
                        ReferenceEquals(state, SharedState))
                    {
                        States.Remove(_key);
                    }
                }
            }
        }
    }
}

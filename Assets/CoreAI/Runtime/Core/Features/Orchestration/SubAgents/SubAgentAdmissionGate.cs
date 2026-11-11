using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// First-in, first-out async gate that bounds how many sub-agents of one plan run at once.
    /// <para>
    /// WebGL-safe by construction: no thread, no timer, no blocking wait. A waiter is a plain task source
    /// created without <c>RunContinuationsAsynchronously</c> (that flag hands continuations to a thread pool a
    /// WebGL player does not have), and every waiter is completed after the lock is released, because its
    /// continuation runs inline on the completing call and may come straight back into this gate.
    /// </para>
    /// </summary>
    internal sealed class SubAgentAdmissionGate
    {
        private readonly object _lock = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private readonly int _capacity;
        private int _active;

        internal SubAgentAdmissionGate(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>Runs holding a slot now.</summary>
        internal int Active
        {
            get
            {
                lock (_lock)
                {
                    return _active;
                }
            }
        }

        /// <summary>Delegations waiting for a slot now.</summary>
        internal int Waiting
        {
            get
            {
                lock (_lock)
                {
                    return _waiters.Count;
                }
            }
        }

        /// <summary>
        /// Completes when a slot is held by the caller, who must call <see cref="Release"/> exactly once
        /// afterwards. Cancelled when <paramref name="cancellationToken"/> fires while still waiting.
        /// </summary>
        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            Waiter waiter;
            lock (_lock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                if (_active < _capacity && _waiters.Count == 0)
                {
                    _active++;
                    return Task.CompletedTask;
                }

                waiter = new Waiter();
                waiter.Node = _waiters.AddLast(waiter);
            }

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => CancelWaiter(waiter, cancellationToken));
            }

            return waiter.Source.Task;
        }

        /// <summary>Gives the caller's slot to the next waiter, or frees it.</summary>
        internal void Release()
        {
            Waiter next = null;
            lock (_lock)
            {
                if (_waiters.First != null)
                {
                    next = _waiters.First.Value;
                    _waiters.RemoveFirst();
                    next.Node = null;
                }
                else if (_active > 0)
                {
                    _active--;
                }
            }

            if (next != null)
            {
                next.Registration.Dispose();
                next.Source.TrySetResult(true);
            }
        }

        private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
        {
            bool removed = false;
            lock (_lock)
            {
                if (waiter.Node != null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Node = null;
                    removed = true;
                }
            }

            if (removed)
            {
                waiter.Source.TrySetCanceled(cancellationToken);
            }
        }

        private sealed class Waiter
        {
            public readonly TaskCompletionSource<bool> Source = new(TaskCreationOptions.None);
            public LinkedListNode<Waiter> Node;
            public CancellationTokenRegistration Registration;
        }
    }
}

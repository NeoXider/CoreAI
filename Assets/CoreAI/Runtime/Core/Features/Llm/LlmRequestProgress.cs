using System;
using System.Threading;

namespace CoreAI.Ai
{
    /// <summary>
    /// A per-request "still working" signal. Whoever can see a request make progress that produces no streamed
    /// text (a tool call of the request's own turn, a sub-agent the request is waiting for) calls
    /// <see cref="Report"/>; an idle deadline subscribed to it re-arms instead of cutting a busy request off at
    /// the wall-clock window. A request nobody reports for keeps its plain deadline, so a silent request still
    /// times out exactly as before.
    /// <para>
    /// <see cref="Report"/> is allocation-free and never throws: it runs inside tool bookkeeping and plan state
    /// changes. Listeners are called synchronously on the reporting thread and must be cheap; a throwing
    /// listener is ignored so it cannot break the reporter. Thread-safe.
    /// </para>
    /// </summary>
    public sealed class LlmRequestProgress
    {
        private static readonly Action[] NoListeners = Array.Empty<Action>();

        private readonly object _gate = new();
        private Action[] _listeners = NoListeners;
        private long _reports;

        /// <summary>How many times progress was reported.</summary>
        public long Reports => Interlocked.Read(ref _reports);

        /// <summary>Reports progress to every current listener.</summary>
        public void Report()
        {
            Interlocked.Increment(ref _reports);
            Action[] listeners = Volatile.Read(ref _listeners);
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    listeners[i]();
                }
                catch (Exception)
                {
                    // WHY: a deadline that failed to re-arm must not fail the tool call or plan change reporting it.
                }
            }
        }

        /// <summary>Adds <paramref name="listener"/> until the returned handle is disposed.</summary>
        public IDisposable Subscribe(Action listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            lock (_gate)
            {
                Action[] next = new Action[_listeners.Length + 1];
                Array.Copy(_listeners, next, _listeners.Length);
                next[next.Length - 1] = listener;
                Volatile.Write(ref _listeners, next);
            }

            return new Subscription(this, listener);
        }

        private void Unsubscribe(Action listener)
        {
            lock (_gate)
            {
                int index = Array.IndexOf(_listeners, listener);
                if (index < 0)
                {
                    return;
                }

                if (_listeners.Length == 1)
                {
                    Volatile.Write(ref _listeners, NoListeners);
                    return;
                }

                Action[] next = new Action[_listeners.Length - 1];
                Array.Copy(_listeners, 0, next, 0, index);
                Array.Copy(_listeners, index + 1, next, index, _listeners.Length - index - 1);
                Volatile.Write(ref _listeners, next);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private LlmRequestProgress _owner;
            private readonly Action _listener;

            public Subscription(LlmRequestProgress owner, Action listener)
            {
                _owner = owner;
                _listener = listener;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_listener);
            }
        }
    }
}

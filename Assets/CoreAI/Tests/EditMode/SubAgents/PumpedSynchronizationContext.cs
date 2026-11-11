using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// A single-threaded context like a game's main thread: every captured <c>await</c> continuation is queued
    /// and runs on a later pump, never inline and never on the pool. Driver tests run under it so their result
    /// cannot depend on continuations running inline (the portable runner's default) when the player loop
    /// would run them later.
    /// </summary>
    internal sealed class PumpedSynchronizationContext : SynchronizationContext
    {
        private readonly object _lock = new();
        private readonly Queue<KeyValuePair<SendOrPostCallback, object>> _queue = new();

        public override void Post(SendOrPostCallback callback, object state)
        {
            lock (_lock)
            {
                _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(callback, state));
                Monitor.Pulse(_lock);
            }
        }

        public override void Send(SendOrPostCallback callback, object state)
        {
            throw new NotSupportedException("A main-thread context does not run synchronous sends from other threads.");
        }

        /// <summary>Runs <paramref name="body"/> to completion with this context current, pumping its posts.</summary>
        public static void Run(Func<Task> body, int timeoutMilliseconds = 20000)
        {
            SynchronizationContext previous = Current;
            PumpedSynchronizationContext context = new();
            SetSynchronizationContext(context);
            try
            {
                Task task = body();
                Stopwatch clock = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (!context.TryRunOne())
                    {
                        context.WaitForPost(10);
                    }

                    if (clock.ElapsedMilliseconds > timeoutMilliseconds)
                    {
                        Assert.Fail("The test did not finish under the pumped main-thread context.");
                    }
                }

                while (context.TryRunOne())
                {
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        private bool TryRunOne()
        {
            KeyValuePair<SendOrPostCallback, object> item;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    return false;
                }

                item = _queue.Dequeue();
            }

            item.Key(item.Value);
            return true;
        }

        private void WaitForPost(int milliseconds)
        {
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    Monitor.Wait(_lock, milliseconds);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Lines the player sends while one turn of the model runs. The chat posts them (<see cref="TryPost"/>) without
    /// touching the running stream or thinking; the tool loop takes them at its next step boundary - after the results
    /// of a tool batch are appended, before the next model request - and appends each one as a user message, in
    /// order (<see cref="TakeForDelivery"/>). A line that arrives while the model gives its final answer makes the loop
    /// run one more round so the model answers it. At the end of the turn the orchestrator records the delivered lines
    /// in the role's history right after the turn's own message and closes the inbox; a turn that ends without an
    /// answer (stopped, failed) also records the lines nobody delivered, so they are not lost and the next turn sees
    /// them. Every member is thread-safe: the chat posts on its thread, the tool loop takes on the provider's.
    /// </summary>
    public sealed class LlmTurnInbox
    {
        private readonly object _gate = new();
        private readonly List<string> _pending = new();
        private readonly List<string> _delivered = new();
        private bool _closed;

        /// <summary>
        /// Adds <paramref name="text"/> (trimmed) for the running turn. False, and nothing added, for a blank line or
        /// once the inbox is closed (the turn is over: the caller sends the line as a turn of its own).
        /// </summary>
        public bool TryPost(string text)
        {
            string line = (text ?? "").Trim();
            if (line.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                _pending.Add(line);
                return true;
            }
        }

        /// <summary>True once <see cref="Close"/> ran: no line is accepted any more.</summary>
        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        /// <summary>Lines posted and not yet delivered to the model.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>The lines the model was given, in the order it saw them.</summary>
        public IReadOnlyList<string> Delivered
        {
            get
            {
                lock (_gate)
                {
                    return _delivered.ToArray();
                }
            }
        }

        /// <summary>
        /// Moves every pending line to <see cref="Delivered"/> and returns them in order; empty when none waits. The
        /// tool loop calls it right before a model request and appends each line as a user message.
        /// </summary>
        public IReadOnlyList<string> TakeForDelivery()
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return Array.Empty<string>();
                }

                string[] taken = _pending.ToArray();
                _pending.Clear();
                _delivered.AddRange(taken);
                return taken;
            }
        }

        /// <summary>
        /// Removes and returns the lines nobody delivered (the turn ended first); empty when none. Whoever takes them
        /// owns them: the orchestrator records them in the history of a turn that ended without an answer, the chat
        /// sends them as a new turn after one that answered.
        /// </summary>
        public IReadOnlyList<string> TakeUndelivered()
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return Array.Empty<string>();
                }

                string[] taken = _pending.ToArray();
                _pending.Clear();
                return taken;
            }
        }

        /// <summary>Stops accepting lines; lines already posted stay for <see cref="TakeUndelivered"/>. Idempotent.</summary>
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }
    }
}

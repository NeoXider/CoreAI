using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Captures Unity's log stream (<c>Application.logMessageReceivedThreaded</c>: every <c>Debug.Log*</c>, every
    /// exception that reaches the console) into a bounded <see cref="RuntimeLogBuffer"/> that debug tools read with a
    /// cursor. Works in the editor and in every player, development or release, WebGL included.
    /// <para>
    /// WHY one process-wide instance (<see cref="Shared"/>): the log stream itself is process-wide, and a capture
    /// owned by a scope would lose its history at every scene load, which is when a developer most needs it. The
    /// CoreAI composition registers <see cref="Shared"/> as <see cref="IRuntimeLogSource"/> unless the host
    /// registered its own source first, and starts it when the container is built; the MCP debug start-up starts
    /// it before the first scene so start-up errors are kept too.
    /// </para>
    /// </summary>
    public sealed class UnityRuntimeLogCapture : IRuntimeLogSource
    {
        private readonly object _gate = new();
        private bool _started;

        /// <param name="buffer">The ring the capture writes; null creates one with the default limits.</param>
        public UnityRuntimeLogCapture(RuntimeLogBuffer buffer = null)
        {
            Buffer = buffer ?? new RuntimeLogBuffer();
        }

        /// <summary>The process-wide capture of Unity's log stream.</summary>
        public static UnityRuntimeLogCapture Shared { get; } = new();

        /// <summary>The ring the capture writes.</summary>
        public RuntimeLogBuffer Buffer { get; }

        /// <summary>True while subscribed to the Unity log stream.</summary>
        public bool IsStarted { get { lock (_gate) return _started; } }

        /// <inheritdoc />
        public long LatestSequence => Buffer.LatestSequence;

        /// <inheritdoc />
        public long OldestRetainedSequence => Buffer.OldestRetainedSequence;

        /// <inheritdoc />
        public RuntimeLogCounts Counts => Buffer.Counts;

        /// <inheritdoc />
        public IReadOnlyList<RuntimeLogEntry> Read(RuntimeLogQuery query) => Buffer.Read(query);

        /// <summary>Subscribes to the Unity log stream once; later calls do nothing.</summary>
        public void EnsureStarted()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }

            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        /// <summary>Unsubscribes; the retained entries stay readable.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (!_started) return;
                _started = false;
            }

            Application.logMessageReceivedThreaded -= OnLogMessage;
        }

        /// <summary>Maps a Unity log type to the engine-free severity.</summary>
        public static RuntimeLogLevel ToLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return RuntimeLogLevel.Warning;
                case LogType.Error:
                case LogType.Assert:
                    return RuntimeLogLevel.Error;
                case LogType.Exception:
                    return RuntimeLogLevel.Exception;
                default:
                    return RuntimeLogLevel.Info;
            }
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // WHY: a log callback that throws would be reported through the same callback.
            try
            {
                Buffer.Append(ToLevel(type), condition, stackTrace);
            }
            catch (System.Exception)
            {
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            // WHY: with domain reload off, the static capture outlives a play session; each session starts empty.
            Shared.Buffer.Clear();
        }
    }
}

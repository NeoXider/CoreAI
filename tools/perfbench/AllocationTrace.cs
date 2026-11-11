using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// Attributes a case's allocations to types with the runtime's sampled allocation ticks (CoreCLR raises
    /// one GCAllocationTick roughly every 100 KB allocated, naming the type of the object that crossed the
    /// threshold). Sampled, so it ranks types; the exact byte count per operation comes from
    /// <see cref="MicroBench"/>. CoreCLR only.
    /// </summary>
    internal sealed class AllocationTrace : EventListener
    {
        private const long GcKeyword = 0x1;
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _bytesByType = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _ticksByType = new(StringComparer.Ordinal);
        private bool _recording;

        public void Start()
        {
            lock (_gate)
            {
                _bytesByType.Clear();
                _ticksByType.Clear();
                _recording = true;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _recording = false;
            }
        }

        public IReadOnlyList<KeyValuePair<string, long>> TopTypes(int count)
        {
            lock (_gate)
            {
                List<KeyValuePair<string, long>> sorted = new(_bytesByType);
                sorted.Sort((left, right) => right.Value.CompareTo(left.Value));
                return sorted.Count > count ? sorted.GetRange(0, count) : sorted;
            }
        }

        public int Ticks(string typeName)
        {
            lock (_gate)
            {
                return _ticksByType.TryGetValue(typeName, out int ticks) ? ticks : 0;
            }
        }

        /// <summary>Runs <paramref name="microCase"/> for <paramref name="ops"/> operations and prints the sampled allocation types.</summary>
        public static void Report(MicroCase microCase, int ops)
        {
            using AllocationTrace trace = new();
            microCase.Setup?.Invoke();
            try
            {
                microCase.Run(Math.Max(1, ops / 10));
                trace.Start();
                microCase.Run(ops);
                trace.Stop();
            }
            finally
            {
                microCase.Teardown?.Invoke();
            }

            Console.WriteLine("ALLOC_TRACE case=\"" + microCase.Group + " / " + microCase.Name + "\" ops=" + ops);
            foreach (KeyValuePair<string, long> type in trace.TopTypes(25))
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,12} sampled bytes  {1,6} ticks  {2}", type.Value, trace.Ticks(type.Key), type.Key));
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)GcKeyword);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
            {
                return;
            }

            string typeName = "?";
            long amount = 0;
            for (int index = 0; index < eventData.PayloadNames.Count; index++)
            {
                string name = eventData.PayloadNames[index];
                if (name == "TypeName")
                {
                    typeName = eventData.Payload[index] as string ?? "?";
                }
                else if (name == "AllocationAmount64")
                {
                    amount = Convert.ToInt64(eventData.Payload[index], CultureInfo.InvariantCulture);
                }
                else if (name == "AllocationAmount" && amount == 0)
                {
                    amount = Convert.ToInt64(eventData.Payload[index], CultureInfo.InvariantCulture);
                }
            }

            lock (_gate)
            {
                if (!_recording)
                {
                    return;
                }

                _bytesByType.TryGetValue(typeName, out long bytes);
                _bytesByType[typeName] = bytes + amount;
                _ticksByType.TryGetValue(typeName, out int ticks);
                _ticksByType[typeName] = ticks + 1;
            }
        }
    }
}

using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// A bounded collector of reports. It keeps at most <see cref="Capacity"/> entries, counts the rest in
    /// <see cref="DroppedCount"/>, and stores an identical report (same code, source, line, column and
    /// message) only once, so a rule evaluated for every element still produces a single report.
    /// </summary>
    public sealed class RuntimeUiDiagnosticList
    {
        private readonly List<RuntimeUiDiagnostic> _items = new();
        private readonly HashSet<RuntimeUiDiagnostic> _seen = new();

        /// <summary>Creates a collector.</summary>
        /// <param name="capacity">Most reports kept; values below 1 keep one.</param>
        public RuntimeUiDiagnosticList(int capacity = 256)
        {
            Capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>Most reports kept.</summary>
        public int Capacity { get; }

        /// <summary>Reports that arrived after the collector was full.</summary>
        public int DroppedCount { get; private set; }

        /// <summary>The kept reports, in arrival order.</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> Items => _items;

        /// <summary>Number of kept reports.</summary>
        public int Count => _items.Count;

        /// <summary>True when any kept report is an error.</summary>
        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Severity == RuntimeUiDiagnosticSeverity.Error)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Adds a report unless an identical one is already kept.</summary>
        public void Add(RuntimeUiDiagnostic diagnostic)
        {
            if (_seen.Contains(diagnostic))
            {
                return;
            }

            if (_items.Count >= Capacity)
            {
                DroppedCount++;
                return;
            }

            _seen.Add(diagnostic);
            _items.Add(diagnostic);
        }

        /// <summary>Adds a report built from its parts.</summary>
        public void Add(
            string code,
            RuntimeUiDiagnosticSeverity severity,
            RuntimeUiSource source,
            int line,
            int column,
            string message)
        {
            Add(new RuntimeUiDiagnostic(code, severity, source, line, column, message));
        }

        /// <summary>Copies every kept report of <paramref name="other"/> into this collector.</summary>
        public void AddRange(IReadOnlyList<RuntimeUiDiagnostic> other)
        {
            if (other == null)
            {
                return;
            }

            for (int i = 0; i < other.Count; i++)
            {
                Add(other[i]);
            }
        }

        /// <summary>Counts kept reports with the given code.</summary>
        public int CountOf(string code)
        {
            int count = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Code == code)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Removes every report.</summary>
        public void Clear()
        {
            _items.Clear();
            _seen.Clear();
            DroppedCount = 0;
        }

        /// <summary>Returns a snapshot copy of the kept reports.</summary>
        public RuntimeUiDiagnostic[] ToArray()
        {
            return _items.ToArray();
        }
    }
}

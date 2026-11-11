using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>
    /// The data of one game composition: its storage, its limits and its namespace, shared by every world the
    /// composition builds (the first one and each one a load or an undo replaces it with), so data outlives worlds.
    /// One per composition scope; disposing it writes every pending change.
    /// </summary>
    /// <remarks>
    /// The namespace of a world is <see cref="RbxDataStoreOptions.Namespace"/> of the game namespace and the world's
    /// id, by default <see cref="RbxDataStoreNamespaces.Compose"/>: the default world and every package saved from it
    /// share the game's data; a world with an id of its own keeps its own. Data is never part of a world package.
    /// </remarks>
    public sealed class RbxDataStoreBackend : IDisposable
    {
        private readonly Dictionary<string, RbxDataStoreDatabase> _databases = new(StringComparer.Ordinal);
        private readonly List<string> _problems = new();
        private readonly Func<double> _clock;
        private readonly Func<long> _wallClock;
        private readonly Action<string> _report;
        private bool _disposed;

        /// <summary>Creates the backend.</summary>
        /// <param name="persistence">Where the data is kept; null keeps it in memory (tests, headless hosts).</param>
        /// <param name="gameNamespace">The game's namespace: the composition's mod store id ("" for the main game).</param>
        /// <param name="options">The limits; null uses <see cref="RbxDataStoreOptions.Desktop"/>. The backend keeps a copy.</param>
        /// <param name="clock">Monotonic seconds for request budgets and write delays; null uses a stopwatch.</param>
        /// <param name="wallClock">Milliseconds since the Unix epoch for key information; null uses the system clock.</param>
        /// <param name="report">Receives each storage problem once (an unreadable file, a failed write); may be null.</param>
        public RbxDataStoreBackend(IRbxDataStorePersistence persistence = null, string gameNamespace = null,
            RbxDataStoreOptions options = null, Func<double> clock = null, Func<long> wallClock = null,
            Action<string> report = null)
        {
            Persistence = persistence ?? new InMemoryRbxDataStorePersistence();
            GameNamespace = (gameNamespace ?? string.Empty).Trim();
            Options = (options ?? RbxDataStoreOptions.Desktop()).Clone();
            Stopwatch watch = Stopwatch.StartNew();
            _clock = clock ?? (() => watch.Elapsed.TotalSeconds);
            _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _report = report;
        }

        /// <summary>The storage behind the data.</summary>
        public IRbxDataStorePersistence Persistence { get; }

        /// <summary>The game's namespace.</summary>
        public string GameNamespace { get; }

        /// <summary>The limits every world of the composition enforces.</summary>
        public RbxDataStoreOptions Options { get; }

        /// <summary>Monotonic seconds.</summary>
        public Func<double> Clock => _clock;

        /// <summary>Milliseconds since the Unix epoch.</summary>
        public Func<long> WallClock => _wallClock;

        /// <summary>Every storage problem reported so far, oldest first.</summary>
        public IReadOnlyList<string> Problems => _problems;

        /// <summary>True while any namespace has a change waiting to be written.</summary>
        public bool HasPendingWrites
        {
            get
            {
                foreach (RbxDataStoreDatabase database in _databases.Values)
                {
                    if (database.HasPendingWrites)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The namespace the data of a world with <paramref name="worldId"/> lives in.</summary>
        public string NamespaceFor(string worldId)
        {
            Func<string, string, string> rule = Options.Namespace;
            return rule != null ? rule(GameNamespace, worldId ?? string.Empty) ?? string.Empty
                : RbxDataStoreNamespaces.Compose(GameNamespace, worldId);
        }

        /// <summary>The data of <paramref name="dataNamespace"/>, created on first use.</summary>
        public RbxDataStoreDatabase Database(string dataNamespace)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RbxDataStoreBackend));
            }

            string key = dataNamespace ?? string.Empty;
            if (!_databases.TryGetValue(key, out RbxDataStoreDatabase database))
            {
                database = new RbxDataStoreDatabase(key, Persistence, Options, _clock, Report);
                _databases.Add(key, database);
            }

            return database;
        }

        /// <summary>Writes the changes that are due, at most the per-frame limit in each namespace.</summary>
        public void FlushDue()
        {
            if (_databases.Count == 0)
            {
                return;
            }

            foreach (RbxDataStoreDatabase database in _databases.Values)
            {
                if (database.HasPendingWrites)
                {
                    database.FlushDue();
                }
            }
        }

        /// <summary>Writes every pending change now; returns false when one could not be written.</summary>
        public bool FlushAll()
        {
            bool allWritten = true;
            foreach (RbxDataStoreDatabase database in _databases.Values)
            {
                allWritten &= database.FlushAll();
            }

            return allWritten;
        }

        /// <summary>Writes every pending change and refuses further use.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            FlushAll();
            _disposed = true;
        }

        private void Report(string problem)
        {
            _problems.Add(problem);
            _report?.Invoke(problem);
        }
    }
}

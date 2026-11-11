using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>What a data store request does.</summary>
    public enum RbxDataStoreOperation
    {
        /// <summary><c>GetAsync</c>.</summary>
        Get = 0,

        /// <summary><c>SetAsync</c>.</summary>
        Set = 1,

        /// <summary><c>IncrementAsync</c>.</summary>
        Increment = 2,

        /// <summary><c>UpdateAsync</c>: granted first, then read and committed by the calling thread in one step.</summary>
        Update = 3,

        /// <summary><c>RemoveAsync</c>.</summary>
        Remove = 4
    }

    /// <summary>Where a request is.</summary>
    public enum RbxDataStoreRequestState
    {
        /// <summary>Waiting for its budget.</summary>
        Queued = 0,

        /// <summary>An UpdateAsync whose budget was taken; the calling thread reads and commits it.</summary>
        Granted = 1,

        /// <summary>Done; <see cref="RbxDataStoreRequest.Result"/> holds the outcome.</summary>
        Done = 2,

        /// <summary>Failed; <see cref="RbxDataStoreRequest.Error"/> says why.</summary>
        Failed = 3
    }

    /// <summary>One data store call on its way through the budgets.</summary>
    public sealed class RbxDataStoreRequest
    {
        /// <summary>Creates a request.</summary>
        public RbxDataStoreRequest(RbxDataStoreOperation operation, RbxGlobalDataStore store, string key, string modId)
        {
            Operation = operation;
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Key = key ?? string.Empty;
            ModId = string.IsNullOrWhiteSpace(modId) ? string.Empty : modId.Trim();
        }

        /// <summary>What the request does.</summary>
        public RbxDataStoreOperation Operation { get; }

        /// <summary>The store.</summary>
        public RbxGlobalDataStore Store { get; }

        /// <summary>The key.</summary>
        public string Key { get; }

        /// <summary>The mod that made the request ("" for a one-off chunk).</summary>
        public string ModId { get; }

        /// <summary>The value to write as JSON text (SetAsync).</summary>
        public string ValueJson { get; set; }

        /// <summary>The amount to add (IncrementAsync).</summary>
        public long Delta { get; set; } = 1L;

        /// <summary>The user ids to tag the key with (SetAsync, IncrementAsync).</summary>
        public long[] UserIds { get; set; } = Array.Empty<long>();

        /// <summary>Where the request is.</summary>
        public RbxDataStoreRequestState State { get; internal set; }

        /// <summary>The entry the request read, wrote or removed; null when the key held nothing.</summary>
        public RbxDataStoreEntry Result { get; internal set; }

        /// <summary>Why the request failed.</summary>
        public RbxError Error { get; internal set; }

        /// <summary>True once the request is done or failed.</summary>
        public bool IsSettled => State == RbxDataStoreRequestState.Done || State == RbxDataStoreRequestState.Failed;

        /// <summary>The mirror method name, as error texts use it.</summary>
        public string Method
        {
            get
            {
                switch (Operation)
                {
                    case RbxDataStoreOperation.Get:
                        return "GetAsync";
                    case RbxDataStoreOperation.Set:
                        return "SetAsync";
                    case RbxDataStoreOperation.Increment:
                        return "IncrementAsync";
                    case RbxDataStoreOperation.Update:
                        return "UpdateAsync";
                    default:
                        return "RemoveAsync";
                }
            }
        }

        internal RbxDataStoreBudgetKind Queue => Operation == RbxDataStoreOperation.Get ? RbxDataStoreBudgetKind.Read
            : Operation == RbxDataStoreOperation.Remove ? RbxDataStoreBudgetKind.Remove
            : RbxDataStoreBudgetKind.Write;
    }

    /// <summary>
    /// Roblox <c>DataStoreService</c> for one world: it hands out the world's stores (one object per name and scope,
    /// shared by every mod, S1.1) and runs their requests through the world's budgets and queues. Detached, like
    /// <c>AIService</c>: <c>game:GetService</c> returns it, but it is not a child of the DataModel, so world packages,
    /// replication and the instance quota never see it. The yielding Lua members are in the Lua binding layer; this
    /// class answers synchronously and a request settles on the next <see cref="Pump"/>.
    /// </summary>
    public sealed class RbxDataStoreService : RbxInstance
    {
        private readonly Dictionary<string, RbxGlobalDataStore> _stores = new(StringComparer.Ordinal);
        private readonly Queue<RbxDataStoreRequest>[] _queues =
        {
            new Queue<RbxDataStoreRequest>(), new Queue<RbxDataStoreRequest>(), new Queue<RbxDataStoreRequest>()
        };

        private readonly Dictionary<string, int> _waitingByMod = new(StringComparer.Ordinal);
        private readonly RbxDataStoreBackend _backend;
        private readonly RbxDataStoreDatabase _database;
        private readonly RbxDataStoreRequestBudgets _budgets;
        private readonly Action<string> _log;
        private int _queued;
        private double _lastQueueWarning = double.NegativeInfinity;
        private bool _closed;

        /// <summary>Creates the service of one world.</summary>
        /// <param name="catalog">The world's class catalog (the surface's classes are added when missing).</param>
        /// <param name="backend">The composition's data.</param>
        /// <param name="worldId">The world's id (<c>InstanceRegistry.WorldId</c>); it picks the namespace.</param>
        /// <param name="playerCount">Players in the world now, for the budgets; null counts one.</param>
        /// <param name="log">Warnings for the mod log (a request waiting in a queue); may be null.</param>
        public RbxDataStoreService(ClassCatalog catalog, RbxDataStoreBackend backend, string worldId,
            Func<int> playerCount = null, Action<string> log = null)
            : base(RbxDataStoreClasses.Descriptor(catalog, RbxDataStoreClasses.Service))
        {
            Name = RbxDataStoreClasses.Service;
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            Namespace = backend.NamespaceFor(worldId);
            _database = backend.Database(Namespace);
            _budgets = new RbxDataStoreRequestBudgets(backend.Options, backend.Clock, playerCount);
            _log = log;
            Catalog = catalog;
        }

        /// <summary>The namespace this world's data lives in.</summary>
        public string Namespace { get; }

        /// <summary>The world's data.</summary>
        public RbxDataStoreDatabase Database => _database;

        /// <summary>The world's request budgets.</summary>
        public RbxDataStoreRequestBudgets Budgets => _budgets;

        /// <summary>Requests waiting for their budget.</summary>
        public int QueuedCount => _queued;

        /// <summary>True once the world closed; every request then fails with Roblox's 401.</summary>
        public bool IsClosed => _closed;

        internal ClassCatalog Catalog { get; }

        /// <summary>Mirror <c>GetDataStore(name, scope = "global")</c>: the same object for the same name and scope.</summary>
        public RbxGlobalDataStore GetDataStore(string name, string scope = null)
        {
            string storeName = name ?? string.Empty;
            string storeScope = scope ?? RbxDataStoreClasses.DefaultScope;
            ValidateName(storeName, "name");
            ValidateName(storeScope, "scope");
            return Lookup(storeName, storeScope);
        }

        /// <summary>Mirror <c>GetGlobalDataStore()</c>: the default store, whose scope is always <c>u</c>.</summary>
        public RbxGlobalDataStore GetGlobalDataStore()
        {
            return Lookup(string.Empty, RbxDataStoreClasses.GlobalDataStoreScope);
        }

        /// <summary>
        /// Mirror <c>GetRequestBudgetForRequestType</c> by <c>Enum.DataStoreRequestType</c> item name: the requests of that
        /// type that may run now. Ordered-store and deprecated types answer 0 (they are not served).
        /// </summary>
        public int GetRequestBudget(string requestTypeName)
        {
            switch (requestTypeName)
            {
                case "GetAsync":
                case "StandardRead":
                case "GetVersionAsync":
                    return _budgets.Available(RbxDataStoreBudgetKind.Read);
                case "SetIncrementAsync":
                case "StandardWrite":
                    return _budgets.Available(RbxDataStoreBudgetKind.Write);
                case "UpdateAsync":
                    return Math.Min(_budgets.Available(RbxDataStoreBudgetKind.Read),
                        _budgets.Available(RbxDataStoreBudgetKind.Write));
                case "StandardRemove":
                    return _budgets.Available(RbxDataStoreBudgetKind.Remove);
                case "ListAsync":
                case "StandardList":
                    return _budgets.Available(RbxDataStoreBudgetKind.List);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Validates and admits <paramref name="request"/>: it runs at once when its queue is empty and its budget
        /// allows, waits in its queue otherwise, and is dropped with Roblox's throttle error when the queue (or the
        /// mod's share) is full. Either way it settles no earlier than the caller's next yield.
        /// </summary>
        public RbxDataStoreRequest Submit(RbxDataStoreRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (_closed)
            {
                throw RbxDataStoreErrors.ShuttingDown();
            }

            ValidateKey(request.Key);
            if (!ReferenceEquals(request.Store.Service, this))
            {
                throw RbxError.BadArgument(request.Method + " was called on a data store of another world",
                    "get the store from this world's DataStoreService:GetDataStore");
            }

            Queue<RbxDataStoreRequest> queue = _queues[(int)request.Queue];
            if (queue.Count == 0 && TryTakeBudget(request))
            {
                Run(request);
                return request;
            }

            if (queue.Count >= Math.Max(0, _backend.Options.QueueSize))
            {
                throw RbxDataStoreErrors.Throttled(request.Method);
            }

            int waiting = _waitingByMod.TryGetValue(request.ModId, out int count) ? count : 0;
            int share = Math.Max(0, _backend.Options.WaitingRequestsPerMod);
            if (waiting >= share)
            {
                throw RbxDataStoreErrors.ModThrottled(request.Method, request.ModId.Length == 0 ? "one-off" : request.ModId,
                    share);
            }

            _waitingByMod[request.ModId] = waiting + 1;
            request.State = RbxDataStoreRequestState.Queued;
            queue.Enqueue(request);
            _queued++;
            WarnQueued(request);
            return request;
        }

        /// <summary>Runs the waiting requests whose budget has refilled, in order per queue; returns at once when nothing waits.</summary>
        public void Pump()
        {
            if (_queued == 0)
            {
                return;
            }

            for (int index = 0; index < _queues.Length; index++)
            {
                Queue<RbxDataStoreRequest> queue = _queues[index];
                while (queue.Count > 0 && TryTakeBudget(queue.Peek()))
                {
                    RbxDataStoreRequest request = queue.Dequeue();
                    _queued--;
                    Release(request.ModId);
                    Run(request);
                }
            }
        }

        /// <summary>Writes this world's changes that are due (once a frame); returns at once when nothing is pending.</summary>
        public void FlushDue()
        {
            if (_database.HasPendingWrites)
            {
                _database.FlushDue();
            }
        }

        /// <summary>The fresh value of a granted UpdateAsync, read by the calling thread right before its update function runs.</summary>
        public RbxDataStoreEntry ReadForUpdate(RbxDataStoreRequest request)
        {
            RequireGranted(request);
            return _database.Read(request.Store.StoreName, request.Store.Scope, request.Key);
        }

        /// <summary>
        /// Commits a granted UpdateAsync with what its update function returned: <paramref name="write"/> false (the
        /// function returned nil) cancels the write and gives the write budget back. Returns the new entry, or null.
        /// </summary>
        public RbxDataStoreEntry CommitUpdate(RbxDataStoreRequest request, bool write, string valueJson, long[] userIds,
            string metadataJson)
        {
            RequireGranted(request);
            try
            {
                if (!write)
                {
                    _budgets.Refund(RbxDataStoreBudgetKind.Write);
                    request.State = RbxDataStoreRequestState.Done;
                    request.Result = null;
                    return null;
                }

                RequireValueSize(valueJson);
                RequireStorableText(metadataJson, "$ (metadata)");
                RbxDataStoreEntry written = _database.Write(request.Store.StoreName, request.Store.Scope, request.Key,
                    valueJson, userIds, metadataJson, _backend.WallClock());
                request.State = RbxDataStoreRequestState.Done;
                request.Result = written;
                return written;
            }
            catch (RbxError error)
            {
                request.State = RbxDataStoreRequestState.Failed;
                request.Error = error;
                throw;
            }
        }

        /// <summary>Fails every waiting request with Roblox's 401 and writes every pending change; the world is closing.</summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            for (int index = 0; index < _queues.Length; index++)
            {
                Queue<RbxDataStoreRequest> queue = _queues[index];
                while (queue.Count > 0)
                {
                    RbxDataStoreRequest request = queue.Dequeue();
                    request.State = RbxDataStoreRequestState.Failed;
                    request.Error = RbxDataStoreErrors.ShuttingDown();
                }
            }

            _queued = 0;
            _waitingByMod.Clear();
            _database.FlushAll();
        }

        /// <summary>The mirror check of a key: not empty, at most 50 characters, valid text.</summary>
        public static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw RbxDataStoreErrors.KeyEmpty();
            }

            if (key.Length > RbxDataStoreOptions.MirrorNameLimit)
            {
                throw RbxDataStoreErrors.KeyTooLong(key.Length);
            }

            if (!IsValidText(key))
            {
                throw RbxDataStoreErrors.NotText("key");
            }
        }

        /// <summary>
        /// The mirror checks of a value: the characters of its JSON encoding (105), and text a store can keep (104:
        /// the mirror says a stored string "must be valid UTF-8", and a lone UTF-16 surrogate has no UTF-8 form).
        /// </summary>
        public void RequireValueSize(string valueJson)
        {
            int limit = Math.Min(_backend.Options.MaxValueCharacters, RbxDataStoreOptions.MirrorValueLimit);
            if (valueJson == null || valueJson.Length > limit)
            {
                throw RbxDataStoreErrors.ValueTooLarge(limit);
            }

            RequireStorableText(valueJson, "$");
        }

        /// <summary>Refuses text with a lone UTF-16 surrogate (104), the one kind of string that is not valid UTF-8 text.</summary>
        public static void RequireStorableText(string text, string path)
        {
            if (text != null && !IsValidText(text))
            {
                throw RbxDataStoreErrors.CantStore("a string that is not valid UTF-8 text", path);
            }
        }

        private static void ValidateName(string value, string what)
        {
            if (value.Length == 0 || value.Length > RbxDataStoreOptions.MirrorNameLimit)
            {
                throw RbxDataStoreErrors.NameInvalid(what, value.Length);
            }

            if (!IsValidText(value))
            {
                throw RbxDataStoreErrors.NotText(what);
            }
        }

        internal static bool IsValidText(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        return false;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            return true;
        }

        private RbxGlobalDataStore Lookup(string name, string scope)
        {
            string key = name + "\n" + scope;
            if (!_stores.TryGetValue(key, out RbxGlobalDataStore store))
            {
                store = new RbxGlobalDataStore(Catalog, this, name, scope);
                _stores.Add(key, store);
            }

            return store;
        }

        private bool TryTakeBudget(RbxDataStoreRequest request)
        {
            switch (request.Operation)
            {
                case RbxDataStoreOperation.Get:
                    return _budgets.TryTake(RbxDataStoreBudgetKind.Read);
                case RbxDataStoreOperation.Remove:
                    return _budgets.TryTake(RbxDataStoreBudgetKind.Remove);
                case RbxDataStoreOperation.Update:
                    return _budgets.TryTakeReadAndWrite();
                default:
                    return _budgets.TryTake(RbxDataStoreBudgetKind.Write);
            }
        }

        private void Run(RbxDataStoreRequest request)
        {
            string name = request.Store.StoreName;
            string scope = request.Store.Scope;
            try
            {
                switch (request.Operation)
                {
                    case RbxDataStoreOperation.Get:
                        request.Result = _database.Read(name, scope, request.Key);
                        break;
                    case RbxDataStoreOperation.Set:
                        RequireValueSize(request.ValueJson);
                        request.Result = _database.Write(name, scope, request.Key, request.ValueJson, request.UserIds,
                            null, _backend.WallClock());
                        break;
                    case RbxDataStoreOperation.Increment:
                        request.Result = Increment(request, name, scope);
                        break;
                    case RbxDataStoreOperation.Update:
                        request.State = RbxDataStoreRequestState.Granted;
                        return;
                    default:
                        request.Result = _database.Remove(name, scope, request.Key);
                        break;
                }

                request.State = RbxDataStoreRequestState.Done;
            }
            catch (RbxError error)
            {
                request.State = RbxDataStoreRequestState.Failed;
                request.Error = error;
            }
        }

        private RbxDataStoreEntry Increment(RbxDataStoreRequest request, string name, string scope)
        {
            RbxDataStoreEntry current = _database.Read(name, scope, request.Key);
            long value = 0L;
            if (current != null && !TryReadInteger(current.ValueJson, out value))
            {
                throw RbxDataStoreErrors.NotAnInteger(request.Key);
            }

            long next;
            try
            {
                next = checked(value + request.Delta);
            }
            catch (OverflowException)
            {
                throw RbxDataStoreErrors.NotAnInteger(request.Key);
            }

            string json = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return _database.Write(name, scope, request.Key, json, request.UserIds, null, _backend.WallClock());
        }

        /// <summary>True when <paramref name="json"/> is a JSON number holding an integer.</summary>
        internal static bool TryReadInteger(string json, out long value)
        {
            value = 0L;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            if (long.TryParse(json, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (!double.TryParse(json, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number)
                || double.IsNaN(number) || double.IsInfinity(number) || number != Math.Truncate(number)
                || number < -9.2233720368547758E18 || number >= 9.2233720368547758E18)
            {
                return false;
            }

            value = (long)number;
            return true;
        }

        private void RequireGranted(RbxDataStoreRequest request)
        {
            if (request == null || request.Operation != RbxDataStoreOperation.Update
                                || request.State != RbxDataStoreRequestState.Granted
                                || !ReferenceEquals(request.Store.Service, this))
            {
                throw RbxError.BadArgument("UpdateAsync was not granted or was already committed",
                    "call store:UpdateAsync(key, function(old) ... end) and let it run once");
            }

            if (_closed)
            {
                request.State = RbxDataStoreRequestState.Failed;
                request.Error = RbxDataStoreErrors.ShuttingDown();
                throw request.Error;
            }
        }

        private void Release(string modId)
        {
            if (_waitingByMod.TryGetValue(modId, out int count))
            {
                if (count <= 1)
                {
                    _waitingByMod.Remove(modId);
                }
                else
                {
                    _waitingByMod[modId] = count - 1;
                }
            }
        }

        private void WarnQueued(RbxDataStoreRequest request)
        {
            double now = _backend.Clock();
            if (_log == null || now - _lastQueueWarning < 10d)
            {
                return;
            }

            _lastQueueWarning = now;
            StringBuilder warning = new(160);
            warning.Append("DataStore request was added to queue. If request queue fills, further requests will be dropped. ")
                .Append("Try sending fewer requests. Key = ").Append(request.Key);
            _log(warning.ToString());
        }
    }

    /// <summary>
    /// Roblox <c>DataStore</c> (a <c>GlobalDataStore</c>): one named, scoped store of the world's data. <c>Name</c> is
    /// the store name. Detached; its yielding members are in the Lua binding layer.
    /// </summary>
    public sealed class RbxGlobalDataStore : RbxInstance
    {
        internal RbxGlobalDataStore(ClassCatalog catalog, RbxDataStoreService service, string name, string scope)
            : base(RbxDataStoreClasses.Descriptor(catalog, RbxDataStoreClasses.DataStore))
        {
            Service = service;
            StoreName = name;
            Scope = scope;
            Name = name.Length == 0 ? "GlobalDataStore" : name;
        }

        /// <summary>The service of the world the store belongs to.</summary>
        public RbxDataStoreService Service { get; }

        /// <summary>The store name ("" for <c>GetGlobalDataStore</c>).</summary>
        public string StoreName { get; }

        /// <summary>The scope.</summary>
        public string Scope { get; }
    }

    /// <summary>
    /// Roblox <c>DataStoreKeyInfo</c>: <c>CreatedTime</c> and <c>UpdatedTime</c> (milliseconds since the Unix epoch),
    /// <c>Version</c>, <c>GetUserIds()</c> and <c>GetMetadata()</c> of one entry at the time it was read or written.
    /// </summary>
    public sealed class RbxDataStoreKeyInfo : RbxInstance
    {
        /// <summary>Creates the information of <paramref name="entry"/>.</summary>
        public RbxDataStoreKeyInfo(ClassCatalog catalog, RbxDataStoreEntry entry)
            : base(RbxDataStoreClasses.Descriptor(catalog, RbxDataStoreClasses.KeyInfo))
        {
            RbxDataStoreEntry source = entry ?? throw new ArgumentNullException(nameof(entry));
            Name = RbxDataStoreClasses.KeyInfo;
            CreatedTime = source.CreatedMilliseconds;
            UpdatedTime = source.UpdatedMilliseconds;
            Version = VersionText(source.Version);
            UserIds = source.UserIds == null ? Array.Empty<long>() : (long[])source.UserIds.Clone();
            MetadataJson = string.IsNullOrEmpty(source.MetadataJson) ? "{}" : source.MetadataJson;
        }

        /// <summary>Mirror <c>CreatedTime</c>.</summary>
        public long CreatedTime { get; }

        /// <summary>Mirror <c>UpdatedTime</c>.</summary>
        public long UpdatedTime { get; }

        /// <summary>Mirror <c>Version</c>: an opaque string, here the write count of the key.</summary>
        public string Version { get; }

        /// <summary>The user ids <c>GetUserIds()</c> returns.</summary>
        public long[] UserIds { get; }

        /// <summary>The metadata <c>GetMetadata()</c> returns, as JSON text.</summary>
        public string MetadataJson { get; }

        /// <summary>The version string of write <paramref name="version"/> of a key.</summary>
        public static string VersionText(long version)
        {
            return version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
